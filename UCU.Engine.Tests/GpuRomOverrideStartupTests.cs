using Microsoft.Extensions.Logging;
using UCU.Contracts;
using UCU.Engine;
using UCU.Hardware.Gpu;

namespace UCU.Engine.Tests;

/// <summary>
/// The engine-side start-up schedule. The engine service starts before the display driver
/// and NVAPI are necessarily ready, so a failed re-apply is retried with backoff — bounded,
/// and stopped by the first success or the first definitive refusal. The store decides which
/// verdict an attempt settles (see the Hardware suite); this pins the loop. An Applied
/// attempt is followed by the settle phase: the EC republishes its own F7 budget after a
/// boot, so the raise is re-checked instead of trusted.
/// </summary>
public sealed class GpuRomOverrideStartupTests
{
    private readonly List<TimeSpan> _waits = new();

    private readonly List<string> _lines = new();

    [Fact]
    public async Task Retry_StopsOnSuccess_AndThenSettles()
    {
        var overrides = new FakeGpuRomOverrideService { Verdict = GpuReapplyVerdict.Transient };
        overrides.Verdicts.Enqueue(GpuReapplyVerdict.Transient);
        overrides.Verdicts.Enqueue(GpuReapplyVerdict.Transient);
        overrides.Verdicts.Enqueue(GpuReapplyVerdict.Applied);

        await Startup(overrides).RetryAsync(CancellationToken.None);

        Assert.Equal(3, overrides.ReapplyCalls);   // two transients, then the success
        Assert.Equal(2, overrides.SettleCalls);    // two clean passes, then the phase ends
        Assert.Equal(5, _waits.Count);             // three retries, then +20 s and +60 s
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
        Assert.Equal(0, overrides.SettleCalls);
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

        Assert.Equal(0, overrides.SettleCalls);   // a re-apply that never applied has nothing to settle
    }

    [Fact]
    public async Task Start_DoesNotRetry_WhenNoUnlockIsStaged()
    {
        var overrides = new FakeGpuRomOverrideService { Verdict = GpuReapplyVerdict.NoIntent };

        GpuRomOverrideStartup startup = Startup(overrides);
        await startup.StartAsync(CancellationToken.None);

        Assert.Equal(1, overrides.ReapplyCalls);
        Assert.Empty(_waits);
        Assert.Null(startup.Background);
        Assert.Equal(0, overrides.SettleCalls);
    }

    [Fact]
    public async Task Start_SettlesOffTheStartPath_WhenTheReApplyApplied()
    {
        var overrides = new FakeGpuRomOverrideService { Verdict = GpuReapplyVerdict.Applied };

        GpuRomOverrideStartup startup = Startup(overrides);
        await startup.StartAsync(CancellationToken.None);
        await startup.Background!;

        Assert.Equal(1, overrides.ReapplyCalls);
        Assert.Equal(2, overrides.SettleCalls);
    }

    [Fact]
    public async Task Settle_ReappliesWhatDrifted_ThenStopsOnTwoCleanPasses()
    {
        // The measured post-boot drift: the budget is back at the EC's 150 W on the first
        // pass, that pass re-applies it, and the next two passes ending clean end the phase.
        var overrides = new FakeGpuRomOverrideService();
        overrides.Settles.Enqueue(new(
            GpuSettleVerdict.Drifted, "budget 150000 mW", "the driver reports a 150 W laptop power budget, not 225 W"));
        overrides.Settles.Enqueue(new(GpuSettleVerdict.Reapplied, "budget 150000 mW", null));
        overrides.Settles.Enqueue(new(GpuSettleVerdict.Settled, null, null));

        await Startup(overrides).SettleAsync(CancellationToken.None);

        Assert.Equal(3, overrides.SettleCalls);
        Assert.Equal(3, _waits.Count);
        Assert.Contains(_lines, line => line.Contains(
            "gpu.settle: attempt 1 at +20 s: budget 150000 mW - the driver reports a 150 W laptop power budget, not 225 W",
            StringComparison.Ordinal));
        Assert.Contains(_lines, line => line.Contains(
            "gpu.settle: attempt 2 at +60 s: budget 150000 mW - re-applied", StringComparison.Ordinal));
        Assert.DoesNotContain(_lines, line => line.Contains("still short", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Settle_StopsEarly_WhenTwoPassesAreClean()
    {
        var overrides = new FakeGpuRomOverrideService();   // Settled on every pass

        await Startup(overrides).SettleAsync(CancellationToken.None);

        Assert.Equal(2, overrides.SettleCalls);
        Assert.Equal(2, _waits.Count);   // +20 s and +60 s; the +120 s pass never runs
        Assert.InRange(_waits[0].TotalSeconds, 15, 20);
        Assert.InRange(_waits[1].TotalSeconds, 55, 60);
    }

    [Fact]
    public async Task Settle_WarnsWithTheNumbers_WhenTheBudgetNeverComesBack()
    {
        var overrides = new FakeGpuRomOverrideService
        {
            SettleAnswer = new(
                GpuSettleVerdict.Drifted, "budget 150000 mW", "the driver reports a 150 W laptop power budget, not 225 W"),
        };

        await Startup(overrides).SettleAsync(CancellationToken.None);

        Assert.Equal(GpuRomOverrideStartup.SettleSchedule.Length, overrides.SettleCalls);
        Assert.Contains(_lines, line =>
            line.Contains("still short after 3 passes", StringComparison.Ordinal) &&
            line.Contains("150 W", StringComparison.Ordinal) &&
            line.Contains("225 W", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Settle_EndsWhenARestoreIntervenes()
    {
        // The owner restores the unlock while the settle phase is live: the store answers
        // Stopped, and no later pass may re-apply what the owner has just undone.
        var overrides = new FakeGpuRomOverrideService();
        overrides.Settles.Enqueue(new(GpuSettleVerdict.Reapplied, "budget 150000 mW", null));
        overrides.Settles.Enqueue(new(GpuSettleVerdict.Stopped, null, null));

        await Startup(overrides).SettleAsync(CancellationToken.None);

        Assert.Equal(2, overrides.SettleCalls);
        Assert.Contains(_lines, line => line.Contains("a restore ran", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Watch_ReRaisesOncePerDrift_AndLogsNothingWhileItHolds()
    {
        // The EC republished a lower F7 once, then the budget held: one raise, then silent
        // ticks until the owner restores — the watch must not log a line per tick.
        var overrides = new FakeGpuRomOverrideService();
        overrides.Watches.Enqueue(new(GpuBudgetWatchVerdict.Raised, null));
        overrides.Watches.Enqueue(new(GpuBudgetWatchVerdict.Holding, null));
        overrides.Watches.Enqueue(new(GpuBudgetWatchVerdict.Holding, null));
        overrides.Watches.Enqueue(new(GpuBudgetWatchVerdict.Stopped, null));

        await Startup(overrides).WatchAsync(CancellationToken.None);

        Assert.Equal(4, overrides.WatchCalls);

        // The first tick is the one that catches the EC's own republish after an apply, so it
        // lands on the short interval; every later tick is the standing watch.
        Assert.Equal(GpuRomOverrideStartup.FirstWatchInterval.TotalSeconds, _waits[0].TotalSeconds);
        Assert.All(_waits.Skip(1), wait => Assert.Equal(30, wait.TotalSeconds));
        Assert.Single(_lines);   // only the restore that ended it
        Assert.Contains("no longer staged", _lines[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnApplyCutsTheWatchesSleepShort_SoTheRepairDoesNotWaitTheInterval()
    {
        // Field, 0.9.14: the apply raised the budget to 225 W and the EC republished its own
        // 150 W, but the watch was already asleep in its interval, so the card read the
        // republish for up to half a minute. A second apply must cut that sleep short.
        var overrides = new FakeGpuRomOverrideService();

        var asleep = new SemaphoreSlim(0);
        var sleeps = new List<CancellationToken>();
        GpuRomOverrideStartup startup = new(overrides, new ListLogger(_lines), async (_, token) =>
        {
            sleeps.Add(token);
            asleep.Release();
            await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
        });

        await startup.StartAsync(CancellationToken.None);   // NoIntent: no background watch
        Assert.Empty(sleeps);

        overrides.RaiseUnlocked();                          // the first apply starts the watch
        Assert.True(await asleep.WaitAsync(TimeSpan.FromSeconds(5)), "the watch never slept");

        overrides.RaiseUnlocked();                          // a second apply nudges it
        Assert.True(sleeps[0].WaitHandle.WaitOne(TimeSpan.FromSeconds(5)),
            "an apply must cut the watch's sleep short");

        await startup.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Watch_EndsAfterTheConsecutiveFailureCap()
    {
        var overrides = new FakeGpuRomOverrideService
        {
            WatchAnswer = new(GpuBudgetWatchVerdict.Failed, "the EC refused the F7 write"),
        };

        await Startup(overrides).WatchAsync(CancellationToken.None);

        Assert.Equal(GpuRomOverrideStartup.WatchFailureLimit, overrides.WatchCalls);
        Assert.Contains(_lines, line =>
            line.Contains($"{GpuRomOverrideStartup.WatchFailureLimit} consecutive ticks failed", StringComparison.Ordinal) &&
            line.Contains("the EC refused the F7 write", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Watch_ResetsTheFailureCountOnASuccessfulTick()
    {
        // Four failures, one clean tick, four more: the cap is consecutive, not cumulative.
        var overrides = new FakeGpuRomOverrideService();
        for (int i = 0; i < 4; i++)
            overrides.Watches.Enqueue(new(GpuBudgetWatchVerdict.Failed, "unreadable"));
        overrides.Watches.Enqueue(new(GpuBudgetWatchVerdict.Holding, null));
        for (int i = 0; i < 4; i++)
            overrides.Watches.Enqueue(new(GpuBudgetWatchVerdict.Failed, "unreadable"));
        overrides.Watches.Enqueue(new(GpuBudgetWatchVerdict.Stopped, null));

        await Startup(overrides).WatchAsync(CancellationToken.None);

        Assert.Equal(10, overrides.WatchCalls);
        Assert.DoesNotContain(_lines, line => line.Contains("consecutive ticks failed", StringComparison.Ordinal));
    }

    private GpuRomOverrideStartup Startup(FakeGpuRomOverrideService overrides) =>
        new(overrides, new ListLogger(_lines), (delay, _) =>
        {
            _waits.Add(delay);
            return Task.CompletedTask;
        });

    private sealed class ListLogger : ILogger<GpuRomOverrideStartup>
    {
        private readonly List<string> _lines;

        public ListLogger(List<string> lines) => _lines = lines;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            _lines.Add(formatter(state, exception));
    }
}
