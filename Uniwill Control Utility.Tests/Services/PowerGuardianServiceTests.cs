using UCU.Contracts;
using UCU.Core.Supervision;
using Uniwill_Control_Utility.Services;

namespace Uniwill_Control_Utility.Tests.Services;

/// <summary>
/// The power-tuning guardian loop (U-107) driven hermetically, one
/// RunCycleAsync at a time on scripted seams (the UninstallSentinel Step
/// shape): scripted EC truth, recorded heal plans, delegate-gated enables,
/// a capture sink for the user note, and a settable clock for the backoff
/// and toast throttles. The acceptance set: drift re-applies within the
/// cycle with evidence (a), the per-feature off switches are honored (b),
/// the driver-transition cooldown skips the GPU half (c), and an engine
/// that is Busy/not-answering is backed off, never queued onto (d).
/// </summary>
public sealed class PowerGuardianServiceTests
{
    private const int Ctgp = 70;
    private const int Db = 25;

    private static PowerContract Contract() =>
        new() { HasGpu = true, GpuCtgpOffset = Ctgp, GpuDbOffset = Db, HasCpu = true, Pl1Watts = 75, Pl2Watts = 85 };

    private static PowerContractTruthDto CleanTruth() => new()
    {
        GpuEnable = PowerContractPolicy.ExpectedGpuEnable(Db),
        GpuCtgp = Ctgp,
        GpuTpp = PowerContractPolicy.ExpectedGpuTpp(Db),
        GpuDb = Db,
        GpuCustomProfileActive = true,
        Pl1 = 75,
        Pl2 = 85,
    };

    private static PowerContractTruthDto DriftedGpuTruth() => CleanTruth() with { GpuEnable = 0x00 };

    private static PowerContractTruthDto DriftedCpuTruth() => CleanTruth() with { Pl1 = 45 };

    private DateTime _now = new(2026, 10, 30, 12, 0, 0, DateTimeKind.Utc);

    private DateTime Now() => _now;

    private sealed class Script
    {
        public List<Func<Task<PowerContractTruthDto?>>> TruthScript { get; } = new();
        public List<HealContractDto> Heals { get; } = new();
        public List<(string Title, string Body)> Toasts { get; } = new();
        public int TruthCalls;
        public Func<HealContractDto, Task>? HealBehavior;

        public PowerGuardianService Service(
            PowerContract? contract = null,
            bool gpuEnabled = true,
            bool cpuEnabled = true,
            bool adaptive = false,
            bool gpuGuardBlocked = false,
            Func<DateTime>? clock = null)
        {
            PowerContract c = contract ?? Contract();

            return new PowerGuardianService(
                readTruth: _ =>
                {
                    // The script CYCLES: a re-tuned machine drifts again, and
                    // the loop's reads are drift, verify, drift, verify…
                    int index = TruthScript.Count == 0 ? 0 : TruthCalls % TruthScript.Count;
                    TruthCalls++;
                    return TruthScript[index]();
                },
                heal: async (plan, _) =>
                {
                    Heals.Add(plan);
                    if (HealBehavior is not null)
                        await HealBehavior(plan);
                },
                contract: () => c,
                gpuEnabled: () => gpuEnabled,
                cpuEnabled: () => cpuEnabled,
                adaptiveRunning: () => adaptive,
                gpuGuardBlocked: () => gpuGuardBlocked,
                notify: (title, body) => Toasts.Add((title, body)),
                clock: clock ?? (Func<DateTime>)(() => DateTime.UtcNow));
        }
    }

    private static PowerGuardianService.EngineNotAnsweringException NotAnswering() =>
        new(EngineErrorCode.Busy, "power queue is saturated");

    [Fact]
    public async Task CleanTruth_HealsNothing_NoToast()
    {
        var s = new Script();
        s.TruthScript.Add(() => Task.FromResult<PowerContractTruthDto?>(CleanTruth()));

        await s.Service().RunCycleAsync();

        Assert.Empty(s.Heals);
        Assert.Empty(s.Toasts);
    }

    [Fact]
    public async Task EmptyContract_CostsNoRpcCall()
    {
        var s = new Script();
        s.TruthScript.Add(() => Task.FromResult<PowerContractTruthDto?>(CleanTruth()));

        await s.Service(contract: PowerContract.Empty).RunCycleAsync();

        Assert.Equal(0, s.TruthCalls);
        Assert.Empty(s.Heals);
    }

    [Fact]
    public async Task GpuDrift_HealsTheContractBytes_Verifies_AndToasts()
    {
        var s = new Script();
        s.TruthScript.Add(() => Task.FromResult<PowerContractTruthDto?>(DriftedGpuTruth())); // drift
        s.TruthScript.Add(() => Task.FromResult<PowerContractTruthDto?>(CleanTruth()));      // post-heal verify

        await s.Service().RunCycleAsync();

        HealContractDto heal = Assert.Single(s.Heals);
        Assert.True(heal.ApplyGpu);
        Assert.Equal(Ctgp, heal.GpuCtgpOffset);   // the contract's bytes, not the truth's
        Assert.Equal(Db, heal.GpuDbOffset);
        Assert.False(heal.ApplyCpu);

        (string title, string _) = s.Toasts.Single();
        Assert.Equal("Power limits restored", title);
    }

    [Fact]
    public async Task CpuDrift_HealsOnlyTheCpuHalf()
    {
        var s = new Script();
        s.TruthScript.Add(() => Task.FromResult<PowerContractTruthDto?>(DriftedCpuTruth()));
        s.TruthScript.Add(() => Task.FromResult<PowerContractTruthDto?>(CleanTruth()));

        await s.Service().RunCycleAsync();

        HealContractDto heal = Assert.Single(s.Heals);
        Assert.False(heal.ApplyGpu);
        Assert.True(heal.ApplyCpu);
        Assert.Equal(75, heal.Pl1Watts);
        Assert.Equal(85, heal.Pl2Watts);
    }

    [Fact]
    public async Task FeatureOff_TogglesRespected_NoReadNoHeal()
    {
        // Acceptance b: "stop/heal off" is not a check that is skipped —
        // with both halves off the guardian does not even ask the EC.
        var s = new Script();
        s.TruthScript.Add(() => Task.FromResult<PowerContractTruthDto?>(DriftedGpuTruth()));

        await s.Service(gpuEnabled: false, cpuEnabled: false).RunCycleAsync();

        Assert.Equal(0, s.TruthCalls);
        Assert.Empty(s.Heals);
    }

    [Fact]
    public async Task OneHalfOff_TheOtherHalfStillHeals_AndTheOffHalfNeverHeals()
    {
        var s = new Script();
        // Both halves drifted; only the GPU is enabled.
        s.TruthScript.Add(() => Task.FromResult<PowerContractTruthDto?>(DriftedGpuTruth() with { Pl1 = 45 }));
        s.TruthScript.Add(() => Task.FromResult<PowerContractTruthDto?>(CleanTruth()));

        await s.Service(cpuEnabled: false).RunCycleAsync();

        HealContractDto heal = Assert.Single(s.Heals);
        Assert.True(heal.ApplyGpu);
        Assert.False(heal.ApplyCpu);
    }

    [Fact]
    public async Task AdaptiveRunning_SkipsTheCycle_Entirely()
    {
        // U-101's ownership rule in heal form: Adaptive's own diff-apply
        // loop owns tuning while it runs; the guardian stays silent.
        var s = new Script();
        s.TruthScript.Add(() => Task.FromResult<PowerContractTruthDto?>(DriftedGpuTruth()));

        await s.Service(adaptive: true).RunCycleAsync();

        Assert.Equal(0, s.TruthCalls);
        Assert.Empty(s.Heals);
    }

    [Fact]
    public async Task GpuGuardCooldown_SkipsTheGpuHalf_KeepsTheCpuHalf()
    {
        // Acceptance c: no GPU healing during a driver-transition cooldown
        // (display hotplug / resume / dGPU PnP churn); the CPU PL pair is
        // EC-only and unaffected by the cooldown.
        var s = new Script();
        s.TruthScript.Add(() => Task.FromResult<PowerContractTruthDto?>(DriftedGpuTruth() with { Pl1 = 45 }));
        s.TruthScript.Add(() => Task.FromResult<PowerContractTruthDto?>(CleanTruth()));

        await s.Service(gpuGuardBlocked: true).RunCycleAsync();

        HealContractDto heal = Assert.Single(s.Heals);
        Assert.False(heal.ApplyGpu);
        Assert.True(heal.ApplyCpu);
    }

    [Fact]
    public async Task EngineBusyAtRead_BacksOff_TheNextCycleIsQuiet()
    {
        // Acceptance d: a saturated engine is honored with backoff, not
        // with a queue of retries.
        var s = new Script();
        s.TruthScript.Add(async () => throw NotAnswering());
        s.TruthScript.Add(() => Task.FromResult<PowerContractTruthDto?>(DriftedGpuTruth()));

        var service = s.Service(clock: Now);

        await service.RunCycleAsync();
        Assert.Equal(1, s.TruthCalls);
        Assert.Empty(s.Heals);

        await service.RunCycleAsync(); // inside the backoff window: no read at all
        Assert.Equal(1, s.TruthCalls);

        _now = _now.Add(TimeSpan.FromMinutes(2)); // past the backoff
        await service.RunCycleAsync();
        // 2 reads again: the drifted truth read + the post-heal verify read.
        Assert.Equal(3, s.TruthCalls);
        Assert.Single(s.Heals);
    }

    [Fact]
    public async Task HealRefusedWithInvalidValue_Logs_NoVerifyNoToast()
    {
        var s = new Script();
        s.TruthScript.Add(() => Task.FromResult<PowerContractTruthDto?>(DriftedGpuTruth()));
        s.HealBehavior = _ => throw new EngineException(EngineErrorCode.InvalidValue, "cTGP offset must be 0-255");

        await s.Service().RunCycleAsync();

        Assert.Single(s.Heals);
        Assert.Equal(1, s.TruthCalls); // no verify read after a refused heal
        Assert.Empty(s.Toasts);
    }

    [Fact]
    public async Task HealNotVerifiedByReRead_KeepsThePromiseQuiet()
    {
        // The guardian claims healing only on the firmware's word: a re-read
        // that still shows drift logs the failure and sends no toast.
        var s = new Script();
        s.TruthScript.Add(() => Task.FromResult<PowerContractTruthDto?>(DriftedGpuTruth()));
        s.TruthScript.Add(() => Task.FromResult<PowerContractTruthDto?>(DriftedGpuTruth())); // heal did not land

        await s.Service().RunCycleAsync();

        Assert.Single(s.Heals);
        Assert.Empty(s.Toasts);
    }

    [Fact]
    public async Task ToastThrottle_TheSecondHealWithinTheWindowStaysQuiet()
    {
        var s = new Script();
        s.TruthScript.Add(() => Task.FromResult<PowerContractTruthDto?>(DriftedGpuTruth()));
        s.TruthScript.Add(() => Task.FromResult<PowerContractTruthDto?>(CleanTruth()));

        var service = s.Service(clock: Now);

        await service.RunCycleAsync();
        Assert.Single(s.Toasts);

        // The tuner resets again a minute later: the log records it, the
        // user is not toasted twice inside the throttle window.
        _now = _now.AddMinutes(1);
        await service.RunCycleAsync();
        Assert.Equal(2, s.Heals.Count);
        Assert.Single(s.Toasts);

        _now = _now.Add(PowerGuardianService.ToastThrottle);
        await service.RunCycleAsync();
        Assert.Equal(3, s.Heals.Count);
        Assert.Equal(2, s.Toasts.Count);
    }
}
