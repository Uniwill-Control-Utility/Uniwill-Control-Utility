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
using Universal_x86_Tuning_Utility.Scripts.GPUs.NVIDIA;

namespace Universal_x86_Tuning_Utility.Views.Controls
{
    /// <summary>
    /// Draggable V-F (voltage-frequency) curve editor.
    /// X-axis: voltage (mV) fixed 600–1100, Y-axis: frequency (MHz) fixed 300–3300.
    /// 101 points evenly spaced across the axis (one per 5 mV: 600, 605, ..., 1100).
    /// Points are read from NvTuning.TryGetVfCurve(); dragging adjusts frequency offsets.
    /// Each point's X position is determined by its index (0→600mV, 100→1100mV), not its GPU voltage.
    /// </summary>
    public partial class VfCurveEditor : UserControl
    {
        private readonly List<VfEditorPoint> _editorPoints = new();
        private readonly InfoTooltip _tooltip = new();

        // Fixed Y-axis range
        private const int YAxisMin = 300;
        private const int YAxisMax = 3300;

        // Fixed X-axis voltage range
        private const int XAxisMin = 600;
        private const int XAxisMax = 1100;

        // 101 points evenly spaced (5 mV increments: 600, 605, ..., 1100)
        private const int EditorPointCount = 101;

        // Frequency step for snapping (MHz)
        private const int FrequencyStep = 15;

        // 4px right-edge margin so last point isn't glued to border
        private const double RightMarginPx = 4;

        // Visual state
        private int? _dragIndex = null;
        private SolidColorBrush? _accentBrush;
        private SolidColorBrush? _fallbackBrush;
        private bool _labelsBuilt = false;

        // Live frequency crosshair
        private DispatcherTimer? _liveFreqTimer;
        private Line? _crosshairH;
        private Line? _crosshairV;
        private TextBlock? _crosshairLabel;

        /// <summary> Fired whenever the user drags a point (curve changes). </summary>
        public event EventHandler? CurveChanged;

        public VfCurveEditor()
        {
            InitializeComponent();
            _fallbackBrush = new SolidColorBrush(Colors.Gray);
        }

        protected override void OnInitialized(EventArgs e)
        {
            base.OnInitialized(e);
            _accentBrush = Application.Current.Resources["AccentFillColorDefaultBrush"] as SolidColorBrush
                          ?? _fallbackBrush;

            _liveFreqTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (s, e) =>
            {
                if (!NvTuning.TryGetClocks(out var clocks)) return;
                UpdateCrosshair(clocks.CurrentCoreMHz);
            }, Dispatcher);

            Unloaded += (s, e) =>
            {
                _liveFreqTimer?.Stop();
                RemoveCrosshair();
            };
        }

        /// <summary>
        /// Loads the base V-F curve from GPU and populates draggable points.
        /// </summary>
        /// <param name="savedOffsets">pointIndex → frequencyOffsetMHz (optional, null = no offsets)</param>
        public void LoadCurve(Dictionary<int, int>? savedOffsets = null)
        {
            RemoveCrosshair();

            if (!NvTuning.TryGetVfCurve(out var points))
            {
                _editorPoints.Clear();
                _canvas.Children.Clear();
                BuildAxisLabels();
                _liveFreqTimer?.Stop();
                return;
            }

            // Take the last EditorPointCount populated points (covering the controllable region).
            // Points at low voltage (indices 0-~26) have fixed V-F mappings and are skipped.
            var usable = points
                .Where(p => p.FrequencyMHz > 0 && p.VoltageMv > 0)
                .OrderBy(p => p.Index)
                .ToList();

            int skip = Math.Max(0, usable.Count - EditorPointCount);
            var selected = usable.Skip(skip).ToList();

            if (selected.Count == 0)
            {
                _editorPoints.Clear();
                _canvas.Children.Clear();
                BuildAxisLabels();
                return;
            }

            _editorPoints.Clear();
            for (int i = 0; i < selected.Count; i++)
            {
                var p = selected[i];
                int offset = 0;
                savedOffsets?.TryGetValue(p.Index, out offset);

                // Assign display voltage by position: point i maps to XAxisMin + i * 5 mV
                int displayVolt = XAxisMin + i * 5;

                _editorPoints.Add(new VfEditorPoint
                {
                    Index = p.Index,
                    DisplayVoltageMv = displayVolt,
                    BaseFrequencyMHz = p.FrequencyMHz,
                    OffsetMHz = offset
                });
            }

            BuildAxisLabels();
            Dispatcher.InvokeAsync(DrawGraph, DispatcherPriority.Render);
            _liveFreqTimer?.Start();
        }

        /// <summary>
        /// Returns the current frequency offsets as pointIndex → MHz offset.
        /// Only includes points that have a non-zero offset.
        /// </summary>
        public Dictionary<int, int> GetOffsets()
        {
            return _editorPoints
                .Where(p => p.OffsetMHz != 0)
                .ToDictionary(p => p.Index, p => p.OffsetMHz);
        }

        /// <summary>
        /// Returns all offsets including zeros (for full preset serialization).
        /// </summary>
        public Dictionary<int, int> GetAllOffsets()
        {
            return _editorPoints
                .ToDictionary(p => p.Index, p => p.OffsetMHz);
        }

        /// <summary>
        /// Resets all offsets to zero (reverts to stock curve).
        /// </summary>
        public void ResetOffsets()
        {
            for (int i = 0; i < _editorPoints.Count; i++)
                _editorPoints[i].OffsetMHz = 0;
            DrawGraph();
            CurveChanged?.Invoke(this, EventArgs.Empty);
        }

        /* ------------------------------------------------------------------ */
        /*  Axis labels                                                       */
        /* ------------------------------------------------------------------ */

        private void BuildAxisLabels()
        {
            double h = _yAxisLabels.ActualHeight;
            double w = _xAxisLabels.ActualWidth;

            // Defer one-shot if layout dimensions aren't available yet (control just became Visible).
            if (h < 10 || w < 10)
            {
                if (!_labelsBuilt)
                {
                    _labelsBuilt = true;
                    Dispatcher.InvokeAsync(() =>
                    {
                        _labelsBuilt = false;
                        BuildAxisLabels();
                    }, DispatcherPriority.Loaded);
                }
                return;
            }

            double drawW = w - RightMarginPx;

            // --- Y-axis: fixed 300–3300 in 100 MHz steps, pixel-precise via FreqToY ---
            _yAxisLabels.Children.Clear();

            int yStep = 100;
            for (int freq = YAxisMin; freq <= YAxisMax; freq += yStep)
            {
                double y = FreqToY(freq, YAxisMin, YAxisMax, h);

                var lbl = new TextBlock
                {
                    Text = $"{freq}",
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Foreground = (Brush?)FindResource("TextFillColorPrimaryBrush") ?? Brushes.Gray,
                    FontSize = 10
                };
                _yAxisLabels.Children.Add(lbl);
                Canvas.SetLeft(lbl, 0);
                Canvas.SetTop(lbl, y - 5);
            }

            // --- X-axis: fixed 600–1100 mV in 50 mV steps, index-based spacing ---
            _xAxisLabels.Children.Clear();

            int xStep = 50;
            int labelCount = (XAxisMax - XAxisMin) / xStep + 1; // 11 labels: 600..1100
            for (int i = 0; i < labelCount; i++)
            {
                int v = XAxisMin + i * xStep;
                // Same index-based spacing as data points: position i/(labelCount-1) across width
                double x = (labelCount == 1) ? drawW / 2
                    : ((double)i / (labelCount - 1)) * drawW;

                var lbl = new TextBlock
                {
                    Text = $"{v}",
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Foreground = (Brush?)FindResource("TextFillColorPrimaryBrush") ?? Brushes.Gray,
                    FontSize = 10
                };
                _xAxisLabels.Children.Add(lbl);
                Canvas.SetLeft(lbl, x);
                Canvas.SetTop(lbl, 0);
            }
        }

        /* ------------------------------------------------------------------ */
        /*  Drawing                                                           */
        /* ------------------------------------------------------------------ */

        private void DrawGraph()
        {
            if (_editorPoints.Count < 2) return;

            var color = _accentBrush ?? _fallbackBrush;

            // Preserve crosshair elements so they survive the clear
            bool hadCrosshair = _crosshairH != null;
            _canvas.Children.Clear();

            double w = _canvas.ActualWidth;
            double h = _canvas.ActualHeight;
            if (w < 10 || h < 10) return;

            // Effective drawing width (subtract right margin)
            double drawW = w - RightMarginPx;
            int count = _editorPoints.Count;

            var coords = new List<Point>(count);
            for (int i = 0; i < count; i++)
            {
                // X: even index-based spacing across the full width (not voltage-based)
                double x = (count == 1) ? drawW / 2
                    : ((double)i / (count - 1)) * drawW;
                double y = FreqToY(_editorPoints[i].EffectiveFrequencyMHz(), YAxisMin, YAxisMax, h);
                coords.Add(new Point(x, y));
            }

            // Shaded fill under the curve
            var fillPoints = new PointCollection
            {
                new Point(coords[0].X, h - 1)
            };
            foreach (var c in coords)
                fillPoints.Add(c);
            fillPoints.Add(new Point(coords[^1].X, h - 1));

            var polygon = new Polygon
            {
                Fill = color,
                Opacity = 0.2,
                Points = fillPoints
            };
            _canvas.Children.Add(polygon);

            // Line
            var segments = new PathSegmentCollection();
            for (int i = 1; i < coords.Count; i++)
                segments.Add(new LineSegment { Point = coords[i] });

            var figure = new PathFigure
            {
                StartPoint = coords[0],
                Segments = segments
            };

            var path = new Path
            {
                Stroke = color,
                StrokeThickness = 2,
                Opacity = 0.5,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Data = new PathGeometry { Figures = new PathFigureCollection { figure } }
            };
            _canvas.Children.Add(path);

            // Draggable dots
            for (int i = 0; i < coords.Count; i++)
            {
                var ellipse = new Ellipse
                {
                    Width = 8,
                    Height = 8,
                    Fill = color,
                    Stroke = (Brush?)FindResource("TextOnAccentFillColorPrimaryBrush") ?? Brushes.White,
                    StrokeThickness = 1.5,
                    Tag = i
                };
                ellipse.SetValue(Canvas.LeftProperty, coords[i].X - 4);
                ellipse.SetValue(Canvas.TopProperty, coords[i].Y - 4);
                _canvas.Children.Add(ellipse);
            }

            // Re-add crosshair on top if it existed before the clear
            if (hadCrosshair)
            {
                _canvas.Children.Add(_crosshairV!);
                _canvas.Children.Add(_crosshairH!);
                _canvas.Children.Add(_crosshairLabel!);
            }
        }

        /* ------------------------------------------------------------------ */
        /*  Live frequency crosshair                                          */
        /* ------------------------------------------------------------------ */

        private void UpdateCrosshair(int currentFreqMhz)
        {
            if (_editorPoints.Count == 0) return;

            double w = _canvas.ActualWidth;
            double h = _canvas.ActualHeight;
            if (w < 10 || h < 10) return;

            double drawW = w - RightMarginPx;

            // Find the V-F curve point with the closest effective frequency to current
            int best = 0;
            int bestDiff = int.MaxValue;
            for (int i = 0; i < _editorPoints.Count; i++)
            {
                int diff = Math.Abs(_editorPoints[i].EffectiveFrequencyMHz() - currentFreqMhz);
                if (diff < bestDiff)
                {
                    bestDiff = diff;
                    best = i;
                }
            }

            int count = _editorPoints.Count;
            double x = (count == 1) ? drawW / 2 : ((double)best / (count - 1)) * drawW;
            double y = FreqToY(currentFreqMhz, YAxisMin, YAxisMax, h);

            // Clamp Y to visible range
            if (y < 0 || y > h || double.IsNaN(x) || double.IsNaN(y))
            {
                RemoveCrosshair();
                return;
            }

            // Ensure crosshair elements exist
            if (_crosshairH == null)
            {
                var baseBrush = _accentBrush ?? _fallbackBrush;
                var dashBrush = new SolidColorBrush(baseBrush.Color) { Opacity = 0.35 };

                _crosshairH = new Line
                {
                    Y1 = 0, Y2 = 0,
                    Stroke = dashBrush,
                    StrokeThickness = 1,
                    StrokeDashArray = new DoubleCollection { 4, 4 },
                    Opacity = 0.6
                };
                _crosshairV = new Line
                {
                    X1 = 0, X2 = 0,
                    Stroke = dashBrush,
                    StrokeThickness = 1,
                    StrokeDashArray = new DoubleCollection { 4, 4 },
                    Opacity = 0.6
                };
                _crosshairLabel = new TextBlock
                {
                    FontSize = 10,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = dashBrush,
                    TextDecorations = TextDecorations.Underline
                };
                _canvas.Children.Add(_crosshairV);
                _canvas.Children.Add(_crosshairH);
                _canvas.Children.Add(_crosshairLabel);
            }

            // Horizontal line at current frequency
            _crosshairH.X1 = 0;
            _crosshairH.X2 = drawW;
            _crosshairH.Y1 = y;
            _crosshairH.Y2 = y;

            // Vertical line at nearest curve point
            _crosshairV.X1 = x;
            _crosshairV.X2 = x;
            _crosshairV.Y1 = 0;
            _crosshairV.Y2 = h - 1;

            // Label at top of vertical crosshair — ensure elements are in canvas first
            if (!_canvas.Children.Contains(_crosshairV))
                _canvas.Children.Add(_crosshairV);
            if (!_canvas.Children.Contains(_crosshairH))
                _canvas.Children.Add(_crosshairH);
            if (!_canvas.Children.Contains(_crosshairLabel))
                _canvas.Children.Add(_crosshairLabel);

            _crosshairLabel.Text = $"{currentFreqMhz} MHz";
            Canvas.SetLeft(_crosshairLabel, x + 6);
            Canvas.SetTop(_crosshairLabel, 4.0);
        }

        private void RemoveCrosshair()
        {
            if (_crosshairH != null && _canvas.Children.Contains(_crosshairH))
                _canvas.Children.Remove(_crosshairH);
            if (_crosshairV != null && _canvas.Children.Contains(_crosshairV))
                _canvas.Children.Remove(_crosshairV);
            if (_crosshairLabel != null && _canvas.Children.Contains(_crosshairLabel))
                _canvas.Children.Remove(_crosshairLabel);

            _crosshairH = null;
            _crosshairV = null;
            _crosshairLabel = null;
        }

        private static double VoltToX(int volt, int min, int max, double width)
        {
            if (max == min) return width / 2;
            return ((double)(volt - min) / (max - min)) * width;
        }

        private static double FreqToY(int freq, int min, int max, double height)
        {
            if (max == min) return height / 2;
            return height - ((double)(freq - min) / (max - min)) * height;
        }

        /* ------------------------------------------------------------------ */
        /*  Drag interaction                                                  */
        /* ------------------------------------------------------------------ */

        private void Canvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            var pos = e.GetPosition(_canvas);
            int? nearest = FindNearestPoint(pos);
            if (nearest.HasValue)
            {
                _dragIndex = nearest.Value;
                _canvas.CaptureMouse();
                ShowTooltip(_dragIndex.Value, pos);
            }
        }

        private void Canvas_MouseMove(object sender, MouseEventArgs e)
        {
            var pos = e.GetPosition(_canvas);
            if (_dragIndex.HasValue)
            {
                ApplyDrag(_dragIndex.Value, pos);
            }
            else
            {
                var nearest = FindNearestPoint(pos);
                if (nearest.HasValue)
                {
                    _canvas.Cursor = Cursors.Hand;
                    ShowTooltip(nearest.Value, pos);
                }
                else
                {
                    _canvas.Cursor = Cursors.Arrow;
                    _tooltip.IsOpen = false;
                }
            }
        }

        private void Canvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_dragIndex.HasValue)
            {
                _dragIndex = null;
                _canvas.ReleaseMouseCapture();
                _tooltip.IsOpen = false;
                CurveChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        private void Canvas_MouseLeave(object sender, MouseEventArgs e)
        {
            _tooltip.IsOpen = false;
            if (_dragIndex.HasValue)
            {
                _dragIndex = null;
                _canvas.ReleaseMouseCapture();
            }
        }

        private int? FindNearestPoint(Point pos)
        {
            int count = _editorPoints.Count;
            if (count == 0) return null;

            double w = _canvas.ActualWidth;
            double h = _canvas.ActualHeight;
            double drawW = w - RightMarginPx;

            int best = 0;
            double bestDist = double.MaxValue;

            for (int i = 0; i < count; i++)
            {
                double x = (count == 1) ? drawW / 2
                    : ((double)i / (count - 1)) * drawW;
                double y = FreqToY(_editorPoints[i].EffectiveFrequencyMHz(), YAxisMin, YAxisMax, h);
                double d = Math.Sqrt((pos.X - x) * (pos.X - x) + (pos.Y - y) * (pos.Y - y));
                if (d < bestDist)
                {
                    bestDist = d;
                    best = i;
                }
            }

            return bestDist < 20 ? best : (int?)null;
        }

        private void ApplyDrag(int index, Point pos)
        {
            if (index < 0 || index >= _editorPoints.Count) return;

            double h = _canvas.ActualHeight;

            // Clamp Y to graph bounds
            double y = Math.Clamp(pos.Y, 0, h - 1);

            // Convert Y back to frequency using fixed range
            int rawFreq = (int)((1 - y / h) * (YAxisMax - YAxisMin) + YAxisMin);

            // Snap to step
            int snapped = (rawFreq / FrequencyStep) * FrequencyStep;

            // Clamp to valid range (within Y-axis bounds)
            snapped = Math.Max(YAxisMin, Math.Min(YAxisMax, snapped));

            int newOffset = snapped - _editorPoints[index].BaseFrequencyMHz;

            if (_editorPoints[index].OffsetMHz != newOffset)
            {
                _editorPoints[index].OffsetMHz = newOffset;
                DrawGraph();
                ShowTooltip(index, pos);
            }
        }

        private void ShowTooltip(int index, Point pos)
        {
            var p = _editorPoints[index];
            _tooltip.Update(p.DisplayVoltageMv, p.EffectiveFrequencyMHz(), p.OffsetMHz);

            _tooltip.Placement = PlacementMode.Custom;
            _tooltip.PlacementTarget = _canvas;
            _tooltip.CustomPopupPlacementCallback = TooltipPlacement;

            _tooltip.HorizontalOffset += -0.1;
            _tooltip.HorizontalOffset += +0.1;

            _tooltip.IsOpen = true;
        }

        private static CustomPopupPlacement[] TooltipPlacement(Size size, Size targetSize, Point offset)
        {
            double x = Math.Clamp(offset.X - size.Width / 2, 0, targetSize.Width - size.Width);
            double y = Math.Clamp(offset.Y - size.Height - 16, 0, targetSize.Height - size.Height);
            return new[] { new CustomPopupPlacement(new Point(x, y), PopupPrimaryAxis.Vertical) };
        }

        /* ------------------------------------------------------------------ */
        /*  Data model                                                        */
        /* ------------------------------------------------------------------ */

        private class VfEditorPoint
        {
            public int Index;              // GPU V-F point index (for applying offsets)
            public int DisplayVoltageMv;   // Display voltage: XAxisMin + position * 5
            public int BaseFrequencyMHz;
            public int OffsetMHz;

            public int EffectiveFrequencyMHz() => BaseFrequencyMHz + OffsetMHz;
        }

        /* ------------------------------------------------------------------ */
        /*  Tooltip                                                           */
        /* ------------------------------------------------------------------ */

        private class InfoTooltip : ToolTip
        {
            private readonly TextBlock _line1 = new() { FontWeight = FontWeights.Medium };
            private readonly TextBlock _line2 = new() { Foreground = Brushes.Gray, FontSize = 11 };

            public InfoTooltip()
            {
                Content = new StackPanel
                {
                    Orientation = Orientation.Vertical,
                    Children = { _line1, _line2 }
                };
            }

            public void Update(int voltMv, int freqMhz, int offsetMhz)
            {
                _line1.Text = $"{freqMhz} MHz @ {voltMv} mV";
                _line2.Text = offsetMhz != 0 ? $"{(offsetMhz >= 0 ? "+" : "")}{offsetMhz} MHz" : "Stock";
            }
        }
    }
}
