using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Serilog;
using UCU.Contracts;
using UCU.Contracts.Capabilities;
using UCU.Core.Ec;
using UCU.Core.Supervision;
using Uniwill_Control_Utility.Properties;

namespace Uniwill_Control_Utility.Services
{
    /// <summary>
    /// The thermal failsafe (US-11): raises the hardware fans, then holds power down, when
    /// the CPU or the GPU stays too hot, and puts everything back once it has cooled.
    /// Off by default - it costs performance by design.
    ///
    /// Response order is deliberate. Fans go up first because that is the fastest and most
    /// reversible thing available; power is held second because it is the effective thing.
    /// The two domains are independent: a hot GPU holds GPU power only, a hot CPU holds CPU
    /// power only, and either raises the fans.
    ///
    /// The CPU hold is not one register. On AMD the binding limit is the SMU slow/fast pair
    /// (an EC PL write is accepted and ignored while the SMU allows more), and on Intel it
    /// is the MSR package limit, so the hold goes through the same vendor verbs presets use
    /// and also mirrors the pair into the EC. The restore target comes from
    /// <see cref="PowerContractStore"/> - the EC can be read back, the SMU cannot.
    ///
    /// Everything captured before a hold is persisted first, so a crash or power cut
    /// mid-hold still leaves the values recoverable and a restart re-applies the hold.
    /// </summary>
    internal sealed class ThermalFailsafeService : BackgroundService
    {
        /// <summary>Sample cadence: one fan-status read, which is one EC round trip.</summary>
        internal static readonly TimeSpan Poll = TimeSpan.FromSeconds(1);

        /// <summary>
        /// The CPU hold. Low enough to matter thermally, high enough that the machine stays
        /// usable - deliberately not the EC's floor, which would make it crawl.
        /// </summary>
        internal const int CpuHoldWatts = 50;

        private static readonly string StatePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "UCU", "thermal-failsafe.json");

        private readonly IFanCapability _fan;
        private readonly IPowerCapability _power;
        private readonly IAmdSmuCapability _amd;
        private readonly IIntelMsrCapability _intel;
        private readonly IEngineCapability _engine;

        private bool _gpuTripped;
        private bool _cpuTripped;
        private bool _fanBoostSetByUs;
        private int _gpuStreak;
        private int _cpuStreak;
        private ThermalFailsafeDecision _gpuPending = ThermalFailsafeDecision.None;
        private ThermalFailsafeDecision _cpuPending = ThermalFailsafeDecision.None;
        private EcPlatform? _platform;

        public ThermalFailsafeService(
            IFanCapability fan,
            IPowerCapability power,
            IAmdSmuCapability amd,
            IIntelMsrCapability intel,
            IEngineCapability engine)
        {
            _fan = fan;
            _power = power;
            _amd = amd;
            _intel = intel;
            _engine = engine;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (File.Exists(StatePath))
            {
                Log.Warning("[Failsafe] a trip from a previous session is on disk (thresholds {Gpu}/{Cpu}C)",
                    GpuThreshold, CpuThreshold);

                if (!Settings.Default.ThermalFailsafeEnabled)
                    await ReleaseAsync("feature disabled").ConfigureAwait(false);
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
                        if (_gpuTripped || _cpuTripped)
                            await ReleaseAsync("feature disabled").ConfigureAwait(false);
                        ResetStreaks();
                        continue;
                    }

                    FanStatusDto? status = await ReadStatusAsync().ConfigureAwait(false);
                    if (status is null)
                        continue;

                    int? gpu = status.GpuTemperature > 0 ? status.GpuTemperature : null;
                    int? cpu = status.CpuTemperature > 0 ? status.CpuTemperature : null;

                    await AdvanceAsync(Domain.Gpu,
                        ThermalFailsafePolicy.Decide(gpu, GpuThreshold, _gpuTripped)).ConfigureAwait(false);
                    await AdvanceAsync(Domain.Cpu,
                        ThermalFailsafePolicy.Decide(cpu, CpuThreshold, _cpuTripped)).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    // No engine, no EC, a model without power control: none of these may take
                    // the app down, and the next tick retries.
                    Log.Warning(ex, "[Failsafe] supervision cycle failed");
                }
            }
        }

        private enum Domain
        {
            Gpu = 0,
            Cpu = 1,
        }

        private int GpuThreshold =>
            ThermalFailsafePolicy.IsValidThreshold(Settings.Default.ThermalFailsafeThreshold)
                ? Settings.Default.ThermalFailsafeThreshold
                : ThermalFailsafePolicy.DefaultThresholdCelsius;

        /// <summary>CPUs run hotter than GPUs under load, so they get their own trip point.</summary>
        private int CpuThreshold =>
            ThermalFailsafePolicy.IsValidCpuThreshold(Settings.Default.ThermalFailsafeCpuThreshold)
                ? Settings.Default.ThermalFailsafeCpuThreshold
                : ThermalFailsafePolicy.DefaultCpuThresholdCelsius;

        private async Task<FanStatusDto?> ReadStatusAsync()
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try
            {
                return await _fan.GetStatusAsync(cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Debug("[Failsafe] status read failed: {Message}", ex.Message);
                return null;
            }
        }

        /// <summary>Counts agreeing samples, then acts. Three must agree, so one bad byte cannot.</summary>
        private async Task AdvanceAsync(Domain domain, ThermalFailsafeDecision decision)
        {
            if (decision == ThermalFailsafeDecision.None)
            {
                if (domain == Domain.Gpu)
                {
                    _gpuStreak = 0;
                    _gpuPending = ThermalFailsafeDecision.None;
                }
                else
                {
                    _cpuStreak = 0;
                    _cpuPending = ThermalFailsafeDecision.None;
                }
                return;
            }

            if (domain == Domain.Gpu)
            {
                if (decision != _gpuPending)
                {
                    _gpuPending = decision;
                    _gpuStreak = 0;
                }

                if (++_gpuStreak < ThermalFailsafePolicy.SamplesRequired)
                    return;

                _gpuStreak = 0;
            }
            else
            {
                if (decision != _cpuPending)
                {
                    _cpuPending = decision;
                    _cpuStreak = 0;
                }

                if (++_cpuStreak < ThermalFailsafePolicy.SamplesRequired)
                    return;

                _cpuStreak = 0;
            }

            if (decision == ThermalFailsafeDecision.Trip)
                await HoldAsync(domain).ConfigureAwait(false);
            else
                await ReleaseAsync(domain == Domain.Gpu ? "GPU cooled" : "CPU cooled").ConfigureAwait(false);
        }

        private void ResetStreaks()
        {
            _gpuStreak = 0;
            _cpuStreak = 0;
            _gpuPending = ThermalFailsafeDecision.None;
            _cpuPending = ThermalFailsafeDecision.None;
        }

        private async Task HoldAsync(Domain domain)
        {
            bool firstHold = !_gpuTripped && !_cpuTripped;

            if (firstHold)
            {
                // Capture, persist, then write: the original values must survive a crash.
                FailsafeState state = new();
                try
                {
                    PowerContractTruthDto truth = await _power.GetContractTruthAsync().ConfigureAwait(false);
                    state.Ctgp = truth.GpuCtgp;
                    state.Db = truth.GpuDb;
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "[Failsafe] could not capture the pre-trip GPU contract");
                }

                state.CpuPl1 = PowerContractStore.Current.HasCpu ? PowerContractStore.Current.Pl1Watts : 0;
                state.CpuPl2 = PowerContractStore.Current.HasCpu ? PowerContractStore.Current.Pl2Watts : 0;

                FanStatusDto? status = await ReadStatusAsync().ConfigureAwait(false);
                state.FanBoostWasOn = status?.TurboActive ?? false;
                state.GpuTripped = domain == Domain.Gpu && !_gpuTripped;
                state.CpuTripped = domain == Domain.Cpu && !_cpuTripped;

                SaveState(state);

                // Fans first: fastest, most reversible, and what the firmware itself does.
                if (!state.FanBoostWasOn)
                {
                    try
                    {
                        if (await _fan.SetFanBoostAsync(true).ConfigureAwait(false))
                            _fanBoostSetByUs = true;
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "[Failsafe] could not raise the hardware fan boost");
                    }
                }
            }

            if (domain == Domain.Gpu && !_gpuTripped)
            {
                _gpuTripped = true;
                PowerContractStore.NoteGpuApplied(0, 0);
                await _power.ActivateGpuPowerControlAsync(0, 0).ConfigureAwait(false);
                Log.Warning("[Failsafe] GPU held at minimum power (sustained GPU temperature)");
            }
            else if (domain == Domain.Cpu && !_cpuTripped)
            {
                _cpuTripped = true;
                PowerContractStore.NoteCpuApplied(CpuHoldWatts, CpuHoldWatts);
                await ApplyCpuWattsAsync(CpuHoldWatts).ConfigureAwait(false);
                Log.Warning("[Failsafe] CPU held at {Watts} W (sustained CPU temperature)", CpuHoldWatts);
            }
        }

        /// <summary>
        /// Applies a CPU package limit through the surface that actually binds: the SMU pair
        /// on AMD, the MSR package limit on Intel, plus the EC mirror the presets also write.
        /// </summary>
        private async Task ApplyCpuWattsAsync(int watts)
        {
            try
            {
                await _power.ApplyCpuPowerLimitsAsync(watts, watts).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "[Failsafe] EC CPU limits not applied");
            }

            EcPlatform platform = await ResolvePlatformAsync().ConfigureAwait(false);

            try
            {
                if (platform == EcPlatform.Amd)
                {
                    // Milliwatts, and both limits together: slow-limit is the sustained cap
                    // the firmware enforces, fast-limit the short one above it.
                    await _amd.ExecuteCommandAsync("slow-limit", (uint)(watts * 1000)).ConfigureAwait(false);
                    await _amd.ExecuteCommandAsync("fast-limit", (uint)(watts * 1000)).ConfigureAwait(false);
                }
                else
                {
                    await _intel.SetTdpAsync(watts, watts).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[Failsafe] CPU limit ({Watts} W) not applied on {Platform}", watts, platform);
            }
        }

        private async Task<EcPlatform> ResolvePlatformAsync()
        {
            if (_platform is { } known)
                return known;

            try
            {
                _platform = (await _engine.GetInfoAsync().ConfigureAwait(false)).Platform;
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "[Failsafe] platform unknown; assuming AMD");
                _platform = EcPlatform.Amd;
            }

            return _platform.Value;
        }

        private async Task ReleaseAsync(string reason)
        {
            FailsafeState state = LoadStateValues();
            bool gpu = _gpuTripped;
            bool cpu = _cpuTripped;
            _gpuTripped = false;
            _cpuTripped = false;

            try
            {
                if (gpu && (state.Ctgp > 0 || state.Db > 0))
                {
                    await _power.HealContractAsync(new HealContractDto
                    {
                        ApplyGpu = true,
                        GpuCtgpOffset = state.Ctgp,
                        GpuDbOffset = state.Db,
                    }).ConfigureAwait(false);

                    PowerContractStore.NoteGpuApplied(state.Ctgp, state.Db);
                }

                if (cpu && state.CpuPl1 > 0 && state.CpuPl2 > 0)
                {
                    PowerContractStore.NoteCpuApplied(state.CpuPl1, state.CpuPl2);
                    await ApplyCpuWattsAsync(state.CpuPl1).ConfigureAwait(false);
                }

                Log.Information(
                    "[Failsafe] released ({Reason}); restored GPU cTGP {Ctgp}/DB {Db}, CPU {Pl1}/{Pl2} W",
                    reason, state.Ctgp, state.Db, state.CpuPl1, state.CpuPl2);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[Failsafe] could not fully restore the pre-trip contract; the next preset apply will");
            }
            finally
            {
                // Only undo the fan state we changed: Full Speed and the Q-key own it too.
                if (_fanBoostSetByUs && !state.FanBoostWasOn)
                {
                    try
                    {
                        await _fan.SetFanBoostAsync(false).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "[Failsafe] could not clear the hardware fan boost");
                    }
                }

                _fanBoostSetByUs = false;
                DeleteState();
            }
        }

        // ---- persistence ---------------------------------------------------

        private sealed class FailsafeState
        {
            public int Ctgp { get; set; }

            public int Db { get; set; }

            public int CpuPl1 { get; set; }

            public int CpuPl2 { get; set; }

            public bool FanBoostWasOn { get; set; }

            public bool GpuTripped { get; set; }

            public bool CpuTripped { get; set; }
        }

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
