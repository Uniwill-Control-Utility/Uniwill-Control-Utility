namespace Uniwill_Control_Utility.Tests.UI;

/// <summary>
/// The three quick configurations are one act each, so the rules that keep them honest are
/// pinned here rather than trusted to review:
///
///   - each button sends its own numbers: 200 W and 225 W share the tested rails and leave the
///     clocks alone, 250 W carries this unit's rails and clock offsets;
///   - the three writes travel in the engine's order - target, then rails, then clocks - because
///     the restart raises the rail policy and the clock write only lands after its cooldown;
///   - the group is gated on the unlock, the consent statement and (for 250 W) GPU clock control,
///     and says which of those is missing;
///   - the measurement tooltip states the live target instead of frozen copy.
/// </summary>
public sealed class PresetsQuickApplyTests
{
    [Fact]
    public void EachButtonSendsItsOwnNumbers()
    {
        string source = ReadPresetsPage();

        // Each handler is sliced up to the next one, so a swapped mapping cannot hide behind a
        // later handler's numbers.
        string twoHundred = Handler(source, "btnApply200_Click", "btnApply225_Click");
        string twoTwentyFive = Handler(source, "btnApply225_Click", "btnApply250_Click");
        string twoFifty = Handler(source, "btnApply250_Click", "private async Task ApplyQuickSetAsync(");

        Assert.Contains("200_000, TestedNvvddMilliamps, TestedMsvddMilliamps, TestedBoardInputMilliamps, null, null", twoHundred, StringComparison.Ordinal);
        Assert.Contains("225_000, TestedNvvddMilliamps, TestedMsvddMilliamps, TestedBoardInputMilliamps, null, null", twoTwentyFive, StringComparison.Ordinal);
        Assert.Contains("250_000, Measured250NvvddMilliamps, Measured250MsvddMilliamps, Measured250BoardInputMilliamps,", twoFifty, StringComparison.Ordinal);
        Assert.Contains("Measured250CoreOffsetMhz, Measured250MemoryOffsetMhz)", twoFifty, StringComparison.Ordinal);

        // The tier's numbers themselves: 212 / 100 / 16.971 A and +245 / +1500 MHz on this unit.
        Assert.Contains("private const int Measured250NvvddMilliamps = 212_000;", source, StringComparison.Ordinal);
        Assert.Contains("private const int Measured250MsvddMilliamps = 100_000;", source, StringComparison.Ordinal);
        Assert.Contains("private const int Measured250BoardInputMilliamps = 16_971;", source, StringComparison.Ordinal);
        Assert.Contains("private const int Measured250CoreOffsetMhz = 245;", source, StringComparison.Ordinal);
        Assert.Contains("private const int Measured250MemoryOffsetMhz = 1500;", source, StringComparison.Ordinal);
    }

    [Fact]
    public void TheThreeWritesTravelInTheEnginesOrder()
    {
        string body = MethodBody(ReadPresetsPage(), "private async Task ApplyQuickSetAsync(");

        int target = body.IndexOf("SetRomOverrideAsync(true, targetMilliwatts)", StringComparison.Ordinal);
        int rails = body.IndexOf("SetRailLimitsAsync(nvvdd, msvdd, board)", StringComparison.Ordinal);
        int clocks = body.IndexOf("NvTuning.SetClocks(core, memory)", StringComparison.Ordinal);

        Assert.True(target >= 0 && rails > target, "The rails are bounded by the policy the restart raises, so they follow it.");
        Assert.True(clocks > rails, "The clock write waits for the restart's cooldown, so it goes last.");

        // A clock write that did not land is stated, never a success with two of three parts.
        Assert.Contains("_quickApplyNote", body, StringComparison.Ordinal);
    }

    [Fact]
    public void TheGroupIsGatedOnTheUnlockTheStatementAndTheClockControl()
    {
        string body = MethodBody(ReadPresetsPage(), "private void RenderRomOverride()");

        Assert.Contains(
            "bool quickEnabled = !busy && !_railLimitsBusy && (unlocked || uncertain) && powerBoost && consent;",
            body, StringComparison.Ordinal);
        Assert.Contains(
            "bool measuredEnabled = quickEnabled && Settings.Default.EnableGpuClockControl;",
            body, StringComparison.Ordinal);

        // The statement the buttons need stays on the card while the unlock is applied - and it
        // shares its row with the unlock pair, so with nothing to state the row keeps Restore.
        Assert.Contains(
            "bool statement = (canUnlock || unlocked || uncertain) && !appliedWhileOff;",
            body, StringComparison.Ordinal);
        Assert.Contains(
            "chkRomConsent.Visibility = statement ? Visibility.Visible : Visibility.Collapsed;",
            body, StringComparison.Ordinal);
        Assert.Contains(
            "brRomConsent.Visibility = statement ? Visibility.Visible : Visibility.Collapsed;",
            body, StringComparison.Ordinal);

        string reason = MethodBody(ReadPresetsPage(), "private string? QuickApplyReason()");
        Assert.Contains("Available once Extra GPU power is unlocked.", reason, StringComparison.Ordinal);
        Assert.Contains("Tick the statement above first.", reason, StringComparison.Ordinal);
        Assert.Contains("GPU clock control on in Settings", reason, StringComparison.Ordinal);

        // The captions are the buttons' tooltips, so the row is one line; the 250 W set's
        // board input sits above 14 A, so its caution keeps a visible marker on the button.
        string xaml = ReadPresetsXaml();
        Assert.Contains("<ui:Button x:Name=\"btnApply250\"", xaml, StringComparison.Ordinal);
        Assert.Contains("<ToolTipService.ToolTip>", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"tbQuick250Tip\"", xaml, StringComparison.Ordinal);
        Assert.Contains("TextWrapping=\"Wrap\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Glyph=\"&#xE7BA;\"", xaml, StringComparison.Ordinal);

        string tooltips = MethodBody(ReadPresetsPage(), "private void setQuickApplyTooltips()");
        Assert.Contains("Above 14.0 A the GPU can outdraw", tooltips, StringComparison.Ordinal);
        Assert.Contains("tbQuick200Tip.Text", tooltips, StringComparison.Ordinal);
        Assert.Contains("tbQuick225Tip.Text", tooltips, StringComparison.Ordinal);
    }

    [Fact]
    public void TheTooltipStatesTheLiveTarget()
    {
        string body = MethodBody(ReadPresetsPage(), "private void setRomInfoTooltip()");

        Assert.DoesNotContain("Row(\"Target\", \"225 W\")", body, StringComparison.Ordinal);
        Assert.Contains("RequestedLimitMilliwatts", body, StringComparison.Ordinal);
        Assert.Contains("follows the preset", body, StringComparison.Ordinal);

        // The measurements stay measurements: the rails and the driver's ceiling are facts.
        Assert.Contains("Row(\"Rails\", \"250 A · 70 A · 14.026 A\")", body, StringComparison.Ordinal);
        Assert.Contains("Row(\"Ceiling (driver)\", \"250 W\")", body, StringComparison.Ordinal);
    }

    private static string Handler(string source, string from, string to)
    {
        int start = source.IndexOf(from, StringComparison.Ordinal);
        int end = source.IndexOf(to, start, StringComparison.Ordinal);

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
