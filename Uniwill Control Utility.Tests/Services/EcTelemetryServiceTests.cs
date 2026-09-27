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
                return new EcFanDutySnapshot(100, 80);
            },
            mainFanRpmCapture: () => 0,
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
            fanDutyCapture: () => EcFanDutySnapshot.Empty,
            mainFanRpmCapture: () => Interlocked.Increment(ref captureCount) * 1000,
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
            fanDutyCapture: () => EcFanDutySnapshot.Empty,
            mainFanRpmCapture: () =>
            {
                int capture = Interlocked.Increment(ref captureCount);
                if (capture == 1)
                {
                    firstCaptureStarted.SetResult();
                    releaseFirstCapture.Task.GetAwaiter().GetResult();
                }

                return capture * 1000;
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
}
