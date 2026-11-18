using System.Text.RegularExpressions;

namespace Uniwill_Control_Utility.Tests.UI;

/// <summary>
/// The rail currents stand on their own: the sliders' own Apply writes them, no preset carries
/// them, and a preset apply leaves the unlock's target and rails untouched. The rules that keep
/// that honest are pinned here:
///
///   - the preset apply writes neither (no rail write, no retarget);
///   - the rails' Apply is the only writer, it sends the three slider values through the verb
///     that persists them (the engine saves a successful write for the next boot), and it reads
///     no preset;
///   - the sliders keep their row, their gating note and their caution, with one rule separating
///     them from the action row below - where the quick sets' Apply limits shares the line with
///     the quick configurations, each control gated on its own.
/// </summary>
public sealed class PresetsRailWriteTests
{
    [Fact]
    public void APresetApplyLeavesTheUnlockTargetAndTheRailsAlone()
    {
        string body = MethodBody(ReadPresetsPage(), "private async Task applyProfileAsync()");

        Assert.DoesNotContain("WriteRailLimitsAsync", body, StringComparison.Ordinal);
        Assert.DoesNotContain("SetRailLimitsAsync", body, StringComparison.Ordinal);
        Assert.DoesNotContain("RetargetUnlockAsync", body, StringComparison.Ordinal);
        Assert.DoesNotContain("SetRomOverrideAsync", body, StringComparison.Ordinal);

        // The card is only re-read, so a preset that moved the EC's GPU ceiling still shows the
        // driver's numbers.
        Assert.Contains("await RefreshRomOverrideAsync()", body, StringComparison.Ordinal);

        // The same holds on the boot / tray / watcher path, which has one apply for all of them.
        string applier = ReadApplier();
        Assert.DoesNotContain("RetargetUnlockAsync", applier, StringComparison.Ordinal);
        Assert.DoesNotContain("ApplyRailsFromPresetAsync", applier, StringComparison.Ordinal);
        Assert.DoesNotContain("SetRailLimitsAsync", applier, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRailsApplyWritesAndPersistsWithoutAPreset()
    {
        string body = MethodBody(ReadPresetsPage(), "private async Task ApplyRailLimitsAsync()");

        // The same capability call the write has always been: three slider values, the verb that
        // saves a successful selection beside the originals for the next boot.
        Assert.Contains("_gpuTuning.SetRailLimitsAsync(", body, StringComparison.Ordinal);
        Assert.Contains("Quantize(sdRailNvvdd.Value, 0)", body, StringComparison.Ordinal);
        Assert.Contains("Quantize(sdRailMsvdd.Value, 0)", body, StringComparison.Ordinal);
        Assert.Contains("Quantize(sdRailBoardInput.Value, 3)", body, StringComparison.Ordinal);

        // No preset is read and none is saved: the currents are not part of one.
        Assert.DoesNotContain("CustomPreset", body, StringComparison.Ordinal);
        Assert.DoesNotContain("SaveCustomPreset", body, StringComparison.Ordinal);
        Assert.DoesNotContain("_currentProfile", body, StringComparison.Ordinal);

        // Scoped to the unlock, and never silent: a refusal and a transport failure both land on
        // the row's reason line.
        Assert.Contains("_romOverride?.Enabled != true", body, StringComparison.Ordinal);
        Assert.Contains("_railLimitsError = answer.Reason;", body, StringComparison.Ordinal);
        Assert.Contains("_railLimitsError = ex.Message;", body, StringComparison.Ordinal);

        // The button that fires it, and the handler behind it.
        string xaml = ReadPresetsXaml();
        string tag = Handler(xaml, "<ui:Button x:Name=\"btnRailApply\"", "</ui:Button>");
        Assert.Contains("Content=\"Apply limits\"", tag, StringComparison.Ordinal);
        Assert.Contains("Appearance=\"Primary\"", tag, StringComparison.Ordinal);
        Assert.Contains("Click=\"btnRailApply_Click\"", tag, StringComparison.Ordinal);

        // Below the sliders, in the row it shares with the quick sets - and outside the panel
        // those sliders are dimmed with, so its own gate is set on the button itself.
        int sliders = xaml.IndexOf("x:Name=\"sdRailBoardInput\"", StringComparison.Ordinal);
        int row = xaml.IndexOf("x:Name=\"spRomActionRow\"", StringComparison.Ordinal);
        int button = xaml.IndexOf("x:Name=\"btnRailApply\"", StringComparison.Ordinal);
        int quick = xaml.IndexOf("x:Name=\"btnApply200\"", StringComparison.Ordinal);
        Assert.True(sliders >= 0 && row > sliders && quick > row && button > quick,
            "Apply limits follows the quick sets in one row under the sliders.");

        string render = MethodBody(ReadPresetsPage(), "private void RenderRailLimits()");
        Assert.Contains("bool usable = applied && bounded && !_railLimitsBusy;", render, StringComparison.Ordinal);
        Assert.Contains("spRailControls.IsEnabled = usable;", render, StringComparison.Ordinal);
        Assert.Contains("btnRailApply.IsEnabled = usable;", render, StringComparison.Ordinal);

        Assert.Contains("private async void btnRailApply_Click(", ReadPresetsPage(), StringComparison.Ordinal);
    }

    [Fact]
    public void TheSlidersSayTheyStandOnTheirOwn()
    {
        string xaml = ReadPresetsXaml();
        Assert.Contains("Text=\"Standalone: these currents are not saved with a preset.\"",
            xaml, StringComparison.Ordinal);

        // The gating note and the adapter caution stay as they were.
        string page = ReadPresetsPage();
        Assert.Contains("Available once Extra GPU power is unlocked.", page, StringComparison.Ordinal);
        Assert.Contains("Text=\"Above 14.0 A the GPU can outdraw", xaml, StringComparison.Ordinal);
        Assert.Contains("sdRailBoardInput.Value > TestedBoardInputMilliamps", page, StringComparison.Ordinal);

        // Nothing claims the values travel with a profile any more.
        Assert.DoesNotContain("saved with the profile", page, StringComparison.Ordinal);
        Assert.DoesNotContain("saved with the profile", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void OneRuleInTheGapBetweenTheSlidersAndTheActionRow()
    {
        // The sliders and the row under them - the quick sets and Apply limits - are separated by
        // exactly one rule: no trailing rule on the sliders plus another on the row.
        string xaml = ReadPresetsXaml();
        int start = xaml.IndexOf("x:Name=\"sdRailBoardInput\"", StringComparison.Ordinal);
        int end = xaml.IndexOf("x:Name=\"spRomActionRow\"", StringComparison.Ordinal);

        Assert.True(start >= 0 && end > start, "The last rail slider and the action row must exist.");

        string gap = xaml.Substring(start, end - start);
        int rules = Regex.Matches(gap, "Height=\"1\"").Count;

        Assert.True(rules == 1, $"The gap between the sliders and the action row carries {rules} rules, not one.");
    }

    private static string Handler(string source, string from, string to)
    {
        int start = source.IndexOf(from, StringComparison.Ordinal);
        int end = start < 0 ? -1 : source.IndexOf(to, start, StringComparison.Ordinal);

        Assert.True(start >= 0 && end > start, $"Could not slice {from} up to {to}.");

        return source.Substring(start, end - start);
    }

    private static string MethodBody(string source, string signature)
    {
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Could not find {signature}.");

        int end = source.IndexOf("\n        }", start, StringComparison.Ordinal);
        Assert.True(end > start, $"Could not find the end of {signature}.");

        return source.Substring(start, end - start);
    }

    private static string ReadPresetsPage() =>
        File.ReadAllText(Path.Combine(RepositoryRoot(), "Uniwill Control Utility", "Views", "Pages", "Presets.xaml.cs"));

    private static string ReadPresetsXaml() =>
        File.ReadAllText(Path.Combine(RepositoryRoot(), "Uniwill Control Utility", "Views", "Pages", "Presets.xaml"));

    private static string ReadApplier() =>
        File.ReadAllText(Path.Combine(RepositoryRoot(), "Uniwill Control Utility", "Services", "PresetProfileApplier.cs"));

    private static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Uniwill Control Utility.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
