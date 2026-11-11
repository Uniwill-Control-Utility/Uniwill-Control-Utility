using UCU.Core.Ec;

namespace UCU.Core.Tests.Ec;

/// <summary>
/// When the engine should re-initialise the EC. Both rules exist because a lost
/// activation bit is silent: every power and fan write is accepted and ignored, which
/// looks exactly like "the preset applied but nothing happened".
/// </summary>
public sealed class EcSupervisionPolicyTests
{
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(5);

    [Theory]
    [InlineData(5, false)]    // normal tick
    [InlineData(14, false)]   // slow tick (GC, IO stall) - still not a resume
    [InlineData(16, true)]    // past 3x: the clock stopped
    [InlineData(3600, true)]  // an hour asleep
    public void LooksLikeResume_OnlyPastThreeIntervals(int elapsedSeconds, bool expected)
    {
        Assert.Equal(expected, EcSupervisionPolicy.LooksLikeResume(
            TimeSpan.FromSeconds(elapsedSeconds), Tick));
    }

    [Fact]
    public void LooksLikeResume_IsFalseForANonPositiveInterval()
    {
        Assert.False(EcSupervisionPolicy.LooksLikeResume(TimeSpan.FromMinutes(5), TimeSpan.Zero));
    }

    [Theory]
    [InlineData(0x01, false)] // activated
    [InlineData(0x05, false)] // activated + EC-owned bit 2 (the normal live value)
    [InlineData(0x81, false)] // activated + the EC's keyboard-history bit
    [InlineData(0x00, true)]  // cleared: the EC reset
    [InlineData(0x04, true)]  // bit 2 only: activation lost
    public void NeedsReinitialization_TracksOnlyTheActivationBit(int register741, bool expected)
    {
        Assert.Equal(expected, EcSupervisionPolicy.NeedsReinitialization(register741));
    }
}
