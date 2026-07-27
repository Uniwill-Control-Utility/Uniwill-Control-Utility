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
using Universal_x86_Tuning_Utility.Models;

namespace Universal_x86_Tuning_Utility.Views.Controls
{
    /// <summary>
    /// Visual fan curve editor for the Uniwill EC with 11 vertical sliders
    /// matching the EC zone IDs 0-10. Zone 0 is disabled (always 0% duty).
    /// Zones 1-10 map to temperature thresholds from the XMG Control Center UserFanTables.
    /// </summary>
    public partial class EcFanCurveEditor : UserControl
    {
        public event EventHandler? CurveChanged;

        private readonly List<Slider> _sliders = new();
        private int[] _temperatures = EcFanCurve.CpuTemperatures;

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
                Dispatcher.InvokeAsync(DrawGraph, DispatcherPriority.Render);
            }
        }

        /// <summary>
        /// When true, all sliders are disabled and the curve is read-only (default presets).
        /// When false, sliders are editable (Custom preset).
        /// </summary>
        public bool IsReadOnly
        {
            set
            {
                foreach (var slider in _sliders)
                {
                    // Zone 0 is always disabled regardless.
                    if (slider.Tag is int index && index == 0)
                        continue;
                    if (value)
                    {
                        // Keep sliders enabled so tooltips work, but block interaction.
                        slider.PreviewMouseDown += ReadOnly_PreviewMouseDown;
                        slider.Foreground = Application.Current.Resources["TextFillColorSecondaryBrush"] as Brush;
                    }
                    else
                    {
                        slider.PreviewMouseDown -= ReadOnly_PreviewMouseDown;
                        slider.Foreground = null;
                    }
                }
            }
        }

        private void ReadOnly_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
        }

        public EcFanCurveEditor()
        {
            InitializeComponent();
            InitializeSliders();
        }

        private void InitializeSliders()
        {
            for (int i = 0; i < _temperatures.Length; i++)
            {
                var slider = new Slider
                {
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Orientation = Orientation.Vertical,
                    IsSnapToTickEnabled = true,
                    TickFrequency = 1,
                    Maximum = 100,
                    Minimum = 0,
                    Value = 0,
                    Tag = i,
                };

                // Zone 0 is always 0 (fan off below threshold) — disable the slider.
                if (i == 0)
                {
                    slider.IsEnabled = false;
                    slider.Foreground = Application.Current.Resources["TextFillColorDisabledBrush"] as Brush;
                }

                slider.MouseMove += Slider_MouseMove;
                slider.ValueChanged += Slider_OnValueChanged;
                Grid.SetColumn(slider, i);
                _sliders.Add(slider);
                _slidersGrid.Children.Add(slider);
            }

            Dispatcher.InvokeAsync(DrawGraph, DispatcherPriority.Render);
        }

        private void UpdateTemperatureLabels()
        {
            _xAxisLabels.Children.Clear();
            for (int i = 0; i < _temperatures.Length; i++)
            {
                var label = new TextBlock
                {
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Foreground = Application.Current.Resources["TextFillColorSecondaryBrush"] as Brush,
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

        private void GraphArea_MouseLeave(object sender, MouseEventArgs e)
        {
            _tooltip.IsOpen = false;
        }

        /// <summary>Populate the editor from an EcFanCurve profile.</summary>
        public void SetCurve(EcFanCurve curve)
        {
            for (int i = 0; i < _sliders.Count && i < curve.Duties.Count; i++)
            {
                _sliders[i].Value = curve.Duties[i];
            }
            Dispatcher.InvokeAsync(DrawGraph, DispatcherPriority.Render);
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
            _tooltip.Placement = PlacementMode.Custom;
            _tooltip.PlacementTarget = track.Thumb;
            _tooltip.CustomPopupPlacementCallback = (size, targetSize, _) =>
                new[] { new CustomPopupPlacement(
                    new((targetSize.Width - size.Width) * 0.5, -targetSize.Height - 8),
                    PopupPrimaryAxis.Vertical) };

            // Force tooltip refresh
            _tooltip.HorizontalOffset += -0.1;
            _tooltip.HorizontalOffset += +0.1;
            _tooltip.IsOpen = true;
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
            DrawGraph();
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

        private void DrawGraph()
        {
            var color = Application.Current.Resources["ControlFillColorDefaultBrush"] as SolidColorBrush;
            if (color == null) return;

            _canvas.Children.Clear();

            var points = _sliders
                .Select(GetThumbLocation)
                .Select(p => new Point(p.X, p.Y))
                .ToArray();

            if (points.Length == 0) return;

            // Line
            var segments = new PathSegmentCollection();
            foreach (var point in points.Skip(1))
                segments.Add(new LineSegment { Point = point });

            var figure = new PathFigure
            {
                StartPoint = points[0],
                Segments = segments
            };

            _canvas.Children.Add(new Path
            {
                StrokeThickness = 2,
                Stroke = color,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Data = new PathGeometry { Figures = new PathFigureCollection { figure } }
            });

            // Shaded fill
            var fillPoints = new PointCollection { new(points[0].X, _canvas.ActualHeight - 1) };
            foreach (var p in points) fillPoints.Add(p);
            fillPoints.Add(new(points[^1].X, _canvas.ActualHeight - 1));

            _canvas.Children.Add(new Polygon
            {
                Fill = color,
                Opacity = 0.25,
                Points = fillPoints
            });
        }

        private Point GetThumbLocation(Slider slider)
        {
            var ratio = slider.Value / (slider.Maximum - slider.Minimum);
            var y = slider.ActualHeight - (slider.ActualHeight * ratio);
            var x = slider.ActualWidth * 0.5;
            return slider.TranslatePoint(new(x, y), _canvas);
        }
    }
}
