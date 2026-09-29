using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Uniwill_Control_Utility.Views.Windows;

namespace Uniwill_Control_Utility.Tests.UI;

[Collection("WPF")]
public sealed class StartupProgressWindowCompositionTests
{
    [Fact]
    public void WindowUsesAnOpaqueSurfaceWithoutATransparentShadowGutter()
    {
        RunOnSta(() =>
        {
            _ = Application.Current ?? new Application();
            var window = new StartupProgressWindow();

            Assert.False(window.AllowsTransparency);
            Assert.Equal(340, window.Width);
            Assert.Equal(250, window.Height);

            var surface = Assert.IsType<Border>(window.Content);
            Assert.Equal(window.Width, surface.Width);
            Assert.Equal(window.Height, surface.Height);
            Assert.Null(surface.Effect);

            var background = Assert.IsType<SolidColorBrush>(window.Background);
            Assert.Equal(Color.FromRgb(0x1B, 0x20, 0x24), background.Color);
        });
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
            finally
            {
                Application.Current?.Shutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
