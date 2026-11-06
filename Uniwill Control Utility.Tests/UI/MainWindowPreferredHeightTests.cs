using Uniwill_Control_Utility.Views.Windows;

namespace Uniwill_Control_Utility.Tests.UI;

/// <summary>
/// The window shrinks to 907 only when both optional nav sections are unavailable:
/// either disabled in Settings, or their device is not connected. Anything else
/// keeps the 1080 base height.
/// </summary>
public sealed class MainWindowPreferredHeightTests
{
    [Theory]
    [InlineData(true, true, false, false, 907)]   // both disabled
    [InlineData(true, true, true, true, 907)]     // both disabled even while connected
    [InlineData(false, false, false, false, 907)] // both enabled, no device
    [InlineData(false, false, true, true, 1080)]  // both enabled and connected
    [InlineData(false, false, true, false, 1080)] // one device is enough
    [InlineData(true, false, false, true, 1080)]  // disabled hydro still shows Flydigi
    [InlineData(true, false, false, false, 907)]  // disabled hydro, Flydigi absent
    public void PreferredHeight_ShrinksOnlyWhenBothSectionsAreUnavailable(
        bool hydroDisabled, bool flydigiDisabled, bool hydroConnected, bool flydigiConnected, double expected)
    {
        Assert.Equal(expected, MainWindow.PreferredHeight(hydroDisabled, flydigiDisabled, hydroConnected, flydigiConnected));
    }
}
