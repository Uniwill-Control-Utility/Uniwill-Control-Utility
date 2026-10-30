using UCU.Core.Supervision;

namespace UCU.Core.Tests.Supervision;

/// <summary>
/// The preset power-mode arbiter policy (OpenRevo's power-arbiter rules ported to
/// UCU's preset-intent shape). The racing writers it arbitrates: startup restore,
/// resume re-apply, tray clicks, the physical Performance button, and the AC/battery
/// automations. Cases mirror OpenRevo's arbiter tests where the shapes align
/// (boot grace, thin-priority, user-explicit precedence) and add UCU's own rules
/// (Adaptive ownership, per-source persistence).
/// </summary>
public sealed class PresetArbiterPolicyTests
{
    private static readonly TimeSpan Grace = PresetArbiterPolicy.BootGraceWindow;   // 10 s
    private static readonly TimeSpan Debounce = PresetArbiterPolicy.DebounceWindow; // 600 ms

    private static PresetArbiterDecision Evaluate(
        PresetIntentSource source,
        string profile,
        TimeSpan uptime,
        bool adaptiveRunning = false,
        string? lastApplied = null,
        TimeSpan? sinceLastApplied = null)
        => PresetArbiterPolicy.Evaluate(source, profile, uptime, adaptiveRunning, lastApplied, sinceLastApplied);

    // ---- boot grace -------------------------------------------------------

    [Theory]
    [InlineData(PresetIntentSource.AutomationAc)]
    [InlineData(PresetIntentSource.AutomationBattery)]
    public void AutomationEdges_AreSuppressed_Under10s(PresetIntentSource source)
    {
        Assert.Equal(PresetArbiterAction.SuppressBootGrace,
            Evaluate(source, "Gaming", TimeSpan.FromSeconds(3)).Action);
        Assert.Equal(PresetArbiterAction.SuppressBootGrace,
            Evaluate(source, "Gaming", Grace - TimeSpan.FromMilliseconds(1)).Action);
    }

    [Theory]
    [InlineData(PresetIntentSource.AutomationAc)]
    [InlineData(PresetIntentSource.AutomationBattery)]
    public void AutomationEdges_AreAccepted_At10sAndLater(PresetIntentSource source)
    {
        Assert.Equal(PresetArbiterAction.Apply,
            Evaluate(source, "Gaming", Grace).Action);
        Assert.Equal(PresetArbiterAction.Apply,
            Evaluate(source, "Gaming", TimeSpan.FromSeconds(15)).Action);
    }

    [Theory]
    [InlineData(PresetIntentSource.UserInterface)]
    [InlineData(PresetIntentSource.HardwarePhysicalButton)]
    [InlineData(PresetIntentSource.BootRestore)]
    [InlineData(PresetIntentSource.ResumeFromSleep)]
    [InlineData(PresetIntentSource.AdaptiveMode)]
    public void NonAutomationIntents_AreNeverSuppressedByBootGrace(PresetIntentSource source)
    {
        Assert.Equal(PresetArbiterAction.Apply,
            Evaluate(source, "Office", TimeSpan.FromMilliseconds(1)).Action);
    }

    // ---- Adaptive Mode ownership ------------------------------------------

    [Theory]
    [InlineData(PresetIntentSource.AutomationAc)]
    [InlineData(PresetIntentSource.AutomationBattery)]
    [InlineData(PresetIntentSource.HardwarePhysicalButton)]
    public void AdaptiveRunning_YieldsEdgesAndButtonToIt(PresetIntentSource source)
    {
        Assert.Equal(PresetArbiterAction.SuppressAdaptiveRunning,
            Evaluate(source, "Office", TimeSpan.FromMinutes(5), adaptiveRunning: true).Action);
    }

    [Theory]
    [InlineData(PresetIntentSource.UserInterface)]
    [InlineData(PresetIntentSource.BootRestore)]
    [InlineData(PresetIntentSource.ResumeFromSleep)]
    [InlineData(PresetIntentSource.AdaptiveMode)]
    public void AdaptiveRunning_UserAndRestoreIntents_StillPass(PresetIntentSource source)
    {
        // The UI is the documented way to take control back from Adaptive;
        // restores and Adaptive's own intents do not challenge its ownership.
        Assert.Equal(PresetArbiterAction.Apply,
            Evaluate(source, "Turbo", TimeSpan.FromMinutes(5), adaptiveRunning: true).Action);
    }

    // ---- duplicate debounce ------------------------------------------------

    [Fact]
    public void SameProfile_WithinDebounce_AppliesOnce()
    {
        Assert.Equal(PresetArbiterAction.SuppressDuplicate,
            Evaluate(PresetIntentSource.AutomationAc, "Gaming",
                TimeSpan.FromMinutes(5), lastApplied: "Gaming",
                sinceLastApplied: Debounce - TimeSpan.FromMilliseconds(100)).Action);
    }

    [Fact]
    public void SameProfile_AfterDebounce_AppliesAgain()
    {
        Assert.Equal(PresetArbiterAction.Apply,
            Evaluate(PresetIntentSource.AutomationAc, "Gaming",
                TimeSpan.FromMinutes(5), lastApplied: "Gaming",
                sinceLastApplied: Debounce + TimeSpan.FromMilliseconds(1)).Action);
    }

    [Fact]
    public void ProfileMatch_IsCaseInsensitive()
    {
        // Preset keys ("office" vs "Office") are matched case-insensitively
        // everywhere in UCU; the debounce must agree with the selection logic.
        Assert.True(PresetArbiterPolicy.IsSameProfile("office", "Office"));
        Assert.Equal(PresetArbiterAction.SuppressDuplicate,
            Evaluate(PresetIntentSource.UserInterface, "office",
                TimeSpan.FromMinutes(1), lastApplied: "Office",
                sinceLastApplied: TimeSpan.FromMilliseconds(200)).Action);
    }

    [Fact]
    public void DifferentProfile_NeverSuppressedAsDuplicate()
    {
        Assert.Equal(PresetArbiterAction.Apply,
            Evaluate(PresetIntentSource.UserInterface, "Turbo",
                TimeSpan.FromMinutes(1), lastApplied: "Office",
                sinceLastApplied: TimeSpan.FromMilliseconds(50)).Action);
    }

    [Fact]
    public void FirstIntent_NoLastAppliedState_IsApplied()
    {
        Assert.Equal(PresetArbiterAction.Apply,
            Evaluate(PresetIntentSource.BootRestore, "Office", TimeSpan.FromSeconds(2)).Action);
    }

    // ---- precedence (OpenRevo's user-explicit test, UCU shape) --------------

    [Fact]
    public void UserUi_SurvivesEveryRule_TheAutomationEdgeDoesNot()
    {
        // The startup storm: an automation edge and a user click arrive at t=2s.
        var edge = Evaluate(PresetIntentSource.AutomationBattery, "Office", TimeSpan.FromSeconds(2));
        var click = Evaluate(PresetIntentSource.UserInterface, "Gaming", TimeSpan.FromSeconds(2));

        Assert.Equal(PresetArbiterAction.SuppressBootGrace, edge.Action);
        Assert.Equal(PresetArbiterAction.Apply, click.Action);
    }

    // ---- persistence rules ---------------------------------------------------

    [Theory]
    [InlineData(PresetIntentSource.UserInterface, true)]
    [InlineData(PresetIntentSource.HardwarePhysicalButton, true)]
    [InlineData(PresetIntentSource.AutomationAc, true)]
    [InlineData(PresetIntentSource.AutomationBattery, true)]
    [InlineData(PresetIntentSource.BootRestore, false)]
    [InlineData(PresetIntentSource.ResumeFromSleep, false)]
    [InlineData(PresetIntentSource.AdaptiveMode, false)]
    public void Persistence_OnlyForSelectionBearingSources(PresetIntentSource source, bool expected)
    {
        Assert.Equal(expected, PresetArbiterPolicy.ShouldPersistSelection(source));
    }
}
