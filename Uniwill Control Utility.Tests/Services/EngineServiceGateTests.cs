using UCU.Client;
using UCU.Contracts;
using Uniwill_Control_Utility.Services;

namespace Uniwill_Control_Utility.Tests.Services;

/// <summary>
/// The transport gate the RPC client consults before connecting. Its value is the
/// cost of a negative: a connect to the missing pipe of a stopped service polls for
/// the whole 5 s connect timeout, and the client is called from the startup path, page
/// constructors, the tray tick and the EC telemetry caches. Answered from the SCM, a
/// stopped engine costs a sub-millisecond query.
///
/// The two "available" states that look unavailable are deliberate: a developer
/// targets a console-mode engine that has no service at all (there the connect is the
/// only probe), and access-denied - the guarded-machine state where the SCM is silent -
/// is answered by the RPC liveness probe, which is the same pipe the verb targets.
/// </summary>
public sealed class EngineServiceGateTests
{
    [Theory]
    [InlineData(EngineServiceState.Stopped, false)]
    [InlineData(EngineServiceState.Running, true)]
    [InlineData(EngineServiceState.NotInstalled, true)]
    public void OnlyAKnownStoppedServiceIsUnavailable(EngineServiceState state, bool expected)
    {
        var gate = new EngineServiceGate(new EngineAvailability(() => state));

        Assert.Equal(expected, gate.IsServiceAvailable());
    }

    [Fact]
    public void AccessDenied_IsAvailableOnlyWhenTheProbeIsAlive()
    {
        // The SCM cannot speak (security product guarding service descriptors), so
        // the probe decides: a live probe means the verb lands; a dead one means
        // the service is down or still inside its ~20 s EC activation handshake,
        // and allowing the connect costs 5 s per verb on the callers that run many.
        var up = new EngineServiceGate(new EngineAvailability(
            () => EngineServiceState.AccessDenied,
            async () =>
            {
                await Task.Delay(10);
                return true;
            }));
        var down = new EngineServiceGate(new EngineAvailability(
            () => EngineServiceState.AccessDenied,
            () => Task.FromResult(false)));

        Assert.True(WaitForProbe(() => up.IsServiceAvailable(), TimeSpan.FromSeconds(2)));
        Assert.False(down.IsServiceAvailable());
        Assert.False(WaitForProbe(() => down.IsServiceAvailable(), TimeSpan.FromMilliseconds(500)));
    }

    private static bool WaitForProbe(Func<bool> condition, TimeSpan timeout)
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
    public async Task StoppedService_RefusesTheVerbWithoutConnecting()
    {
        var client = new EngineClient(
            serviceGate: new EngineServiceGate(new EngineAvailability(() => EngineServiceState.Stopped)));

        EngineException ex = await Assert.ThrowsAsync<EngineException>(
            () => client.InvokeAsync(EngineVerbs.Engine.Ping));

        Assert.Equal(EngineErrorCode.HardwareUnavailable, ex.Code);
        Assert.Contains("not running", ex.Message, StringComparison.Ordinal);
        Assert.Contains("no connect attempted", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoGate_KeepsTheConnectBehaviourForAConsoleEngine()
    {
        // The dev composition (a console-mode engine, no service, no gate) must still
        // reach the pipe, so the gate has to be opt-in. A name that cannot exist, so
        // the result does not depend on whether a real engine is running on the
        // machine running the tests.
        var client = new EngineClient(
            pipeName: "UCU.Tests.Absent." + Guid.NewGuid().ToString("N"),
            connectTimeout: TimeSpan.FromMilliseconds(250));

        await Assert.ThrowsAsync<EngineException>(
            () => client.InvokeAsync(EngineVerbs.Engine.Ping));
    }
}
