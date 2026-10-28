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
/// only probe), and a service this process cannot open has an unknown state - guessing
/// "down" would hide a running engine behind a fake unavailability.
/// </summary>
public sealed class EngineServiceGateTests
{
    [Theory]
    [InlineData(EngineServiceState.Stopped, false)]
    [InlineData(EngineServiceState.Running, true)]
    [InlineData(EngineServiceState.NotInstalled, true)]
    [InlineData(EngineServiceState.AccessDenied, true)]
    public void OnlyAKnownStoppedServiceIsUnavailable(EngineServiceState state, bool expected)
    {
        var gate = new EngineServiceGate(new EngineAvailability(() => state));

        Assert.Equal(expected, gate.IsServiceAvailable());
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
