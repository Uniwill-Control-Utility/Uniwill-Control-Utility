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

    [Theory]
    [InlineData(0, 1079, 1920, 40, (int)TrayEdge.Bottom, 0, 1040, 1920, 40)]
    [InlineData(0, -39, 1920, 40, (int)TrayEdge.Top, 0, 0, 1920, 40)]
    [InlineData(-39, 0, 40, 1080, (int)TrayEdge.Left, 0, 0, 40, 1080)]
    [InlineData(1919, 0, 40, 1080, (int)TrayEdge.Right, 1880, 0, 40, 1080)]
    public void NormalizeTaskbarBounds_RestoresFullThicknessWhenTaskbarIsRetracted(
        int taskbarX,
        int taskbarY,
        int taskbarWidth,
        int taskbarHeight,
        int edge,
        int expectedX,
        int expectedY,
        int expectedWidth,
        int expectedHeight)
    {
        Rectangle normalized = TrayWidgetPlacement.NormalizeTaskbarBounds(
            new Rectangle(0, 0, 1920, 1080),
            new Rectangle(taskbarX, taskbarY, taskbarWidth, taskbarHeight),
            (TrayEdge)edge);

        Assert.Equal(
            new Rectangle(expectedX, expectedY, expectedWidth, expectedHeight),
            normalized);
    }

    [Fact]
    public void NormalizeTaskbarBounds_PreservesNegativeMonitorCoordinates()
    {
        Rectangle normalized = TrayWidgetPlacement.NormalizeTaskbarBounds(
            new Rectangle(-2560, 0, 2560, 1440),
            new Rectangle(-2560, 1439, 2560, 48),
            TrayEdge.Bottom);

        Assert.Equal(new Rectangle(-2560, 1392, 2560, 48), normalized);
    }
}
