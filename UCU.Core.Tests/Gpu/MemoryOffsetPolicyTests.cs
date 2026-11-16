using UCU.Core.Gpu;

namespace UCU.Core.Tests.Gpu;

/// <summary>
/// The memory-offset readback judgement. The driver accepts a clock write and then erases
/// it with the next pstate write (field, 2026-11-16: GPU-Z 1812.6 -> 1750.1 MHz one sample
/// after a V-F curve apply), so the readback is the only witness.
/// </summary>
public sealed class MemoryOffsetPolicyTests
{
    private const int StockMax = 1750;   // 14001 MT/s / 8 on the field card

    [Fact]
    public void ACurveApplyWhoseReadbackShowsTheOffset_IsLive()
    {
        // The defect's inverse: the offset moved the clock, so it is in force.
        MemoryOffsetVerdict verdict = MemoryOffsetPolicy.Judge(
            requestedMhz: 500, stockMaxMhz: StockMax,
            beforeMhz: 1750, afterMhz: 1812, pStateBefore: 0, pStateAfter: 0);

        Assert.True(verdict.Verified);
        Assert.True(verdict.Live);
        Assert.Contains("1750 -> 1812", verdict.Detail, StringComparison.Ordinal);
        Assert.Contains("+500 MHz (live)", verdict.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAcceptedWriteThatReadsBackStock_IsNotLive()
    {
        // The field failure: the write was accepted and the clock never moved off stock.
        MemoryOffsetVerdict verdict = MemoryOffsetPolicy.Judge(
            requestedMhz: 500, stockMaxMhz: StockMax,
            beforeMhz: 1750, afterMhz: 1750, pStateBefore: 0, pStateAfter: 0);

        Assert.True(verdict.Verified);
        Assert.False(verdict.Live);
        Assert.Contains("NOT in force", verdict.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void AnOffsetTheWriteErased_ReadsAsNotLive()
    {
        // Before the curve it was live at 1812; after it the clock fell back to stock.
        MemoryOffsetVerdict verdict = MemoryOffsetPolicy.Judge(
            requestedMhz: 500, stockMaxMhz: StockMax,
            beforeMhz: 1812, afterMhz: 1750, pStateBefore: 0, pStateAfter: 0);

        Assert.True(verdict.Verified);
        Assert.False(verdict.Live);
    }

    [Fact]
    public void AnOffsetAlreadyLiveThatStayedPut_IsStillLive()
    {
        // Re-applying an offset that was already in force leaves the clock where it was; at
        // pstate 0 the stock clock is the reported maximum, so above it means in force.
        MemoryOffsetVerdict verdict = MemoryOffsetPolicy.Judge(
            requestedMhz: 500, stockMaxMhz: StockMax,
            beforeMhz: 1812, afterMhz: 1812, pStateBefore: 0, pStateAfter: 0);

        Assert.True(verdict.Verified);
        Assert.True(verdict.Live);
    }

    [Fact]
    public void ALowerPstateOffsetThatMoved_IsLive()
    {
        // Measured at the lower pstate: 1375.1 + 62.5 = 1437.6 MHz.
        MemoryOffsetVerdict verdict = MemoryOffsetPolicy.Judge(
            requestedMhz: 500, stockMaxMhz: StockMax,
            beforeMhz: 1375, afterMhz: 1437, pStateBefore: 8, pStateAfter: 8);

        Assert.True(verdict.Live);
    }

    [Fact]
    public void APstateChangeOrAnUnchangedLowerClock_IsUnverifiedNotAFailure()
    {
        MemoryOffsetVerdict moved = MemoryOffsetPolicy.Judge(
            requestedMhz: 500, stockMaxMhz: StockMax,
            beforeMhz: 1750, afterMhz: 1437, pStateBefore: 0, pStateAfter: 8);

        MemoryOffsetVerdict unchanged = MemoryOffsetPolicy.Judge(
            requestedMhz: 500, stockMaxMhz: StockMax,
            beforeMhz: 1375, afterMhz: 1375, pStateBefore: 8, pStateAfter: 8);

        Assert.False(moved.Verified);
        Assert.False(unchanged.Verified);
        Assert.Contains("pstate", moved.Detail, StringComparison.Ordinal);
        Assert.Contains("lower pstate", unchanged.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void NoOffsetRequested_IsALiveStockState()
    {
        MemoryOffsetVerdict verdict = MemoryOffsetPolicy.Judge(
            requestedMhz: 0, stockMaxMhz: StockMax,
            beforeMhz: 1750, afterMhz: 1750, pStateBefore: 0, pStateAfter: 0);

        Assert.True(verdict.Live);
        Assert.Contains("no memory offset requested", verdict.Detail, StringComparison.Ordinal);
    }
}
