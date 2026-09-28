using Uniwill_Control_Utility.Services;

namespace Uniwill_Control_Utility.Tests.Services;

public sealed class EcSmartFanCycleGateTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 28, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Running_cycle_rejects_overlapping_tick()
    {
        var gate = new EcSmartFanCycleGate();

        Assert.True(gate.TryEnter(Start));
        Assert.False(gate.TryEnter(Start.AddSeconds(2)));

        gate.CompleteWithoutWrite();

        Assert.True(gate.TryEnter(Start.AddSeconds(2)));
        gate.CompleteWithoutWrite();
    }

    [Fact]
    public void Successful_cycles_are_spaced_by_at_least_1500_milliseconds()
    {
        var gate = new EcSmartFanCycleGate();

        Assert.True(gate.TryEnter(Start));
        gate.CompleteSuccess();

        Assert.False(gate.TryEnter(Start.AddMilliseconds(1499)));
        Assert.True(gate.TryEnter(Start.AddMilliseconds(1500)));
        gate.CompleteWithoutWrite();
    }

    [Fact]
    public void Three_consecutive_failures_pause_writes_for_five_seconds()
    {
        var gate = new EcSmartFanCycleGate();

        FailCycle(gate, Start);
        FailCycle(gate, Start.AddMilliseconds(1500));
        FailCycle(gate, Start.AddMilliseconds(3000));

        Assert.False(gate.TryEnter(Start.AddMilliseconds(7999)));
        Assert.True(gate.TryEnter(Start.AddMilliseconds(8000)));
        gate.CompleteWithoutWrite();
    }

    [Fact]
    public void Successful_write_resets_failure_streak()
    {
        var gate = new EcSmartFanCycleGate();

        FailCycle(gate, Start);
        FailCycle(gate, Start.AddMilliseconds(1500));

        Assert.True(gate.TryEnter(Start.AddMilliseconds(3000)));
        gate.CompleteSuccess();

        FailCycle(gate, Start.AddMilliseconds(4500));
        FailCycle(gate, Start.AddMilliseconds(6000));

        Assert.True(gate.TryEnter(Start.AddMilliseconds(7500)));
        gate.CompleteWithoutWrite();
    }

    private static void FailCycle(EcSmartFanCycleGate gate, DateTimeOffset at)
    {
        Assert.True(gate.TryEnter(at));
        gate.CompleteFailure(at);
    }
}
