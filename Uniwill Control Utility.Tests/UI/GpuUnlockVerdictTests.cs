using Uniwill_Control_Utility.Services;

namespace Uniwill_Control_Utility.Tests.UI;

/// <summary>
/// The Extra GPU power status row's judgement. Field, 0.9.14: the owner set the Custom
/// preset to 250 W and the row still said "225 W · Tested" while the GPU TGP line above it
/// read 250 W — the badge compared "at least the target" and nothing said the card was not
/// delivering what was asked. So: the badge is the measured target and nothing else, and a
/// shortfall is its own sentence with both numbers.
/// </summary>
public sealed class GpuUnlockVerdictTests
{
    [Fact]
    public void OnlyTheMeasuredTargetCarriesTheTestedBadge()
    {
        Assert.True(GpuUnlockVerdict.IsTested(225_000));
        Assert.False(GpuUnlockVerdict.IsTested(250_000));
        Assert.False(GpuUnlockVerdict.IsTested(200_000));
        Assert.False(GpuUnlockVerdict.IsTested(null));
    }

    [Fact]
    public void Enforced_IsTheSmallestOfTheRequestTheBudgetAndTheEcLimit()
    {
        // The driver answers min(request, budget) and the EC's cTGP caps that again.
        Assert.Equal(150_000, GpuUnlockVerdict.Enforced(250_000, 250_000, 150_000));
        Assert.Equal(225_000, GpuUnlockVerdict.Enforced(250_000, 225_000, 250_000));
        Assert.Equal(250_000, GpuUnlockVerdict.Enforced(250_000, 250_000, 250_000));
        Assert.Equal(250_000, GpuUnlockVerdict.Enforced(250_000, null, null));
        Assert.Null(GpuUnlockVerdict.Enforced(null, null, null));
    }

    [Fact]
    public void AShortfall_IsOneLineWithBothNumbers_AndADeliveredRequestIsSilent()
    {
        Assert.Null(GpuUnlockVerdict.Shortfall(250_000, 250_000));
        Assert.Null(GpuUnlockVerdict.Shortfall(null, 150_000));

        string line = GpuUnlockVerdict.Shortfall(250_000, 150_000)!;
        Assert.Contains("150 W", line, StringComparison.Ordinal);
        Assert.Contains("250 W", line, StringComparison.Ordinal);
    }
}
