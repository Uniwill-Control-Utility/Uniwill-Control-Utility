using UCU.Contracts;

namespace UCU.Engine.Tests;

/// <summary>
/// The GPU tuning verbs end to end: client proxy → pipe → dispatcher → scripted
/// NVAPI. The asInvoker app's own write is refused with
/// NVAPI_INVALID_USER_PRIVILEGE (0xFFFFFF77), which is why these writes moved
/// engine-side; the suite pins that the request reaches the service intact and
/// that a refusal comes back as DATA (the raw status on the result DTO), never
/// as an error envelope — the app logs the status, so it must not be swallowed.
/// </summary>
public sealed class GpuVerbsTests
{
    [Fact]
    public async Task ApplyVfCurve_DeliversTheOffsets_AndAnswersTheResult()
    {
        var gpu = new FakeGpuTuningService();

        await using var host = await EngineHostFixture.StartAsync(
            configure: o => o with { GpuTuningOverride = gpu });

        GpuTuningResultDto result = await host.Gpu.ApplyVfCurveAsync(
            new Dictionary<int, int> { [72] = 150, [80] = -30 });

        Assert.True(result.Succeeded);
        Assert.Equal(0, result.Status);
        Assert.Equal(2, result.AppliedPoints);

        Assert.NotNull(gpu.LastOffsets);
        Assert.Equal(150, gpu.LastOffsets![72]);
        Assert.Equal(-30, gpu.LastOffsets[80]);
    }

    [Fact]
    public async Task ApplyVfCurve_CarriesTheMemoryOffset_AndDeliversIt()
    {
        // The curve write rebuilds the pstate table, which is where the memory delta lives,
        // so the offset must ride the same payload: sent separately it is erased (field,
        // 2026-11-16, GPU-Z 1812.6 -> 1750.1 MHz after a curve apply).
        var gpu = new FakeGpuTuningService();

        await using var host = await EngineHostFixture.StartAsync(
            configure: o => o with { GpuTuningOverride = gpu });

        GpuTuningResultDto result = await host.Gpu.ApplyVfCurveAsync(
            new Dictionary<int, int> { [72] = 150 }, memoryOffsetMhz: 500);

        Assert.True(result.Succeeded);
        Assert.Equal(500, gpu.LastMemoryOffsetMhz);
        Assert.Equal(150, gpu.LastOffsets![72]);
    }

    [Fact]
    public async Task ApplyVfCurve_AcceptedCurveWithARefusedMemoryOffset_ReachesTheClientAsData()
    {
        // The engine answers both facts: the curve landed (AppliedPoints > 0) and the memory
        // offset did not. The app must be able to tell that apart from a refused curve.
        var gpu = new FakeGpuTuningService
        {
            Answer = new GpuTuningResultDto
            {
                Succeeded = false,
                Status = unchecked((int)0xFFFFFF77),
                AppliedPoints = 1,
                Detail = "the V-F curve applied (1 points) but the 500 MHz memory offset was refused",
            },
        };

        await using var host = await EngineHostFixture.StartAsync(
            configure: o => o with { GpuTuningOverride = gpu });

        GpuTuningResultDto result = await host.Gpu.ApplyVfCurveAsync(
            new Dictionary<int, int> { [72] = 150 }, memoryOffsetMhz: 500);

        Assert.False(result.Succeeded);
        Assert.Equal(unchecked((int)0xFFFFFF77), result.Status);
        Assert.Equal(1, result.AppliedPoints);
        Assert.Contains("memory offset was refused", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SetDrsPreference_DeliversTheEnum_AndAnswersTheResult()
    {
        var gpu = new FakeGpuTuningService();

        await using var host = await EngineHostFixture.StartAsync(
            configure: o => o with { GpuTuningOverride = gpu });

        GpuTuningResultDto result = await host.Gpu.SetDrsPreferenceAsync(DrsGpuPreference.Discrete);

        Assert.True(result.Succeeded);
        Assert.Equal(DrsGpuPreference.Discrete, gpu.LastPreference);
    }

    [Fact]
    public async Task SetClocks_DeliversBothOffsets_AndAnswersTheResult()
    {
        var gpu = new FakeGpuTuningService();

        await using var host = await EngineHostFixture.StartAsync(
            configure: o => o with { GpuTuningOverride = gpu });

        GpuTuningResultDto result = await host.Gpu.SetClocksAsync(150, -300);

        Assert.True(result.Succeeded);
        Assert.Equal(0, result.Status);
        Assert.Equal((150, -300), gpu.LastClocks);
    }

    [Fact]
    public async Task SetClocks_RefusedStatus_ReachesTheClientAsData()
    {
        // The field shape for the clock sliders: the wrapper throws
        // NVAPI_INVALID_USER_PRIVILEGE, so the app needs the status, not a fault.
        var gpu = new FakeGpuTuningService
        {
            Answer = new GpuTuningResultDto
            {
                Succeeded = false,
                Status = unchecked((int)0xFFFFFF77),
                Detail = "NVAPI_INVALID_USER_PRIVILEGE: The user does not have the privilege to perform this operation.",
            },
        };

        await using var host = await EngineHostFixture.StartAsync(
            configure: o => o with { GpuTuningOverride = gpu });

        GpuTuningResultDto result = await host.Gpu.SetClocksAsync(150, -300);

        Assert.False(result.Succeeded);
        Assert.Equal(unchecked((int)0xFFFFFF77), result.Status);
        Assert.Contains("INVALID_USER_PRIVILEGE", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RefusedWrite_ReachesTheClientAsData_NotAnErrorEnvelope()
    {
        // The field shape: the write is refused for privilege. The app needs the
        // status to log it, so the verb must not turn it into a wire fault.
        var gpu = new FakeGpuTuningService
        {
            Answer = new GpuTuningResultDto
            {
                Succeeded = false,
                Status = unchecked((int)0xFFFFFF77),
                Detail = "NVAPI_INVALID_USER_PRIVILEGE",
            },
        };

        await using var host = await EngineHostFixture.StartAsync(
            configure: o => o with { GpuTuningOverride = gpu });

        GpuTuningResultDto result = await host.Gpu.ApplyVfCurveAsync(new Dictionary<int, int> { [72] = 150 });

        Assert.False(result.Succeeded);
        Assert.Equal(unchecked((int)0xFFFFFF77), result.Status);
        Assert.Equal("NVAPI_INVALID_USER_PRIVILEGE", result.Detail);
    }

    [Fact]
    public async Task GetRomOverrideState_AnswersTheTierAndTheVerifiedCeiling()
    {
        // The read is what the page renders: the tier's numbers, the staged records'
        // state, whether the driver's ceiling moved and what the driver was asked for. A
        // refusal (Unsupported) is the same DTO with a reason, never a fault.
        var overrides = new FakeGpuRomOverrideService
        {
            Answer = new GpuRomOverrideStateDto
            {
                State = GpuRomOverrideStates.Unlocked,
                VbiosVersion = "98.03.5E.00.5D",
                TierId = "extra_gpu_power",
                PowerMilliwatts = 250_000,
                NvVddMilliAmps = 294_000,
                MsVddMilliAmps = 84_000,
                BoardInputMilliAmps = 14_026,
                CeilingLimitMilliwatts = 250_000,
                CurrentLimitMilliwatts = 150_000,
                BudgetMilliwatts = 225_000,
                RequestedLimitMilliwatts = 225_000,
                EnforcedLimitMoved = true,
                Restarted = true,
            },
        };

        await using var host = await EngineHostFixture.StartAsync(
            configure: o => o with { GpuRomOverrideOverride = overrides });

        GpuRomOverrideStateDto state = await host.Gpu.GetRomOverrideStateAsync();

        Assert.Equal(GpuRomOverrideStates.Unlocked, state.State);
        Assert.Equal(250_000, state.PowerMilliwatts);
        Assert.Equal(14_026, state.BoardInputMilliAmps);
        Assert.Equal(250_000, state.CeilingLimitMilliwatts);
        Assert.Equal(150_000, state.CurrentLimitMilliwatts);
        Assert.Equal(225_000, state.BudgetMilliwatts);
        Assert.Equal(225_000, state.RequestedLimitMilliwatts);
        Assert.True(state.EnforcedLimitMoved);
        Assert.True(state.Restarted);
        Assert.Null(overrides.LastEnabled);
    }

    [Fact]
    public async Task SetRomOverride_DeliversTheEnableFlag_AndAnswersTheState()
    {
        var overrides = new FakeGpuRomOverrideService
        {
            Answer = new GpuRomOverrideStateDto { State = GpuRomOverrideStates.Unlocked, Restarted = true },
        };

        await using var host = await EngineHostFixture.StartAsync(
            configure: o => o with { GpuRomOverrideOverride = overrides });

        GpuRomOverrideStateDto state = await host.Gpu.SetRomOverrideAsync(enabled: true);

        Assert.Equal(GpuRomOverrideStates.Unlocked, state.State);
        Assert.True(overrides.LastEnabled);
    }

    [Fact]
    public async Task SetRomOverride_Restore_ReachesTheClientAsTheRestoredState()
    {
        var overrides = new FakeGpuRomOverrideService
        {
            Answer = new GpuRomOverrideStateDto { State = GpuRomOverrideStates.NotUnlocked },
        };

        await using var host = await EngineHostFixture.StartAsync(
            configure: o => o with { GpuRomOverrideOverride = overrides });

        GpuRomOverrideStateDto state = await host.Gpu.SetRomOverrideAsync(enabled: false);

        Assert.Equal(GpuRomOverrideStates.NotUnlocked, state.State);
        Assert.False(overrides.LastEnabled);
    }

    [Fact]
    public async Task GetRailLimits_AnswersTheLivePolicyBoundedShape()
    {
        // The sliders' read: the currents and the policy range each is bounded by. A
        // refusal is the same DTO with a reason and no numbers, never a fault.
        var overrides = new FakeGpuRomOverrideService();

        await using var host = await EngineHostFixture.StartAsync(
            configure: o => o with { GpuRomOverrideOverride = overrides });

        GpuRailLimitsDto rails = await host.Gpu.GetRailLimitsAsync();

        Assert.Null(rails.Reason);
        Assert.Equal(250_000, rails.NvvddMilliamps);
        Assert.Equal(210_000, rails.NvvddMinMilliamps);
        Assert.Equal(294_000, rails.NvvddMaxMilliamps);
        Assert.Equal(70_000, rails.MsvddMilliamps);
        Assert.Equal(14_026, rails.BoardInputMilliamps);
        Assert.Equal(11_053, rails.BoardInputMinMilliamps);
        Assert.Null(overrides.LastRailSet);
    }

    [Fact]
    public async Task SetRailLimits_DeliversTheThreeValues_AndAnswersTheReadBack()
    {
        var overrides = new FakeGpuRomOverrideService();

        await using var host = await EngineHostFixture.StartAsync(
            configure: o => o with { GpuRomOverrideOverride = overrides });

        GpuRailLimitsDto rails = await host.Gpu.SetRailLimitsAsync(null, 70_000, null);

        Assert.Equal((null, 70_000, null), overrides.LastRailSet);
        Assert.Equal(70_000, rails.MsvddMilliamps);
        Assert.Null(rails.Reason);
    }
}
