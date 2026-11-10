using System;
using System.IO;
using System.Linq;

namespace Uniwill_Control_Utility.Tests.Services;

/// <summary>
/// The startup fan apply must not fight the preset that owns the fans (field,
/// 2026-11-02), and it must not stand in for a preset that carries its own fan
/// values (field, 2026-11-10).
///
/// ApplyStartupSettingsAsync runs the power-preset apply and the saved fan-curve
/// apply in parallel. A stock preset hands the fans to the EC firmware
/// (ApplyOemStockMode clears the custom-profile bit and the fan page locks), so
/// that 2026-11-02 field case had "Office applied" with the fans blasting the
/// saved Full Speed curve. The gate then covered STOCK presets only - and a
/// Custom preset never applied its own fan block on the boot path at all, so a
/// Custom restore ran the Fan Control page's values (2026-11-10, PCSpecialist
/// Recoil 3 16: the page held Full Speed, so the fans ran Full Speed under a
/// Custom 1 restore, with the editor read-only as if a stock preset were
/// active). The gate now covers both owners, and the boot custom path applies
/// the preset's fans.
/// </summary>
public sealed class FanStartupOwnershipTests
{
    [Fact]
    public void StartupFanApply_IsGatedOnThePresetNotOwningTheFans()
    {
        string source = ReadAppFile("Views", "Windows", "MainWindow.xaml.cs");

        Assert.Contains(
            "bool fansOwnedByPreset = PresetProfileApplier.ActiveProfileOwnsFans();",
            source,
            StringComparison.Ordinal);

        // Both apply paths (smart-fan start and the plain curve write) must sit
        // behind that gate; an ungated else branch is the bug returning.
        Assert.Contains("if (settings.EcSmartFanMode && !fansOwnedByPreset)", source, StringComparison.Ordinal);
        Assert.Contains("else if (!fansOwnedByPreset)", source, StringComparison.Ordinal);

        // The ownership decision leaves a breadcrumb: the field log has to say
        // which writer owned the fans on that boot, or the next report is blind.
        // It rides the default Warning level for the surprising answer (the page
        // owns them) and Information otherwise.
        Assert.Contains("[Fan] startup fan owner: {fanOwner}", source, StringComparison.Ordinal);
        Assert.Contains("DiagnosticLogger.LogWarning(", source, StringComparison.Ordinal);
    }

    [Fact]
    public void PageSavedSettings_DoNotOverrideAStockPreset_OutsideTheStartupApply()
    {
        // The startup apply is not the only writer of the Fan Control page's saved
        // settings: the Adaptive override lift re-applies them, and the page's own
        // Load restarts Smart Fan Mode. Both run long after boot and both used to
        // write the page's curve over a stock preset - the EC then ran Full Speed
        // while the UI claimed Office/Gaming/Turbo was driving the fans.
        string applier = ReadAppFile("Services", "DeviceApplier.cs");
        int start = applier.IndexOf("public void DisableEcFanOverride", StringComparison.Ordinal);
        Assert.True(start > 0, "DisableEcFanOverride not found");
        int end = applier.IndexOf("\n        }", start, StringComparison.Ordinal);
        string liftBody = applier.Substring(start, end - start);

        int gate = liftBody.IndexOf("PresetProfileApplier.IsBuiltInProfile(Properties.Settings.Default.presetProfile)", StringComparison.Ordinal);
        int write = liftBody.IndexOf("_fan.ApplyCurveAsync", StringComparison.Ordinal);
        Assert.True(gate >= 0, "The override lift must ask whether a stock preset owns the fans.");
        Assert.True(write >= 0, "The override lift still restores the settings for a Custom preset.");
        Assert.True(gate < write, "The stock-preset gate must come BEFORE the EC write.");

        string page = ReadAppFile("Views", "Pages", "FanControl.xaml.cs");
        Assert.Contains(
            "if (settings.EcSmartFanMode && _fan is not null && !_deviceApplier.IsManualControlLocked)",
            page,
            StringComparison.Ordinal);
    }

    [Fact]
    public void BootCustomPreset_AppliesItsOwnFanBlock()
    {
        // The defect: a Custom restore applied the SMU command, the V-F curve and
        // the EC limits, and left the fans to the parallel fan-page task.
        string source = ReadAppFile("Views", "Windows", "MainWindow.xaml.cs");

        int start = source.IndexOf("private static async Task ApplyCustomOnStart", StringComparison.Ordinal);
        Assert.True(start > 0, "ApplyCustomOnStart not found");
        int end = source.IndexOf("\n        }", start, StringComparison.Ordinal);
        Assert.True(end > start, "ApplyCustomOnStart body not terminated");

        string body = source.Substring(start, end - start);
        Assert.Contains("PresetProfileApplier.ApplyFanSettingsFromPreset(p);", body, StringComparison.Ordinal);
    }

    [Fact]
    public void FanOwnershipHelper_ResolvesTheBootSlotTheSameWayTheRestoreDoes()
    {
        // The gate and the apply must agree on WHICH preset is active (keyed
        // "Custom N (Boost)", legacy "Custom", first-changed-slot fallback), or
        // both writers fire and the race is back.
        string source = ReadAppFile("Services", "PresetProfileApplier.cs");

        int start = source.IndexOf("public static bool ActiveCustomPresetHasFanChanges", StringComparison.Ordinal);
        Assert.True(start > 0, "ActiveCustomPresetHasFanChanges not found");
        int end = source.IndexOf("\n        }", start, StringComparison.Ordinal);
        string body = source.Substring(start, end - start);

        Assert.Contains("TryParseCustomSlotExtended(profileName)", body, StringComparison.Ordinal);
        Assert.Contains("Settings.Default.EnablePowerBoostMode", body, StringComparison.Ordinal);
        Assert.Contains("Settings.Default.customPresetSlotBoost", body, StringComparison.Ordinal);
        Assert.Contains("presets[i].HasChanges", body, StringComparison.Ordinal);
        Assert.Contains("HasFanChanges", body, StringComparison.Ordinal);
    }

    [Fact]
    public void StartupPresetApplyIsUnconditional()
    {
        // The "Apply on startup" option is gone (2026-11-02): a relaunch must
        // never leave the EC on the previous session's values while the UI says
        // the saved preset is active. No setting may gate the apply again.
        string source = ReadAppFile("Views", "Windows", "MainWindow.xaml.cs");

        int start = source.IndexOf("internal static async Task ApplyOnStart()", StringComparison.Ordinal);
        Assert.True(start > 0, "ApplyOnStart not found");
        int end = source.IndexOf("internal static", start + 40);
        string body = end > 0 ? source.Substring(start, end - start) : source.Substring(start);

        Assert.DoesNotContain("Settings.Default.ApplyOnStart", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Settings.Default.ApplyOnStart", source, StringComparison.Ordinal);
    }

    private static string ReadAppFile(params string[] parts) =>
        File.ReadAllText(Path.Combine(new[] { RepositoryRoot(), "Uniwill Control Utility" }.Concat(parts).ToArray()));

    private static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "Uniwill Control Utility")) &&
                File.Exists(Path.Combine(directory.FullName, "Uniwill Control Utility.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Repository root not found from " + AppContext.BaseDirectory);
    }
}
