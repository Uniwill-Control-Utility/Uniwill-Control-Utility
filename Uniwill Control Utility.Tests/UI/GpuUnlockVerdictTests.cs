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
    public void APresetBelowTheStockCeilingCarriesNoTargetAndTheRestOfTheAcceptedRangeDoes()
    {
        // The retarget guard: the engine accepts the stock ceiling (175 W) up to its tier
        // ceiling (250 W), so 200 W is a target - the bug was a floor at the measured 225 W,
        // which left a 200 W preset aiming at 225 W. Below the stock ceiling the preset carries
        // nothing and the old target stands.
        Assert.Equal(200_000, GpuUnlockVerdict.UnlockTarget(200_000));
        Assert.Equal(175_000, GpuUnlockVerdict.UnlockTarget(175_000));
        Assert.Equal(225_000, GpuUnlockVerdict.UnlockTarget(225_000));
        Assert.Equal(250_000, GpuUnlockVerdict.UnlockTarget(250_000));

        Assert.Null(GpuUnlockVerdict.UnlockTarget(174_000));
        Assert.Null(GpuUnlockVerdict.UnlockTarget(251_000));

        // A stock preset hands its TGP to the firmware, so it carries no target at all.
        Assert.Null(GpuUnlockVerdict.UnlockTarget(null));
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
