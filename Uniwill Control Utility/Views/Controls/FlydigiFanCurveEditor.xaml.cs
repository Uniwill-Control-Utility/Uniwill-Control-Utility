using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Uniwill_Control_Utility.Models;

namespace Uniwill_Control_Utility.Views.Controls
{
    /// <summary>
     /// Visual fan curve editor with vertical sliders at fixed temperature intervals (0-100°C).
     /// Draws a connected line graph on a canvas with shaded fill underneath.
     /// Adapted from LenovoLegionToolkit's FanCurveControl design.
     /// </summary>
    public partial class FlydigiFanCurveEditor : UserControl
    {
        private readonly List<Slider> _sliders = new();
        private readonly InfoTooltip _customToolTip = new();
        private DispatcherOperation? _pendingFallbackRedraw;

        // Fixed temperature points: 0, 10, 20, ..., 100 °C (11 points)
        private static readonly int[] Temperatures = { 0, 10, 20, 30, 40, 50, 60, 70, 80, 90, 100 };

        /// <summary> Fired whenever the user adjusts a slider (curve changes). </summary>
        public event EventHandler? CurveChanged;

        public FlydigiFanCurveEditor()
        {
            InitializeComponent();
            InitializeSliders();
        }

        private void InitializeSliders()
        {
            for (int i = 0; i < Temperatures.Length; i++)
            {
                var slider = GenerateSlider(i);
                _sliders.Add(slider);
                _slidersGrid.Children.Add(slider);
            }
        }

        private void GraphArea_MouseLeave(object sender, MouseEventArgs e)
        {
            _customToolTip.IsOpen = false;
        }

        protected override Size ArrangeOverride(Size arrangeBounds)
        {
            var size = base.ArrangeOverride(arrangeBounds);
            DrawGraph();
            return size;
        }

        /// <summary>
        /// Populates the editor with RPM values from a fan curve profile.
        /// Sliders use a 0-100 percentage range for smooth dragging; RPM is derived on read.
        /// </summary>
        public void SetCurve(FlydigiFanCurveProfile profile)
        {
            ushort minRpm = FlydigiFanCurveProfile.MinRpm;
            ushort maxRpm = FlydigiFanCurveProfile.MaxRpm;

            for (int i = 0; i < _sliders.Count && i < Temperatures.Length; i++)
            {
                var rpm = profile.GetRpmForTemperature(Temperatures[i]);
                var percent = RpmToPercent(rpm, minRpm, maxRpm);
                _sliders[i].Value = percent;
            }

            // Clear the old curve line immediately so it doesn't persist while
            // slider thumbs animate to their new positions. Then defer the redraw
            // to Background priority so the layout system has fully processed all
            // value changes and thumbs are at their final positions before DrawGraph
            // samples them. Using InvalidateArrange directly would fire ArrangeOverride
            // before the thumbs reposition, drawing a line at the stale curve shape.
            _canvas.Children.Clear();

            Dispatcher.InvokeAsync(() => InvalidateArrange(), DispatcherPriority.Background);
        }

        /// <summary>
        /// Returns the edited curve as a FlydigiFanCurveProfile.
        /// Slider percentage values are mapped back to RPM, rounded to the nearest 100 RPM.
        /// </summary>
        public FlydigiFanCurveProfile GetCurve()
        {
            ushort minRpm = FlydigiFanCurveProfile.MinRpm;
            ushort maxRpm = FlydigiFanCurveProfile.MaxRpm;

            var points = new List<FanCurvePoint>();

            for (int i = 0; i < _sliders.Count; i++)
            {
                var percent = _sliders[i].Value;
                var rpm = PercentToRpm(percent, minRpm, maxRpm);
                points.Add(new FanCurvePoint
                {
                    Temperature = Temperatures[i],
                    Rpm = rpm
                });
            }

            return new FlydigiFanCurveProfile
            {
                Name = "Custom",
                Id = Guid.NewGuid().ToString(),
                Points = points
            };
        }

        private Slider GenerateSlider(int index)
        {
            // Use a 0-100 percentage range so each 1% step is ~2px of travel,
            // matching EcFanCurveEditor and giving smooth drag feel.
            // The percentage is mapped to RPM in GetCurve() / SetCurve().
            var slider = new Slider
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                Orientation = Orientation.Vertical,
                IsSnapToTickEnabled = true,
                TickFrequency = 1,
                Maximum = 100,
                Minimum = 0,
                Tag = index,
            };

            slider.PreviewMouseDown += Slider_PreviewMouseDown;
            slider.MouseMove += Slider_MouseMove;
            slider.ValueChanged += Slider_OnValueChanged;

            Grid.SetColumn(slider, index);

            return slider;
        }

        private void Slider_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            // No-op: ensures the slider receives the mouse event before the template
            // processes it. Unlike EcFanCurveEditor, this editor is always editable.
        }

        private void Slider_MouseMove(object sender, MouseEventArgs e)
        {
            if (sender is not Slider slider)
                return;

            if (slider.Template.FindName("PART_Track", slider) is not Track track)
                return;

            if (!track.Thumb.IsMouseOver)
            {
                _customToolTip.IsOpen = false;
                return;
            }

            var index = (int)slider.Tag;
            var temp = Temperatures[index];
            var rpm = PercentToRpm(slider.Value, FlydigiFanCurveProfile.MinRpm, FlydigiFanCurveProfile.MaxRpm);

            _customToolTip.Update(temp, rpm);

            Point pointer = e.GetPosition(_canvas);
            Point thumbTopLeft = track.Thumb.TranslatePoint(new Point(), _canvas);
            var thumbBounds = new Rect(
                thumbTopLeft,
                new Size(track.Thumb.ActualWidth, track.Thumb.ActualHeight));
            _customToolTip.Placement = PlacementMode.Custom;
            _customToolTip.PlacementTarget = _canvas;
            _customToolTip.CustomPopupPlacementCallback = (size, targetSize, _) =>
                new[] { new CustomPopupPlacement(
                    EcFanCurveEditor.GetTooltipPosition(pointer, thumbBounds, size, targetSize, 12),
                    PopupPrimaryAxis.Vertical) };

            // Force tooltip refresh
            _customToolTip.HorizontalOffset += -0.1;
            _customToolTip.HorizontalOffset += +0.1;

            _customToolTip.IsOpen = true;
        }

        private void Slider_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_sliders.Count < 10)
                return;

            if (sender is not Slider currentSlider)
                return;

            // Only apply monotonicity when the user is interacting
            if (currentSlider is { IsKeyboardFocusWithin: false, IsMouseCaptureWithin: false })
                return;

            VerifyMonotonicity(currentSlider);
            InvalidateArrange();
            CurveChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Schedules a Background-priority redraw as a safety net when layout hasn't settled.</summary>
        private void ScheduleFallbackRedraw()
        {
            if (_pendingFallbackRedraw is { Status: DispatcherOperationStatus.Pending })
                return;
            _pendingFallbackRedraw = Dispatcher.InvokeAsync(() =>
            {
                _pendingFallbackRedraw = null;
                InvalidateArrange();
            }, DispatcherPriority.Background);
        }

        /// <summary>
        /// Enforces monotonicity: raising a slider raises all lower sliders;
        /// lowering a slider lowers all higher sliders.
        /// </summary>
        private void VerifyMonotonicity(Slider currentSlider)
        {
            var currentIndex = _sliders.IndexOf(currentSlider);
            if (currentIndex < 0)
                return;

            var currentValue = currentSlider.Value;
            var slidersBefore = _sliders.Take(currentIndex);
            var slidersAfter = _sliders.Skip(currentIndex + 1);

            foreach (var slider in slidersBefore)
            {
                if (slider.Value > currentValue)
                    slider.Value = currentValue;
            }

            foreach (var slider in slidersAfter)
            {
                if (slider.Value < currentValue)
                    slider.Value = currentValue;
            }
        }

        private void DrawGraph()
        {
            // Try multiple brush sources: app resources, then Foreground, then fallback.
            SolidColorBrush color = (Application.Current.Resources["ControlFillColorDefaultBrush"] as SolidColorBrush)
                ?? (this.FindResource("ControlFillColorDefaultBrush") as SolidColorBrush)
                ?? (this.Foreground as SolidColorBrush)
                ?? new SolidColorBrush(Colors.Gray);
            if (color == null)
                return;

            // Check if sliders are actually measured yet. If not, skip drawing to
            // avoid clearing the canvas with (0,0) points during early layout passes.
            bool allMeasured = _sliders.Count > 0 && _sliders.All(s => s.ActualWidth > 0 && s.ActualHeight > 0);
            if (!allMeasured)
            {
                ScheduleFallbackRedraw();
                return;
            }

            _canvas.Children.Clear();

            var points = _sliders
                .Select(GetThumbLocation)
                .Select(p => new Point(p.X, p.Y))
                .ToArray();

            if (points.Length == 0)
                return;

            // Line
            var pathSegmentCollection = new PathSegmentCollection();
            foreach (var point in points.Skip(1))
                pathSegmentCollection.Add(new LineSegment { Point = point });

            var pathFigure = new PathFigure
            {
                StartPoint = points[0],
                Segments = pathSegmentCollection
            };

            var path = new Path
            {
                StrokeThickness = 2,
                Stroke = color,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Data = new PathGeometry { Figures = new PathFigureCollection { pathFigure } }
            };
            _canvas.Children.Add(path);

            // Shaded fill
            var pointCollection = new PointCollection { new(points[0].X, _canvas.ActualHeight - 1) };
            foreach (var point in points)
                pointCollection.Add(point);
            pointCollection.Add(new(points[^1].X, _canvas.ActualHeight - 1));

            var polygon = new Polygon
            {
                Fill = color,
                Opacity = 0.3,
                Points = pointCollection
            };
            _canvas.Children.Add(polygon);
        }

        private Point GetThumbLocation(Slider slider)
        {
            slider.ApplyTemplate();
            if (slider.Template.FindName("PART_Track", slider) is Track track &&
                track.Thumb is { ActualWidth: > 0, ActualHeight: > 0 } thumb)
            {
                return thumb.TranslatePoint(
                    new Point(thumb.ActualWidth * 0.5, thumb.ActualHeight * 0.5),
                    _canvas);
            }

            var ratio = slider.Value / (slider.Maximum - slider.Minimum);
            var y = slider.ActualHeight - (slider.ActualHeight * ratio);
            var x = slider.ActualWidth * 0.5;
            return slider.TranslatePoint(new(x, y), _canvas);
        }

        /// <summary>Converts an RPM value to a 0-100 percentage for the slider.</summary>
        private static double RpmToPercent(ushort rpm, ushort minRpm, ushort maxRpm)
        {
            var range = maxRpm - minRpm;
            if (range == 0) return 0;
            var percent = (rpm - minRpm) * 100.0 / range;
            return Math.Clamp(percent, 0, 100);
        }

        /// <summary>Converts a 0-100 percentage back to an RPM value, rounded to the nearest 100 RPM.</summary>
        private static ushort PercentToRpm(double percent, ushort minRpm, ushort maxRpm)
        {
            percent = Math.Clamp(percent, 0, 100);
            var range = maxRpm - minRpm;
            var rpm = minRpm + (int)Math.Round(percent * range / 100.0);
            // Round to nearest 100 RPM step.
            rpm = (int)Math.Round(rpm / 100.0) * 100;
            return (ushort)Math.Clamp(rpm, minRpm, maxRpm);
        }

        /* ------------------------------------------------------------------ */
        /*  Tooltip                                                            */
        /* ------------------------------------------------------------------ */

        private sealed class InfoTooltip : ToolTip
        {
            private readonly TextBlock _tempLabel = new()
            {
                FontWeight = FontWeights.Medium,
                Margin = new(0, 0, 8, 0)
            };
            private readonly TextBlock _rpmLabel = new();

            public InfoTooltip()
            {
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Children = { _tempLabel, _rpmLabel }
                };
            }

            public void Update(int temp, ushort rpm)
            {
                _tempLabel.Text = $"{temp}°C";
                _rpmLabel.Text = $"{rpm} RPM";
            }
        }
    }
}
