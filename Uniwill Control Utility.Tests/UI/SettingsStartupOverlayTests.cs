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

    [Fact]
    public void AppStartup_UpgradesOncePerVersion_ButCarriesOverOnEveryStart()
    {
        string source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "Uniwill Control Utility",
            "App.xaml.cs"));

        // Field, 0.9.15: returning early on the flag skipped Upgrade() *and* the
        // carry-over, because a leftover config for the running version carries the
        // flag false. The pass must not depend on it.
        Assert.DoesNotContain("if (!Settings.Default.SettingsUpgradeRequired)", source, StringComparison.Ordinal);

        int guard = source.IndexOf("if (upgradeRequired)", StringComparison.Ordinal);
        int upgrade = source.IndexOf("Settings.Default.Upgrade()", StringComparison.Ordinal);
        int carryOver = source.IndexOf("SettingsCarryOver.Run(", StringComparison.Ordinal);

        Assert.True(guard >= 0 && upgrade > guard,
            "Upgrade() must run only inside the once-per-version guard.");
        Assert.True(carryOver > upgrade,
            "The carry-over pass must run after the upgrade gate, on every start.");
        Assert.Equal(1, source.Split("Settings.Default.Upgrade()").Length - 1);

        // Twice for one version is impossible only if the flag is cleared inside that
        // same block and the block does the rest of the work (the carry-over) outside it.
        string guarded = BlockAfter(source, guard);
        Assert.Contains("Settings.Default.Upgrade()", guarded, StringComparison.Ordinal);
        Assert.Contains("SettingsUpgradeRequired = false", guarded, StringComparison.Ordinal);
        Assert.DoesNotContain("SettingsCarryOver.Run(", guarded, StringComparison.Ordinal);
    }

    /// <summary>Text inside the braced block that opens at or after <paramref name="index"/>.</summary>
    private static string BlockAfter(string source, int index)
    {
        int open = source.IndexOf('{', index);
        Assert.True(open >= 0, "No block body found after the guard.");

        int depth = 0;
        for (int i = open; i < source.Length; i++)
        {
            if (source[i] == '{')
            {
                depth++;
            }
            else if (source[i] == '}' && --depth == 0)
            {
                return source[open..i];
            }
        }

        throw new InvalidOperationException("Unbalanced braces in App.xaml.cs.");
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
