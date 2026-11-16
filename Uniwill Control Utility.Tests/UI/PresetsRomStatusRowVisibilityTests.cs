namespace Uniwill_Control_Utility.Tests.UI;

/// <summary>
/// The unlock's status row is keyed to the intent marker (<c>Enabled</c>), never to the
/// derived state. After a reboot the state reads Uncertain until the engine's start-up
/// re-apply lands, so a row keyed to the state appeared for an owner who had not touched
/// the feature since booting. With the marker, the row is absent until the unlock has been
/// completed at least once, and once enabled it renders whatever the numbers are — including
/// "Not read" / "Unverified" while the re-apply is still settling.
/// </summary>
public sealed class PresetsRomStatusRowVisibilityTests
{
    [Fact]
    public void TheStatusRow_ShowsOnlyWhenTheUnlockIsEnabled()
    {
        string body = MethodBody(ReadPresetsPage(), "private void RenderRomStatusRow()");

        int enabled = body.IndexOf("state?.Enabled == true", StringComparison.Ordinal);
        int visible = body.IndexOf(
            "spRomStatusRow.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;",
            StringComparison.Ordinal);

        Assert.True(enabled >= 0, "The row must key on the marker, not on State.");
        Assert.True(visible > enabled, "The visibility must be decided from Enabled.");

        // The derived state still names the verdict on each value, never the row's visibility.
        Assert.DoesNotContain("GpuRomOverrideStates.Uncertain", body, StringComparison.Ordinal);
    }

    [Fact]
    public void TheXaml_KeepsTheStatusRowCollapsedUntilEnabled()
    {
        // The default is Collapsed, so a state read that has not arrived yet cannot flash
        // the row for a feature the owner never enabled.
        string xaml = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "Uniwill Control Utility", "Views", "Pages", "Presets.xaml"));

        Assert.Contains(
            "<StackPanel x:Name=\"spRomStatusRow\" Visibility=\"Collapsed\">",
            xaml, StringComparison.Ordinal);
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
        File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "Uniwill Control Utility",
            "Views",
            "Pages",
            "Presets.xaml.cs"));

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "Uniwill Control Utility")) &&
                Directory.Exists(Path.Combine(directory.FullName, "Uniwill Control Utility.Tests")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Repository root with the Presets page sources not found.");
    }
}
