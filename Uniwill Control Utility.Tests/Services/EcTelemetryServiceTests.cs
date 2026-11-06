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

    [Fact]
    public async Task EngineMetrics_PublishesTheKernelSideReadings()
    {
        // The kernel-side readings (CPU Ring0 power/clock, SPD) are the fields the
        // unelevated app's own probe cannot read; the cached read has to reach the
        // dashboard's merge exactly like the fan duty does.
        using var service = new EcTelemetryService(
            fanDutyCapture: () => Task.FromResult(EcFanDutySnapshot.Empty),
            mainFanRpmCapture: () => Task.FromResult(0),
            maxAge: TimeSpan.FromMinutes(1),
            engineMetricsCapture: () => Task.FromResult(new EngineMetricsSnapshot
            {
                CpuPowerWatts = 104,
                CpuClockMhz = 5298,
                MemorySpeedMts = 5602,
                ProbeOpen = true,
            }));

        EngineMetricsSnapshot metrics = await WaitForEngineMetricsAsync(service);

        Assert.Equal(104, metrics.CpuPowerWatts);
        Assert.Equal(5298, metrics.CpuClockMhz);
        Assert.Equal(5602, metrics.MemorySpeedMts);
    }

    [Fact]
    public void EngineMetrics_WithoutAnEngine_IsEmptyNotThrown()
    {
        // No engine capability (the ctor's default): every field stays 0, which the
        // dashboard merge treats as "no reading" - the pre-engine dashboard, unchanged.
        using var service = new EcTelemetryService(
            fanDutyCapture: () => Task.FromResult(EcFanDutySnapshot.Empty),
            mainFanRpmCapture: () => Task.FromResult(0),
            maxAge: TimeSpan.FromMinutes(1));

        _ = service.ReadEngineMetrics();

        Assert.Equal(EngineMetricsSnapshot.Empty, service.ReadEngineMetrics());
    }

    [Fact]
    public async Task EngineMetrics_DeadEngineChain_AnswersEmptyNotFrozenNumbers()
    {
        // Stale-while-revalidate keeps the LAST entry across refresh failures -
        // right for fan duty (a momentary blip must not blank the gauge), wrong
        // for the sensor snapshot: an engine that died an hour ago must not keep
        // painting 104 W as if live (field audit 2026-10-29). The freshness gate
        // turns the un-refreshable entry into Empty, the "--" value.
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-10-29T00:00:00Z"));
        bool engineDown = false;

        using var service = new EcTelemetryService(
            fanDutyCapture: () => Task.FromResult(EcFanDutySnapshot.Empty),
            mainFanRpmCapture: () => Task.FromResult(0),
            maxAge: TimeSpan.FromMinutes(1),
            timeProvider: clock,
            engineMetricsCapture: () => engineDown
                ? Task.FromException<EngineMetricsSnapshot>(new InvalidOperationException("engine down"))
                : Task.FromResult(new EngineMetricsSnapshot { CpuPowerWatts = 104, ProbeOpen = true }));

        engineDown = false;
        EngineMetricsSnapshot live = await WaitForEngineMetricsAsync(service);
        Assert.Equal(104, live.CpuPowerWatts);

        engineDown = true;
        clock.Advance(TimeSpan.FromSeconds(31));

        Assert.Equal(EngineMetricsSnapshot.Empty, service.ReadEngineMetrics());
    }

    [Fact]
    public async Task EngineMetrics_WedgedEngineProbeLoop_ReadsPastTheBudgetAreNoReadings()
    {
        // The engine's probe loop is outside the watchdog: a wedged open keeps
        // the LAST published DTO forever, and the verb stamps its serve-time
        // age. Past the freshness budget the app treats the DTO as no reading,
        // so a frozen 5298 MHz renders "--" instead of a fake live clock.
        using var service = new EcTelemetryService(
            fanDutyCapture: () => Task.FromResult(EcFanDutySnapshot.Empty),
            mainFanRpmCapture: () => Task.FromResult(0),
            maxAge: TimeSpan.FromMinutes(1),
            engineMetricsCapture: () => Task.FromResult(new EngineMetricsSnapshot
            {
                CpuClockMhz = 5298,
                ProbeOpen = true,
                ReadingAgeSeconds = 31,
            }));

        // The gate answers Empty, so the assertion keys on the capture having run
        // (the first Read() kicks the background publish, which completes at
        // once): after a moment, the gated answer is stable.
        _ = service.ReadEngineMetrics();
        await Task.Delay(250);

        Assert.Equal(EngineMetricsSnapshot.Empty, service.ReadEngineMetrics());
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

    private static async Task<EngineMetricsSnapshot> WaitForEngineMetricsAsync(EcTelemetryService service)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            EngineMetricsSnapshot metrics = service.ReadEngineMetrics();
            if (metrics.ProbeOpen)
                return metrics;

            await Task.Delay(10);
        }

        throw new TimeoutException("The EC telemetry cache did not publish the engine metrics.");
    }
}
