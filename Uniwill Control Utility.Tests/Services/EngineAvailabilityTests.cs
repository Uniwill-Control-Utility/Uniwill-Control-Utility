using System.ComponentModel;
using Uniwill_Control_Utility.Services;

namespace Uniwill_Control_Utility.Tests.Services;

/// <summary>
/// The engine-service probe that every EC call site gates on. Its job is to answer
/// "is the engine running" without touching the pipe (a connect to a stopped engine's
/// missing pipe costs the full 5 s connect timeout, which is what stalled the splash),
/// to keep that answer cheap by caching it briefly, and to separate the states that
/// need different repairs - not installed, stopped, and installed-but-not-openable
/// (a DACL from a build that predates the SERVICE_START grant).
/// </summary>
public sealed class EngineAvailabilityTests
{
    [Fact]
    public void ScmAccessDenied_IsNotTreatedAsMissing()
    {
        // The repair differs: a missing service is registered, an unopenable one is
        // re-asserted (the elevated install pass re-applies the DACL). Classifying
        // access denied as "missing" is what hides a broken DACL behind an install
        // that appears to succeed.
        var ex = new InvalidOperationException(
            "Cannot open 'UCUEngine' service on computer '.'.",
            new Win32Exception(EngineAvailability.ErrorAccessDenied));

        Assert.Equal(
            EngineServiceState.AccessDenied,
            EngineAvailability.ClassifyScmFailure(ex));
    }

    [Theory]
    [InlineData(EngineAvailability.ErrorServiceDoesNotExist)]
    [InlineData(1722)] // The SCM itself is unreachable: nothing to open, same repair.
    public void OtherScmFailures_AreTreatedAsMissing(int nativeCode)
    {
        var ex = new InvalidOperationException("Cannot open service.", new Win32Exception(nativeCode));

        Assert.Equal(
            EngineServiceState.NotInstalled,
            EngineAvailability.ClassifyScmFailure(ex));
    }

    [Fact]
    public void Query_IsCachedForTheTtl_SoACallBurstAsksTheScmOnce()
    {
        int reads = 0;
        var availability = new EngineAvailability(() =>
        {
            reads++;
            return EngineServiceState.Stopped;
        });

        for (int i = 0; i < 12; i++)
            Assert.Equal(EngineServiceState.Stopped, availability.Query());

        Assert.Equal(1, reads);
    }

    [Fact]
    public void Invalidate_MakesTheNextQueryReadTheScmAgain()
    {
        // The app starts, repairs or stops the service itself, and a cached
        // "Stopped" from before that must not hide the start.
        int reads = 0;
        var availability = new EngineAvailability(() =>
        {
            reads++;
            return reads >= 2 ? EngineServiceState.Running : EngineServiceState.Stopped;
        });

        Assert.Equal(EngineServiceState.Stopped, availability.Query());
        availability.Invalidate();

        Assert.True(availability.IsRunning);
        Assert.Equal(2, reads);
    }

    [Fact]
    public void Started_FiresWhenTheServiceComesUp()
    {
        int state = (int)EngineServiceState.Stopped;
        var availability = new EngineAvailability(() => (EngineServiceState)state);

        int started = 0;
        availability.Started += () => started++;

        Assert.Equal(EngineServiceState.Stopped, availability.Query());
        state = (int)EngineServiceState.Running;
        availability.Invalidate();

        Assert.Equal(EngineServiceState.Running, availability.Query());
        Assert.Equal(1, started);
    }

    [Fact]
    public void Started_DoesNotFireOnTheFirstObservation()
    {
        // A process that launches while the engine is already up deferred nothing,
        // so a "transition" from the very first read must not trigger an apply.
        var availability = new EngineAvailability(() => EngineServiceState.Running);

        int started = 0;
        availability.Started += () => started++;

        Assert.True(availability.IsRunning);
        Assert.Equal(0, started);
    }

    [Fact]
    public void AccessDenied_WithALiveProbe_IsRunning()
    {
        // The guarded-machine case (field-proven: Emsisoft): a security product
        // reverts every service-descriptor write, so the app's SCM opens are
        // denied permanently while the engine answers RPC fine. Reporting
        // "not running" here skipped EC writes on a working engine - the fan
        // curve and charge limit silently never applied. The probe is the
        // liveness truth for exactly this state.
        var availability = new EngineAvailability(
            () => EngineServiceState.AccessDenied,
            async () =>
            {
                await Task.Delay(10);
                return true;
            });

        Assert.True(WaitFor(() => availability.IsRunning, TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void AccessDenied_WithADeadProbe_IsNotRunning()
    {
        var availability = new EngineAvailability(
            () => EngineServiceState.AccessDenied,
            () => Task.FromResult(false));

        Assert.False(availability.IsRunning);
        Assert.False(WaitFor(() => availability.IsRunning, TimeSpan.FromMilliseconds(500)));
    }

    [Fact]
    public void TheScmAnswerOutranksTheProbe()
    {
        // The probe speaks only where the SCM cannot. Running is Running with a
        // dead probe (a pipe hiccup must not mark a healthy service down), and
        // Stopped is Stopped with a live probe (the SCM can describe the service
        // and says it is not up; the pipe answering a dev console engine must
        // not make the app believe the service runs).
        var running = new EngineAvailability(
            () => EngineServiceState.Running,
            () => Task.FromResult(false));
        Assert.True(running.IsRunning);

        var stopped = new EngineAvailability(
            () => EngineServiceState.Stopped,
            () => Task.FromResult(true));
        Assert.False(stopped.IsRunning);
        Assert.False(WaitFor(() => stopped.IsRunning, TimeSpan.FromMilliseconds(500)));
    }

    [Fact]
    public void ProbeLiveness_TransitionFiresStartedOnce()
    {
        // Consumers deferred EC work because the engine looked down; the probe
        // noticing it alive is the same "it came up" moment the SCM transition
        // is, and the deferred apply is a once-only consume.
        var availability = new EngineAvailability(
            () => EngineServiceState.AccessDenied,
            async () =>
            {
                await Task.Delay(10);
                return true;
            });

        int started = 0;
        availability.Started += () => started++;

        Assert.True(WaitFor(() => availability.IsRunning, TimeSpan.FromSeconds(2)));
        Assert.Equal(1, started);
        Thread.Sleep(300);
        Assert.Equal(1, started);
    }

    [Fact]
    public void AccessDenied_RepeatedReads_KeepOneProbeLoop()
    {
        // A guarded machine reads the SCM on every gate call (2 s TTL), and each
        // read used to allocate a fresh 5 s probe timer while orphaning the last
        // one - timers accumulated and every one of them kept pinging the pipe.
        // One probe loop per EngineAvailability is the contract.
        int probes = 0;
        var availability = new EngineAvailability(
            () => EngineServiceState.AccessDenied,
            async () =>
            {
                Interlocked.Increment(ref probes);
                await Task.Delay(1);
                return true;
            },
            ttl: TimeSpan.Zero);

        for (int i = 0; i < 20; i++)
            _ = availability.IsRunning;

        // The immediate first tick of ONE timer lands in this window; twenty
        // orphaned timers would land twenty.
        Thread.Sleep(300);
        Assert.InRange(probes, 1, 3);
    }

    private static bool WaitFor(Func<bool> condition, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return true;
            Thread.Sleep(25);
        }

        return condition();
    }

    [Fact]
    public void EngineStartupApply_MarksAndConsumesExactlyOnce()
    {
        // The deferred EC apply must run for one consumer only: the post-splash
        // ensure and a Running transition can both ask for it.
        var apply = new EngineStartupApply();

        Assert.False(apply.IsPending);

        apply.MarkPending();
        Assert.True(apply.IsPending);
        Assert.True(apply.ConsumePending());
        Assert.False(apply.ConsumePending());
        Assert.False(apply.IsPending);
    }

    [Fact]
    public void EngineStartupApply_KeepsTheMarkForTheNextTransition()
    {
        // The EC resets the battery charge limit to the BIOS default on every cold
        // boot, so a write skipped while the engine was down must stay pending -
        // "skipped" must not silently become "lost".
        var apply = new EngineStartupApply();

        apply.MarkPending();
        apply.MarkPending();

        Assert.True(apply.IsPending);
        Assert.True(apply.ConsumePending());
    }
}
