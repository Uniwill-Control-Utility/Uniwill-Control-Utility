namespace Uniwill_Control_Utility.Tests.UI;

/// <summary>
/// The rail currents have no Apply of their own any more: the page's Apply writes them, in the
/// same act that applies the preset. The rules that keep that honest are pinned here:
///
///   - the write happens after the preset's own apply and before the read-back, because that
///     read re-binds the sliders to what the driver reports;
///   - it is scoped to the unlock being applied, and says nothing when it is not;
///   - a refusal or a transport failure comes back for the card's reason line, never silence;
///   - the sliders still say how they take effect, and why they are inert without the unlock.
/// </summary>
public sealed class PresetsRailWriteTests
{
    [Fact]
    public void ThePageApplyWritesTheRailsAfterThePresetAndBeforeTheReadBack()
    {
        string body = MethodBody(ReadPresetsPage(), "private async Task applyProfileAsync()");

        int preset = body.IndexOf("RetargetUnlockAsync(", StringComparison.Ordinal);
        int rails = body.IndexOf("await WriteRailLimitsAsync()", StringComparison.Ordinal);
        int readBack = body.IndexOf("await RefreshRomOverrideAsync()", StringComparison.Ordinal);

        Assert.True(preset >= 0 && rails > preset,
            "The rails are written as part of the preset's apply, not before it.");
        Assert.True(readBack > rails,
            "The read-back re-binds the sliders to the driver's numbers, so it may not run before the write.");

        // The read clears the refusals it did not ask for, so this write's own outcome goes back.
        Assert.Contains("_railLimitsError = railError ?? _railLimitsError;", body, StringComparison.Ordinal);
        Assert.Contains("_railLimitsNote = railNote ?? _railLimitsNote;", body, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRailWriteIsScopedToTheUnlockAndReportsItsOwnFailure()
    {
        string body = MethodBody(ReadPresetsPage(), "private async Task<(string? Note, string? Error)> WriteRailLimitsAsync()");

        // Scoped to the unlock: no unlock, no write and no line about it.
        Assert.Contains("_romOverride?.Enabled != true", body, StringComparison.Ordinal);
        Assert.Contains("return (null, null);", body, StringComparison.Ordinal);

        // The same capability call the card's own button used.
        Assert.Contains("SetRailLimitsAsync(", body, StringComparison.Ordinal);
        Assert.Contains("Quantize(sdRailNvvdd.Value, 0)", body, StringComparison.Ordinal);
        Assert.Contains("Quantize(sdRailMsvdd.Value, 0)", body, StringComparison.Ordinal);
        Assert.Contains("Quantize(sdRailBoardInput.Value, 3)", body, StringComparison.Ordinal);

        // Never silent: a refusal and a transport failure both come back for the reason line.
        Assert.Contains("error = answer.Reason;", body, StringComparison.Ordinal);
        Assert.Contains("error = ex.Message;", body, StringComparison.Ordinal);
    }

    [Fact]
    public void TheCardNoLongerCarriesItsOwnApplyButton()
    {
        Assert.DoesNotContain("btnRailApply", ReadPresetsXaml(), StringComparison.Ordinal);
        Assert.DoesNotContain("btnRailApply", ReadPresetsPage(), StringComparison.Ordinal);

        // The sliders explain themselves: why they are inert, and how a move reaches the driver.
        string source = ReadPresetsPage();
        Assert.Contains("Available once Extra GPU power is unlocked.", source, StringComparison.Ordinal);
        Assert.Contains("Written when you press Apply, and kept for the next boot.", source, StringComparison.Ordinal);
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
