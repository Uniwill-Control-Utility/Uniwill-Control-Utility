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
        private DispatcherOperation? _pendingGraphRedraw;

        // Fixed temperature points: 0, 10, 20, ..., 100 °C (11 points)
        private static readonly int[] Temperatures = { 0, 10, 20, 30, 40, 50, 60, 70, 80, 90, 100 };

        /// <summary> Fired whenever the user adjusts a slider (curve changes). </summary>
        public event EventHandler? CurveChanged;

        public FlydigiFanCurveEditor()
        {
            InitializeComponent();

            MouseLeave += (s, e) => _customToolTip.IsOpen = false;
        }

        protected override Size ArrangeOverride(Size arrangeBounds)
        {
            var size = base.ArrangeOverride(arrangeBounds);
            DrawGraph();
            return size;
        }

        /// <summary>
        /// Populates the editor with RPM values from a fan curve profile.
        /// </summary>
        public void SetCurve(FlydigiFanCurveProfile profile)
        {
            _sliders.Clear();
            _slidersGrid.Children.Clear();

            ushort minRpm = FlydigiFanCurveProfile.MinRpm;
            ushort maxRpm = FlydigiFanCurveProfile.MaxRpm;
            int steps = Steps(minRpm, maxRpm);

            for (int i = 0; i < Temperatures.Length; i++)
            {
                // Interpolate the RPM at this temperature from the profile's sparse points
                var rpm = profile.GetRpmForTemperature(Temperatures[i]);
                // Round to nearest 100 RPM step
                var rounded = RoundToStep(rpm, minRpm, maxRpm);
                // Convert to step index for the slider
                var stepIndex = StepIndex(rounded, minRpm, maxRpm);

                var slider = GenerateSlider(i, 0, steps);
                slider.Value = stepIndex;
                _sliders.Add(slider);
                _slidersGrid.Children.Add(slider);
            }

            // Force ArrangeOverride to fire so DrawGraph runs after the new sliders
            // have been measured. Using InvalidateArrange is more reliable than
            // RequestGraphRedraw here because the dispatcher-queued redraw can run
            // before layout settles, producing zero-sized thumb positions.
            InvalidateArrange();
        }

        /// <summary>
        /// Returns the edited curve as a FlydigiFanCurveProfile.
        /// Only temperatures whose RPM differs from their neighbor are kept as points.
        /// </summary>
        public FlydigiFanCurveProfile GetCurve()
        {
            ushort minRpm = FlydigiFanCurveProfile.MinRpm;
            ushort maxRpm = FlydigiFanCurveProfile.MaxRpm;

            var points = new List<FanCurvePoint>();

            for (int i = 0; i < _sliders.Count; i++)
            {
                var step = (int)_sliders[i].Value;
                var rpm = RpmFromStep(step, minRpm, maxRpm);
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

        private Slider GenerateSlider(int index, int minimum, int maximum)
        {
            var slider = new Slider
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                Orientation = Orientation.Vertical,
                IsSnapToTickEnabled = true,
                TickFrequency = 1,
                Maximum = maximum,
                Minimum = minimum,
                Tag = index,
            };

            slider.MouseMove += Slider_MouseMove;
            slider.ValueChanged += Slider_OnValueChanged;

            Grid.SetColumn(slider, index);

            return slider;
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
            var rpm = RpmFromStep((int)slider.Value, FlydigiFanCurveProfile.MinRpm, FlydigiFanCurveProfile.MaxRpm);

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
            RequestGraphRedraw();
            CurveChanged?.Invoke(this, EventArgs.Empty);
        }

        private void RequestGraphRedraw()
        {
            if (_pendingGraphRedraw is { Status: DispatcherOperationStatus.Pending })
                return;

            _pendingGraphRedraw = Dispatcher.InvokeAsync(() =>
            {
                _pendingGraphRedraw = null;
                DrawGraph();
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

        /// <summary>Number of 100-RPM steps in the range [minRpm, maxRpm]. E.g. 1300-4000 → 27 steps.</summary>
        private static int Steps(ushort minRpm, ushort maxRpm)
        {
            return (maxRpm - minRpm) / 100;
        }

        /// <summary>Rounds an RPM value to the nearest 100-RPM step boundary.</summary>
        private static ushort RoundToStep(ushort rpm, ushort minRpm, ushort maxRpm)
        {
            var offset = rpm - minRpm;
            var step = (int)Math.Round(offset / 100.0);
            step = Math.Clamp(step, 0, Steps(minRpm, maxRpm));
            return (ushort)(minRpm + step * 100);
        }

        /// <summary>Converts a step index (0..Steps) back to an RPM value.</summary>
        private static ushort RpmFromStep(int step, ushort minRpm, ushort maxRpm)
        {
            step = Math.Clamp(step, 0, Steps(minRpm, maxRpm));
            return (ushort)(minRpm + step * 100);
        }

        /// <summary>Converts an RPM value to a step index (0..Steps).</summary>
        private static int StepIndex(ushort rpm, ushort minRpm, ushort maxRpm)
        {
            var offset = rpm - minRpm;
            var step = (int)Math.Round(offset / 100.0);
            return Math.Clamp(step, 0, Steps(minRpm, maxRpm));
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
