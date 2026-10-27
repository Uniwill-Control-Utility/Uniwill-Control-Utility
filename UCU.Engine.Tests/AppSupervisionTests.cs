using Microsoft.Extensions.Logging.Abstractions;

namespace UCU.Engine.Tests;

/// <summary>
/// The supervisor's state machine against fake probes: arming, the
/// graceful-vs-crash split, backoff scheduling, and the fast-crash budget.
/// The policy math itself is GuardianPolicy's own suite (UCU.Core.Tests);
/// this pins how AppSupervision feeds and applies it.
/// </summary>
public sealed class AppSupervisionTests
{
    private sealed class Harness
    {
        private DateTime _base = new(2026, 10, 26, 12, 0, 0, DateTimeKind.Utc);

        /// <summary>What the probe reports: a start time while running, null when not.</summary>
        public DateTime? AppStartUtc { get; set; }

        public bool Graceful { get; set; }

        public int Launches { get; private set; }

        public DateTime Now { get; private set; }

        public AppSupervision Supervision { get; }

        public Harness()
        {
            Now = _base;
            Supervision = new AppSupervision(
                new EngineRuntimeOptions { SupervisionEnabled = false },
                NullLogger<AppSupervision>.Instance,
                probeAppStartUtc: () => AppStartUtc,
                gracefulExitFresh: _ => Graceful,
                launchApp: () => { Launches++; return true; },
                clock: () => Now);
        }

        public void Advance(TimeSpan by) => Now = Now.Add(by);

        public void AppRunning() => AppStartUtc = Now;

        public void AppGone() => AppStartUtc = null;

        public void Tick() => Supervision.Tick();
    }

    [Fact]
    public void NeverSeenRunning_NeverArms_NeverLaunches()
    {
        var h = new Harness();

        for (int i = 0; i < 10; i++)
            h.Tick();

        Assert.Equal(0, h.Launches);
    }

    [Fact]
    public void GracefulExit_Disarms_AndNeverRelaunches()
    {
        var h = new Harness();
        h.AppRunning();
        h.Tick(); // arms

        h.Advance(TimeSpan.FromMinutes(30));
        h.Graceful = true;
        h.AppGone();
        h.Tick(); // decision: Stop

        for (int i = 0; i < 10; i++)
            h.Tick();

        Assert.Equal(0, h.Launches);
    }

    [Fact]
    public void CrashAfterRealRun_RelaunchesImmediately()
    {
        var h = new Harness();
        h.AppRunning();
        h.Tick(); // arms

        h.Advance(TimeSpan.FromMinutes(30)); // a real run
        h.AppGone();
        h.Tick();

        Assert.Equal(1, h.Launches);
    }

    [Fact]
    public void FastCrash_RelaunchIsDelayedByTheBackoff()
    {
        var h = new Harness();
        h.AppRunning();
        h.Tick();

        h.Advance(TimeSpan.FromMinutes(1)); // below the 2-min fast-exit threshold
        h.AppGone();
        h.Tick(); // decision: RelaunchAfter 10 s

        Assert.Equal(0, h.Launches);

        h.Advance(TimeSpan.FromSeconds(5));
        h.Tick();
        Assert.Equal(0, h.Launches); // not due yet

        h.Advance(TimeSpan.FromSeconds(10));
        h.Tick();
        Assert.Equal(1, h.Launches);
    }

    [Fact]
    public void FastCrashLoop_ExhaustsTheBudgetAndStops()
    {
        var h = new Harness();

        // Three consecutive fast crashes get the three budgeted relaunches...
        for (int crash = 0; crash < 3; crash++)
        {
            h.AppRunning();
            h.Tick();
            h.Advance(TimeSpan.FromMinutes(1));
            h.AppGone();
            h.Tick(); // RelaunchAfter (budgeted)
            h.Advance(TimeSpan.FromMinutes(10));
            h.Tick(); // due: launch
        }

        Assert.Equal(3, h.Launches);

        // ...the fourth fast crash meets the exhausted budget: Stop, no relaunch.
        h.AppRunning();
        h.Tick();
        h.Advance(TimeSpan.FromMinutes(1));
        h.AppGone();
        h.Tick();
        h.Advance(TimeSpan.FromMinutes(10));
        h.Tick();

        Assert.Equal(3, h.Launches);
    }

    [Fact]
    public void RealRunResetsTheFastCrashBudget()
    {
        var h = new Harness();

        h.AppRunning();
        h.Tick();
        h.Advance(TimeSpan.FromMinutes(1));
        h.AppGone();
        h.Tick(); // RelaunchAfter, failure 1
        h.Advance(TimeSpan.FromMinutes(10));
        h.Tick(); // due: launch

        h.AppRunning();
        h.Tick();
        h.Advance(TimeSpan.FromMinutes(30)); // a real run
        h.AppGone();
        h.Tick(); // Relaunch immediately, budget reset

        Assert.Equal(2, h.Launches);

        // Fast crashes after the reset get the full budget again.
        h.AppRunning();
        h.Tick();
        h.Advance(TimeSpan.FromMinutes(1));
        h.AppGone();
        h.Tick(); // RelaunchAfter (budget not spent: 0 failures)
        h.Advance(TimeSpan.FromSeconds(30));
        h.Tick();

        Assert.Equal(3, h.Launches);
    }

    [Fact]
    public void GracefulSignal_MustBelongToTheRunThatEnded()
    {
        DateTime now = new(2026, 10, 26, 12, 0, 0, DateTimeKind.Utc);

        var inRun = new AppExitSignal();
        inRun.SignalGracefulExit(now.AddSeconds(-30));

        // Signalled during the run (run started 11:55, signal 11:59:30): the run
        // ended cleanly.
        Assert.True(inRun.IsFreshGracefulExit(now.AddMinutes(-5), now, AppSupervision.GracefulExitWindow));

        // Signalled BEFORE the run started: it belongs to an earlier run, so this
        // death is a crash - the rule the flag file needed for the same case.
        Assert.False(inRun.IsFreshGracefulExit(now.AddSeconds(-10), now, AppSupervision.GracefulExitWindow));

        // Signalled outside the window: that app died long ago, and an old signal
        // must not suppress the relaunch of a later run.
        var stale = new AppExitSignal();
        stale.SignalGracefulExit(now - AppSupervision.GracefulExitWindow - TimeSpan.FromSeconds(1));
        Assert.False(stale.IsFreshGracefulExit(now.AddHours(-3), now, AppSupervision.GracefulExitWindow));
    }

    [Fact]
    public void GracefulSignal_NothingSignalled_IsACrash()
    {
        Assert.False(new AppExitSignal().IsFreshGracefulExit(null, DateTime.UtcNow, AppSupervision.GracefulExitWindow));
    }

    [Fact]
    public async Task GracefulSignal_ReadsWhatAnotherThreadSignalled()
    {
        // The verb handler runs on a pipe worker and the supervisor polls on its
        // own timer: the Interlocked hand-off is what makes the value visible there.
        DateTime when = new(2026, 10, 26, 12, 0, 0, DateTimeKind.Utc);
        var signal = new AppExitSignal();

        Assert.Null(signal.LastGracefulExitUtc);
        await Task.Run(() => signal.SignalGracefulExit(when));
        Assert.Equal(when, signal.LastGracefulExitUtc);
    }
}
