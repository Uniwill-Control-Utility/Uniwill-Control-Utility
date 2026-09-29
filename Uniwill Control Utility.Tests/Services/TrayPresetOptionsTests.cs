using Uniwill_Control_Utility.Services;
using Uniwill_Control_Utility.Models;

namespace Uniwill_Control_Utility.Tests.Services;

public sealed class TrayPresetOptionsTests
{
    [Fact]
    public void ProfileNames_MatchTheFourTopLevelPresetPageButtons()
    {
        Assert.Equal(["Office", "Gaming", "Turbo", "Custom"], TrayPresetOptions.ProfileNames);
    }

    [Fact]
    public void Build_MapsEveryPresetToAButtonAndMarksTheActiveOne()
    {
        TrayPresetOption[] options = TrayPresetOptions.Build(
            ["Office", "Gaming", "Custom 1 (Boost)"],
            selectedPreset: "custom 1 (boost)");

        Assert.Collection(
            options,
            option => Assert.Equal(new TrayPresetOption("Office", false), option),
            option => Assert.Equal(new TrayPresetOption("Gaming", false), option),
            option => Assert.Equal(new TrayPresetOption("Custom 1 (Boost)", true), option));
    }

    [Fact]
    public void Build_RemovesBlankAndDuplicatePresetNames()
    {
        TrayPresetOption[] options = TrayPresetOptions.Build(
            ["Office", "", "office", "Gaming"],
            selectedPreset: "Office");

        Assert.Equal(
            [new TrayPresetOption("Office", true), new TrayPresetOption("Gaming", false)],
            options);
    }

    [Fact]
    public void ResolveApplicationKey_UsesTheActiveSavedCustomSlot()
    {
        string key = TrayPresetOptions.ResolveApplicationKey(
            "Custom",
            PresetMode.Boost,
            activeSlot: 0,
            slotHasChanges: true);

        Assert.Equal("Custom 1 (Boost)", key);
    }

    [Fact]
    public void ResolveApplicationKey_KeepsLegacyCustomWhenTheActiveSlotIsEmpty()
    {
        string key = TrayPresetOptions.ResolveApplicationKey(
            "Custom",
            PresetMode.Normal,
            activeSlot: 2,
            slotHasChanges: false);

        Assert.Equal("Custom", key);
    }
}
