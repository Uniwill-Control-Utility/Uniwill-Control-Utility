using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Serilog;
using UCU.Contracts;
using UCU.Contracts.Capabilities;
using UCU.Core.Supervision;
using Uniwill_Control_Utility.Properties;

namespace Uniwill_Control_Utility.Services
{
    /// <summary>
    /// Holds the GPU at minimum power when it stays too hot, and puts the previous
    /// contract back once it has cooled (US-11). Off by default: it deliberately costs
    /// performance, so the user opts in.
    ///
    /// What it does on a trip: capture the GPU contract the EC is enforcing right now,
    /// write it to disk, then activate the base TGP with Dynamic Boost off (0x0743/0x0744
    /// = 0, 0x0745 = 0xFF, 0x0746 = 0). The capture is persisted before the write, so a
    /// crash or a power cut mid-hold still leaves the original values recoverable, and the
    /// state file is what makes the hold survive a reboot.
    ///
    /// What it does on release: heal the captured contract back through the engine's own
    /// replay verb, and record it in <see cref="PowerContractStore"/> so the power guardian
    /// heals *to* the restored values instead of fighting them.
    ///
    /// The decision rule and its two failure directions live in
    /// <see cref="ThermalFailsafePolicy"/>; this service is the cadence, the persistence
    /// and the evidence.
    /// </summary>
    internal sealed class ThermalFailsafeService : BackgroundService
    {
        /// <summary>Sample cadence. One fan-status read, which is one EC round trip.</summary>
        internal static readonly TimeSpan Poll = TimeSpan.FromSeconds(1);

        private static readonly string StatePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "UCU", "thermal-failsafe.json");

        private readonly IFanCapability _fan;
        private readonly IPowerCapability _power;

        private bool _tripped;
        private int _consecutive;
        private ThermalFailsafeDecision _pending = ThermalFailsafeDecision.None;

        public ThermalFailsafeService(IFanCapability fan, IPowerCapability power)
        {
            _fan = fan;
            _power = power;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _tripped = LoadState();

            if (_tripped)
            {
                Log.Warning("[Failsafe] resuming a trip from a previous session (threshold {Threshold}C)", Threshold);

                if (!Settings.Default.ThermalFailsafeEnabled)
                {
                    // The feature was switched off while held: never leave the GPU held.
                    await ReleaseAsync("feature disabled").ConfigureAwait(false);
                }
                else
                {
                    await HoldAsync(reason: "resumed").ConfigureAwait(false);
                }
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(Poll, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                try
                {
                    if (!Settings.Default.ThermalFailsafeEnabled)
                    {
                        if (_tripped)
                            await ReleaseAsync("feature disabled").ConfigureAwait(false);
                        _consecutive = 0;
                        continue;
                    }

                    ThermalFailsafeDecision decision = ThermalFailsafePolicy.Decide(
                        await ReadGpuTemperatureAsync().ConfigureAwait(false), Threshold, _tripped);

                    if (decision == ThermalFailsafeDecision.None)
                    {
                        _consecutive = 0;
                        continue;
                    }

                    // Three samples must agree, so one stray reading cannot act.
                    if (decision != _pending)
                    {
                        _pending = decision;
                        _consecutive = 0;
                    }

                    if (++_consecutive < ThermalFailsafePolicy.SamplesRequired)
                        continue;

                    _consecutive = 0;
                    if (decision == ThermalFailsafeDecision.Trip)
                        await HoldAsync(reason: "sustained temperature").ConfigureAwait(false);
                    else
                        await ReleaseAsync("cooled").ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    // No engine, no EC, a model without GPU control: none of these may take
                    // the app down, and the next tick retries.
                    Log.Warning(ex, "[Failsafe] supervision cycle failed");
                }
            }
        }

        private int Threshold =>
            ThermalFailsafePolicy.IsValidThreshold(Settings.Default.ThermalFailsafeThreshold)
                ? Settings.Default.ThermalFailsafeThreshold
                : ThermalFailsafePolicy.DefaultThresholdCelsius;

        /// <summary>GPU temperature, or null when the reading is not usable.</summary>
        private async Task<int?> ReadGpuTemperatureAsync()
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            FanStatusDto status = await _fan.GetStatusAsync(cts.Token).ConfigureAwait(false);
            return status.GpuTemperature > 0 ? status.GpuTemperature : null;
        }

        private async Task HoldAsync(string reason)
        {
            if (_tripped)
                return;

            // Capture first, persist, then write: the original values must survive a crash.
            int ctgp = 0, db = 0;
            try
            {
                PowerContractTruthDto truth = await _power.GetContractTruthAsync().ConfigureAwait(false);
                ctgp = truth.GpuCtgp;
                db = truth.GpuDb;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[Failsafe] could not capture the pre-trip contract; restoring will fall back to the preset");
            }

            SaveState(new FailsafeState { Ctgp = ctgp, Db = db });
            PowerContractStore.NoteGpuApplied(0, 0);

            try
            {
                await _power.ActivateGpuPowerControlAsync(0, 0).ConfigureAwait(false);
                _tripped = true;
                Log.Warning("[Failsafe] GPU held at minimum power ({Reason}); was cTGP {Ctgp}, DB {Db}", reason, ctgp, db);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[Failsafe] could not apply the hold");
            }
        }

        private async Task ReleaseAsync(string reason)
        {
            FailsafeState state = LoadStateValues();
            _tripped = false;

            try
            {
                if (state.Ctgp > 0 || state.Db > 0)
                {
                    await _power.HealContractAsync(new HealContractDto
                    {
                        ApplyGpu = true,
                        GpuCtgpOffset = state.Ctgp,
                        GpuDbOffset = state.Db,
                    }).ConfigureAwait(false);

                    PowerContractStore.NoteGpuApplied(state.Ctgp, state.Db);
                    Log.Information("[Failsafe] released ({Reason}); restored cTGP {Ctgp}, DB {Db}", reason, state.Ctgp, state.Db);
                }
                else
                {
                    Log.Information("[Failsafe] released ({Reason}); no captured contract to restore", reason);
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[Failsafe] could not restore the pre-trip contract; the next preset apply will");
            }
            finally
            {
                DeleteState();
            }
        }

        // ---- persistence ---------------------------------------------------

        private sealed class FailsafeState
        {
            public int Ctgp { get; set; }

            public int Db { get; set; }
        }

        private static bool LoadState() => File.Exists(StatePath);

        private static FailsafeState LoadStateValues()
        {
            try
            {
                return File.Exists(StatePath)
                    ? JsonSerializer.Deserialize<FailsafeState>(File.ReadAllText(StatePath)) ?? new FailsafeState()
                    : new FailsafeState();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[Failsafe] state file unreadable; treating as unknown");
                return new FailsafeState();
            }
        }

        private static void SaveState(FailsafeState state)
        {
            try
            {
                AtomicJsonFile.Write(StatePath, JsonSerializer.Serialize(state));
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[Failsafe] could not persist the trip; a restart will not know it was held");
            }
        }

        private static void DeleteState()
        {
            try
            {
                if (File.Exists(StatePath))
                    File.Delete(StatePath);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[Failsafe] could not clear the trip state file");
            }
        }
    }
}
