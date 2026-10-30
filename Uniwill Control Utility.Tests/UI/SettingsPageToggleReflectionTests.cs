namespace Uniwill_Control_Utility.Tests.UI;

/// <summary>
/// A Settings page toggle has two kinds of writer: the user (who is asking for a
/// device change) and the page itself (reflecting a saved setting, a live device
/// state, or a radio StateChanged event). The Checked/Unchecked handlers
/// actuate, and actuating a reflection is work the user never asked for - the
/// field report was a **PowerShell UAC prompt when opening Settings**: the
/// hardware sync wrote the live webcam state into the toggle, its handler fired,
/// and SetWebcamAsync runs PowerShell elevated. The same path elevates the
/// touchpad admin flow, re-registers (or unregisters) the startup scheduled
/// task, and writes NVRAM for the CPU Advanced Performance Menu.
///
/// The rule is STATE EQUALITY, not a time window: a write that reflects moves
/// the row's belief together with the toggle, so the event it raises sees
/// "toggle state == believed state" and returns. The first fix used a flag set
/// around the write, and the field showed it leaking - WPF-UI's ToggleSwitch
/// raises Checked/Unchecked in a LATER dispatcher pass, after the flag had been
/// released (measured 2026-10-30: an elevated Enable-PnpDevice webcam command ran
/// 23 s after launch with no navigation at all). These assertions pin the rule
/// and keep the temporal mechanism from coming back.
/// </summary>
public sealed class SettingsPageToggleReflectionTests
{
    private static readonly string[] DeviceToggles =
    [
        "cbWifiToggle", "cbBluetoothToggle", "cbWebcamToggle", "cbTouchpadToggle",
    ];

    [Fact]
    public void SettingsPage_NeverWritesADeviceToggleDirectly()
    {
        string source = ReadSettingsPage();

        foreach (string toggle in DeviceToggles)
        {
            Assert.DoesNotContain($"{toggle}.IsChecked =", source, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("cbWifiToggle_Checked", "SetWifiAsync", "cbWifiToggle", "_wifiOn")]
    [InlineData("cbWifiToggle_Unchecked", "SetWifiAsync", "cbWifiToggle", "_wifiOn")]
    [InlineData("cbBluetoothToggle_Checked", "SetBluetoothAsync", "cbBluetoothToggle", "_bluetoothOn")]
    [InlineData("cbBluetoothToggle_Unchecked", "SetBluetoothAsync", "cbBluetoothToggle", "_bluetoothOn")]
    [InlineData("cbWebcamToggle_Checked", "SetWebcamAsync", "cbWebcamToggle", "_webcamOn")]
    [InlineData("cbWebcamToggle_Unchecked", "SetWebcamAsync", "cbWebcamToggle", "_webcamOn")]
    [InlineData("cbTouchpadToggle_Checked", "SetTouchpadAsync", "cbTouchpadToggle", "_touchpadOn")]
    [InlineData("cbTouchpadToggle_Unchecked", "SetTouchpadAsync", "cbTouchpadToggle", "_touchpadOn")]
    [InlineData("cbStartBoot_Checked", "UpdateScheduledTask", "cbStartBoot", "_startOnBoot")]
    [InlineData("cbStartBoot_Unchecked", "ScheduledTaskService.Unregister", "cbStartBoot", "_startOnBoot")]
    [InlineData("cbCpuAdvancedPerformanceMenu_Checked", "WriteCpuApMenuUnlock", "cbCpuAdvancedPerformanceMenu", "_apMenuUnlocked")]
    [InlineData("cbCpuAdvancedPerformanceMenu_Unchecked", "WriteCpuApMenuUnlock", "cbCpuAdvancedPerformanceMenu", "_apMenuUnlocked")]
    [InlineData("cbPowerBoostMode_Checked", "InvalidateBoostDependentPages", "cbPowerBoostMode", "_powerBoost")]
    [InlineData("cbPowerBoostMode_Unchecked", "InvalidateBoostDependentPages", "cbPowerBoostMode", "_powerBoost")]
    [InlineData("cbHideFlydigi_Checked", "RefreshNavigationItems", "cbHideFlydigi", "_flydigiEnabled")]
    [InlineData("cbHideFlydigi_Unchecked", "RefreshNavigationItems", "cbHideFlydigi", "_flydigiEnabled")]
    public void ActuatingHandlers_ActuateOnlyOnUserIntent(
        string handler, string actuation, string toggle, string belief)
    {
        string body = MethodBody(ReadSettingsPage(), $"private void {handler}");

        int guard = body.IndexOf($"if (!UserIntent({toggle}, ref {belief},", StringComparison.Ordinal);
        int acts = body.IndexOf(actuation, StringComparison.Ordinal);

        Assert.True(acts >= 0, $"{handler} must still actuate for the user ({actuation}).");
        Assert.True(guard >= 0 && guard < acts,
            $"{handler} must return when the toggle state equals the row's belief: that is a reflected write, and the user did not ask for {actuation}.");
    }

    [Fact]
    public void UserIntentRule_LivesInOneHelper_AndLogsBothDirections()
    {
        string source = ReadSettingsPage();
        string body = MethodBody(source, "private bool UserIntent(");

        // The rule is a state comparison; the belief moves to the commanded state
        // so a re-raised event is not a second device operation; and both
        // directions are logged, because this defect class hides in dispatcher
        // timing and the field report named no row.
        // The rule is a state comparison over a KNOWN state: a row whose belief is
        // still null has never been observed, so nothing moved and nothing was
        // asked for. p48 proved the necessity: at startup the four device rows all
        // logged "toggle=true belief=null" (WPF-UI applies the XAML IsChecked value
        // in the template pass and raises from there), and the plain comparison
        // read that as intent - Wi-Fi, Bluetooth, webcam and touchpad all actuated
        // on app start.
        Assert.Contains("belief.HasValue && toggle.IsChecked != belief", body, StringComparison.Ordinal);
        Assert.Contains("belief = toggle.IsChecked;", body, StringComparison.Ordinal);
        Assert.Contains("actuating: toggle=", body, StringComparison.Ordinal);
        Assert.Contains("reflection, not intent: toggle=", body, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryActuatingRow_BeliefStartsAsTheTogglesConstructedState()
    {
        string source = ReadSettingsPage();

        (string Toggle, string Belief)[] rows =
        [
            ("cbWifiToggle", "_wifiOn"),
            ("cbBluetoothToggle", "_bluetoothOn"),
            ("cbWebcamToggle", "_webcamOn"),
            ("cbTouchpadToggle", "_touchpadOn"),
            ("cbStartBoot", "_startOnBoot"),
            ("cbPowerBoostMode", "_powerBoost"),
            ("cbHideFlydigi", "_flydigiEnabled"),
            ("cbCpuAdvancedPerformanceMenu", "_apMenuUnlocked"),
        ];

        foreach ((string toggle, string belief) in rows)
        {
            // A belief of null means "unknown", and the XAML initial value's
            // queued raise (true != null) would read as user intent - that is how
            // p45 shipped an elevated webcam command 1 ms before the page's
            // status query.
            Assert.Contains($"{belief} = {toggle}.IsChecked;", source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void SavedSettingsInit_ReflectsEveryActuatingRow_WithItsBelief()
    {
        string source = ReadSettingsPage();

        (string Toggle, string Setting, string Belief)[] rows =
        [
            ("cbStartBoot", "Settings.Default.StartOnBoot", "_startOnBoot"),
            ("cbPowerBoostMode", "Settings.Default.EnablePowerBoostMode", "_powerBoost"),
            ("cbHideFlydigi", "Settings.Default.EnableFlydigi", "_flydigiEnabled"),
            ("cbCpuAdvancedPerformanceMenu", "Settings.Default.EnableCpuAdvancedPerformanceMenu", "_apMenuUnlocked"),
            ("cbTouchpadToggle", "Settings.Default.TouchpadEnabled", "_touchpadOn"),
        ];

        foreach ((string toggle, string setting, string belief) in rows)
        {
            // Writing the saved value into an actuating toggle directly would
            // raise a Checked/Unchecked event that reads as user intent in a
            // later dispatcher pass.
            Assert.DoesNotContain($"{toggle}.IsChecked = {setting};", source, StringComparison.Ordinal);
            Assert.Contains($"ReflectToggleState({toggle}, {setting}, ref {belief});", source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void HardwareSync_ReflectsEveryDeviceToggle_WithItsBelief()
    {
        string source = ReadSettingsPage();
        string body = MethodBody(source, "private async System.Threading.Tasks.Task SyncHardwareManagementStates");

        (string Toggle, string Belief)[] rows =
        [
            ("cbWifiToggle", "_wifiOn"),
            ("cbBluetoothToggle", "_bluetoothOn"),
            ("cbWebcamToggle", "_webcamOn"),
            ("cbTouchpadToggle", "_touchpadOn"),
        ];

        foreach ((string toggle, string belief) in rows)
        {
            Assert.Contains($"ReflectToggleState({toggle},", body, StringComparison.Ordinal);
            Assert.Contains($"ref {belief}", body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ReflectWriter_MovesTheBeliefWithTheToggle()
    {
        string source = ReadSettingsPage();
        string body = MethodBody(source, "private void ReflectToggleState(");

        int belief = body.IndexOf("belief = on;", StringComparison.Ordinal);
        int write = body.IndexOf("toggle.IsChecked = on;", StringComparison.Ordinal);

        Assert.True(belief >= 0 && write > belief,
            "The reflection writer sets the row's belief first, then writes the toggle: the event that write raises arrives later and must see equality.");
    }

    [Fact]
    public void RadioStateChangedListeners_ReflectTheDevice_DoNotWriteTheToggleDirectly()
    {
        string source = ReadSettingsPage();

        foreach (string handler in new[] { "WifiRadioStateChanged", "BluetoothRadioStateChanged" })
        {
            string body = MethodBody(source, $"private void {handler}");
            Assert.Contains("ReflectToggleState(", body, StringComparison.Ordinal);
            Assert.DoesNotContain(".IsChecked =", body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void NoTemporalGuard_Returns()
    {
        string source = ReadSettingsPage();

        // The first fix guarded the write with a flag; WPF-UI raises the event in
        // a later dispatcher pass, so the flag was already released. The state
        // equality rule replaces it, and the temporal one must not come back.
        Assert.DoesNotContain("_reflectingToggleState", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ReflectionInProgress", source, StringComparison.Ordinal);
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

        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
