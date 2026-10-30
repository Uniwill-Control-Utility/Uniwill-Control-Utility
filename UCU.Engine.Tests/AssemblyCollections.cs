namespace UCU.Engine.Tests;

/// <summary>
/// Serializes the engine classes that carry tight host timing against the
/// host-boot-heavy gate suite. The suite boots a full in-process engine per
/// test; the SlowHardware test probes the fan queue with a 300 ms call
/// budget, and a wave of concurrently booting hosts from parallel collections
/// starves that probe (the queue survives, the client budget expires). One
/// shared collection makes the two classes never run at once; everything else
/// stays parallel.
/// </summary>
[CollectionDefinition("EngineHostTiming")]
public sealed class EngineHostTimingCollection
{
}
