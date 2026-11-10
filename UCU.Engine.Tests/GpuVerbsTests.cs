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
}
