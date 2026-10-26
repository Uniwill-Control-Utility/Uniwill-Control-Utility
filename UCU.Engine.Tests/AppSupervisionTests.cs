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
                gracefulFlagFresh: _ => Graceful,
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
    public void GracefulFlag_MustBelongToTheRunThatEnded()
    {
        DateTime now = new(2026, 10, 26, 12, 0, 0, DateTimeKind.Utc);
        string flag = Path.Combine(Path.GetTempPath(), "ucu-test-graceful-" + Guid.NewGuid().ToString("N") + ".flag");

        try
        {
            File.WriteAllText(flag, "2026-10-26T11:00:00Z");
            DateTime lastWrite = new(2026, 10, 26, 11, 59, 30, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(flag, lastWrite);

            // Flag written during the run (run 11:54:30, flag 11:59:30): the run
            // ended cleanly.
            Assert.True(AppSupervision.GracefulExitSignaled(flag, lastWrite.AddMinutes(-5), lastWrite.AddSeconds(2)));
            // Flag written BEFORE the run started: it is an earlier clean exit's
            // residue (the file outlives the process) — this death is a crash.
            Assert.False(AppSupervision.GracefulExitSignaled(flag, runStartedUtc: lastWrite.AddSeconds(30), nowUtc: lastWrite.AddSeconds(31)));

            // Flag written during the run, fresh: graceful.
            Assert.True(AppSupervision.GracefulExitSignaled(flag, lastWrite.AddMinutes(-2), lastWrite.AddSeconds(2)));

            // Stale flag (older than the window): crash.
            Assert.False(AppSupervision.GracefulExitSignaled(flag, lastWrite.AddHours(-3), lastWrite.AddMinutes(5)));
        }
        finally
        {
            File.Delete(flag);
        }
    }

    [Fact]
    public void GracefulFlag_MissingFile_IsACrash()
    {
        Assert.False(AppSupervision.GracefulExitSignaled(
            Path.Combine(Path.GetTempPath(), "ucu-definitely-absent-" + Guid.NewGuid().ToString("N") + ".flag"),
            runStartedUtc: null,
            nowUtc: DateTime.UtcNow));
    }
}
