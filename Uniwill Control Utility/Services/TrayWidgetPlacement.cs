using System;
using System.Drawing;
using System.Linq;

namespace Uniwill_Control_Utility.Services;

internal enum TrayEdge
{
    Left,
    Top,
    Right,
    Bottom
}

internal readonly record struct TrayWidgetPlacementResult(Point Location, TrayEdge Edge);

internal static class TrayWidgetPlacement
{
    internal static TrayWidgetPlacementResult Calculate(
        Rectangle monitorBounds,
        Rectangle workingArea,
        Point cursor,
        Size windowSize,
        int gap,
        Rectangle? taskbarBounds = null)
    {
        int visibleGap = Math.Max(0, gap);
        TrayEdge edge;
        if (taskbarBounds is Rectangle taskbar && taskbar.IntersectsWith(monitorBounds))
        {
            edge = DetectTaskbarEdge(monitorBounds, taskbar);
            // Derive the working area from the taskbar bounds directly rather
            // than relying on Screen.WorkingArea, which may be stale after a
            // taskbar position change (Windows only updates it on WM_SETTINGCHANGE).
            workingArea = ExcludeTaskbar(monitorBounds, taskbar, edge);
        }
        else
        {
            edge = DetectTaskbarEdge(monitorBounds, workingArea, cursor);
        }

        int minimumX = workingArea.Left + visibleGap;
        int maximumX = Math.Max(minimumX, workingArea.Right - windowSize.Width - visibleGap);
        int minimumY = workingArea.Top + visibleGap;
        int maximumY = Math.Max(minimumY, workingArea.Bottom - windowSize.Height - visibleGap);

        int centeredX = Math.Clamp(cursor.X - windowSize.Width / 2, minimumX, maximumX);
        int centeredY = Math.Clamp(cursor.Y - windowSize.Height / 2, minimumY, maximumY);

        Point location = edge switch
        {
            TrayEdge.Left => new Point(minimumX, centeredY),
            TrayEdge.Top => new Point(centeredX, minimumY),
            TrayEdge.Right => new Point(maximumX, centeredY),
            _ => new Point(centeredX, maximumY)
        };

        return new TrayWidgetPlacementResult(location, edge);
    }

    internal static Rectangle NormalizeTaskbarBounds(
        Rectangle monitorBounds,
        Rectangle taskbarBounds,
        TrayEdge edge)
    {
        int horizontalThickness = Math.Clamp(taskbarBounds.Height, 1, monitorBounds.Height);
        int verticalThickness = Math.Clamp(taskbarBounds.Width, 1, monitorBounds.Width);

        return edge switch
        {
            TrayEdge.Left => new Rectangle(
                monitorBounds.Left,
                monitorBounds.Top,
                verticalThickness,
                monitorBounds.Height),
            TrayEdge.Top => new Rectangle(
                monitorBounds.Left,
                monitorBounds.Top,
                monitorBounds.Width,
                horizontalThickness),
            TrayEdge.Right => new Rectangle(
                monitorBounds.Right - verticalThickness,
                monitorBounds.Top,
                verticalThickness,
                monitorBounds.Height),
            _ => new Rectangle(
                monitorBounds.Left,
                monitorBounds.Bottom - horizontalThickness,
                monitorBounds.Width,
                horizontalThickness)
        };
    }

    internal static Rectangle NormalizeTaskbarBounds(
        Rectangle monitorBounds,
        Rectangle taskbarBounds) =>
        NormalizeTaskbarBounds(
            monitorBounds,
            taskbarBounds,
            DetectTaskbarEdge(monitorBounds, taskbarBounds));

    private static TrayEdge DetectTaskbarEdge(Rectangle monitorBounds, Rectangle taskbarBounds)
    {
        bool horizontal = taskbarBounds.Width >= taskbarBounds.Height;
        if (horizontal)
        {
            int distanceToTop = Math.Abs(taskbarBounds.Top - monitorBounds.Top);
            int distanceToBottom = Math.Abs(monitorBounds.Bottom - taskbarBounds.Bottom);
            return distanceToTop <= distanceToBottom ? TrayEdge.Top : TrayEdge.Bottom;
        }

        int distanceToLeft = Math.Abs(taskbarBounds.Left - monitorBounds.Left);
        int distanceToRight = Math.Abs(monitorBounds.Right - taskbarBounds.Right);
        return distanceToLeft <= distanceToRight ? TrayEdge.Left : TrayEdge.Right;
    }

    private static Rectangle ExcludeTaskbar(
        Rectangle workingArea,
        Rectangle taskbarBounds,
        TrayEdge edge)
    {
        return edge switch
        {
            TrayEdge.Left => Rectangle.FromLTRB(
                Math.Max(workingArea.Left, taskbarBounds.Right),
                workingArea.Top,
                workingArea.Right,
                workingArea.Bottom),
            TrayEdge.Top => Rectangle.FromLTRB(
                workingArea.Left,
                Math.Max(workingArea.Top, taskbarBounds.Bottom),
                workingArea.Right,
                workingArea.Bottom),
            TrayEdge.Right => Rectangle.FromLTRB(
                workingArea.Left,
                workingArea.Top,
                Math.Min(workingArea.Right, taskbarBounds.Left),
                workingArea.Bottom),
            _ => Rectangle.FromLTRB(
                workingArea.Left,
                workingArea.Top,
                workingArea.Right,
                Math.Min(workingArea.Bottom, taskbarBounds.Top))
        };
    }

    private static TrayEdge DetectTaskbarEdge(
        Rectangle monitorBounds,
        Rectangle workingArea,
        Point cursor)
    {
        (TrayEdge Edge, int Inset)[] insets =
        {
            (TrayEdge.Left, Math.Max(0, workingArea.Left - monitorBounds.Left)),
            (TrayEdge.Top, Math.Max(0, workingArea.Top - monitorBounds.Top)),
            (TrayEdge.Right, Math.Max(0, monitorBounds.Right - workingArea.Right)),
            (TrayEdge.Bottom, Math.Max(0, monitorBounds.Bottom - workingArea.Bottom))
        };

        (TrayEdge Edge, int Inset) reservedEdge = insets.MaxBy(item => item.Inset);
        if (reservedEdge.Inset > 0)
            return reservedEdge.Edge;

        (TrayEdge Edge, int Distance)[] distances =
        {
            (TrayEdge.Left, Math.Abs(cursor.X - monitorBounds.Left)),
            (TrayEdge.Top, Math.Abs(cursor.Y - monitorBounds.Top)),
            (TrayEdge.Right, Math.Abs(monitorBounds.Right - cursor.X)),
            (TrayEdge.Bottom, Math.Abs(monitorBounds.Bottom - cursor.Y))
        };

        return distances.MinBy(item => item.Distance).Edge;
    }
}
