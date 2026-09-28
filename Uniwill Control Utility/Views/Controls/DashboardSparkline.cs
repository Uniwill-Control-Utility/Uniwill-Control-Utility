using System;
using System.Windows;
using System.Windows.Media;

namespace Uniwill_Control_Utility.Views.Controls;

public sealed class DashboardSparkline : FrameworkElement
{
    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke),
        typeof(Brush),
        typeof(DashboardSparkline),
        new FrameworkPropertyMetadata(Brushes.DodgerBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register(
        nameof(Minimum),
        typeof(double),
        typeof(DashboardSparkline),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum),
        typeof(double),
        typeof(DashboardSparkline),
        new FrameworkPropertyMetadata(100d, FrameworkPropertyMetadataOptions.AffectsRender));

    private readonly DashboardMetricSeries _series = new(60);

    public Brush Stroke
    {
        get => (Brush)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public double Minimum
    {
        get => (double)GetValue(MinimumProperty);
        set => SetValue(MinimumProperty, value);
    }

    public double Maximum
    {
        get => (double)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public void AddSample(double value)
    {
        _series.Add(value);
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);

        double width = ActualWidth;
        double height = ActualHeight;
        if (width <= 0 || height <= 0)
            return;

        var guideBrush = new SolidColorBrush(Color.FromArgb(36, 128, 128, 128));
        guideBrush.Freeze();
        var guidePen = new Pen(guideBrush, 1);
        guidePen.Freeze();

        drawingContext.DrawLine(guidePen, new Point(0, height * 0.25), new Point(width, height * 0.25));
        drawingContext.DrawLine(guidePen, new Point(0, height * 0.5), new Point(width, height * 0.5));
        drawingContext.DrawLine(guidePen, new Point(0, height * 0.75), new Point(width, height * 0.75));

        if (_series.Samples.Count < 2 || Maximum <= Minimum)
            return;

        var geometry = new StreamGeometry();
        using (StreamGeometryContext context = geometry.Open())
        {
            bool figureStarted = false;
            for (int index = 0; index < _series.Samples.Count; index++)
            {
                double sample = _series.Samples[index];
                if (!double.IsFinite(sample))
                {
                    figureStarted = false;
                    continue;
                }

                double x = index * width / Math.Max(1, _series.Samples.Count - 1);
                double normalized = Math.Clamp(
                    (sample - Minimum) / (Maximum - Minimum),
                    0,
                    1);
                double y = height - (normalized * height);

                if (!figureStarted)
                {
                    context.BeginFigure(new Point(x, y), false, false);
                    figureStarted = true;
                }
                else
                    context.LineTo(new Point(x, y), true, false);
            }
        }

        geometry.Freeze();
        drawingContext.DrawGeometry(null, new Pen(Stroke, 1.6), geometry);
    }
}
