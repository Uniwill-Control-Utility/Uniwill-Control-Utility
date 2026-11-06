using System;
using System.IO;
using System.Linq;

namespace Uniwill_Control_Utility.Tests.Services;

/// <summary>
/// The startup fan apply must not fight a stock preset (field, 2026-11-02).
///
/// ApplyStartupSettingsAsync runs the power-preset apply and the saved fan-curve
/// apply in parallel, and a stock preset hands the fans to the EC firmware
/// (ApplyOemStockMode clears the custom-profile bit and the fan page locks). So
/// whether the user's saved curve overrode the preset was a race - the field
/// case had "Office applied" with the fans blasting the saved Full Speed curve
/// until the preset was re-applied by hand. The startup apply is therefore
/// gated on the preset not owning the fans.
/// </summary>
public sealed class FanStartupOwnershipTests
{
    [Fact]
    public void StartupFanApply_IsGatedOnThePresetNotOwningTheFans()
    {
        string source = ReadAppFile("Views", "Windows", "MainWindow.xaml.cs");

        Assert.Contains(
            "bool fansOwnedByPreset = PresetProfileApplier.IsBuiltInProfile(Properties.Settings.Default.presetProfile);",
            source,
            StringComparison.Ordinal);

        // Both apply paths (smart-fan start and the plain curve write) must sit
        // behind that gate; an ungated else branch is the bug returning.
        Assert.Contains("if (settings.EcSmartFanMode && !fansOwnedByPreset)", source, StringComparison.Ordinal);
        Assert.Contains("else if (!fansOwnedByPreset)", source, StringComparison.Ordinal);
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
