using Uniwill_Control_Utility.Services;

namespace Uniwill_Control_Utility.Tests.UI;

/// <summary>
/// The Extra GPU power card's judgement. Field, 0.9.14: the owner set the Custom preset to 250 W
/// and the card still reported 225 W — the target was pinned to the measured tier and nothing said
/// the card was not delivering what was asked. So: the accepted range is the stock ceiling up to the
/// tier ceiling, and a shortfall is its own sentence with both numbers. The status row shows the
/// numbers without a verdict colour now, which makes that sentence the only place a failure speaks.
/// </summary>
public sealed class GpuUnlockVerdictTests
{
    [Fact]
    public void APresetBelowTheStockCeilingCarriesNoTargetAndTheRestOfTheAcceptedRangeDoes()
    {
        // The range guard: the engine accepts the stock ceiling (175 W) up to its tier ceiling
        // (250 W), so 200 W is a target - the bug was a floor at the measured 225 W, which
        // refused a 200 W request. Outside the range there is no target at all, and the old one
        // stands.
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
