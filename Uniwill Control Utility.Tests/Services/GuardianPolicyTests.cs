using Uniwill_Control_Utility.Services;

namespace Uniwill_Control_Utility.Tests.Services;

public sealed class GuardianPolicyTests
{
    private const int MaxAttempts = 3;
    private static readonly TimeSpan BaseDelay = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MaxDelay = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan FastThreshold = TimeSpan.FromMinutes(2);

    private static GuardianDecision Evaluate(
        bool graceful,
        TimeSpan runDuration,
        int consecutiveFailures = 0)
        => GuardianPolicy.Evaluate(graceful, runDuration, consecutiveFailures,
            MaxAttempts, BaseDelay, MaxDelay, FastThreshold);

    [Fact]
    public void GracefulExit_Stops_NeverRelaunches()
    {
        // Tray "Close Application" must not be fought by the watchdog,
        // no matter how long or short the run was.
        var decision = Evaluate(graceful: true, TimeSpan.FromSeconds(5));
        Assert.Equal(GuardianAction.Stop, decision.Action);

        decision = Evaluate(graceful: true, TimeSpan.FromHours(9));
        Assert.Equal(GuardianAction.Stop, decision.Action);
    }

    [Fact]
    public void CrashAfterLongRun_RelaunchesImmediately()
    {
        var decision = Evaluate(graceful: false, TimeSpan.FromMinutes(30));
        Assert.Equal(GuardianAction.Relaunch, decision.Action);
        Assert.Equal(TimeSpan.Zero, decision.Delay);
    }

    [Fact]
    public void CrashAtExactlyThreshold_RelaunchesImmediately()
    {
        // Boundary: runDuration == fastExitThreshold is NOT a fast crash.
        var decision = Evaluate(graceful: false, FastThreshold);
        Assert.Equal(GuardianAction.Relaunch, decision.Action);
    }

    [Fact]
    public void FastCrash_RelaunchesWithExponentialBackoff()
    {
        var first = Evaluate(graceful: false, TimeSpan.FromSeconds(5), consecutiveFailures: 0);
        Assert.Equal(GuardianAction.RelaunchAfter, first.Action);
        Assert.Equal(BaseDelay, first.Delay);

        var second = Evaluate(graceful: false, TimeSpan.FromSeconds(5), consecutiveFailures: 1);
        Assert.Equal(GuardianAction.RelaunchAfter, second.Action);
        Assert.Equal(TimeSpan.FromSeconds(20), second.Delay);

        var third = Evaluate(graceful: false, TimeSpan.FromSeconds(5), consecutiveFailures: 2);
        Assert.Equal(GuardianAction.RelaunchAfter, third.Action);
        Assert.Equal(TimeSpan.FromSeconds(40), third.Delay);
    }

    [Fact]
    public void FastCrash_BackoffIsCappedAtMaxDelay()
    {
        // With a generous attempt budget, the exponential backoff must cap
        // at maxDelay instead of growing unbounded.
        var decision = GuardianPolicy.Evaluate(
            gracefulExitSignaled: false,
            runDuration: TimeSpan.FromSeconds(1),
            consecutiveFailures: 5,
            maxFastCrashAttempts: 10,
            baseDelay: TimeSpan.FromMinutes(1),
            maxDelay: MaxDelay,
            fastExitThreshold: FastThreshold);

        Assert.Equal(GuardianAction.RelaunchAfter, decision.Action);
        Assert.Equal(MaxDelay, decision.Delay);
    }

    [Fact]
    public void FastCrashLoop_StopsAfterMaxAttempts()
    {
        // A startup crash loop must not hammer the EC/hardware forever.
        var decision = Evaluate(graceful: false, TimeSpan.FromSeconds(1), consecutiveFailures: MaxAttempts);
        Assert.Equal(GuardianAction.Stop, decision.Action);
    }

    [Fact]
    public void LongRunCrash_DoesNotCountTowardFastCrashBudget()
    {
        // Failures only accumulate for fast crashes; a long healthy run
        // followed by a crash relaunches immediately regardless of budget.
        var decision = Evaluate(graceful: false, TimeSpan.FromHours(2), consecutiveFailures: MaxAttempts);
        Assert.Equal(GuardianAction.Relaunch, decision.Action);
    }
}
