using Microsoft.Extensions.Logging.Abstractions;

namespace UCU.Engine.Tests;

/// <summary>
/// A launch attempt that produces no process must not leave supervision disarmed
/// with nothing scheduled Ã¢â‚¬â€ the app used to stay dead until the user started it.
/// The retry budget is bounded the same way the fast-crash budget is: a machine
/// whose launcher cannot work at all must not retry forever.
/// </summary>
public sealed class AppSupervisionLaunchRetryTests
{
    private sealed class Harness
    {
        private DateTime _now = new(2026, 11, 3, 12, 0, 0, DateTimeKind.Utc);

        public DateTime? AppStartUtc { get; set; }

        public bool LaunchSucceeds { get; set; }

        public int Launches { get; private set; }

        public AppSupervision Supervision { get; }

        public Harness()
        {
            Supervision = new AppSupervision(
                new EngineRuntimeOptions { SupervisionEnabled = false },
                NullLogger<AppSupervision>.Instance,
                probeAppStartUtc: () => AppStartUtc,
                gracefulExitFresh: _ => false,
                launchApp: () => { Launches++; return LaunchSucceeds; },
                clock: () => _now);
        }

        public void Tick() => Supervision.Tick();

        public void AppRunning() => AppStartUtc = _now;

        public void AppGone() => AppStartUtc = null;

        public void Advance(TimeSpan by) => _now = _now.Add(by);

        /// <summary>Runs the app for a real stretch, then kills it Ã¢â‚¬â€ the Relaunch decision.</summary>
        public void RunThenDie()
        {
            AppStartUtc = _now;
            Tick();                 // arms
            Advance(TimeSpan.FromMinutes(10));
            AppStartUtc = null;
            Tick();                 // running -> stopped, decision Relaunch
        }

        /// <summary>Six poll intervals, so every scheduled retry is due.</summary>
        public void TickForAWhile()
        {
            for (int i = 0; i < 6; i++)
            {
                Advance(AppSupervision.PollInterval);
                Tick();
            }
        }
    }

    [Fact]
    public void SuccessfulLaunch_IsAttemptedOnce()
    {
        var h = new Harness { LaunchSucceeds = true };

        h.RunThenDie();
        h.TickForAWhile();

        Assert.Equal(1, h.Launches);
    }

    [Fact]
    public void FailedLaunch_IsRetriedTwice_ThenGivesUp()
    {
        var h = new Harness { LaunchSucceeds = false };

        h.RunThenDie();          // first attempt
        h.TickForAWhile();       // two scheduled retries, then the budget is spent

        // 1 initial + AppSupervision.MaxLaunchRetries retries, and no more afterwards.
        Assert.Equal(1 + AppSupervision.MaxLaunchRetries, h.Launches);

        h.TickForAWhile();
        Assert.Equal(1 + AppSupervision.MaxLaunchRetries, h.Launches);
    }

    [Fact]
    public void FailedLaunchThenSuccess_StopsRetrying()
    {
        var h = new Harness { LaunchSucceeds = false };
        h.RunThenDie();          // attempt 1 fails, retry scheduled

        h.LaunchSucceeds = true;
        h.Advance(AppSupervision.BaseDelay);
        h.Tick();                // retry succeeds

        Assert.Equal(2, h.Launches);

        // The app comes back up and arms again; no further launches.
        h.AppRunning();
        h.TickForAWhile();
        Assert.Equal(2, h.Launches);
    }

    [Fact]
    public void RetryBudgetResetsOnceTheAppRunsAgain()
    {
        var h = new Harness { LaunchSucceeds = false };
        h.RunThenDie();          // attempt 1 fails

        // The user starts the app by hand: supervision sees it and resets the budget.
        h.AppRunning();
        h.Tick();

        // It dies again for real, the launch fails once more: a full budget is available.
        h.LaunchSucceeds = false;
        h.Advance(TimeSpan.FromMinutes(10));
        h.AppGone();
        h.Tick();
        h.TickForAWhile();

        Assert.Equal(1 + 1 + AppSupervision.MaxLaunchRetries, h.Launches);
    }
}