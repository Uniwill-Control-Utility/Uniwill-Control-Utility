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
using Uniwill_Control_Utility.Services;
using Wpf.Ui.Appearance;

namespace Uniwill_Control_Utility.Views.Controls
{
    /// <summary>
    /// Visual fan curve editor with vertical sliders at fixed temperature intervals (0-100°C).
    /// Draws a connected line graph on a canvas with shaded fill underneath.
    /// Adapted from LenovoLegionToolkit's FanCurveControl design.
    ///
    /// The sliders are templated down to a dot, and the curve, area fill and rails are
    /// drawn on a canvas behind them — the same presentation as <see cref="EcFanCurveEditor"/>.
    /// </summary>
    public partial class FlydigiFanCurveEditor : UserControl
    {
        /// <summary>Edge length of the dot thumb in device-independent pixels; also the travel inset at both ends.</summary>
        private const double ThumbSize = 20;

        private const double ThumbHalf = ThumbSize / 2;

        /// <summary>Used only when a WPF-UI theme brush cannot be resolved (e.g. no Application).</summary>
        private static readonly Brush GuidelineFallbackBrush = new SolidColorBrush(Colors.Gray) { Opacity = 0.3 };

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

            // "Fan Speed" is the existing catalog key (all 26 shipped cultures), so the
            // bottom axis label needs no new translation entry.
            FanSpeedAxisLabel.Content = LocalizationService.Get("Fan Speed");

            // A theme switch swaps the resource dictionaries, so the brushes baked into
            // the drawing have to be resolved again.
            Loaded += (_, _) => ApplicationThemeManager.Changed += OnThemeChanged;
            Unloaded += (_, _) => ApplicationThemeManager.Changed -= OnThemeChanged;
        }

        private void InitializeSliders()
        {
            for (int i = 0; i < Temperatures.Length; i++)
            {
                var slider = new Slider
                {
                    Style = (Style)FindResource("CurvePointSliderStyle"),
                    Tag = i,
                };

                slider.MouseMove += Slider_MouseMove;
                slider.ValueChanged += Slider_OnValueChanged;

                Grid.SetColumn(slider, i);
                _sliders.Add(slider);
                _slidersGrid.Children.Add(slider);
            }
        }

        private void GraphArea_MouseLeave(object sender, MouseEventArgs e)
        {
            _customToolTip.IsOpen = false;
        }

        private void OnThemeChanged(ApplicationTheme theme, Color accent) => DrawGraph();

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

            // Clear the old curve immediately so it doesn't persist while the thumbs
            // move, then redraw at Background priority once layout has settled.
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

        /// <summary>Resolves a WPF-UI theme brush right now (so a theme switch can recolour a redraw).</summary>
        private Brush FindBrush(string key, Brush fallback) =>
            Application.Current?.TryFindResource(key) as Brush
            ?? TryFindResource(key) as Brush
            ?? fallback;

        /// <summary>
        /// Dot centre for a percentage. The thumb travels between its own half-height
        /// and height minus half-height, so 0 % and 100 % are not clipped.
        /// </summary>
        private static double PercentToY(double percent, double height) =>
            ThumbHalf + ((100 - Math.Clamp(percent, 0, 100)) / 100.0 * (height - ThumbSize));

        /// <summary>
        /// The 0 % line: the 1 px axis border at the bottom of the graph area. Sits one
        /// pixel above the plot edge so a fill that ends here meets the border instead
        /// of covering it.
        /// </summary>
        private static double AxisY(double height) => height - 1;

        private double GetPointX(Slider slider) =>
            slider.TranslatePoint(new Point(slider.ActualWidth * 0.5, 0), _canvas).X;

        private void DrawGraph()
        {
            // Skip if sliders aren't measured yet — avoid clearing the canvas
            // with (0,0) points during early layout passes.
            if (!(_sliders.Count > 0 && _sliders.All(s => s.ActualWidth > 0 && s.ActualHeight > 0)))
            {
                ScheduleFallbackRedraw();
                return;
            }

            double width = _canvas.ActualWidth;
            double height = _canvas.ActualHeight;
            if (width <= 0 || height <= 0)
            {
                ScheduleFallbackRedraw();
                return;
            }

            _canvas.Children.Clear();

            var points = _sliders
                .Select(s => new Point(GetPointX(s), PercentToY(s.Value, height)))
                .ToArray();

            if (points.Length == 0)
                return;

            var curveBrush = FindBrush("AccentFillColorDefaultBrush", Brushes.Gray);
            var railBrush = FindBrush("DividerStrokeColorDefaultBrush", GuidelineFallbackBrush);

            // Area under the curve down to the temperature axis, before the rails and
            // the polyline so both stay legible on top of it.
            if (points.Length > 1)
            {
                var baseline = AxisY(height);
                var areaPoints = new List<Point>(points.Length + 2)
                {
                    new(points[0].X, baseline)
                };
                areaPoints.AddRange(points);
                areaPoints.Add(new Point(points[^1].X, baseline));

                _canvas.Children.Add(new Polygon
                {
                    Fill = curveBrush,
                    Opacity = 0.18,
                    Points = new PointCollection(areaPoints),
                    IsHitTestVisible = false
                });
            }

            // One rail per zone, at the x the dots and the curve use.
            foreach (var slider in _sliders)
            {
                var x = GetPointX(slider);
                _canvas.Children.Add(new Line
                {
                    X1 = x,
                    X2 = x,
                    Y1 = ThumbHalf,
                    Y2 = height - ThumbHalf,
                    Stroke = railBrush,
                    StrokeThickness = 1,
                    Opacity = 0.6
                });
            }

            _canvas.Children.Add(new Polyline
            {
                StrokeThickness = 2,
                Stroke = curveBrush,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
                Points = new PointCollection(points)
            });
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
