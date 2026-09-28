using System;
using System.Windows;
using System.Windows.Media;

namespace Uniwill_Control_Utility.Views.Controls;

public sealed class DashboardSegmentedBar : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value),
        typeof(double),
        typeof(DashboardSegmentedBar),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum),
        typeof(double),
        typeof(DashboardSegmentedBar),
        new FrameworkPropertyMetadata(100d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SegmentsProperty = DependencyProperty.Register(
        nameof(Segments),
        typeof(int),
        typeof(DashboardSegmentedBar),
        new FrameworkPropertyMetadata(20, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SegmentGapProperty = DependencyProperty.Register(
        nameof(SegmentGap),
        typeof(double),
        typeof(DashboardSegmentedBar),
        new FrameworkPropertyMetadata(3d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill),
        typeof(Brush),
        typeof(DashboardSegmentedBar),
        new FrameworkPropertyMetadata(Brushes.DodgerBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackProperty = DependencyProperty.Register(
        nameof(Track),
        typeof(Brush),
        typeof(DashboardSegmentedBar),
        new FrameworkPropertyMetadata(
            new SolidColorBrush(Color.FromArgb(54, 160, 160, 160)),
            FrameworkPropertyMetadataOptions.AffectsRender));

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public double Maximum
    {
        get => (double)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public int Segments
    {
        get => (int)GetValue(SegmentsProperty);
        set => SetValue(SegmentsProperty, value);
    }

    public double SegmentGap
    {
        get => (double)GetValue(SegmentGapProperty);
        set => SetValue(SegmentGapProperty, value);
    }

    public Brush Fill
    {
        get => (Brush)GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    public Brush Track
    {
        get => (Brush)GetValue(TrackProperty);
        set => SetValue(TrackProperty, value);
    }

    internal static int CalculateActiveSegments(double value, double maximum, int segments)
    {
        if (!double.IsFinite(value) || !double.IsFinite(maximum) || maximum <= 0 || segments <= 0 || value <= 0)
            return 0;

        double normalized = Math.Clamp(value / maximum, 0, 1);
        return Math.Clamp((int)Math.Ceiling(normalized * segments), 0, segments);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);

        if (ActualWidth <= 0 || ActualHeight <= 0 || Segments <= 0)
            return;

        double gap = Math.Max(0, SegmentGap);
        double width = (ActualWidth - gap * (Segments - 1)) / Segments;
        if (width <= 0)
            return;

        int activeSegments = CalculateActiveSegments(Value, Maximum, Segments);
        double radius = Math.Min(2, ActualHeight / 2);

        for (int index = 0; index < Segments; index++)
        {
            double x = index * (width + gap);
            Brush brush = index < activeSegments ? Fill : Track;
            drawingContext.DrawRoundedRectangle(
                brush,
                null,
                new Rect(x, 0, width, ActualHeight),
                radius,
                radius);
        }
    }
}
