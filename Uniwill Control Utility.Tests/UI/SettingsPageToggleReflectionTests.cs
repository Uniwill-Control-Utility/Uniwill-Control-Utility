namespace Uniwill_Control_Utility.Tests.UI;

/// <summary>
/// A Settings page toggle has two kinds of writer: the user (who is asking for
/// a device change) and the page itself (reflecting a saved setting, a live
/// device state, or a radio StateChanged event). The Checked/Unchecked handlers
/// actuate, and actuating a reflection is work the user never asked for - the
/// field report was a **PowerShell UAC prompt when clicking the Settings nav
/// item**, before the page appeared: the load-time sync wrote the live webcam
/// state into the toggle, its Unchecked handler fired, and SetWebcamAsync runs
/// PowerShell elevated. The same path elevates the touchpad admin flow,
/// re-registers (or unregisters) the startup scheduled task, and writes NVRAM
/// for the CPU Advanced Performance Menu. These assertions lock the rule:
/// reflected writes go through the guard, and handlers that reach outside the
/// settings store honor it.
/// </summary>
public sealed class SettingsPageToggleReflectionTests
{
    private static readonly string[] DeviceToggles =
    {
        "cbWifiToggle", "cbBluetoothToggle", "cbWebcamToggle", "cbTouchpadToggle",
    };

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
    [InlineData("cbWifiToggle_Checked", "SetWifiAsync")]
    [InlineData("cbWifiToggle_Unchecked", "SetWifiAsync")]
    [InlineData("cbBluetoothToggle_Checked", "SetBluetoothAsync")]
    [InlineData("cbBluetoothToggle_Unchecked", "SetBluetoothAsync")]
    [InlineData("cbWebcamToggle_Checked", "SetWebcamAsync")]
    [InlineData("cbWebcamToggle_Unchecked", "SetWebcamAsync")]
    [InlineData("cbTouchpadToggle_Checked", "SetTouchpadAsync")]
    [InlineData("cbTouchpadToggle_Unchecked", "SetTouchpadAsync")]
    [InlineData("cbStartBoot_Checked", "UpdateScheduledTask")]
    [InlineData("cbStartBoot_Unchecked", "ScheduledTaskService.Unregister")]
    [InlineData("cbCpuAdvancedPerformanceMenu_Checked", "WriteCpuApMenuUnlock")]
    [InlineData("cbCpuAdvancedPerformanceMenu_Unchecked", "WriteCpuApMenuUnlock")]
    public void ActuatingHandlers_GuardAgainstReflectedWrites(string handler, string actuation)
    {
        string body = HandlerBody(ReadSettingsPage(), handler);

        int guard = body.IndexOf("if (ReflectionInProgress())", StringComparison.Ordinal);
        int acts = body.IndexOf(actuation, StringComparison.Ordinal);

        Assert.True(acts >= 0, $"{handler} must still actuate for the user ({actuation}).");
        Assert.True(guard >= 0 && guard < acts,
            $"{handler} must return early on a reflected write: the page writes live/saved state into the toggle, and acting on that is a device operation the user never asked for.");
    }

    [Fact]
    public void SavedSettingsInit_ReflectsUnderTheGuard()
    {
        string source = ReadSettingsPage();

        int guard = source.IndexOf("_reflectingToggleState = true;", StringComparison.Ordinal);
        int firstWrite = source.IndexOf("cbStartBoot.IsChecked =", StringComparison.Ordinal);
        int reset = source.IndexOf("_reflectingToggleState = false;", StringComparison.Ordinal);

        Assert.True(guard >= 0, "The constructor's saved-settings writes must be marked as reflection.");
        Assert.True(firstWrite > guard, "The reflection guard must be set before the first toggle write.");
        Assert.True(reset > firstWrite, "The guard must be released after the init writes.");
        Assert.Contains("finally", source.Substring(guard, reset - guard), StringComparison.Ordinal);
    }

    [Fact]
    public void HardwareSync_ReflectsEveryToggleThroughTheGuard()
    {
        string source = ReadSettingsPage();
        string body = MethodBody(source, "private async System.Threading.Tasks.Task SyncHardwareManagementStates");

        foreach (string toggle in DeviceToggles)
        {
            Assert.Contains($"ReflectToggleState({toggle},", body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void RadioStateChangedListeners_ReflectTheDevice_DoNotWriteTheToggleDirectly()
    {
        string source = ReadSettingsPage();

        foreach (string handler in new[] { "WifiRadioStateChanged", "BluetoothRadioStateChanged" })
        {
            string body = HandlerBody(source, handler);
            Assert.Contains("ReflectToggleState(", body, StringComparison.Ordinal);
            Assert.DoesNotContain(".IsChecked =", body, StringComparison.Ordinal);
        }
    }

    private static string HandlerBody(string source, string handlerName) =>
        MethodBody(source, $"private void {handlerName}");

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
