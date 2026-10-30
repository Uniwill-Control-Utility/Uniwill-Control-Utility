namespace Uniwill_Control_Utility.Tests.UI;

/// <summary>
/// Startup presentation order (field report 2026-10-30: during the pre-loader,
/// at 92%, the main window "blips" - one frame of it showing). The cause is
/// compositing order, not logic: the gated ShowWindow showed the window at its
/// CenterScreen position and parked it off-screen afterwards, and this window's
/// backdrop (WindowBackdropType="Mica") is composited by DWM, which WPF's
/// Opacity=0 does not hide - so a 1080x1080 rounded panel painted one frame at
/// screen center behind the 320x290 splash card, which exposes the whole screen
/// around it. These assertions lock the order that removes it: park BEFORE the
/// first Show, keep the work-area fit off the parked window (its
/// Screen.FromHandle resolves to the nearest monitor and the re-center would
/// drag the parked window back on-screen), and land the window only after the
/// splash has closed.
/// </summary>
public sealed class MainWindowStartupPresentationTests
{
    [Fact]
    public void ShowWindow_ParksOffscreen_BeforeTheFirstShow()
    {
        string source = ReadSource("MainWindow.xaml.cs", "Views", "Windows");

        int gateCheck = source.IndexOf("if (GateFirstShowWindow)", StringComparison.Ordinal);
        Assert.True(gateCheck >= 0, "The startup splash gate must stay in ShowWindow.");

        int gatedBlockEnd = source.IndexOf("return;", gateCheck, StringComparison.Ordinal);
        Assert.True(gatedBlockEnd > gateCheck);
        string gated = source.Substring(gateCheck, gatedBlockEnd - gateCheck);

        int firstShow = gated.IndexOf("Show();", StringComparison.Ordinal);
        Assert.True(firstShow >= 0, "The gated path must Show: the host navigates right after it.");

        int parkLeft = gated.IndexOf("Left = GatedParkOffset", StringComparison.Ordinal);
        int parkTop = gated.IndexOf("Top = GatedParkOffset", StringComparison.Ordinal);
        int manualStartup = gated.IndexOf("WindowStartupLocation.Manual", StringComparison.Ordinal);

        Assert.True(parkLeft >= 0 && parkLeft < firstShow,
            "The window must be parked off-screen before Show: the Mica backdrop is DWM-composited, so Show-then-park paints one visible frame at the CenterScreen position.");
        Assert.True(parkTop >= 0 && parkTop < firstShow, "The vertical park must precede Show too.");
        Assert.True(manualStartup >= 0 && manualStartup < firstShow,
            "CenterScreen is applied during Show and overwrites a parked position, so the gated path must switch to Manual first.");
    }

    [Fact]
    public void LoadedWorkAreaFit_IsSkippedWhileTheWindowIsParked()
    {
        string source = ReadSource("MainWindow.xaml.cs", "Views", "Windows");

        int loaded = source.IndexOf("private void mainWindow_Loaded", StringComparison.Ordinal);
        Assert.True(loaded >= 0);
        int fit = source.IndexOf("App.FitWindowToWorkArea(this);", loaded, StringComparison.Ordinal);
        Assert.True(fit > loaded, "Loaded keeps the window inside the work area.");

        string beforeFit = source.Substring(loaded, fit - loaded);
        Assert.True(beforeFit.Contains("if (!_startupGateParked)", StringComparison.Ordinal),
            "Fitting the parked window resolves Screen.FromHandle to the nearest monitor and re-centers it on-screen, painting the backdrop frame the gate exists to prevent.");
    }

    [Fact]
    public void AppStartup_ClosesTheSplashBeforeTheWindowLands()
    {
        string source = ReadSource("App.xaml.cs");

        int playback = source.IndexOf("WaitForProgressPlaybackAsync()", StringComparison.Ordinal);
        int splashClose = source.IndexOf("startupWindow.Close();", StringComparison.Ordinal);
        int release = source.IndexOf("ReleaseStartupGate()", StringComparison.Ordinal);

        Assert.True(playback >= 0 && splashClose > playback);
        Assert.True(release > splashClose,
            "Releasing the gate while the small splash card is up exposes the dashboard on the rest of the screen; the window must land after the splash is gone.");
    }

    [Fact]
    public void MainWindow_UsesTheMicaBackdrop_TheReasonZeroOpacityCannotHideTheFirstFrame()
    {
        // Documents the premise of the ordering rules above: with a DWM-composited
        // backdrop, WPF's Opacity=0 hides only the WPF content, so position is the
        // only reliable way to keep a first frame unseen.
        string xaml = ReadSource("MainWindow.xaml", "Views", "Windows");

        Assert.Contains("WindowBackdropType=\"Mica\"", xaml, StringComparison.Ordinal);
        Assert.Contains("WindowStartupLocation=\"CenterScreen\"", xaml, StringComparison.Ordinal);
    }

    private static string ReadSource(string fileName, params string[] subfolders)
    {
        var parts = new List<string> { FindRepositoryRoot(), "Uniwill Control Utility" };
        parts.AddRange(subfolders);
        parts.Add(fileName);

        return File.ReadAllText(Path.Combine(parts.ToArray()));
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
