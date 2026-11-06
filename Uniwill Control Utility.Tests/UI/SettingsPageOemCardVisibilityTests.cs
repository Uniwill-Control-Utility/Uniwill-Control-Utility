namespace Uniwill_Control_Utility.Tests.UI;

/// <summary>
/// The OEM takeover card is only meaningful where the OEM stack is installed.
/// Field, 2026-11-06: after XMG Control Center was uninstalled (and
/// C:\Program Files\OEM emptied) the Settings card still advertised a takeover
/// it could not perform - the engine reported zero catalog services and zero
/// OEM processes, but an orphaned takeover journal kept the policy state at
/// Stale. The card now starts collapsed and the page reveals it unless the
/// status reports both counts as zero.
/// </summary>
public sealed class SettingsPageOemCardVisibilityTests
{
    [Fact]
    public void OemCard_StartsCollapsed()
    {
        string xaml = ReadSettingsPageXaml();

        int card = xaml.IndexOf("x:Name=\"OemTakeoverCard\"", StringComparison.Ordinal);
        Assert.True(card >= 0, "The OEM takeover card must keep its name (the visibility code targets it).");

        int elementEnd = xaml.IndexOf('>', card);
        Assert.True(elementEnd > card);
        string element = xaml.Substring(card, elementEnd - card);

        // Collapsed by default, so a machine without the stack never paints the
        // card before the status answers.
        Assert.Contains("Visibility=\"Collapsed\"", element, StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsPage_HidesTheCardOnlyWhenTheEngineReportsNotInstalled()
    {
        string source = ReadSettingsPageSource();

        int load = source.IndexOf("private async void UiPage_Loaded", StringComparison.Ordinal);
        Assert.True(load >= 0);

        int visibilityCall = source.IndexOf("RefreshOemCardVisibilityAsync", load, StringComparison.Ordinal);
        Assert.True(visibilityCall > load,
            "UiPage_Loaded must start the OEM card visibility read (the card is collapsed by default).");

        int method = source.IndexOf(
            "private async System.Threading.Tasks.Task RefreshOemCardVisibilityAsync",
            StringComparison.Ordinal);
        Assert.True(method > visibilityCall, "The visibility helper must exist below its call site.");

        string body = source.Substring(method, 2000);
        Assert.Contains("overview.EngineReachable", body, StringComparison.Ordinal);
        Assert.Contains("overview.ServicesInstalled == 0", body, StringComparison.Ordinal);
        Assert.Contains("overview.OemProcessesRunning == 0", body, StringComparison.Ordinal);
        Assert.Contains("Visibility.Collapsed", body, StringComparison.Ordinal);

        // An unknown answer must keep the card: hiding on a failed read would hide
        // it on every transient engine restart.
        Assert.Contains("catch (Exception ex)", body, StringComparison.Ordinal);
        Assert.Contains("Visibility.Visible", body, StringComparison.Ordinal);
    }

    private static string ReadSettingsPageXaml() =>
        File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "Uniwill Control Utility", "Views", "Pages", "SettingsPage.xaml"));

    private static string ReadSettingsPageSource() =>
        File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "Uniwill Control Utility", "Views", "Pages", "SettingsPage.xaml.cs"));

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

        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
