using Uniwill_Control_Utility.Views.Controls;

namespace Uniwill_Control_Utility.Tests.UI;

/// <summary>
/// The dashboard battery-health classifier: 80 % design capacity and up reads
/// Healthy, anything above zero reads Degraded (the word the card shows), and
/// zero/absent reads Unavailable.
/// </summary>
public sealed class DashboardBatteryHealthClassifierTests
{
    [Theory]
    [InlineData(100, "Healthy")]
    [InlineData(80, "Healthy")]
    [InlineData(79, "Degraded")]
    [InlineData(1, "Degraded")]
    [InlineData(0, "Unavailable")]
    [InlineData(-1, "Unavailable")]
    public void ClassifyUsesThePresentationThreshold(int percent, string expected)
    {
        Assert.Equal(expected, DashboardBatteryHealthClassifier.Classify(percent).ToString());
    }
}
