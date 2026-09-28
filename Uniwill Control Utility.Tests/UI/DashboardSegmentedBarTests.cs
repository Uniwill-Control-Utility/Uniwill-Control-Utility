using Uniwill_Control_Utility.Views.Controls;

namespace Uniwill_Control_Utility.Tests.UI;

public sealed class DashboardSegmentedBarTests
{
    [Theory]
    [InlineData(0, 100, 20, 0)]
    [InlineData(1, 100, 20, 0)]
    [InlineData(5, 100, 20, 1)]
    [InlineData(35, 100, 20, 7)]
    [InlineData(50, 100, 20, 10)]
    [InlineData(100, 100, 20, 20)]
    [InlineData(120, 100, 20, 20)]
    [InlineData(-5, 100, 20, 0)]
    public void CalculateActiveSegments_ClampsAndFloorsProportion(
        double value,
        double maximum,
        int segments,
        int expected)
    {
        Assert.Equal(expected, DashboardSegmentedBar.CalculateActiveSegments(value, maximum, segments));
    }

    [Fact]
    public void CalculateActiveSegments_InvalidScale_ReturnsZero()
    {
        Assert.Equal(0, DashboardSegmentedBar.CalculateActiveSegments(50, 0, 20));
        Assert.Equal(0, DashboardSegmentedBar.CalculateActiveSegments(50, 100, 0));
    }
}
