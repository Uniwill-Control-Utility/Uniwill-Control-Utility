using System.IO;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Uniwill_Control_Utility.Views.Windows;

namespace Uniwill_Control_Utility.Tests.UI;

/// <summary>
/// The pre-loader card's composition. The invariants exist against a shipped
/// defect each: a transparent shadow gutter (ghosted rectangle around the
/// card) is why <see cref="Window.AllowsTransparency"/> stays false and the
/// content keeps an inset margin, and the card is a borderless, non-taskbar,
/// non-resizable surface (320x290 - the size the window-geometry observer
/// measures during the pre-loader). Its surface is painted by the Mica
/// backdrop, which DWM composites: that is the premise of the 92 % blip fix
/// (WPF's <c>Opacity=0</c> does not hide a DWM backdrop, so the main window
/// must be parked before its first <c>Show()</c>).
/// </summary>
[Collection("WPF")]
public sealed class StartupProgressWindowCompositionTests
{
    [Fact]
    public void WindowUsesAnOpaqueSurfaceWithoutATransparentShadowGutter()
    {
        RunOnSta(() =>
        {
            var window = new StartupProgressWindow();

            // The shadow gutter: a transparent window composites its own drop
            // shadow region as visible surface, which read as a ghost card.
            Assert.False(window.AllowsTransparency);

            // The card the geometry observer measures.
            Assert.Equal(320, window.Width);
            Assert.Equal(290, window.Height);

            // A card, not a window. The frame is invisible: WPF-UI's FluentWindow
            // keeps a real (SingleBorderWindow) frame because the Mica backdrop and
            // the rounded corners need one — the title bar disappears because the
            // content is extended into it.
            Assert.Equal(WindowStyle.SingleBorderWindow, window.WindowStyle);
            Assert.True(window.ExtendsContentIntoTitleBar);
            Assert.Equal(ResizeMode.NoResize, window.ResizeMode);
            Assert.False(window.ShowInTaskbar);

            var content = Assert.IsType<Grid>(window.Content);
            Assert.Null(content.Effect);
            Assert.True(content.Margin.Left > 0 && content.Margin.Top > 0,
                "Content is inset from the window edge, so nothing sits under the rounded corners.");

            // The surface comes from the Mica backdrop; a per-window brush may
            // only ever be opaque.
            if (window.Background is SolidColorBrush fill)
            {
                Assert.True(fill.Color.A > 0 && fill.Opacity > 0,
                    "The splash surface must never be transparent.");
            }

            window.Close();
        });
    }

    [Fact]
    public void WindowBackdropIsMica_CompositedByDwm()
    {
        string xaml = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "Uniwill Control Utility", "Views", "Windows", "StartupProgressWindow.xaml"));

        Assert.Contains("WindowBackdropType=\"Mica\"", xaml, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

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

    private static void RunOnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
