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
using Universal_x86_Tuning_Utility.Scripts.GPUs.NVIDIA;

namespace Universal_x86_Tuning_Utility.Views.Controls
{
    /// <summary>
    /// Draggable V-F (voltage-frequency) curve editor.
    /// X-axis: voltage (mV) fixed 600–1100, Y-axis: frequency (MHz) fixed 100–3200.
    /// 101 points evenly spaced across the axis (one per 5 mV: 600, 605, ..., 1100).
    /// Points are read from NvTuning.TryGetVfCurve(); dragging adjusts frequency offsets.
    /// Stock baseline is cached on first run and used for Reset / reference line.
    /// </summary>
    public partial class VfCurveEditor : UserControl
    {
        private readonly List<VfEditorPoint> _editorPoints = new();
        private readonly InfoTooltip _tooltip = new();

        // Stock (factory) curve snapshot — voltageMv → stockFrequencyMhz
        private Dictionary<int, int>? _stockCurve;

        // Full V-F curve (all points including hidden low-voltage ones) for crosshair voltage lookup
        private List<VfEditorPoint> _allPoints = new();

        // Fixed Y-axis range (0–3200 to always show boundary labels)
        private const int YAxisMin = 0;
        private const int YAxisMax = 3200;

        // Minimum draggable frequency (MHz) — prevents points from being dragged below this
        private const int MinDraggableFreq = 150;

        // Fixed X-axis voltage range (600–1250 mV)
        private const int XAxisMin = 600;
        private const int XAxisMax = 1250;

        // 101 points evenly spaced (5 mV increments: 600, 605, ..., 1100)
        private const int EditorPointCount = 101;

        // Frequency step for snapping (MHz)
        private const int FrequencyStep = 15;

        // 16px right-edge margin so last point isn't glued to border
        private const double RightMarginPx = 16;

        // Visual state
        private int? _dragIndex = null;
        private int? _dragStartIndex = null;       // Point clicked to start drag (null = no drag pending)
        private Point? _dragStartPos;              // Mouse pos at MouseDown (to detect movement threshold)
        private int _dragStartOffset;              // Offset of dragged point at drag-start (for delta computation)
        private Dictionary<int, int> _dragStartOffsets = new(); // Per-point offsets at drag start (for range drag)
        private bool _isShiftSelecting = false;    // True during Shift+drag selection phase (no movement yet)
        private SolidColorBrush? _fallbackBrush;

        /// <summary> Resolves the current system accent brush dynamically. </summary>
        private SolidColorBrush accentBrush =>
            Application.Current.Resources["AccentFillColorDefaultBrush"] as SolidColorBrush
            ?? _fallbackBrush;

        private bool _labelsBuilt = false;

        // Movement tooltip (shows offset + frequency during multi-point drag)
        private ToolTip? _movementTooltip;
        private TextBlock? _movementLine1;
        private TextBlock? _movementLine2;

        // Live frequency crosshair
        private DispatcherTimer? _liveFreqTimer;
        private Line? _crosshairH;
        private Line? _crosshairV;
        private TextBlock? _crosshairLabel;

        // Theme change detection
        private DispatcherTimer? _themeWatchTimer;
        private SolidColorBrush? _lastAccentBrush;
        private SolidColorBrush? _lastTextBrush;

        // Resize debounce – pause crosshair while window is being resized
        private DispatcherTimer? _resizeDebounceTimer;

        // Selection model (Phase 1)
        private int? _selectedPoint;          // Single active point
        private List<int>? _rangeSelection;    // Range of selected points (null = no range)
        private Ellipse? _selectionRing;       // Visual ring around selected point
        private Rectangle? _rangeHighlight;    // Semi-transparent rect over range
        private DateTime _lastMouseDown;       // For double-click detection
        private int? _lastClickedPoint;        // Last clicked point index for double-click
        private Ellipse? _placementDot;       // Zero-size anchor for tooltip placement near points

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

            _liveFreqTimer = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, (s, e) =>
            {
                if (!NvTuning.TryGetClocks(out var clocks)) return;
                UpdateCrosshair(clocks.CurrentCoreMHz);
            }, Dispatcher);

            // Poll for theme/accent color changes so the graph updates live without reopening the window.
            _themeWatchTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (s, e) =>
            {
                bool themeChanged = false;

                // Detect accent color change.
                var currentAccent = accentBrush;
                if (currentAccent != null && currentAccent.Color != _lastAccentBrush?.Color)
                {
                    _lastAccentBrush = currentAccent;
                    themeChanged = true;
                }

                // Detect text/theme color change (light/dark theme switch).
                var currentText = Application.Current.Resources["TextFillColorPrimaryBrush"] as SolidColorBrush;
                if (currentText != null && currentText.Color != _lastTextBrush?.Color)
                {
                    _lastTextBrush = currentText;
                    themeChanged = true;
                }

                if (themeChanged)
                {
                    BuildAxisLabels();
                    DrawGraph();
                    UpdateSelectionVisuals();
                }
            }, Dispatcher);

            // Re-draw graph when canvas size changes (window resize, popup open, etc.)
            // Hide crosshair during resize, show again after resize settles.
            _canvas.SizeChanged += (s, args) =>
            {
                if (_canvas.ActualWidth > 10 && _canvas.ActualHeight > 10)
                {
                    _liveFreqTimer?.Stop();
                    RemoveCrosshair(); // hide completely during resize
                    BuildAxisLabels();
                    DrawGraph();
                    UpdateSelectionVisuals();

                    // Debounce: restore crosshair 300ms after resize stops.
                    if (_resizeDebounceTimer == null)
                    {
                        _resizeDebounceTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(300), DispatcherPriority.Background, (_, _) =>
                        {
                            _resizeDebounceTimer!.Stop();
                            _resizeDebounceTimer = null;
                            _liveFreqTimer?.Start();
                        }, Dispatcher);
                    }
                    _resizeDebounceTimer.Stop(); // reset if already ticking
                    _resizeDebounceTimer.Start();
                }
            };

            Unloaded += (s, e) =>
            {
                _liveFreqTimer?.Stop();
                _themeWatchTimer?.Stop();
                _resizeDebounceTimer?.Stop();
                RemoveCrosshair();
            };
        }

        /// <summary>
        /// Loads the base V-F curve from GPU and populates draggable points.
        /// On first run, captures the stock curve snapshot; on subsequent runs, loads from file.
        /// </summary>
        /// <param name="savedOffsets">pointIndex → frequencyOffsetMHz (optional, null = no offsets)</param>
        public void LoadCurve(Dictionary<int, int>? savedOffsets = null)
        {
            RemoveCrosshair();
            ClearSelection();

            if (!NvTuning.TryGetVfCurve(out var points))
            {
                _editorPoints.Clear();
                _canvas.Children.Clear();
                BuildAxisLabels();
                _liveFreqTimer?.Stop();
                return;
            }

            // Load or capture stock curve snapshot.
            LoadOrCaptureStockCurve(points);

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

            // Build point list (all usable points).
            _allPoints.Clear();
            _editorPoints.Clear();
            for (int i = 0; i < selected.Count; i++)
            {
                var p = selected[i];
                int offset = 0;
                savedOffsets?.TryGetValue(p.Index, out offset);
                int baseFreq = _stockCurve!.GetValueOrDefault(p.VoltageMv, p.FrequencyMHz);

                var point = new VfEditorPoint
                {
                    Index = p.Index,
                    DisplayVoltageMv = p.VoltageMv,
                    BaseFrequencyMHz = baseFreq,
                    OffsetMHz = offset
                };
                _allPoints.Add(point);
                _editorPoints.Add(point);
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
            ClearSelection();
            DrawGraph();
            CurveChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Loads the stock curve snapshot from disk, or captures the current curve as stock on first run.
        /// </summary>
        private void LoadOrCaptureStockCurve(List<NvTuning.VfPoint> currentPoints)
        {
            var cached = VfStockCurveStore.Load();
            if (cached != null && cached.Count > 0)
            {
                // Use cached stock snapshot.
                _stockCurve = cached.ToDictionary(p => p.VoltageMv, p => p.FrequencyMhz);
            }
            else
            {
                // First run — capture current curve as stock baseline.
                _stockCurve = currentPoints
                    .Where(p => p.FrequencyMHz > 0 && p.VoltageMv > 0)
                    .ToDictionary(p => p.VoltageMv, p => p.FrequencyMHz);

                var stockPoints = _stockCurve.Select(kvp => new VfStockPoint
                {
                    Index = 0, // Not needed for lookup; voltage is the key
                    VoltageMv = kvp.Key,
                    FrequencyMhz = kvp.Value
                }).ToList();
                VfStockCurveStore.Save(stockPoints);
            }
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

            // --- Y-axis: responsive frequency legend (always shows 0 and 3200) ---
            _yAxisLabels.Children.Clear();

            // Always show boundary labels first.
            foreach (int boundaryFreq in new int[] { YAxisMin, YAxisMax })
            {
                double y = FreqToY(boundaryFreq, YAxisMin, YAxisMax, h);

                var lbl = new TextBlock
                {
                    Text = $"{boundaryFreq}",
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Foreground = (Brush?)FindResource("TextFillColorPrimaryBrush") ?? Brushes.Gray,
                    FontSize = 10
                };
                _yAxisLabels.Children.Add(lbl);
                Canvas.SetLeft(lbl, 0);
                Canvas.SetTop(lbl, y - 5);
            }

            // Choose increment based on available height for responsiveness.
            int yStep;
            if (h >= 680)
                yStep = 100;
            else if (h >= 500)
                yStep = 200;
            else
                yStep = 200;

            for (int freq = YAxisMin + yStep; freq < YAxisMax; freq += yStep)
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

            // --- X-axis: responsive voltage legend (600–1250 mV) ---
            _xAxisLabels.Children.Clear();

            // Choose increment based on available width for responsiveness.
            int xIncrement;
            if (drawW <= 480)
                xIncrement = 100;
            else if (drawW <= 720)
                xIncrement = 50;
            else
                xIncrement = 25;

            for (int v = XAxisMin; v <= XAxisMax; v += xIncrement)
            {
                double x = VoltToX(v, XAxisMin, XAxisMax, drawW);

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

            var color = accentBrush;

            double w = _canvas.ActualWidth;
            double h = _canvas.ActualHeight;
            if (w < 10 || h < 10) return;

            // Position axis lines to match drawing area
            _axisLineLeft.X2 = 0;
            _axisLineLeft.Y2 = h - 1;

            double drawW = w - RightMarginPx;
            _axisLineBottom.X1 = 0;
            _axisLineBottom.X2 = drawW;
            _axisLineBottom.Y1 = h - 1;
            _axisLineBottom.Y2 = h - 1;

            // Preserve crosshair and selection elements so they survive the clear
            bool hadCrosshair = _crosshairH != null;
            bool hadSelection = _selectionRing != null || _rangeSelection != null;
            _canvas.Children.Clear();

            int count = _editorPoints.Count;

            var coords = new List<Point>(count);
            for (int i = 0; i < count; i++)
            {
                // X: voltage-based position within the 600–1250 mV grid range.
                double x = (count == 1) ? drawW / 2
                    : VoltToX(_editorPoints[i].DisplayVoltageMv, XAxisMin, XAxisMax, drawW);
                double y = FreqToY(_editorPoints[i].EffectiveFrequencyMHz(), YAxisMin, YAxisMax, h);
                coords.Add(new Point(x, y));
            }

            // Faint grid lines matching axis increments
            var gridBrush = new SolidColorBrush(Colors.Gray) { Opacity = 0.12 };

            // Horizontal grid lines (Y-axis: 300–3300 in 100 MHz steps)
            for (int freq = YAxisMin; freq <= YAxisMax; freq += 100)
            {
                double y = FreqToY(freq, YAxisMin, YAxisMax, h);
                var hLine = new Line
                {
                    X1 = 0,
                    Y1 = y,
                    X2 = drawW,
                    Y2 = y,
                    Stroke = gridBrush,
                    StrokeThickness = 1
                };
                _canvas.Children.Add(hLine);
            }

            // Vertical grid lines (X-axis: 600–1250 with responsive increment)
            int xIncrement;
            if (drawW <= 480)
                xIncrement = 100;
            else if (drawW <= 720)
                xIncrement = 50;
            else
                xIncrement = 25;

            for (int v = XAxisMin; v <= XAxisMax; v += xIncrement)
            {
                double x = VoltToX(v, XAxisMin, XAxisMax, drawW);
                var vLine = new Line
                {
                    X1 = x,
                    Y1 = 0,
                    X2 = x,
                    Y2 = h - 1,
                    Stroke = gridBrush,
                    StrokeThickness = 1
                };
                _canvas.Children.Add(vLine);
            }

            // Stock (baseline) curve — faint, non-interactive reference line
            var stockCoords = new Point[count];
            for (int i = 0; i < count; i++)
            {
                double x = (count == 1) ? drawW / 2
                    : VoltToX(_editorPoints[i].DisplayVoltageMv, XAxisMin, XAxisMax, drawW);
                double y = FreqToY(_editorPoints[i].BaseFrequencyMHz, YAxisMin, YAxisMax, h);
                stockCoords[i] = new Point(x, y);
            }

            var stockSegments = new PathSegmentCollection();
            for (int i = 1; i < stockCoords.Length; i++)
                stockSegments.Add(new LineSegment { Point = stockCoords[i] });

            var stockFigure = new PathFigure
            {
                StartPoint = stockCoords[0],
                Segments = stockSegments
            };

            var stockPath = new Path
            {
                Stroke = (Brush?)FindResource("TextFillColorPrimaryBrush") ?? Brushes.Gray,
                StrokeThickness = 1.5,
                Opacity = 0.5,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Data = new PathGeometry { Figures = new PathFigureCollection { stockFigure } }
            };
            _canvas.Children.Add(stockPath);

            // Horizontal extension lines: left edge → first point, last point → right edge
            var extLine = new Line
            {
                X1 = 0,
                Y1 = coords[0].Y,
                X2 = coords[0].X,
                Y2 = coords[0].Y,
                Stroke = color,
                StrokeThickness = 2,
                Opacity = 0.5,
                StrokeEndLineCap = PenLineCap.Round
            };
            _canvas.Children.Add(extLine);

            var extLine2 = new Line
            {
                X1 = coords[^1].X,
                Y1 = coords[^1].Y,
                X2 = drawW,
                Y2 = coords[^1].Y,
                Stroke = color,
                StrokeThickness = 2,
                Opacity = 0.5,
                StrokeStartLineCap = PenLineCap.Round
            };
            _canvas.Children.Add(extLine2);

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

            // Re-add selection visuals on top
            if (hadSelection)
            {
                if (_rangeHighlight != null && _rangeHighlight.Visibility == Visibility.Visible)
                    _canvas.Children.Add(_rangeHighlight);
                if (_selectionRing != null && _selectionRing.Visibility == Visibility.Visible)
                    _canvas.Children.Add(_selectionRing);
            }

            // Re-add tooltip placement anchor on top
            if (_placementDot != null)
                _canvas.Children.Add(_placementDot);
        }

        /* ------------------------------------------------------------------ */
        /*  Live frequency crosshair                                          */
        /* ------------------------------------------------------------------ */

        private void UpdateCrosshair(int currentFreqMhz)
        {
            if (_allPoints.Count == 0) return;

            double w = _canvas.ActualWidth;
            double h = _canvas.ActualHeight;
            if (w < 10 || h < 10) return;

            double drawW = w - RightMarginPx;

            // Find the V-F curve point with the closest effective frequency to current.
            // Use _allPoints (includes hidden low-voltage points) so the voltage resolves correctly.
            int best = 0;
            int bestDiff = int.MaxValue;
            for (int i = 0; i < _allPoints.Count; i++)
            {
                int diff = Math.Abs(_allPoints[i].EffectiveFrequencyMHz() - currentFreqMhz);
                if (diff < bestDiff)
                {
                    bestDiff = diff;
                    best = i;
                }
            }

            // Get voltage from the matched point.
            int voltageMv = _allPoints[best].DisplayVoltageMv;

            int count = _allPoints.Count;
            double x = (count == 1) ? drawW / 2 : VoltToX(_allPoints[best].DisplayVoltageMv, XAxisMin, XAxisMax, drawW);

            double y = FreqToY(currentFreqMhz, YAxisMin, YAxisMax, h);

            // Clamp Y to visible range
            if (y < 0 || y > h || double.IsNaN(x) || double.IsNaN(y))
            {
                RemoveCrosshair();
                return;
            }

            // Ensure crosshair elements exist
            var baseBrush = accentBrush;
            var dashBrush = new SolidColorBrush(baseBrush.Color) { Opacity = 0.35 };

            if (_crosshairH == null)
            {
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
                    Foreground = new SolidColorBrush((FindResource("AccentTextFillColorPrimaryBrush") as SolidColorBrush ?? Brushes.Gray).Color) { Opacity = 0.75 },
                    TextDecorations = TextDecorations.Underline
                };
                _canvas.Children.Add(_crosshairV);
                _canvas.Children.Add(_crosshairH);
                _canvas.Children.Add(_crosshairLabel);
            }
            else
            {
                // Update brushes on each call so theme changes are reflected live.
                _crosshairH.Stroke = dashBrush;
                _crosshairV.Stroke = dashBrush;
                _crosshairLabel.Foreground = new SolidColorBrush((FindResource("AccentTextFillColorPrimaryBrush") as SolidColorBrush ?? Brushes.Gray).Color) { Opacity = 0.75 };
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

            _crosshairLabel.Text = $"{currentFreqMhz} MHz @ {voltageMv} mV";
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

        /* ------------------------------------------------------------------ */
        /*  Selection model (Phase 1)                                          */
        /* ------------------------------------------------------------------ */

        /// <summary>
        /// Clears all selection state (single point, range, visual overlays).
        /// </summary>
        private void ClearSelection()
        {
            _selectedPoint = null;
            _rangeSelection = null;
            RemoveSelectionVisuals();
        }

        private void RemoveSelectionVisuals()
        {
            if (_selectionRing != null && _canvas.Children.Contains(_selectionRing))
                _canvas.Children.Remove(_selectionRing);
            if (_rangeHighlight != null && _canvas.Children.Contains(_rangeHighlight))
                _canvas.Children.Remove(_rangeHighlight);

            _selectionRing = null;
            _rangeHighlight = null;
        }

        private void EnsureSelectionVisuals()
        {
            if (_selectionRing == null)
            {
                var color = accentBrush;
                _selectionRing = new Ellipse
                {
                    Width = 14,
                    Height = 14,
                    Stroke = color,
                    StrokeThickness = 2,
                    Fill = null,
                    IsHitTestVisible = false
                };
                _canvas.Children.Add(_selectionRing);
            }

            if (_rangeHighlight == null)
            {
                _rangeHighlight = new Rectangle
                {
                    Fill = new SolidColorBrush(accentBrush.Color) { Opacity = 0.25 },
                    IsHitTestVisible = false
                };
                _canvas.Children.Add(_rangeHighlight);
            }
        }

        private void UpdateSelectionVisuals()
        {
            EnsureSelectionVisuals();

            // Keep highlight brush in sync with current accent color (theme-reactive).
            _rangeHighlight.Fill = new SolidColorBrush(accentBrush.Color) { Opacity = 0.25 };
            _selectionRing.Stroke = accentBrush;

            double w = _canvas.ActualWidth;
            double h = _canvas.ActualHeight;
            double drawW = w - RightMarginPx;
            int count = _editorPoints.Count;

            if (_rangeSelection != null && _rangeSelection.Count >= 2)
            {
                // Re-add to canvas if it was removed (e.g., by DrawGraph clearing children).
                if (!_canvas.Children.Contains(_rangeHighlight))
                    _canvas.Children.Add(_rangeHighlight);

                // Show range highlight rect — edges centered on first/last selected points.
                int minIdx = _rangeSelection.Min();
                int maxIdx = _rangeSelection.Max();

                double x1 = PointX(minIdx, count, drawW);
                double x2 = PointX(maxIdx, count, drawW);

                // Span exactly from center of first point to center of last point.
                _rangeHighlight.Width = Math.Abs(x2 - x1);
                _rangeHighlight.Height = h;
                Canvas.SetLeft(_rangeHighlight, Math.Min(x1, x2));
                Canvas.SetTop(_rangeHighlight, 0);
                _rangeHighlight.Visibility = Visibility.Visible;
                _selectionRing.Visibility = Visibility.Collapsed;
            }
            else if (_selectedPoint.HasValue)
            {
                // Re-add to canvas if it was removed (e.g., by DrawGraph clearing children).
                if (!_canvas.Children.Contains(_selectionRing))
                    _canvas.Children.Add(_selectionRing);

                // Show single selection ring
                int i = _selectedPoint.Value;
                double x = PointX(i, count, drawW);
                double y = FreqToY(_editorPoints[i].EffectiveFrequencyMHz(), YAxisMin, YAxisMax, h);

                _selectionRing.SetValue(Canvas.LeftProperty, x - 7);
                _selectionRing.SetValue(Canvas.TopProperty, y - 7);
                _selectionRing.Visibility = Visibility.Visible;
                _rangeHighlight.Visibility = Visibility.Collapsed;
            }
            else
            {
                _selectionRing.Visibility = Visibility.Collapsed;
                _rangeHighlight.Visibility = Visibility.Collapsed;
            }
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

        /// <summary> Returns the X coordinate for a point at the given index (voltage-based). </summary>
        private double PointX(int index, int count, double drawW)
        {
            if (count <= 1) return drawW / 2;
            return VoltToX(_editorPoints[index].DisplayVoltageMv, XAxisMin, XAxisMax, drawW);
        }

        /* ------------------------------------------------------------------ */
        /*  Drag interaction + selection                                       */
        /* ------------------------------------------------------------------ */

        /// <summary> Minimum mouse movement (px) before a click becomes a drag. Prevents accidental moves when clicking to select. </summary>
        private const int DragThresholdPx = 4;

        private void Canvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _canvas.Focus();

            var pos = e.GetPosition(_canvas);
            int? nearest = FindNearestPoint(pos);
            bool shift = Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift);

            // --- Double-click detection ---
            bool isDoubleClick = (DateTime.Now - _lastMouseDown).TotalMilliseconds < 500
                                 && _lastClickedPoint == nearest;
            _lastMouseDown = DateTime.Now;
            _lastClickedPoint = nearest;

            if (isDoubleClick)
            {
                if (nearest.HasValue)
                {
                    // Double-click on point: select it only
                    _selectedPoint = nearest.Value;
                    _rangeSelection = null;
                    _isShiftSelecting = false;
                    UpdateSelectionVisuals();
                }
                else
                {
                    // Double-click empty space: clear selection
                    ClearSelection();
                }
                e.Handled = true;
                return;
            }

            if (shift)
            {
                // Shift+click anywhere: start selection phase (no movement).
                // Find point by X coordinate — works even in empty space.
                int anchor = _selectedPoint ?? FindPointAtX(pos.X).GetValueOrDefault(0);

                _selectedPoint = anchor;
                _rangeSelection = new List<int> { anchor };
                _isShiftSelecting = true;
                _dragStartIndex = anchor;
                _dragStartPos = pos;
                _dragStartOffset = _editorPoints[anchor].OffsetMHz;

                // Capture per-point offsets at selection start
                _dragStartOffsets.Clear();
                _dragStartOffsets[anchor] = _editorPoints[anchor].OffsetMHz;

                UpdateSelectionVisuals();
                _canvas.CaptureMouse();
            }
            else if (nearest.HasValue)
            {
                // Plain click on point: prepare for drag.
                // If there's an existing multi-point selection and user clicked on a selected point,
                // preserve the range selection so dragging moves all selected points together.
                bool clickedOnSelected = _rangeSelection != null && _rangeSelection.Contains(nearest.Value);

                if (!clickedOnSelected)
                {
                    // Click on a new point — single select
                    _selectedPoint = nearest.Value;
                    _rangeSelection = null;
                }
                // else: keep existing _rangeSelection intact

                _isShiftSelecting = false;
                UpdateSelectionVisuals();

                _dragStartIndex = nearest.Value;
                _dragStartPos = pos;
                _dragStartOffset = _editorPoints[nearest.Value].OffsetMHz;

                // Capture per-point offsets for multi-point drag
                if (clickedOnSelected && _rangeSelection != null)
                {
                    _dragStartOffsets.Clear();
                    foreach (int idx in _rangeSelection)
                        _dragStartOffsets[idx] = _editorPoints[idx].OffsetMHz;
                }
                else
                {
                    _dragStartOffsets.Clear();
                    _dragStartOffsets[nearest.Value] = _editorPoints[nearest.Value].OffsetMHz;
                }

                _canvas.CaptureMouse();
            }
        }

        private void Canvas_MouseMove(object sender, MouseEventArgs e)
        {
            var pos = e.GetPosition(_canvas);

            /* --- Shift-selection mode (selection only, no movement) --- */
            if (_isShiftSelecting && _dragStartIndex.HasValue)
            {
                int targetIdx = FindPointAtX(pos.X).GetValueOrDefault(-1);
                if (targetIdx >= 0)
                {
                    int anchor = _dragStartIndex.Value;
                    int start = Math.Min(anchor, targetIdx);
                    int end = Math.Max(anchor, targetIdx);
                    _rangeSelection = new List<int>();
                    for (int i = start; i <= end; i++)
                    {
                        _rangeSelection.Add(i);
                        if (!_dragStartOffsets.ContainsKey(i))
                            _dragStartOffsets[i] = _editorPoints[i].OffsetMHz;
                    }
                    _selectedPoint = anchor;
                    UpdateSelectionVisuals();
                }
                _canvas.Cursor = Cursors.Hand;
                return;
            }

            /* --- Pending drag threshold check --- */
            if (_dragStartIndex.HasValue && _dragStartPos.HasValue && _dragIndex == null)
            {
                double dx = pos.X - _dragStartPos.Value.X;
                double dy = pos.Y - _dragStartPos.Value.Y;
                if (Math.Sqrt(dx * dx + dy * dy) >= DragThresholdPx)
                    _dragIndex = _dragStartIndex.Value;
            }

            /* --- Active drag --- */
            if (_dragIndex.HasValue)
            {
                if (_rangeSelection != null && _rangeSelection.Count > 1)
                {
                    ApplyRangeDrag(pos);
                }
                else
                {
                    ApplyDrag(_dragIndex.Value, pos);
                }
            }
            else if (_dragStartIndex.HasValue)
            {
                // Mouse is held on a point but drag threshold not yet reached.
                // Hide the onHover tooltip while the point is being clicked.
                _tooltip.IsOpen = false;
                _canvas.Cursor = Cursors.Hand;
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
            if (_dragIndex.HasValue || _dragStartIndex.HasValue)
            {
                // Hide movement tooltip on release
                if (_movementTooltip != null) _movementTooltip.IsOpen = false;

                _dragIndex = null;
                _dragStartIndex = null;
                _dragStartPos = null;
                _isShiftSelecting = false;
                _dragStartOffsets.Clear();
                _canvas.ReleaseMouseCapture();
                _tooltip.IsOpen = false;

                // Re-apply selection visuals after drag ends so the highlight persists.
                UpdateSelectionVisuals();

                CurveChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        private void Canvas_MouseLeave(object sender, MouseEventArgs e)
        {
            _tooltip.IsOpen = false;
            if (_movementTooltip != null) _movementTooltip.IsOpen = false;
            if (_dragIndex.HasValue || _dragStartIndex.HasValue)
            {
                _dragIndex = null;
                _dragStartIndex = null;
                _dragStartPos = null;
                _isShiftSelecting = false;
                _canvas.ReleaseMouseCapture();
            }
        }

        /// <summary>
        /// Handles double-click on empty space to clear selection.
        /// Note: double-click on a point is handled in MouseLeftButtonDown via timestamp detection.
        /// For empty-space double-click, we use the MouseLeftButtonDown double-click path
        /// where FindNearestPoint returns null.
        /// </summary>
        private void Canvas_MouseLeftButtonDblClick(object sender, MouseButtonEventArgs e)
        {
            var pos = e.GetPosition(_canvas);
            if (FindNearestPoint(pos) == null)
            {
                // Double-click on empty space: clear selection
                ClearSelection();
                e.Handled = true;
            }
        }

        /// <summary>
        /// Handles keyboard shortcuts for point navigation and selection.
        /// Tab: next point, Shift+Tab: previous point.
        /// Only active when canvas has keyboard focus.
        /// </summary>
        private void Canvas_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (_editorPoints.Count == 0) return;

            bool shift = Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift);

            if (e.Key == Key.Tab && !shift)
            {
                // Tab: select next point
                if (_selectedPoint.HasValue)
                    _selectedPoint = (_selectedPoint.Value + 1) % _editorPoints.Count;
                else
                    _selectedPoint = 0;
                _rangeSelection = null;
                UpdateSelectionVisuals();
                e.Handled = true;
            }
            else if (e.Key == Key.Tab && shift)
            {
                // Shift+Tab: select previous point
                if (_selectedPoint.HasValue)
                    _selectedPoint = (_selectedPoint.Value - 1 + _editorPoints.Count) % _editorPoints.Count;
                else
                    _selectedPoint = _editorPoints.Count - 1;
                _rangeSelection = null;
                UpdateSelectionVisuals();
                e.Handled = true;
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
                    : VoltToX(_editorPoints[i].DisplayVoltageMv, XAxisMin, XAxisMax, drawW);
                double y = FreqToY(_editorPoints[i].EffectiveFrequencyMHz(), YAxisMin, YAxisMax, h);
                double d = Math.Sqrt((pos.X - x) * (pos.X - x) + (pos.Y - y) * (pos.Y - y));
                if (d < bestDist)
                {
                    bestDist = d;
                    best = i;
                }
            }

            return bestDist < 6 ? best : (int?)null;
        }

        /// <summary>
        /// Finds the point index whose X position is closest to the given X coordinate.
        /// Used for Shift+drag range selection — matches MSI Afterburner behavior where
        /// the vertical axis position determines which points get selected.
        /// </summary>
        private int? FindPointAtX(double x)
        {
            int count = _editorPoints.Count;
            if (count == 0) return null;

            double w = _canvas.ActualWidth;
            double drawW = w - RightMarginPx;

            int best = 0;
            double bestDist = double.MaxValue;

            for (int i = 0; i < count; i++)
            {
                double px = (count == 1) ? drawW / 2
                    : VoltToX(_editorPoints[i].DisplayVoltageMv, XAxisMin, XAxisMax, drawW);
                double d = Math.Abs(x - px);
                if (d < bestDist)
                {
                    bestDist = d;
                    best = i;
                }
            }

            return best;
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

            // Clamp to valid range (min draggable freq to prevent going below 150 MHz)
            snapped = Math.Max(MinDraggableFreq, Math.Min(YAxisMax, snapped));

            int newOffset = snapped - _editorPoints[index].BaseFrequencyMHz;

            if (_editorPoints[index].OffsetMHz != newOffset)
            {
                _editorPoints[index].OffsetMHz = newOffset;
                DrawGraph();
                UpdateSelectionVisuals();
                ShowTooltip(index, pos);
            }
        }

        /// <summary>
        /// Applies a uniform frequency delta to all points in the current range selection.
        /// Delta is computed from pixel displacement relative to drag-start position.
        /// All selected points move the same amount (in unison).
        /// </summary>
        private void ApplyRangeDrag(Point pos)
        {
            if (_rangeSelection == null || _rangeSelection.Count < 1 || _dragStartPos == null) return;

            double h = _canvas.ActualHeight;

            // Clamp Y to graph bounds so dragging outside the window doesn't keep applying offset.
            double y = Math.Clamp(pos.Y, 0, h);

            // Compute frequency delta from pixel displacement since drag start.
            // Each point's offset at drag start is stored per-point in _dragStartOffsets.
            double dy = y - _dragStartPos.Value.Y;
            int freqDelta = (int)(-dy * (YAxisMax - YAxisMin) / h);
            freqDelta = (freqDelta / FrequencyStep) * FrequencyStep;

            // Apply the same delta to all selected points from their drag-start offset.
            bool anyChanged = false;
            foreach (int idx in _rangeSelection)
            {
                int startOffset = _dragStartOffsets.GetValueOrDefault(idx, _editorPoints[idx].OffsetMHz);
                int newOffset = startOffset + freqDelta;
                int newFreq = _editorPoints[idx].BaseFrequencyMHz + newOffset;
                newFreq = Math.Max(MinDraggableFreq, Math.Min(YAxisMax, newFreq));
                newOffset = newFreq - _editorPoints[idx].BaseFrequencyMHz;

                if (_editorPoints[idx].OffsetMHz != newOffset)
                {
                    _editorPoints[idx].OffsetMHz = newOffset;
                    anyChanged = true;
                }
            }

            if (anyChanged)
            {
                DrawGraph();
                UpdateSelectionVisuals();
            }

            // Show/hide movement tooltip with offset and frequency for multi-point drags.
            if (_rangeSelection != null && _rangeSelection.Count > 1 && freqDelta != 0)
            {
                if (_movementTooltip == null)
                {
                    _movementLine1 = new TextBlock
                    {
                        FontWeight = FontWeights.SemiBold,
                        FontSize = 12,
                        Foreground = (Brush?)FindResource("TextFillColorPrimaryBrush"),
                    };
                    _movementLine2 = new TextBlock
                    {
                        FontSize = 11,
                        Foreground = (Brush?)FindResource("TextFillColorSecondaryBrush"),
                    };
                    var stackPanel = new StackPanel
                    {
                        Orientation = Orientation.Vertical,
                        Children = { _movementLine1, _movementLine2 }
                    };

                    _movementTooltip = new ToolTip
                    {
                        Content = stackPanel,
                        Placement = PlacementMode.MousePoint,
                        HasDropShadow = false,
                        StaysOpen = true,
                        Background = (Brush?)FindResource("CardBackgroundFillColorDefaultBrush"),
                        Padding = new Thickness(6, 2, 6, 2),
                    };
                }
                // First row: offset, second row: frequency in brackets.
                _movementLine1!.Text = (freqDelta >= 0 ? "+ " : "") + freqDelta;
                int firstIdx = _rangeSelection[0];
                _movementLine2!.Text = $"({_editorPoints[firstIdx].EffectiveFrequencyMHz()} MHz)";
                if (!_movementTooltip.IsOpen) _movementTooltip.IsOpen = true;
            }
            else
            {
                if (_movementTooltip != null) _movementTooltip.IsOpen = false;
            }
        }

        private void ShowTooltip(int index, Point pos)
        {
            var p = _editorPoints[index];
            _tooltip.Update(p.DisplayVoltageMv, p.EffectiveFrequencyMHz(), p.OffsetMHz);

            // Update tooltip colors live so theme changes are reflected without reopening.
            _tooltip.Background = (Brush?)Application.Current.Resources["TextOnAccentFillColorPrimaryBrush"] ?? Brushes.White;
            _tooltip.Foreground = (Brush?)Application.Current.Resources["AccentFillColorPrimaryBrush"] ?? Brushes.Gray;

            // Position tooltip above the point using custom placement.
            if (_placementDot == null)
            {
                _placementDot = new Ellipse { Width = 0, Height = 0, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
                _canvas.Children.Add(_placementDot);
            }
            Canvas.SetLeft(_placementDot, pos.X);
            Canvas.SetTop(_placementDot, pos.Y);

            _tooltip.Placement = PlacementMode.Custom;
            _tooltip.PlacementTarget = _placementDot;
            _tooltip.CustomPopupPlacementCallback = (size, targetSize, offset) =>
            {
                // Place tooltip 12px above the point, centered horizontally.
                var tooltipW = size.Width;
                var tx = pos.X - tooltipW / 2;
                var ty = pos.Y - size.Height - 12;
                // Clamp to canvas bounds
                if (tx < 2) tx = 2;
                if (ty < 2) ty = pos.Y + 12; // fallback: below the point
                return new[] { new CustomPopupPlacement(new Point(tx, ty), PopupPrimaryAxis.None) };
            };

            _tooltip.IsOpen = true;
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
            private readonly TextBlock _line2 = new() { FontSize = 11 };

            public InfoTooltip()
            {
                Content = new StackPanel
                {
                    Orientation = Orientation.Vertical,
                    Children = { _line1, _line2 }
                };
                Background = (Brush?)Application.Current.Resources["TextOnAccentFillColorPrimaryBrush"] ?? Brushes.White;
                _line1.Foreground = (Brush?)Application.Current.Resources["AccentFillColorPrimaryBrush"] ?? Brushes.Gray;
                _line2.Foreground = (Brush?)Application.Current.Resources["AccentFillColorPrimaryBrush"] ?? Brushes.Gray;
            }

            public void Update(int voltMv, int freqMhz, int offsetMhz)
            {
                _line1.Text = $"{freqMhz} MHz @ {voltMv} mV";
                _line2.Text = offsetMhz != 0 ? $"{(offsetMhz >= 0 ? "+" : "")}{offsetMhz} MHz" : "Stock";
            }
        }
    }
}
