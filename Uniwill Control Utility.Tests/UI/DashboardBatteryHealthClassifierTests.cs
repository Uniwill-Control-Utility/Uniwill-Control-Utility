using Uniwill_Control_Utility.Views.Controls;

namespace Uniwill_Control_Utility.Tests.UI;

public sealed class DashboardBatteryHealthClassifierTests
{
    [Theory]
    [InlineData(100, "Healthy")]
    [InlineData(80, "Healthy")]
    [InlineData(79, "Reduced")]
    [InlineData(1, "Reduced")]
    [InlineData(0, "Unavailable")]
    [InlineData(-1, "Unavailable")]
    public void ClassifyUsesThePresentationThreshold(int percent, string expected)
    {
        Assert.Equal(expected, DashboardBatteryHealthClassifier.Classify(percent).ToString());
    }
}
