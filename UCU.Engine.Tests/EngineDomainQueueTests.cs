using UCU.Contracts;

namespace UCU.Engine.Tests;

/// <summary>
/// Queue selection by verb domain. Every prefix must resolve to a queue of its
/// own, to the inline set, or to the documented telemetry fallback: the
/// dictionary indexer threw KeyNotFoundException for a prefix it did not know,
/// which reached the client as "unexpected engine fault" — the shape the first
/// verb to add a domain after the original eight hit (rtss.start).
/// </summary>
public sealed class EngineDomainQueueTests
{
    [Fact]
    public async Task KnownDomains_GetTheirOwnQueue()
    {
        await using var queues = new EngineDomainQueues(new EngineRuntimeOptions());

        Assert.NotNull(queues.QueueFor(EngineVerbs.Fan.ToggleTurbo));
        Assert.NotNull(queues.QueueFor(EngineVerbs.Power.GetLimits));
        Assert.NotNull(queues.QueueFor(EngineVerbs.Keyboard.GetFnLock));
    }

    [Fact]
    public async Task IdentitySensorAndSessionLaunchVerbs_RunInline()
    {
        await using var queues = new EngineDomainQueues(new EngineRuntimeOptions());

        Assert.Null(queues.QueueFor(EngineVerbs.Engine.Ping));
        Assert.Null(queues.QueueFor(EngineVerbs.Telemetry.GetMetricsSnapshot));

        // A user-session process launch must not queue behind a parked
        // hardware upload.
        Assert.Null(queues.QueueFor(EngineVerbs.Rtss.Start));
    }

    [Fact]
    public async Task AnUnregisteredDomain_FallsBackToTelemetry_WithoutThrowing()
    {
        await using var queues = new EngineDomainQueues(new EngineRuntimeOptions());

        SerialExecutor? fallback = queues.QueueFor("brandnew.some_read");

        Assert.NotNull(fallback);
        Assert.Same(queues.Queues[EngineDomainQueues.TelemetryDomain], fallback);
    }
}
