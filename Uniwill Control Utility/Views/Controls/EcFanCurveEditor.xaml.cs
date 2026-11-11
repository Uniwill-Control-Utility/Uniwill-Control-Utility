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
using Wpf.Ui.Appearance;

namespace Uniwill_Control_Utility.Views.Controls
{
    /// <summary>
    /// Visual fan curve editor for the Uniwill EC with 11 vertical sliders
    /// matching the EC zone IDs 0-10. Zone 0 is disabled (always 0% duty).
    /// Zones 1-10 map to temperature thresholds from the XMG Control Center UserFanTables.
    /// The sliders are templated down to a single dot; the curve and the 25% grid
    /// lines are drawn on a canvas behind them.
    /// </summary>
    public partial class EcFanCurveEditor : UserControl
    {
        /// <summary>Edge length of the dot thumb in device-independent pixels; also the travel inset at both ends.</summary>
        private const double ThumbSize = 20;

        private const double ThumbHalf = ThumbSize / 2;

        /// <summary>
        /// Lowest duty the EC acts on (50 on its 0-200 scale). Sliders 1-10 start here;
        /// zone 0 stays at 0 as the "fan off" marker.
        /// </summary>
        private const int MinEditableDuty = 25;

        /// <summary>Used only when a WPF-UI theme brush cannot be resolved (e.g. no Application).</summary>
        private static readonly Brush GuidelineFallbackBrush = new SolidColorBrush(Colors.Gray) { Opacity = 0.3 };

        public event EventHandler? CurveChanged;

        private readonly List<Slider> _sliders = new();
        private int[] _temperatures = EcFanCurve.CpuTemperatures;
        private bool _isReadOnly;
        private DispatcherOperation? _pendingFallbackRedraw;

        /// <summary>
        /// Sets the temperature labels displayed on the X-axis and in tooltips.
        /// Call this before the control is rendered (e.g., in the page constructor).
        /// </summary>
        public int[] Temperatures
        {
            get => _temperatures;
            set
            {
                _temperatures = value;
                UpdateTemperatureLabels();
                InvalidateArrange();
            }
        }

        /// <summary>
        /// When true, sliders 1-10 are read-only (stock presets).
        /// When false, sliders are editable (Custom preset).
        /// Zone 0 is always disabled regardless of this flag.
        /// </summary>
        public bool IsReadOnly
        {
            set
            {
                _isReadOnly = value;
                UpdateDotBrushes();
            }
        }

        private void Slider_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (_isReadOnly)
            {
                e.Handled = true;
            }
        }

        public EcFanCurveEditor()
        {
            InitializeComponent();
            InitializeSliders();
            ApplyTrackHeights(GraphArea.Height);
            UpdateDotBrushes();

            // A theme switch swaps the resource dictionaries, so the cached dot fills
            // and the brushes baked into the drawing have to be resolved again.
            Loaded += (_, _) => ApplicationThemeManager.Changed += OnThemeChanged;
            Unloaded += (_, _) => ApplicationThemeManager.Changed -= OnThemeChanged;
        }

        /// <summary>
        /// Sliders stop at the 25 % floor, so their track spans only the upper
        /// (100 − floor) % of the plot: on that shorter track one duty percent is the
        /// same step as on the full scale, which puts the dot at the floor exactly on
        /// the 25 % line and leaves the dead band empty. Zone 0 keeps the full height
        /// so the OFF marker still sits on the 0 % line.
        /// </summary>
        private void ApplyTrackHeights(double plotHeight)
        {
            if (double.IsNaN(plotHeight) || plotHeight <= ThumbSize) return;

            var trackHeight = ThumbSize + ((plotHeight - ThumbSize) * (100 - MinEditableDuty) / 100.0);
            for (int i = 0; i < _sliders.Count; i++)
                _sliders[i].Height = i == 0 ? plotHeight : trackHeight;
        }

        private void InitializeSliders()
        {
            for (int i = 0; i < _temperatures.Length; i++)
            {
                var slider = new Slider
                {
                    Style = (Style)FindResource("CurvePointSliderStyle"),
                    Minimum = i == 0 ? 0 : MinEditableDuty,
                    Tag = i,
                };

                // Zone 0 is always 0 (fan off below threshold) — disable the slider.
                if (i == 0)
                {
                    slider.IsEnabled = false;
                }

                slider.PreviewMouseDown += Slider_PreviewMouseDown;
                slider.MouseMove += Slider_MouseMove;
                slider.ValueChanged += Slider_OnValueChanged;
                Grid.SetColumn(slider, i);
                _sliders.Add(slider);
                _slidersGrid.Children.Add(slider);
            }

            InvalidateArrange();
        }

        /// <summary>Dot fill per zone: disabled for OFF, dimmed while read-only, accent when editable.</summary>
        private void UpdateDotBrushes()
        {
            var accent = FindBrush("AccentFillColorDefaultBrush", Brushes.Gray);
            var secondary = FindBrush("TextFillColorSecondaryBrush", Brushes.Gray);
            var disabled = FindBrush("TextFillColorDisabledBrush", Brushes.Gray);

            for (int i = 0; i < _sliders.Count; i++)
            {
                _sliders[i].Foreground = i == 0 ? disabled : _isReadOnly ? secondary : accent;
            }
        }

        private void UpdateTemperatureLabels()
        {
            _xAxisLabels.Children.Clear();
            for (int i = 0; i < _temperatures.Length; i++)
            {
                var label = new TextBlock
                {
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Foreground = FindBrush("TextFillColorSecondaryBrush", Brushes.Gray),
                    FontSize = 10,
                    Text = i == 0 ? "OFF" : $"{_temperatures[i]}°",
                };
                Grid.SetColumn(label, i);
                _xAxisLabels.Children.Add(label);
            }
        }

        protected override Size ArrangeOverride(Size arrangeBounds)
        {
            var size = base.ArrangeOverride(arrangeBounds);
            DrawGraph();
            return size;
        }

        private void GraphArea_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            ApplyTrackHeights(e.NewSize.Height);
            DrawGraph();
        }

        private void GraphArea_MouseLeave(object sender, MouseEventArgs e)
        {
            _tooltip.IsOpen = false;
        }

        /// <summary>Populate the editor from an EcFanCurve profile.</summary>
        public void SetCurve(EcFanCurve curve)
        {
            for (int i = 0; i < _sliders.Count && i < curve.Duties.Count; i++)
            {
                // A saved curve can predate the 25 % EC floor; those duties are raised
                // to the slider minimum on load (zone 0 keeps its own minimum of 0).
                _sliders[i].Value = Math.Clamp(curve.Duties[i], (int)_sliders[i].Minimum, 100);
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

        /// <summary>Read the current slider values into an EcFanCurve.</summary>
        public EcFanCurve GetCurve()
        {
            var curve = new EcFanCurve { Name = "Custom" };
            curve.Duties.Clear();
            foreach (var slider in _sliders)
                curve.Duties.Add((int)slider.Value);
            return curve;
        }

        private void Slider_MouseMove(object sender, MouseEventArgs e)
        {
            if (sender is not Slider slider) return;
            if (slider.Template.FindName("PART_Track", slider) is not Track track) return;

            if (!track.Thumb.IsMouseOver)
            {
                _tooltip.IsOpen = false;
                return;
            }

            var index = (int)slider.Tag;
            var temp = index < _temperatures.Length ? _temperatures[index] : 0;
            var duty = (int)slider.Value;

            _tooltip.Content = index == 0
                ? $"Fan OFF (below ~52°C)"
                : $"{temp}°C → {duty}%";
            Point pointer = e.GetPosition(_canvas);
            Point thumbTopLeft = track.Thumb.TranslatePoint(new Point(), _canvas);
            var thumbBounds = new Rect(
                thumbTopLeft,
                new Size(track.Thumb.ActualWidth, track.Thumb.ActualHeight));
            _tooltip.Placement = PlacementMode.Custom;
            _tooltip.PlacementTarget = _canvas;
            _tooltip.CustomPopupPlacementCallback = (size, targetSize, _) =>
                new[] { new CustomPopupPlacement(
                    GetTooltipPosition(pointer, thumbBounds, size, targetSize, 12),
                    PopupPrimaryAxis.Vertical) };

            // Force tooltip refresh
            _tooltip.HorizontalOffset += -0.1;
            _tooltip.HorizontalOffset += +0.1;
            _tooltip.IsOpen = true;
        }

        internal static Point GetTooltipPosition(
            Point pointer,
            Rect thumbBounds,
            Size popupSize,
            Size targetSize,
            double gap)
        {
            double visibleGap = Math.Max(0, gap);
            double maximumX = Math.Max(0, targetSize.Width - popupSize.Width);
            return new Point(
                Math.Clamp(pointer.X - popupSize.Width * 0.5, 0, maximumX),
                Math.Min(pointer.Y, thumbBounds.Top) - popupSize.Height - visibleGap);
        }

        private readonly ToolTip _tooltip = new();

        private void Slider_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (sender is not Slider currentSlider) return;
            if (_sliders.Count < 2) return;

            // Only enforce monotonicity during user interaction
            if (currentSlider is { IsKeyboardFocusWithin: false, IsMouseCaptureWithin: false })
                return;

            VerifyMonotonicity(currentSlider);
            InvalidateArrange();
            CurveChanged?.Invoke(this, EventArgs.Empty);
        }

        private void VerifyMonotonicity(Slider currentSlider)
        {
            var currentIndex = _sliders.IndexOf(currentSlider);
            if (currentIndex < 0) return;
            var currentValue = currentSlider.Value;

            foreach (var s in _sliders.Take(currentIndex))
                if (s.Value > currentValue) s.Value = currentValue;

            foreach (var s in _sliders.Skip(currentIndex + 1))
                if (s.Value < currentValue) s.Value = currentValue;
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

        private void OnThemeChanged(ApplicationTheme theme, Color accent)
        {
            UpdateDotBrushes();
            DrawGraph();
        }

        /// <summary>Resolves a WPF-UI theme brush right now (so a theme switch can recolour a redraw).</summary>
        private Brush FindBrush(string key, Brush fallback) =>
            Application.Current?.TryFindResource(key) as Brush
            ?? TryFindResource(key) as Brush
            ?? fallback;

        /// <summary>
        /// Dot centre for a duty value. The thumb travels between its own half-height
        /// and height minus half-height, so 0 % and 100 % are not clipped.
        /// </summary>
        private static double DutyToY(int duty, double height) =>
            ThumbHalf + ((100 - Math.Clamp(duty, 0, 100)) / 100.0 * (height - ThumbSize));

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

            var guidelineBrush = FindBrush("DividerStrokeColorDefaultBrush", GuidelineFallbackBrush);
            var boundaryBrush = FindBrush("ControlStrokeColorDefaultBrush", GuidelineFallbackBrush);

            // The EC ignores anything under 25 %, so the dead band below the floor is
            // shaded rather than explained in text. The divider token is already
            // near-transparent, so it is used as-is.
            var floorY = DutyToY(MinEditableDuty, height);
            var deadBand = new Rectangle
            {
                Width = width,
                Height = Math.Max(0, DutyToY(0, height) - floorY),
                Fill = guidelineBrush,
                IsHitTestVisible = false
            };
            Canvas.SetTop(deadBand, floorY);
            _canvas.Children.Add(deadBand);

            foreach (var percent in new[] { 0, 25, 50, 75, 100 })
            {
                var y = DutyToY(percent, height);
                _canvas.Children.Add(new Line
                {
                    X1 = 0,
                    X2 = width,
                    Y1 = y,
                    Y2 = y,
                    // The floor is the one line that has to read as a boundary.
                    Stroke = percent == MinEditableDuty ? boundaryBrush : guidelineBrush,
                    StrokeThickness = 1
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
                    Stroke = guidelineBrush,
                    StrokeThickness = 1,
                    Opacity = 0.6
                });
            }

            var points = _sliders
                .Select(s => new Point(GetPointX(s), DutyToY((int)Math.Round(s.Value), height)))
                .ToArray();

            if (points.Length < 2) return;

            _canvas.Children.Add(new Polyline
            {
                StrokeThickness = 2,
                Stroke = FindBrush("AccentFillColorDefaultBrush", Brushes.Gray),
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
                Points = new PointCollection(points)
            });
        }
    }
}
