using UCU.Contracts;
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
    public void AReadInsideAHushWindowDoesNotReDeriveTheActions()
    {
        // Field, v0.9.14: pressing an apply briefly enabled Unlock extra GPU power. The read
        // the post-apply window makes lands on the EC's republish and answers Uncertain, which
        // counts as unlockable, so the buttons were re-derived from a mid-repair read and the
        // settled Unlocked read disabled them again.
        Assert.Equal(
            GpuRomOverrideStates.Unlocked,
            GpuUnlockVerdict.Settled(GpuRomOverrideStates.Unlocked, GpuRomOverrideStates.Uncertain, hushing: true));

        // Both hush windows hold the same way, and a failed read inside one changes nothing
        // either: the card keeps the actions it was already offering.
        Assert.Equal(
            GpuRomOverrideStates.Unlocked,
            GpuUnlockVerdict.Settled(GpuRomOverrideStates.Unlocked, null, hushing: true));
        Assert.Equal(
            GpuRomOverrideStates.NotUnlocked,
            GpuUnlockVerdict.Settled(GpuRomOverrideStates.NotUnlocked, GpuRomOverrideStates.Uncertain, hushing: true));

        // Outside a window the read is the state, so the last verification pass (which closes
        // the window first) settles the card on what the driver actually answered.
        Assert.Equal(
            GpuRomOverrideStates.Uncertain,
            GpuUnlockVerdict.Settled(GpuRomOverrideStates.Unlocked, GpuRomOverrideStates.Uncertain, hushing: false));
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

    [Fact]
    public void AShortfallIsHushedWhileThePostApplyVerificationIsOpen_AndSaidOnceItCloses()
    {
        // Field, 0.9.14: the 225 W apply succeeded, then the EC republished its own 150 W
        // budget for a few seconds and the card reported "Partly applied ... enforces 150 W".
        // The transient gap is the recovery, so the verification window keeps it quiet.
        Assert.Null(GpuUnlockVerdict.Shortfall(225_000, 150_000, midRecovery: true));

        // The re-read that clears the transient: the budget is back at the request.
        Assert.Null(GpuUnlockVerdict.Shortfall(225_000, 225_000, midRecovery: true));

        // The last verification pass renders with the window closed: a gap that survived it is
        // still said, so the hush never hides a real shortfall.
        string line = GpuUnlockVerdict.Shortfall(225_000, 150_000)!;
        Assert.Contains("150 W", line, StringComparison.Ordinal);
        Assert.Contains("225 W", line, StringComparison.Ordinal);
    }

    [Fact]
    public void ABudgetBelowTheRequestIsTheGapAReadAsksToRepair()
    {
        // The read-triggered repair's own condition: the driver answers min(request, budget),
        // so a budget under the request is the gap the drift watch is asked to close.
        Assert.True(GpuUnlockVerdict.BudgetShort(225_000, 150_000));
        Assert.True(GpuUnlockVerdict.BudgetShort(225_000, 175_000));
        Assert.False(GpuUnlockVerdict.BudgetShort(225_000, 225_000));
        Assert.False(GpuUnlockVerdict.BudgetShort(225_000, 250_000));

        // No request means the budget caps nothing, and an unread budget is not a shortfall.
        Assert.False(GpuUnlockVerdict.BudgetShort(null, 150_000));
        Assert.False(GpuUnlockVerdict.BudgetShort(225_000, null));
    }

    [Fact]
    public void TheEnvelopeStatesTheEcCeilingOnce_AndNamesItWhenItIsTheBindingLimit()
    {
        // The clamp keeps the request inside the EC's ceiling, so the ordinary line is the EC's
        // number and the enforced one - the request and the budget are the row above.
        string line = GpuUnlockVerdict.Envelope(200_000, 200_000, 200_000)!;
        Assert.Contains("EC ceiling 200 W", line, StringComparison.Ordinal);
        Assert.Contains("enforced 200 W", line, StringComparison.Ordinal);
        Assert.DoesNotContain("is the limit", line, StringComparison.Ordinal);

        // Nothing was read: no invented number, so no line at all.
        Assert.Null(GpuUnlockVerdict.Envelope(null, 250_000, 150_000));

        // A ceiling below the unlock's floor is not a target (the thermal failsafe holds the EC
        // at its base): the EC is then the binding number and the line says so. Asserted as the
        // line growing the binding clause, so a translated catalog cannot fail the test.
        string held = GpuUnlockVerdict.Envelope(150_000, 225_000, 150_000)!;
        Assert.Contains("150 W", held, StringComparison.Ordinal);
        Assert.NotEqual(GpuUnlockVerdict.Envelope(150_000, 150_000, 150_000), held);
    }
}
