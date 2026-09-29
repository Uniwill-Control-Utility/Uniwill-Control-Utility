namespace Uniwill_Control_Utility.Tests.UI;

public sealed class SettingsStartupOverlayTests
{
    [Fact]
    public void SettingsPage_ExposesStartupProgressToggle()
    {
        string xaml = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "Uniwill Control Utility",
            "Views",
            "Pages",
            "SettingsPage.xaml"));

        Assert.Contains("x:Name=\"cbShowStartupProgress\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"Show startup progress\"", xaml, StringComparison.Ordinal);
        Assert.Contains(
            "Text=\"Display the startup progress window while the application is loading\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains("Checked=\"cbShowStartupProgress_Checked\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Unchecked=\"cbShowStartupProgress_Unchecked\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void AppStartup_UsesSavedPreferenceBeforeCreatingProgressWindow()
    {
        string source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "Uniwill Control Utility",
            "App.xaml.cs"));

        int settingsUpgrade = source.IndexOf("PrepareUserSettings();", StringComparison.Ordinal);
        int guardedStartup = source.IndexOf(
            "try",
            source.IndexOf("private async void OnStartup", StringComparison.Ordinal),
            StringComparison.Ordinal);
        int preferenceRead = source.IndexOf(
            "Settings.Default.ShowStartupProgressOverlay",
            StringComparison.Ordinal);
        int conditionalWindow = source.IndexOf(
            "if (showStartupProgressOverlay)",
            StringComparison.Ordinal);

        Assert.True(settingsUpgrade >= 0, "User settings must be upgraded before startup preferences are read.");
        Assert.True(guardedStartup >= 0 && guardedStartup < settingsUpgrade,
            "Settings preparation and preference reads must be inside the startup exception boundary.");
        Assert.True(preferenceRead > settingsUpgrade);
        Assert.True(conditionalWindow > preferenceRead);
        Assert.Contains(
            "StartupProgressReporter.Create(showStartupProgressOverlay",
            source,
            StringComparison.Ordinal);
    }

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
