namespace Uniwill_Control_Utility.Tests.UI;

/// <summary>
/// Power boost mode owns two surfaces and two irreversible acts, so the rules that hold them
/// together are pinned here rather than trusted to review:
///
///   - the toggle persists the setting in the handler that takes the decision, so the mode can
///     never be on screen without being written down;
///   - turning it on asks for the risk notice once per session, and a declined notice leaves the
///     mode off;
///   - turning it off removes a live driver-level override first, because the card that removes
///     it would otherwise disappear with the feature;
///   - the Extra GPU power surfaces follow the mode, with one honest exception - an override that
///     is still applied stays visible as one line, and then it carries no sliders.
/// </summary>
public sealed class PowerBoostModeGateTests
{
    [Theory]
    [InlineData("cbPowerBoostMode_Checked", "true")]
    [InlineData("cbPowerBoostMode_Unchecked", "false")]
    public void BothHandlers_PersistTheValueTheyDecide(string handler, string value)
    {
        string body = MethodBody(ReadSettingsPage(), $"private async void {handler}");

        int guard = body.IndexOf("UserIntent(cbPowerBoostMode, ref _powerBoost", StringComparison.Ordinal);
        int assign = body.IndexOf($"Settings.Default.EnablePowerBoostMode = {value};", StringComparison.Ordinal);
        int save = body.IndexOf("Settings.Default.Save();", StringComparison.Ordinal);

        Assert.True(guard >= 0, $"{handler} must stay behind the reflection guard.");
        Assert.True(assign > guard, $"{handler} must write the setting it decided.");
        Assert.True(save > assign,
            $"{handler} must Save in the same handler: a value that only lives in memory reads back False at the next start.");
    }

    [Fact]
    public void TurningItOn_AsksTheRiskNoticeOncePerSession_AndDecliningLeavesItOff()
    {
        string source = ReadSettingsPage();
        string body = MethodBody(source, "private async void cbPowerBoostMode_Checked");

        // Session-scoped memory, not a saved flag: the next session asks again.
        Assert.Contains("private static bool _powerBoostRiskAcknowledged;", source, StringComparison.Ordinal);

        int notice = body.IndexOf("if (!_powerBoostRiskAcknowledged)", StringComparison.Ordinal);
        int dialog = body.IndexOf("AcknowledgePowerBoostRiskAsync()", StringComparison.Ordinal);
        int enable = body.IndexOf("Settings.Default.EnablePowerBoostMode = true;", StringComparison.Ordinal);

        Assert.True(notice >= 0 && dialog > notice, "The notice must be gated on the session flag, never asked twice.");
        Assert.True(enable > dialog, "The setting may only be written once the notice was answered.");

        // Cancel leaves the mode off: the toggle goes back and nothing is saved.
        int cancel = body.IndexOf("ReflectToggleState(cbPowerBoostMode, false, ref _powerBoost);", StringComparison.Ordinal);
        Assert.True(cancel > dialog && cancel < enable,
            "A declined notice must put the toggle back to off before the setting is touched.");
    }

    [Fact]
    public void TurningItOff_RestoresALiveOverrideBeforeTheSettingGoesOff()
    {
        string source = ReadSettingsPage();
        string body = MethodBody(source, "private async void cbPowerBoostMode_Unchecked");

        int restore = body.IndexOf("RestoreUnlockBeforePowerBoostOffAsync()", StringComparison.Ordinal);
        int disable = body.IndexOf("Settings.Default.EnablePowerBoostMode = false;", StringComparison.Ordinal);

        Assert.True(restore >= 0, "Turning the mode off must check for a live override first.");
        Assert.True(disable > restore, "The mode may only go off once the override is gone.");

        // Refused/cancelled: the mode stays on and the toggle goes back.
        Assert.Contains("ReflectToggleState(cbPowerBoostMode, true, ref _powerBoost);", body, StringComparison.Ordinal);

        // The restore is the engine's own verb, behind the GPU-restart guard the card uses, and it
        // keys on the staged marker the status row keys on.
        string restoreBody = MethodBody(source, "private async Task<bool> RestoreUnlockBeforePowerBoostOffAsync()");
        Assert.Contains("GetRomOverrideStateAsync()", restoreBody, StringComparison.Ordinal);
        Assert.Contains("state?.Enabled != true", restoreBody, StringComparison.Ordinal);

        // A restore that did not report the override gone may not let the mode off either.
        string applyBody = MethodBody(source, "private async Task<bool> RestoreGpuRomOverrideAsync(");
        Assert.Contains("GpuDeviceRestartGuard.RunAsync(", applyBody, StringComparison.Ordinal);
        Assert.Contains("SetRomOverrideAsync(false)", applyBody, StringComparison.Ordinal);
        Assert.Contains("restored?.Enabled == false", applyBody, StringComparison.Ordinal);
        Assert.Contains("return false;", applyBody, StringComparison.Ordinal);
    }

    [Fact]
    public void TheExtraGpuPowerCard_FollowsPowerBoostMode()
    {
        string body = MethodBody(ReadPresetsPage(), "private void RenderRomOverride()");

        Assert.Contains("bool powerBoost = Settings.Default.EnablePowerBoostMode;", body, StringComparison.Ordinal);
        Assert.Contains("bool appliedWhileOff = !powerBoost && state?.Enabled == true;", body, StringComparison.Ordinal);
        Assert.Contains("_hasNvidia && (powerBoost || appliedWhileOff)", body, StringComparison.Ordinal);

        // That one state shows the line and the way out, and no sliders.
        Assert.Contains("tbRomPbmOffLine.Visibility = appliedWhileOff ? Visibility.Visible : Visibility.Collapsed;",
            body, StringComparison.Ordinal);
        Assert.Contains("spRailSection.Visibility = appliedWhileOff ? Visibility.Collapsed : Visibility.Visible;",
            body, StringComparison.Ordinal);
        Assert.Contains("btnRomUnlock.Visibility = appliedWhileOff ? Visibility.Collapsed : Visibility.Visible;",
            body, StringComparison.Ordinal);
    }

    [Fact]
    public void TheStatusRow_ReplacesItsNumbersWithTheLine_WhenTheModeIsOff()
    {
        string body = MethodBody(ReadPresetsPage(), "private void RenderRomStatusRow()");

        Assert.Contains("spRomStatusValues.Visibility = powerBoost ? Visibility.Visible : Visibility.Collapsed;",
            body, StringComparison.Ordinal);
        Assert.Contains("tbRomStatusPbmOff.Visibility = powerBoost ? Visibility.Collapsed : Visibility.Visible;",
            body, StringComparison.Ordinal);

        // The line is the shipped copy, and it starts hidden like the row it lives in.
        string xaml = ReadPresetsXaml();
        Assert.Contains(
            "Extra GPU power is applied but Power boost mode is off. Restore stock to turn it off, or turn Power boost mode on.",
            xaml, StringComparison.Ordinal);
        Assert.Contains("<ui:TextBlock x:Name=\"tbRomStatusPbmOff\"", xaml, StringComparison.Ordinal);
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
        File.ReadAllText(Path.Combine(RepositoryRoot(), "Uniwill Control Utility", "Views", "Pages", "SettingsPage.xaml.cs"));

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
