namespace Uniwill_Control_Utility.Tests.UI;

/// <summary>
/// The U-108 hardware-management EC rows (Windows-key lock, Fn-key lock, AC
/// power recovery) are the Settings page's first rows whose state lives ONLY
/// in the firmware: no saved setting to reflect, and the SET verbs answer
/// with the OBSERVED EC state, not the request (the win-lock trigger is a
/// toggle a firmware debounce can swallow — a toggle that "succeeded" while
/// the lock did not move would be a lying UI). These pins keep the shape:
/// truth read at construction behind the EC-availability gate, rows shown
/// only on a successful read (version skew -> UnsupportedVerb -> hidden),
/// the same belief discipline as the device rows, and observed-state
/// reflection after every actuation.
/// </summary>
public sealed class SettingsPageDeviceSwitchRowTests
{
    private static readonly (string Toggle, string Belief, string Row, string Verb)[] Rows =
    [
        ("cbWinLockToggle", "_winLockOn", "WinLockRow", "SetWinLockAsync"),
        ("cbFnLockToggle", "_fnLockOn", "FnLockRow", "SetFnLockAsync"),
        ("cbAcRecoveryToggle", "_acRecoveryOn", "AcRecoveryRow", "SetAcRecoveryAsync"),
    ];

    [Fact]
    public void TheConstructor_InitializesTheSwitchRows()
    {
        string source = ReadSettingsPage();

        Assert.Contains("InitDeviceSwitchRows();", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Init_GatesOnEcAvailability_AndReadsEveryTruthSynchronously()
    {
        string source = ReadSettingsPage();
        string body = MethodBody(source, "private void InitDeviceSwitchRows()");

        Assert.Contains("IsHardwareAvailableAsync().GetAwaiter().GetResult()", body, StringComparison.Ordinal);

        // The truth rides the read verbs; a belief set from anything else is a guess.
        Assert.Contains("GetWinLockAsync()", body, StringComparison.Ordinal);
        Assert.Contains("GetFnLockAsync()", body, StringComparison.Ordinal);
        Assert.Contains("GetAcRecoveryAsync()", body, StringComparison.Ordinal);
    }

    [Fact]
    public void TruthRead_ShowsTheRowOnlyOnSuccess_AndHidesOnVersionSkew()
    {
        string source = ReadSettingsPage();
        string body = MethodBody(source, "private void ReadSwitchTruth(");

        // Visible ONLY after the read returns; the XAML default is Collapsed.
        int read = body.IndexOf("read().GetAwaiter().GetResult();", StringComparison.Ordinal);
        int visible = body.IndexOf("row.Visibility = Visibility.Visible;", StringComparison.Ordinal);
        Assert.True(read >= 0 && visible > read,
            "A row shows only once the EC has answered with its truth.");

        Assert.Contains("EngineErrorCode.UnsupportedVerb", body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ActuateWinLock", "cbWinLockToggle", "_winLockOn", "SetWinLockAsync")]
    [InlineData("ActuateFnLock", "cbFnLockToggle", "_fnLockOn", "SetFnLockAsync")]
    [InlineData("ActuateAcRecovery", "cbAcRecoveryToggle", "_acRecoveryOn", "SetAcRecoveryAsync")]
    public void ActuationPaths_ActuateOnlyOnUserIntent(
        string actuator, string toggle, string belief, string verb)
    {
        string source = ReadSettingsPage();
        string body = MethodBody(source, $"private void {actuator}(");

        int guard = body.IndexOf($"if (!UserIntent({toggle}, ref {belief},", StringComparison.Ordinal);
        int acts = body.IndexOf(verb, StringComparison.Ordinal);

        Assert.True(acts >= 0, $"{actuator} must actuate for the user ({verb}).");
        Assert.True(guard >= 0 && guard < acts,
            $"{actuator} must return when the toggle state equals the row's belief.");
    }

    [Theory]
    [InlineData("cbWinLockToggle_Checked", "ActuateWinLock(true)")]
    [InlineData("cbWinLockToggle_Unchecked", "ActuateWinLock(false)")]
    [InlineData("cbFnLockToggle_Checked", "ActuateFnLock(true)")]
    [InlineData("cbFnLockToggle_Unchecked", "ActuateFnLock(false)")]
    [InlineData("cbAcRecoveryToggle_Checked", "ActuateAcRecovery(true)")]
    [InlineData("cbAcRecoveryToggle_Unchecked", "ActuateAcRecovery(false)")]
    public void ToggleHandlers_DelegateToTheSingleActuator(string handler, string call)
    {
        string source = ReadSettingsPage();

        Assert.Contains($"private void {handler}(object sender, RoutedEventArgs e) => {call};",
            source, StringComparison.Ordinal);
    }

    [Fact]
    public void ActuationHelper_ReflectsTheObservedState_TheFirmwareOwnsTheOutcome()
    {
        string source = ReadSettingsPage();
        string body = MethodBody(source, "private async System.Threading.Tasks.Task ActuateSwitchAsync(");

        // The verb's ANSWER (the EC's post-act state) drives the toggle - not
        // the command. The win-lock's swallowed fire must show on the toggle.
        Assert.Contains("observed = await actuate();", body, StringComparison.Ordinal);
        Assert.Contains("reflect(observed)", body, StringComparison.Ordinal);

        // A failed actuation reverts to the complement of the command: the
        // page's last observed truth.
        Assert.Contains("observed = !commanded;", body, StringComparison.Ordinal);

        // Engine swapped for a pre-U-108 build mid-session: hide, do not lie.
        Assert.Contains("EngineErrorCode.UnsupportedVerb", body, StringComparison.Ordinal);
        Assert.Contains("hide();", body, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSwitchToggles_AreNeverWrittenDirectly()
    {
        string source = ReadSettingsPage();

        foreach (var (toggle, _, _, _) in Rows)
        {
            Assert.DoesNotContain($"{toggle}.IsChecked =", source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheXaml_KeepsTheRowsCollapsedUntilTheTruthReadSucceeds()
    {
        string xaml = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "Uniwill Control Utility", "Views", "Pages", "SettingsPage.xaml"));

        foreach (var (_, _, row, _) in Rows)
        {
            Assert.Contains(
                $"<Grid x:Name=\"{row}\" Visibility=\"Collapsed\">",
                xaml, StringComparison.Ordinal);
        }
    }

    private static string MethodBody(string source, string signature)
    {
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Could not find {signature}.");

        int end = source.IndexOf("\n        }", start, StringComparison.Ordinal);
        Assert.True(end > start, $"Could not find the end of {signature}.");

        return source.Substring(start, end - start);
    }

    private static string ReadSettingsPage() =>
        File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "Uniwill Control Utility",
            "Views",
            "Pages",
            "SettingsPage.xaml.cs"));

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

        throw new DirectoryNotFoundException("Repository root with the Settings page sources not found.");
    }
}
