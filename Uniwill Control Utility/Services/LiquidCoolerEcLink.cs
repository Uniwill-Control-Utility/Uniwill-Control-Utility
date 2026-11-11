using Microsoft.Extensions.Hosting;
using Microsoft.Win32;
using Serilog;
using System;
using System.Threading;
using System.Threading.Tasks;
using UCU.Contracts.Capabilities;

namespace Uniwill_Control_Utility.Services
{
    /// <summary>
    /// Keeps the EC's liquid-cooler flag (0x07C7 bit 1, "LCSE") in step with the Hydro
    /// UI's real connection state (US-04).
    ///
    /// The EC cannot detect the cooler on its own: the OEM sets this bit from its own BLE
    /// handler when the cooler connects. With it set the firmware moves to its cooler
    /// policy - higher CPU preset ceilings, moved fan-table bases, and (per the Linux
    /// driver) OEM TDP profile index 3 instead of 2 for Performance - so the bit must
    /// follow reality in both directions. A stale set bit would raise ceilings with no
    /// cooler attached, which is why startup clears it rather than trusting the EC.
    ///
    /// Lifecycle rules, each an acceptance criterion:
    ///  - start: clear (a leftover flag from a previous session cannot be assumed live);
    ///    auto-connect re-sets it seconds later if the cooler is really there;
    ///  - Hydro connect/disconnect: follow the event;
    ///  - resume: re-assert, because the EC clears the flag on power transitions;
    ///  - shutdown: clear, so the flag never outlives the process that owns the cooler.
    ///
    /// The engine performs the bit-only read-modify-write and refuses on models whose
    /// 0x07A4 bit 7 is clear, so this service can run unconditionally.
    /// </summary>
    internal sealed class LiquidCoolerEcLink : IHostedService, IDisposable
    {
        /// <summary>
        /// Re-assert cadence. Measured 2026-11-10 on X6FR5xxY: LCSE was set at 22:26 and
        /// had cleared itself by 22:40 without the cooler being touched, so a one-shot set
        /// is not enough. One byte every 30 s is free next to the fan tick, and the engine
        /// no-ops when the register already matches.
        /// </summary>
        private static readonly TimeSpan Reconcile = TimeSpan.FromSeconds(30);

        /// <summary>Budget for one flag write: it is one EC byte plus a readback.</summary>
        private static readonly TimeSpan WriteBudget = TimeSpan.FromSeconds(5);

        private readonly HydroUIService _hydro;
        private readonly IPowerCapability _power;

        /// <summary>
        /// Serializes the writes. The startup clear is enqueued before any connection
        /// event can fire, so FIFO order guarantees the pair lands as "clear, then set"
        /// and a fast auto-connect can never be undone by the startup clear.
        /// </summary>
        private readonly SemaphoreSlim _gate = new(1, 1);

        private readonly CancellationTokenSource _reconcileCts = new();
        private Task? _reconcileLoop;
        private bool _disposed;

        public LiquidCoolerEcLink(HydroUIService hydro, IPowerCapability power)
        {
            _hydro = hydro;
            _power = power;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _hydro.ConnectionStateChanged += OnConnectionStateChanged;
            SystemEvents.PowerModeChanged += OnPowerModeChanged;

            // Clear first: a flag left by a previous session would raise ceilings with
            // no cooler present. Auto-connect re-sets it if the cooler is really there.
            _ = ApplyAsync(attached: false, reason: "startup");

            _reconcileLoop = Task.Run(ReconcileLoopAsync);
            return Task.CompletedTask;
        }

        /// <summary>
        /// Keeps the flag matched to the live connection state. The EC owns and clears
        /// this bit on its own (observed), so following only connect/disconnect events
        /// leaves it drifted until the next cooler interaction.
        /// </summary>
        private async Task ReconcileLoopAsync()
        {
            using var timer = new PeriodicTimer(Reconcile);
            try
            {
                while (await timer.WaitForNextTickAsync(_reconcileCts.Token).ConfigureAwait(false))
                    await ApplyAsync(_hydro.IsConnected, "reconcile").ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Shutdown.
            }
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            Unsubscribe();

            _reconcileCts.Cancel();
            if (_reconcileLoop is not null)
            {
                try
                {
                    await _reconcileLoop.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Expected on shutdown.
                }
            }

            // Awaited (bounded by WriteBudget): the engine outlives the app, so the clear
            // can land, and it must not be cut off by teardown.
            await ApplyAsync(attached: false, reason: "shutdown").ConfigureAwait(false);
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            // The semaphore is deliberately not disposed: an event handler already waiting
            // on it must finish (it is caught), and SemaphoreSlim holds no kernel handle
            // unless AvailableWaitHandle is used.
            Unsubscribe();
            _disposed = true;
        }

        private void Unsubscribe()
        {
            _hydro.ConnectionStateChanged -= OnConnectionStateChanged;
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        }

        private void OnConnectionStateChanged(object? sender, HydroUIService.WatercoolerConnectionState state)
        {
            switch (state)
            {
                case HydroUIService.WatercoolerConnectionState.Connected:
                    _ = ApplyAsync(attached: true, reason: "cooler connected");
                    break;
                case HydroUIService.WatercoolerConnectionState.Disconnected:
                    _ = ApplyAsync(attached: false, reason: "cooler disconnected");
                    break;
                // Scanning is not a state change the EC cares about.
            }
        }

        private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode == PowerModes.Resume)
                _ = ApplyAsync(attached: _hydro.IsConnected, reason: "resume");
        }

        private async Task ApplyAsync(bool attached, string reason)
        {
            try
            {
                using var cts = new CancellationTokenSource(WriteBudget);
                await _gate.WaitAsync(cts.Token).ConfigureAwait(false);
                try
                {
                    bool applied = await _power.SetLiquidCoolerAttachedAsync(attached, cts.Token)
                        .ConfigureAwait(false);
                    Log.Information(
                        "[CoolerEcFlag] {Reason}: 0x07C7 LCSE {State}{Note}",
                        reason,
                        attached ? "set" : "clear",
                        applied ? string.Empty : " - refused or unverified, see the engine log");
                }
                finally
                {
                    _gate.Release();
                }
            }
            catch (Exception ex)
            {
                // No cooler, no engine, model without liquid cooling: none of these may
                // take the app down, and none of them leave the EC in a worse state than
                // the clear-at-startup default.
                Log.Warning(ex, "[CoolerEcFlag] {Reason}: could not update 0x07C7", reason);
            }
        }
    }
}
