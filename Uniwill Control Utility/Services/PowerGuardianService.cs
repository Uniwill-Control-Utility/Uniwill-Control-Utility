using Microsoft.Extensions.Hosting;
using Serilog;
using System;
using System.Threading;
using System.Threading.Tasks;
using UCU.Contracts;
using UCU.Contracts.Capabilities;
using UCU.Core.Supervision;
using Uniwill_Control_Utility.Properties;
using Uniwill_Control_Utility.Scripts.GPUs.NVIDIA;
using Uniwill_Control_Utility.Scripts.Misc;

namespace Uniwill_Control_Utility.Services
{
    /// <summary>
    /// The power-tuning guardian (U-107, the OpenRevo heal-contract pattern):
    /// a periodic drift check that asks the EC what power contract it enforces
    /// right now and, when a third party (MSI Afterburner, an OEM control
    /// center leftover) has reset it, re-applies the contract the preset
    /// pipeline last applied — through the engine's power.heal_contract verb,
    /// which replays the preset pipeline's own activation sequence.
    ///
    /// Scope is deliberately narrow (v1): the GPU cTGP/DB activation and the
    /// CPU PL1/PL2 pair, each behind its own opt-in toggle (default OFF —
    /// healing wars with third-party tuners, so the user opts in per feature).
    /// The decision is pure Core policy (PowerContractPolicy); this service is
    /// the cadence, the gates, and the evidence.
    ///
    /// The gates, each a story acceptance:
    ///  - per-feature toggle off → that half is never read for healing, never
    ///    healed (acceptance b);
    ///  - Adaptive running → skip the cycle: Adaptive's own diff-apply loop
    ///    owns tuning while it runs (the U-101 ownership rule);
    ///  - GpuInteropGuard blocked (driver-transition cooldown) → the GPU half
    ///    is skipped (acceptance c);
    ///  - engine Busy / not answering → back off (acceptance d: queue
    ///    backpressure honored, the cycle never queues onto a saturated power
    ///    domain);
    ///  - the heal re-verifies against the EC's answer and logs before/after
    ///    evidence (acceptance a), and the user note is a throttled toast.
    /// </summary>
    internal sealed class PowerGuardianService : IHostedService, IDisposable
    {
        /// <summary>First cycle waits out startup apply + engine start.</summary>
        internal static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(60);

        /// <summary>Drift cadence (the story's poll cycle; the EC reads ride
        /// the power domain, one round-trip per cycle).</summary>
        internal static readonly TimeSpan Poll = TimeSpan.FromSeconds(30);

        /// <summary>After an engine-not-answering cycle: do not hammer.</summary>
        internal static readonly TimeSpan EngineBackoff = TimeSpan.FromSeconds(60);

        /// <summary>Minimum spacing between user toasts (the log keeps the
        /// full evidence trail regardless).</summary>
        internal static readonly TimeSpan ToastThrottle = TimeSpan.FromMinutes(10);

        private readonly Func<CancellationToken, Task<PowerContractTruthDto?>> _readTruth;
        private readonly Func<HealContractDto, CancellationToken, Task> _heal;
        private readonly Func<PowerContract> _contract;
        private readonly Func<bool> _gpuEnabled;
        private readonly Func<bool> _cpuEnabled;
        private readonly Func<bool> _adaptiveRunning;
        private readonly Func<bool> _gpuGuardBlocked;
        private readonly Action<string, string> _notify;
        private readonly TimeSpan _startupDelay;
        private readonly TimeSpan _poll;
        private readonly Func<DateTime> _clock;

        private readonly CancellationTokenSource _cts = new();
        private Task? _loopTask;
        private int _inCycle;
        private DateTime _lastToastUtc = DateTime.MinValue;
        private DateTime _backoffUntilUtc = DateTime.MinValue;
        private bool _disposed;

        public PowerGuardianService(IPowerCapability power)
            : this(
                async ct =>
                {
                    try
                    {
                        return await power.GetContractTruthAsync(ct).ConfigureAwait(false);
                    }
                    catch (EngineException ex) when (EngineNotAnswering(ex))
                    {
                        // The engine is not answering; "no truth this cycle",
                        // the loop treats it as a backoff signal.
                        throw new EngineNotAnsweringException(ex.Code, ex.Message);
                    }
                },
                (plan, ct) => power.HealContractAsync(plan, ct),
                () => PowerContractStore.Current,
                () => Settings.Default.PowerGuardGpu,
                () => Settings.Default.PowerGuardCpu,
                () => Settings.Default.isAdaptiveModeRunning,
                () => GpuInteropGuard.IsBlocked,
                (title, body) => ToastNotification.ShowToastNotification(title, body),
                StartupDelay, Poll, () => DateTime.UtcNow)
        {
        }

        /// <summary>Test seam: scripted truth/heal, delegate-gated enables,
        /// capture sink for the user note, injectable clock.</summary>
        internal PowerGuardianService(
            Func<CancellationToken, Task<PowerContractTruthDto?>> readTruth,
            Func<HealContractDto, CancellationToken, Task> heal,
            Func<PowerContract> contract,
            Func<bool> gpuEnabled,
            Func<bool> cpuEnabled,
            Func<bool> adaptiveRunning,
            Func<bool> gpuGuardBlocked,
            Action<string, string> notify,
            TimeSpan? startupDelay = null,
            TimeSpan? poll = null,
            Func<DateTime>? clock = null)
        {
            _readTruth = readTruth;
            _heal = heal;
            _contract = contract;
            _gpuEnabled = gpuEnabled;
            _cpuEnabled = cpuEnabled;
            _adaptiveRunning = adaptiveRunning;
            _gpuGuardBlocked = gpuGuardBlocked;
            _notify = notify;
            _startupDelay = startupDelay ?? StartupDelay;
            _poll = poll ?? Poll;
            _clock = clock ?? (() => DateTime.UtcNow);
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _loopTask = Task.Run(() => RunLoopAsync(_cts.Token));
            return Task.CompletedTask;
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            _cts.Cancel();
            if (_loopTask is not null)
            {
                try
                {
                    await Task.WhenAny(_loopTask, Task.Delay(3000, CancellationToken.None));
                }
                catch (Exception ex)
                {
                    Log.Warning("[PowerGuardian] {ExceptionType} while stopping: {Message}", ex.GetType().Name, ex.Message);
                }
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _cts.Cancel();
            _cts.Dispose();
        }

        private async Task RunLoopAsync(CancellationToken ct)
        {
            await Task.Delay(_startupDelay, ct).ConfigureAwait(false);

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await RunCycleAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // The guardian never takes the app down over a drift
                    // check; the next cycle tries again.
                    Log.Information("[PowerGuardian] cycle failed: {ExceptionType}: {Message}", ex.GetType().Name, ex.Message);
                }

                try
                {
                    await Task.Delay(_poll, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        /// <summary>
        /// One drift check: gates → truth read → pure plan → heal → verify →
        /// evidence. Internal for hermetic tests, which drive this directly
        /// with scripted seams (the UninstallSentinel Step shape).
        /// </summary>
        internal async Task RunCycleAsync(CancellationToken ct = default)
        {
            if (Interlocked.CompareExchange(ref _inCycle, 1, 0) != 0)
                return; // a slow cycle is in flight; never stack cycles

            try
            {
                PowerContract contract = _contract();
                if (contract.IsEmpty)
                    return;

                bool wantGpu = _gpuEnabled() && contract.HasGpu && !_adaptiveRunning() && !_gpuGuardBlocked();
                bool wantCpu = _cpuEnabled() && contract.HasCpu && !_adaptiveRunning();
                if (!wantGpu && !wantCpu)
                    return;

                if (_clock() < _backoffUntilUtc)
                    return;

                PowerContractTruthDto? truth;
                try
                {
                    truth = await _readTruth(ct).ConfigureAwait(false);
                }
                catch (EngineNotAnsweringException ex)
                {
                    _backoffUntilUtc = _clock().Add(EngineBackoff);
                    Log.Information("[PowerGuardian] engine not answering ({Code}: {Message}) - backing off", ex.Code, ex.Message);
                    return;
                }

                if (truth is null)
                    return;

                // Only the enabled, observable halves are masked in: a null
                // truth half is "no answer", the policy heals on nothing.
                PowerContract masked = contract with { HasGpu = wantGpu, HasCpu = wantCpu };
                var gpuTruth = wantGpu
                    ? new GpuPowerTruth((byte)truth.GpuEnable, (byte)truth.GpuCtgp, (byte)truth.GpuTpp, (byte)truth.GpuDb, truth.GpuCustomProfileActive)
                    : (GpuPowerTruth?)null;
                var cpuTruth = wantCpu ? new CpuPowerTruth(truth.Pl1, truth.Pl2) : (CpuPowerTruth?)null;

                PowerHealPlan plan = PowerContractPolicy.ComputeHealPlan(masked, gpuTruth, cpuTruth);
                if (plan.IsClean)
                    return;

                // Acceptance a: the evidence lands before the act.
                Log.Information("[PowerGuardian] drift detected: gpu={GpuDrift} cpu={CpuDrift}",
                    plan.GpuReason ?? "clean", plan.CpuReason ?? "clean");

                try
                {
                    await _heal(new HealContractDto
                    {
                        ApplyGpu = plan.HealGpu,
                        GpuCtgpOffset = plan.GpuCtgpOffset,
                        GpuDbOffset = plan.GpuDbOffset,
                        ApplyCpu = plan.HealCpu,
                        Pl1Watts = plan.Pl1Watts,
                        Pl2Watts = plan.Pl2Watts,
                    }, ct).ConfigureAwait(false);
                }
                catch (EngineNotAnsweringException ex)
                {
                    _backoffUntilUtc = _clock().Add(EngineBackoff);
                    Log.Information("[PowerGuardian] heal refused ({Code}: {Message}) - backing off", ex.Code, ex.Message);
                    return;
                }
                catch (EngineException ex)
                {
                    // A refused heal (InvalidValue: contract bytes no longer
                    // valid for this firmware) is not retried blindly.
                    Log.Information("[PowerGuardian] heal refused: {Code}: {Message}", ex.Code, ex.Message);
                    return;
                }

                await VerifyAsync(masked, ct).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Exchange(ref _inCycle, 0);
            }
        }

        /// <summary>Post-heal truth: the plan must come back clean. The toast
        /// fires only on the firmware's word that the contract is restored
        /// (the guardian claims healing, it does not hope it); the log always
        /// carries the verdict.</summary>
        private async Task VerifyAsync(PowerContract masked, CancellationToken ct)
        {
            PowerContractTruthDto? after;
            try
            {
                after = await _readTruth(ct).ConfigureAwait(false);
            }
            catch (EngineNotAnsweringException)
            {
                return;
            }

            if (after is null)
                return;

            PowerHealPlan recheck = PowerContractPolicy.ComputeHealPlan(
                masked,
                masked.HasGpu ? new GpuPowerTruth((byte)after.GpuEnable, (byte)after.GpuCtgp, (byte)after.GpuTpp, (byte)after.GpuDb, after.GpuCustomProfileActive) : null,
                masked.HasCpu ? new CpuPowerTruth(after.Pl1, after.Pl2) : null);

            if (recheck.IsClean)
            {
                Log.Information("[PowerGuardian] contract restored, verified by EC re-read");

                if (_clock() - _lastToastUtc >= ToastThrottle)
                {
                    _lastToastUtc = _clock();
                    _notify("Power limits restored",
                        "GPU/CPU power limits were changed by another program. UCU re-applied your tuning.");
                }
            }
            else
            {
                Log.Information("[PowerGuardian] heal NOT verified: gpu={GpuLeft} cpu={CpuLeft}",
                    recheck.GpuReason ?? "clean", recheck.CpuReason ?? "clean");
            }
        }

        private static bool EngineNotAnswering(EngineException ex) =>
            ex.Code is EngineErrorCode.HardwareUnavailable or EngineErrorCode.Timeout or EngineErrorCode.Busy;

        /// <summary>The triple, re-thrown from the read seam so the loop's
        /// backoff catches read and heal refusals with one shape. Internal:
        /// the hermetic tests script a not-answering engine through it.</summary>
        internal sealed class EngineNotAnsweringException : Exception
        {
            public EngineNotAnsweringException(EngineErrorCode code, string message) : base(message) => Code = code;

            public EngineErrorCode Code { get; }
        }
    }
}
