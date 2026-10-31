namespace Uniwill_Control_Utility.Tests.UI;

/// <summary>
/// The U-110 HVCI diagnostic on the PawnIO install dialog. A failed install
/// on a Memory Integrity machine is NOT the generic exit-code failure: the
/// "driver installed + Memory Integrity ON + device absent" shape names
/// Core Isolation as the blocker (the recognition OpenRevo applies to the
/// hard Win32 1275 from StartService). The copy NAMES the blocker and
/// prescribes the latest-driver remedy — it never advises disabling Memory
/// Integrity: PawnIO's latest release is signed to load with it ON, and
/// asking users to lower a Windows security boundary for a utility is not
/// the app's place. These pins keep the shape: the block is consulted
/// BEFORE the generic copy, the block copy is the only change to the
/// failure surface (the success, exit-code, and exception copies stay
/// verbatim — acceptance b), and the Security button ships collapsed so a
/// machine without the block never sees it.
/// </summary>
public sealed class InstallPawnIoHvciDiagnosticTests
{
    [Fact]
    public void FailedInstall_ConsultsTheBlockReason_BeforeTheGenericCopy()
    {
        string body = MethodBody(ReadSource(), "private async Task InstallPawnIoAsync()");

        int blockCheck = body.IndexOf("PawnIoDetectionService.GetBlockReason()", StringComparison.Ordinal);
        int genericCopy = body.IndexOf("PawnIO installer exited with code {0}", StringComparison.Ordinal);

        Assert.True(blockCheck >= 0, "The failed-install path must consult the U-110 block diagnostic.");
        Assert.True(genericCopy >= 0, "The generic exit-code copy must stay (acceptance b).");
        Assert.True(blockCheck < genericCopy,
            "The Core Isolation diagnostic is checked first; the generic copy answers only when no block decodes.");
    }

    [Fact]
    public void BlockCopy_NamesMemoryIntegrity_AndShowsTheSecurityButton_AndCarriesTheEvidence()
    {
        string body = MethodBody(ReadSource(), "private void ShowDriverBlockedByMemoryIntegrity(");

        Assert.Contains("Memory Integrity is blocking the driver", body, StringComparison.Ordinal);
        Assert.Contains("Core Isolation", body, StringComparison.Ordinal);

        // The visible chrome: custom title bar + card header (Window.Title
        // alone never renders in this window).
        Assert.Contains("titleBar.Title = title;", body, StringComparison.Ordinal);
        Assert.Contains("headerText.Text = title;", body, StringComparison.Ordinal);

        Assert.Contains("SecurityButton.Visibility = Visibility.Visible;", body, StringComparison.Ordinal);

        // The installer exit code rides the body as the evidence line.
        Assert.Contains("Installer exit code: {0}", body, StringComparison.Ordinal);
    }

    [Fact]
    public void BlockCopy_NeverAdvisesDisablingMemoryIntegrity_TheRemedyIsTheLatestDriver()
    {
        // A gaming utility does not tell the user to lower a Windows
        // security boundary: PawnIO's latest release is signed to load with
        // Memory Integrity ON, and the installer always fetches it. The copy
        // names the blocker, prescribes re-running the installer, and stays
        // silent on disabling anything.
        string body = MethodBody(ReadSource(), "private void ShowDriverBlockedByMemoryIntegrity(");

        Assert.Contains("does NOT need to be turned off", body, StringComparison.Ordinal);
        Assert.Contains("Run the installer again", body, StringComparison.Ordinal);

        Assert.DoesNotContain("turn Memory Integrity off", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Memory Integrity off", body, StringComparison.Ordinal);
        Assert.DoesNotContain("turn off", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("disable", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SecurityButton_DeepLinksCoreIsolation_WithTheSettingsFallback()
    {
        string body = MethodBody(ReadSource(), "private void SecurityButton_Click(");

        // The exact page with the Memory Integrity toggle, and the Defender
        // settings page as the fallback; both shell-executed.
        Assert.Contains("windowsdefender://coreisolation", body, StringComparison.Ordinal);
        Assert.Contains("ms-settings:windowsdefender", body, StringComparison.Ordinal);
        Assert.Contains("UseShellExecute = true", body, StringComparison.Ordinal);
    }

    [Fact]
    public void TheExistingFailureClasses_KeepTheirCopy_Verbatim()
    {
        string source = ReadSource();

        // Success, generic exit-code, and exception copies — acceptance b.
        Assert.Contains("PawnIO driver installation completed successfully.", source, StringComparison.Ordinal);
        Assert.Contains("PawnIO installer exited with code {0}.\\n\\nThe driver may not have installed correctly.", source, StringComparison.Ordinal);
        Assert.Contains("PawnIO driver installation failed.\\n\\n{0}", source, StringComparison.Ordinal);
    }

    [Fact]
    public void TheXaml_ShipsTheSecurityButtonCollapsed()
    {
        string xaml = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "Uniwill Control Utility", "Views", "Windows", "InstallPawnIO.xaml"));

        Assert.Contains("x:Name=\"SecurityButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Visibility=\"Collapsed\"", xaml, StringComparison.Ordinal);

        // The block copy re-titles the visible chrome: the header needs a name.
        Assert.Contains("x:Name=\"headerText\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"titleBar\"", xaml, StringComparison.Ordinal);
    }

    private static string MethodBody(string source, string signature)
    {
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Could not find {signature}.");

        int end = source.IndexOf("\n        }", start, StringComparison.Ordinal);
        Assert.True(end > start, $"Could not find the end of {signature}.");

        return source.Substring(start, end - start);
    }

    private static string ReadSource() =>
        File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "Uniwill Control Utility",
            "Views",
            "Windows",
            "InstallPawnIO.xaml.cs"));

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

        throw new DirectoryNotFoundException("Repository root with the InstallPawnIO sources not found.");
    }
}
