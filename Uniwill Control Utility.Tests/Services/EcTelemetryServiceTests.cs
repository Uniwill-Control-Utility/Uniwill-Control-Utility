using Uniwill_Control_Utility.Models;
using Uniwill_Control_Utility.Services;

namespace Uniwill_Control_Utility.Tests.Services;

public sealed class EcTelemetryServiceTests
{
    [Fact]
    public async Task Read_ReturnsImmediatelyWhileOneBackgroundCaptureRuns()
    {
        var captureStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCapture = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int captureCount = 0;

        using var service = new EcTelemetryService(
            fanDutyCapture: () =>
            {
                Interlocked.Increment(ref captureCount);
                captureStarted.SetResult();
                releaseCapture.Task.GetAwaiter().GetResult();
                return Task.FromResult(new EcFanDutySnapshot(100, 80));
            },
            mainFanRpmCapture: () => Task.FromResult(0),
            maxAge: TimeSpan.FromSeconds(1));

        EcFanDutySnapshot first = service.ReadFanDuty();
        await captureStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        EcFanDutySnapshot second = service.ReadFanDuty();

        Assert.Equal(EcFanDutySnapshot.Empty, first);
        Assert.Equal(EcFanDutySnapshot.Empty, second);
        Assert.Equal(1, Volatile.Read(ref captureCount));

        releaseCapture.SetResult();
        EcFanDutySnapshot refreshed = await WaitForSnapshotAsync(service);

        Assert.Equal(50, refreshed.CpuFanPercent);
        Assert.Equal(40, refreshed.GpuFanPercent);
    }

    [Theory]
    [InlineData(-10, 0)]
    [InlineData(0, 0)]
    [InlineData(100, 50)]
    [InlineData(200, 100)]
    [InlineData(250, 100)]
    public void FanPercent_ClampsEcDutyToDisplayRange(int duty, int expected)
    {
        var snapshot = new EcFanDutySnapshot(duty, duty);

        Assert.Equal(expected, snapshot.CpuFanPercent);
        Assert.Equal(expected, snapshot.GpuFanPercent);
    }

    [Fact]
    public async Task RefreshAsync_ForcesAndReturnsCompletedCapture()
    {
        int captureCount = 0;
        using var service = new EcTelemetryService(
            fanDutyCapture: () => Task.FromResult(EcFanDutySnapshot.Empty),
            mainFanRpmCapture: () => Task.FromResult(Interlocked.Increment(ref captureCount) * 1000),
            maxAge: TimeSpan.FromMinutes(1));

        int first = await service.RefreshMainFanRpmAsync();
        int second = await service.RefreshMainFanRpmAsync();

        Assert.Equal(1000, first);
        Assert.Equal(2000, second);
    }

    [Fact]
    public async Task RefreshAsync_WhenCaptureIsAlreadyRunning_ReturnsNewGeneration()
    {
        var firstCaptureStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstCapture = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int captureCount = 0;
        using var service = new EcTelemetryService(
            fanDutyCapture: () => Task.FromResult(EcFanDutySnapshot.Empty),
            mainFanRpmCapture: () =>
            {
                int capture = Interlocked.Increment(ref captureCount);
                if (capture == 1)
                {
                    firstCaptureStarted.SetResult();
                    releaseFirstCapture.Task.GetAwaiter().GetResult();
                }

                return Task.FromResult(capture * 1000);
            },
            maxAge: TimeSpan.FromMinutes(1));

        _ = service.ReadMainFanRpm();
        await firstCaptureStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task<int> refresh = service.RefreshMainFanRpmAsync();
        releaseFirstCapture.SetResult();

        int result = await refresh.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2000, result);
        Assert.Equal(2, Volatile.Read(ref captureCount));
    }

    [Fact]
    public async Task CpuTemperature_PublishesTheEcReading()
    {
        // The EC reading is the CPU-temperature source for a process that cannot open the
        // CPU sensor's driver, so the cached read has to reach a polling consumer.
        using var service = new EcTelemetryService(
            fanDutyCapture: () => Task.FromResult(EcFanDutySnapshot.Empty),
            mainFanRpmCapture: () => Task.FromResult(0),
            maxAge: TimeSpan.FromMinutes(1),
            cpuTemperatureCapture: () => Task.FromResult(57));

        Assert.Equal(57, await WaitForCpuTemperatureAsync(service));
    }

    [Fact]
    public void CpuTemperature_WithoutAnEcSource_IsUnavailableNotThrown()
    {
        // Composition with no EC source (the test ctor's default, as in a dev run without
        // the engine): the fallback answers "unavailable", which the UI renders as "--".
        using var service = new EcTelemetryService(
            fanDutyCapture: () => Task.FromResult(EcFanDutySnapshot.Empty),
            mainFanRpmCapture: () => Task.FromResult(0),
            maxAge: TimeSpan.FromMinutes(1));

        _ = service.ReadCpuTemperature();

        Assert.Equal(0, service.ReadCpuTemperature());
    }

    private static async Task<EcFanDutySnapshot> WaitForSnapshotAsync(
        EcTelemetryService service)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            EcFanDutySnapshot snapshot = service.ReadFanDuty();
            if (snapshot.CpuFanDuty == 100)
                return snapshot;

            await Task.Delay(10);
        }

        throw new TimeoutException("The EC telemetry cache did not publish the capture.");
    }

    private static async Task<int> WaitForCpuTemperatureAsync(EcTelemetryService service)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            int temperature = service.ReadCpuTemperature();
            if (temperature > 0)
                return temperature;

            await Task.Delay(10);
        }

        throw new TimeoutException("The EC telemetry cache did not publish the CPU temperature.");
    }
}
