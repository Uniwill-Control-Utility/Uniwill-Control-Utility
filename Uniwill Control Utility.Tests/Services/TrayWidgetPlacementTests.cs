using System.Drawing;
using Uniwill_Control_Utility.Services;

namespace Uniwill_Control_Utility.Tests.Services;

public sealed class TrayWidgetPlacementTests
{
    [Theory]
    [InlineData(0, 0, 1920, 1040, 1840, 1060, 1496, 530, (int)TrayEdge.Bottom)]
    [InlineData(0, 40, 1920, 1040, 1800, 20, 1496, 50, (int)TrayEdge.Top)]
    [InlineData(40, 0, 1880, 1080, 20, 1000, 50, 570, (int)TrayEdge.Left)]
    [InlineData(0, 0, 1880, 1080, 1900, 1000, 1456, 570, (int)TrayEdge.Right)]
    public void Calculate_AnchorsInsideWorkingAreaNextToTaskbar(
        int workX,
        int workY,
        int workWidth,
        int workHeight,
        int cursorX,
        int cursorY,
        int expectedX,
        int expectedY,
        int expectedEdge)
    {
        Rectangle monitor = new(0, 0, 1920, 1080);
        Rectangle workingArea = new(workX, workY, workWidth, workHeight);

        TrayWidgetPlacementResult result = TrayWidgetPlacement.Calculate(
            monitor,
            workingArea,
            new Point(cursorX, cursorY),
            new Size(414, 500),
            gap: 10);

        Assert.Equal(new Point(expectedX, expectedY), result.Location);
        Assert.Equal((TrayEdge)expectedEdge, result.Edge);
    }

    [Fact]
    public void Calculate_PreservesNegativeCoordinatesOnSecondaryMonitor()
    {
        Rectangle monitor = new(-2560, 0, 2560, 1440);
        Rectangle workingArea = new(-2560, 0, 2560, 1400);

        TrayWidgetPlacementResult result = TrayWidgetPlacement.Calculate(
            monitor,
            workingArea,
            new Point(-100, 1420),
            new Size(414, 500),
            gap: 10);

        Assert.Equal(new Point(-424, 890), result.Location);
        Assert.Equal(TrayEdge.Bottom, result.Edge);
    }

    [Fact]
    public void Calculate_UsesNearestMonitorEdgeWhenTaskbarIsAutoHidden()
    {
        Rectangle monitor = new(0, 0, 1920, 1080);
        Rectangle visibleTaskbar = new(0, 1040, 1920, 40);

        TrayWidgetPlacementResult result = TrayWidgetPlacement.Calculate(
            monitor,
            monitor,
            new Point(1850, 1079),
            new Size(414, 500),
            gap: 10,
            taskbarBounds: visibleTaskbar);

        Assert.Equal(new Point(1496, 530), result.Location);
        Assert.Equal(TrayEdge.Bottom, result.Edge);
    }
}
