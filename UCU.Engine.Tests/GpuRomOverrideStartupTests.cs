using Microsoft.Extensions.Logging.Abstractions;
using UCU.Contracts;
using UCU.Engine;
using UCU.Hardware.Gpu;

namespace UCU.Engine.Tests;

/// <summary>
/// The engine-side start-up schedule. The engine service starts before the display driver
/// and NVAPI are necessarily ready, so a failed re-apply is retried with backoff — bounded,
/// and stopped by the first success or the first definitive refusal. The store decides which
/// verdict an attempt settles (see the Hardware suite); this pins the loop.
/// </summary>
public sealed class GpuRomOverrideStartupTests
{
    private readonly List<TimeSpan> _waits = new();

    [Fact]
    public async Task Retry_StopsOnSuccess()
    {
        var overrides = new FakeGpuRomOverrideService { Verdict = GpuReapplyVerdict.Transient };
        overrides.Verdicts.Enqueue(GpuReapplyVerdict.Transient);
        overrides.Verdicts.Enqueue(GpuReapplyVerdict.Transient);
        overrides.Verdicts.Enqueue(GpuReapplyVerdict.Applied);

        await Startup(overrides).RetryAsync(CancellationToken.None);

        Assert.Equal(3, overrides.ReapplyCalls);   // two transients, then the success
        Assert.Equal(3, _waits.Count);
    }

    [Fact]
    public async Task Retry_StopsOnADefinitiveRefusal()
    {
        // Foreign records, an unpinned VBIOS or records of another tier cannot become ours
        // by waiting: the loop stops instead of spending four more attempts on it.
        var overrides = new FakeGpuRomOverrideService { Verdict = GpuReapplyVerdict.Transient };
        overrides.Verdicts.Enqueue(GpuReapplyVerdict.Refused);

        await Startup(overrides).RetryAsync(CancellationToken.None);

        Assert.Equal(1, overrides.ReapplyCalls);
        Assert.Single(_waits);
    }

    [Fact]
    public async Task Retry_IsBounded_AndDoesNotGiveUpOnATransientFailure()
    {
        // The driver, NVAPI or the EC may come up late: every scheduled attempt is used, and
        // then it stops rather than spinning at every boot.
        var overrides = new FakeGpuRomOverrideService { Verdict = GpuReapplyVerdict.Transient };

        await Startup(overrides).RetryAsync(CancellationToken.None);

        Assert.Equal(GpuRomOverrideStartup.Schedule.Length - 1, overrides.ReapplyCalls);

        // The attempts land on the schedule — +10, +30, +60 and +120 s after start — so each
        // wait is that offset less whatever the earlier attempts took.
        int[] offsets = { 10, 30, 60, 120 };
        Assert.Equal(offsets.Length, _waits.Count);
        for (int i = 0; i < offsets.Length; i++)
            Assert.InRange(_waits[i].TotalSeconds, offsets[i] - 5, offsets[i]);
    }

    [Fact]
    public async Task Start_DoesNotRetry_WhenNoUnlockIsStaged()
    {
        var overrides = new FakeGpuRomOverrideService { Verdict = GpuReapplyVerdict.NoIntent };

        await Startup(overrides).StartAsync(CancellationToken.None);

        Assert.Equal(1, overrides.ReapplyCalls);
        Assert.Empty(_waits);
    }

    private GpuRomOverrideStartup Startup(FakeGpuRomOverrideService overrides) =>
        new(overrides, NullLogger<GpuRomOverrideStartup>.Instance, (delay, _) =>
        {
            _waits.Add(delay);
            return Task.CompletedTask;
        });
}
