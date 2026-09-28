using System.Windows;
using Uniwill_Control_Utility.Views.Controls;

namespace Uniwill_Control_Utility.Tests.UI;

public sealed class EcFanCurveEditorTooltipTests
{
    [Fact]
    public void TooltipIsCentredAboveThePointerWithAVisibleGap()
    {
        var pointer = new Point(100, 80);
        var thumbBounds = new Rect(94, 80, 12, 20);
        var popupSize = new Size(60, 24);
        var graphSize = new Size(200, 180);
        Point position = EcFanCurveEditor.GetTooltipPosition(
            pointer,
            thumbBounds,
            popupSize,
            graphSize,
            12);

        Assert.Equal(70, position.X);
        Assert.Equal(44, position.Y);
        Assert.True(position.Y + popupSize.Height < pointer.Y);
    }

    [Fact]
    public void TooltipStaysAboveTheEntireThumbWhenPointerIsNearItsBottom()
    {
        var pointer = new Point(100, 100);
        var thumbBounds = new Rect(94, 80, 12, 20);
        var popupSize = new Size(60, 24);
        var graphSize = new Size(200, 180);
        const double gap = 12;

        Point position = EcFanCurveEditor.GetTooltipPosition(
            pointer,
            thumbBounds,
            popupSize,
            graphSize,
            gap);

        Assert.True(position.Y + popupSize.Height <= thumbBounds.Top - gap);
    }

    [Theory]
    [InlineData(5, 0)]
    [InlineData(195, 140)]
    public void TooltipStaysWithinTheGraphWidth(double pointerX, double expectedX)
    {
        var pointer = new Point(pointerX, 80);
        var thumbBounds = new Rect(pointerX - 6, 74, 12, 20);
        var popupSize = new Size(60, 24);
        var graphSize = new Size(200, 180);

        Point position = EcFanCurveEditor.GetTooltipPosition(
            pointer,
            thumbBounds,
            popupSize,
            graphSize,
            12);

        Assert.Equal(expectedX, position.X);
    }
}
