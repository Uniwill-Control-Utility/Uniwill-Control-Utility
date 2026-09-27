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
            capture: () =>
            {
                Interlocked.Increment(ref captureCount);
                captureStarted.SetResult();
                releaseCapture.Task.GetAwaiter().GetResult();
                return new EcTelemetrySnapshot(70, 64, 2400, 2100, 100, 80);
            },
            maxAge: TimeSpan.FromSeconds(1));

        EcTelemetrySnapshot first = service.Read();
        await captureStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        EcTelemetrySnapshot second = service.Read();

        Assert.Equal(EcTelemetrySnapshot.Empty, first);
        Assert.Equal(EcTelemetrySnapshot.Empty, second);
        Assert.Equal(1, Volatile.Read(ref captureCount));

        releaseCapture.SetResult();
        EcTelemetrySnapshot refreshed = await WaitForSnapshotAsync(service);

        Assert.Equal(2400, refreshed.MainFanRpm);
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
        var snapshot = new EcTelemetrySnapshot(0, 0, 0, 0, duty, duty);

        Assert.Equal(expected, snapshot.CpuFanPercent);
        Assert.Equal(expected, snapshot.GpuFanPercent);
    }

    [Fact]
    public async Task RefreshAsync_ForcesAndReturnsCompletedCapture()
    {
        int captureCount = 0;
        using var service = new EcTelemetryService(
            capture: () => new EcTelemetrySnapshot(
                0,
                0,
                Interlocked.Increment(ref captureCount) * 1000,
                0,
                0,
                0),
            maxAge: TimeSpan.FromMinutes(1));

        EcTelemetrySnapshot first = await service.RefreshAsync();
        EcTelemetrySnapshot second = await service.RefreshAsync();

        Assert.Equal(1000, first.MainFanRpm);
        Assert.Equal(2000, second.MainFanRpm);
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
            capture: () =>
            {
                int capture = Interlocked.Increment(ref captureCount);
                if (capture == 1)
                {
                    firstCaptureStarted.SetResult();
                    releaseFirstCapture.Task.GetAwaiter().GetResult();
                }

                return new EcTelemetrySnapshot(0, 0, capture * 1000, 0, 0, 0);
            },
            maxAge: TimeSpan.FromMinutes(1));

        _ = service.Read();
        await firstCaptureStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task<EcTelemetrySnapshot> refresh = service.RefreshAsync();
        releaseFirstCapture.SetResult();

        EcTelemetrySnapshot result = await refresh.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2000, result.MainFanRpm);
        Assert.Equal(2, Volatile.Read(ref captureCount));
    }

    private static async Task<EcTelemetrySnapshot> WaitForSnapshotAsync(
        EcTelemetryService service)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            EcTelemetrySnapshot snapshot = service.Read();
            if (snapshot.MainFanRpm == 2400)
                return snapshot;

            await Task.Delay(10);
        }

        throw new TimeoutException("The EC telemetry cache did not publish the capture.");
    }
}
