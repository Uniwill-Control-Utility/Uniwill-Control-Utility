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
using Uniwill_Control_Utility.Scripts.GPUs.NVIDIA;
using Uniwill_Control_Utility.Scripts.Misc;
using Wpf.Ui.Appearance;

namespace Uniwill_Control_Utility.Views.Controls
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

        // WPF-UI resource key names (Fluent theme brushes)
        private const string ResAccentFillColorDefaultBrush = "AccentFillColorDefaultBrush";
        private const string ResControlStrongFillColorDefaultBrush = "ControlStrongFillColorDefaultBrush";
        private const string ResTextFillColorPrimaryBrush = "TextFillColorPrimaryBrush";
        private const string ResAccentTextFillColorPrimaryBrush = "AccentTextFillColorPrimaryBrush";
        private const string ResTextOnAccentFillColorPrimaryBrush = "TextOnAccentFillColorPrimaryBrush";
        private const string ResAccentFillColorPrimaryBrush = "AccentFillColorPrimaryBrush";
        private const string ResCardBackgroundFillColorDefaultBrush = "CardBackgroundFillColorDefaultBrush";

        // Drawing constants
        private const double GridLineOpacity = 0.12;
        private const double StockCurveOpacity = 0.125;
        private const double SelectionHighlightOpacity = 0.25;
        private const double CrosshairDashOpacity = 0.35;
        private const double ExtensionLineOpacity = 0.5;
        private const double CrosshairOpacity = 0.6;
        private const double CrosshairLabelOpacity = 0.75;
        private const double UnselectedPointOpacity = 0.8;
        private const double StockCurveStrokeThickness = 1.5;
        private const double CurveStrokeThickness = 2.0;
        private const double PointDiameter = 8.0;
        private const double PointRadius = 4.0;
        private const double AxisLabelFontSize = 10.0;
        private const double AxisLabelVerticalOffset = 5.0;
        private const double CrosshairStrokeThickness = 1.0;
        private const double CrosshairDashSize = 4.0;
        private const double CrosshairLabelFontSize = 10.0;
        private const double CrosshairLabelXOffset = 6.0;
        private const double CrosshairLabelYOffset = 4.0;
        private const int PointProximityThresholdPx = 6;
        private const int GridFreqStep = 100;
        private const int TooltipFontSize = 12;
        private const int TooltipSecondaryFontSize = 11;
        private const int TooltipVerticalOffset = 12;
        private const int TooltipMinEdgeMargin = 2;
        private const int MovementTooltipPaddingH = 6;
        private const int MovementTooltipPaddingV = 2;
        private const int MovementTooltipLine1FontSize = 12;
        private const int MovementTooltipLine2FontSize = 11;
        private const int ShiftEnterDoublePressMs = 500;
        private const int ResizeDebounceMs = 300;
        private const int LiveFreqTimerSeconds = 2;
        private const int MinLayoutDimension = 10;
        private const int YAxisLabelHeightBreakpointHigh = 680;
        private const int YAxisLabelHeightBreakpointLow = 500;
        private const int XAxisWidthBreakpointNarrow = 480;
        private const int XAxisWidthBreakpointMedium = 720;
        private const int XAxisIncrementNarrow = 100;
        private const int XAxisIncrementMedium = 50;
        private const int XAxisIncrementWide = 25;
        private const int YAxisIncrementTall = 100;
        private const int YAxisIncrementShort = 200;

        // Visual state
        private int? _dragIndex = null;
        private int? _dragStartIndex = null;       // Point clicked to start drag (null = no drag pending)
        private Point? _dragStartPos;              // Mouse pos at MouseDown (to detect movement threshold)
        private int _dragStartOffset;              // Offset of dragged point at drag-start (for delta computation)
        private Dictionary<int, int> _dragStartOffsets = new(); // Per-point offsets at drag start (for range drag)
        private bool _isShiftSelecting = false;    // True during Shift+drag selection phase (no movement yet)
        private SolidColorBrush? _fallbackBrush;

        /// <summary> Resolves the current system accent brush dynamically. </summary>
        private SolidColorBrush? accentBrush =>
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

        // Resize debounce – pause crosshair while window is being resized
        private DispatcherTimer? _resizeDebounceTimer;

        // Selection model (Phase 1)
        private int? _selectedPoint;          // Single active point
        private List<int>? _rangeSelection;    // Range of selected points (null = no range)
        private Rectangle? _rangeHighlight;    // Semi-transparent rect over range
        private int? _pendingSelectedPoint;    // Pending selection from MouseDown (applied in MouseUp based on ClickCount)
        private List<int>? _pendingRangeSelection; // Pending range selection from Shift+click
        private bool _clickStartShift;         // Shift state at MouseDown
        private bool _clickStartCtrl;          // Ctrl state at MouseDown
        private int? _clickStartNearest;       // Nearest point at MouseDown
        private Point? _clickStartPos;         // Click position at MouseDown
        private Ellipse? _placementDot;       // Zero-size anchor for tooltip placement near points

        // Undo/redo stacks
        private Stack<Dictionary<int, int>> _undoStack = new();
        private Stack<Dictionary<int, int>> _redoStack = new();

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

            // Listen for theme/accent color changes via WPF-UI's event-driven notification.
            ApplicationThemeManager.Changed += OnThemeChanged;

            // Re-draw graph when canvas size changes (window resize, popup open, etc.)
            // Hide crosshair during resize, show again after resize settles.
            _canvas.SizeChanged += (s, args) =>
            {
                if (_canvas.ActualWidth > 10 && _canvas.ActualHeight > 10)
                {
                    _liveFreqTimer?.Stop();
                    RemoveCrosshair(); // hide completely during resize

                    // Defer to ContextIdle so _yAxisLabels.ActualHeight is final
                    // before we position labels (fixes maximize/minimize misalignment).
                    Dispatcher.InvokeAsync(() =>
                    {
                        BuildAxisLabels();
                        DrawGraph();
                        UpdateSelectionVisuals();
                    }, DispatcherPriority.ContextIdle);

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
                ApplicationThemeManager.Changed -= OnThemeChanged;
                _resizeDebounceTimer?.Stop();
                RemoveCrosshair();
            };
        }

        private void OnThemeChanged(ApplicationTheme theme, Color accent)
        {
            BuildAxisLabels();
            DrawGraph();
            UpdateSelectionVisuals();
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
            // Cache stock voltages for nearest-neighbor lookup.
            int[] stockVolts = _stockCurve!.Keys.OrderBy(v => v).ToArray();

            // Debug: log stock curve range and count.
            DiagnosticLogger.LogInfo($"VfCurveEditor: stock curve loaded — {_stockCurve.Count} points, voltages {stockVolts.Min()}-{stockVolts.Max()} mV");
            DiagnosticLogger.LogInfo($"VfCurveEditor: live curve — {selected.Count} editor points, indices {selected[0].Index}-{selected.Last().Index}");

            _allPoints.Clear();
            _editorPoints.Clear();
            for (int i = 0; i < selected.Count; i++)
            {
                var p = selected[i];
                int offset = 0;
                savedOffsets?.TryGetValue(p.Index, out offset);

                // Find nearest stock voltage within ±15 mV tolerance.
                int baseFreq = FindNearestStockFreq(stockVolts, p.VoltageMv, 15, p.FrequencyMHz);

                // Debug: log first, middle, and last editor point to verify stock lookup.
                if (i == 0 || i == selected.Count / 2 || i == selected.Count - 1)
                {
                    DiagnosticLogger.LogInfo($"VfCurveEditor: idx={p.Index} volt={p.VoltageMv}mV liveFreq={p.FrequencyMHz}MHz baseFreq={baseFreq}MHz offset={offset}MHz");
                }

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

        /// <summary> Gives keyboard focus to the canvas so shortcuts (Ctrl+A, Ctrl+D) work. </summary>
        public void FocusCanvas() => _canvas.Focus();

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
        /// Finds the stock frequency for the nearest voltage within tolerance.
        /// Falls back to live frequency if no stock point is close enough.
        /// </summary>
        private int FindNearestStockFreq(int[] sortedVolts, int targetMv, int toleranceMv, int fallbackFreq)
        {
            int bestVolt = -1;
            int bestDist = int.MaxValue;
            for (int i = 0; i < sortedVolts.Length; i++)
            {
                int dist = Math.Abs(sortedVolts[i] - targetMv);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    bestVolt = sortedVolts[i];
                }
            }
            if (bestDist <= toleranceMv && _stockCurve!.TryGetValue(bestVolt, out int freq))
                return freq;
            // Fallback to live frequency — log warning so we can diagnose voltage mismatch.
            DiagnosticLogger.LogInfo($"VfCurveEditor: WARNING — stock lookup fallback for {targetMv}mV (nearest={bestVolt}mV, dist={bestDist}mV > {toleranceMv}mV), using live freq={fallbackFreq}MHz");
            return fallbackFreq;
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
            double w = _canvas.ActualWidth;

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

            var defaultColor = Application.Current.Resources["ControlStrongFillColorDefaultBrush"] as SolidColorBrush
                ?? _fallbackBrush;
            var selectedIndices = new HashSet<int>();
            if (_selectedPoint.HasValue)
                selectedIndices.Add(_selectedPoint.Value);
            if (_rangeSelection != null)
                selectedIndices.UnionWith(_rangeSelection);

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
            bool hadSelection = _rangeSelection != null;
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
                Opacity = 0.125,
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
                Stroke = defaultColor,
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
                Stroke = defaultColor,
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
                Stroke = defaultColor,
                StrokeThickness = 2,
                Opacity = 0.5,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Data = new PathGeometry { Figures = new PathFigureCollection { figure } }
            };
            _canvas.Children.Add(path);

            // Draggable dots — selected points use accent color, others use default color
            for (int i = 0; i < coords.Count; i++)
            {
                bool isSelected = selectedIndices.Contains(i);
                var ellipse = new Ellipse
                {
                    Width = 8,
                    Height = 8,
                    Fill = isSelected ? (Brush?)accentBrush : defaultColor,
                    Opacity = isSelected ? 1.0 : 0.8,
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

            // Re-add selection highlight at the bottom so it doesn't block mouse events on dots
            if (hadSelection)
            {
                if (_rangeHighlight != null && _rangeHighlight.Visibility == Visibility.Visible)
                {
                    _canvas.Children.Insert(0, _rangeHighlight);
                }
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
            if (_rangeHighlight != null && _canvas.Children.Contains(_rangeHighlight))
                _canvas.Children.Remove(_rangeHighlight);
            _rangeHighlight = null;
        }

        private void EnsureSelectionVisuals()
        {
            if (_rangeHighlight == null)
            {
                _rangeHighlight = new Rectangle
                {
                    Fill = new SolidColorBrush(accentBrush.Color) { Opacity = 0.25 },
                    IsHitTestVisible = false
                };
                _canvas.Children.Insert(0, _rangeHighlight);
            }
        }

        private void UpdateSelectionVisuals()
        {
            EnsureSelectionVisuals();

            // Keep highlight brush in sync with current accent color (theme-reactive).
            _rangeHighlight.Fill = new SolidColorBrush(accentBrush.Color) { Opacity = 0.25 };

            double w = _canvas.ActualWidth;
            double h = _canvas.ActualHeight;
            double drawW = w - RightMarginPx;
            int count = _editorPoints.Count;

            if (_rangeSelection != null && _rangeSelection.Count >= 2)
            {
                // Re-add to canvas if it was removed (e.g., by DrawGraph clearing children).
                if (!_canvas.Children.Contains(_rangeHighlight))
                    _canvas.Children.Insert(0, _rangeHighlight);

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
            }
            else
            {
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

        /// <summary> Ensures the canvas has focus on any mouse click so keyboard shortcuts work. </summary>
        private void Grid_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _canvas.Focus();
        }

        private void Canvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _canvas.Focus();

            var pos = e.GetPosition(_canvas);
            int? nearest = FindNearestPoint(pos);
            bool shift = Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift);
            bool ctrl = Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl);

            // Store click info — we defer all selection decisions to MouseLeftButtonUp
            // where e.ClickCount reliably tells us single vs double click.
            _clickStartPos = pos;
            _clickStartNearest = nearest;
            _clickStartShift = shift;
            _clickStartCtrl = ctrl;

            // Always capture mouse so we get MouseUp even for clicks in empty space.
            _canvas.CaptureMouse();

            if (nearest.HasValue)
            {
                // Click on a point — prepare drag on that point.
                bool clickedOnSelected = _rangeSelection != null && _rangeSelection.Contains(nearest.Value);

                // Store pending selection to apply in MouseUp (only if not part of a double-click).
                if (ctrl)
                {
                    // Ctrl+click on point: select all points, no highlight background.
                    _pendingSelectedPoint = 0;
                    _pendingRangeSelection = new List<int>(_editorPoints.Count);
                    for (int i = 0; i < _editorPoints.Count; i++)
                        _pendingRangeSelection.Add(i);
                    _isShiftSelecting = false;
                }
                else if (shift)
                {
                    // Shift+click on point: create range from existing selection to clicked point.
                    int anchor = _selectedPoint ?? nearest.Value;
                    int start = Math.Min(anchor, nearest.Value);
                    int end = Math.Max(anchor, nearest.Value);
                    _pendingSelectedPoint = anchor;
                    _pendingRangeSelection = new List<int>();
                    for (int i = start; i <= end; i++)
                        _pendingRangeSelection.Add(i);
                    _isShiftSelecting = true;
                }
                else if (!clickedOnSelected)
                {
                    // Plain click on a new point — single select.
                    _pendingSelectedPoint = nearest.Value;
                    _pendingRangeSelection = null;
                    _isShiftSelecting = false;
                }
                // else: clicked on already-selected point in existing range — keep current selection.

                _dragStartIndex = nearest.Value;
                _dragStartPos = pos;
                _dragStartOffset = _editorPoints[nearest.Value].OffsetMHz;

                // Capture per-point offsets for multi-point drag
                _dragStartOffsets.Clear();
                if (ctrl && _pendingRangeSelection != null)
                {
                    // Ctrl+click: capture offsets for all points
                    foreach (int idx in _pendingRangeSelection)
                        _dragStartOffsets[idx] = _editorPoints[idx].OffsetMHz;
                }
                else if (shift && _pendingRangeSelection != null)
                {
                    // Shift+click: capture offsets for the entire pending range
                    foreach (int idx in _pendingRangeSelection)
                        _dragStartOffsets[idx] = _editorPoints[idx].OffsetMHz;
                }
                else if (clickedOnSelected && _rangeSelection != null)
                {
                    foreach (int idx in _rangeSelection)
                        _dragStartOffsets[idx] = _editorPoints[idx].OffsetMHz;
                }
                else
                {
                    _dragStartOffsets[nearest.Value] = _editorPoints[nearest.Value].OffsetMHz;
                }
            }
            // else: click in empty space — no action.
        }

        private void Canvas_MouseMove(object sender, MouseEventArgs e)
        {
            var pos = e.GetPosition(_canvas);

            /* --- Shift-selection mode (selection + transition to movement) --- */
            if (_isShiftSelecting && _dragStartIndex.HasValue)
            {
                // Check if this is a vertical drag (movement) vs horizontal drag (selection).
                double dx = Math.Abs(pos.X - _dragStartPos.Value.X);
                double dy = Math.Abs(pos.Y - _dragStartPos.Value.Y);

                if (dy >= DragThresholdPx && dx < DragThresholdPx * 2)
                {
                    // Vertical movement detected — transition from selection to drag mode.
                    _isShiftSelecting = false;
                    _dragIndex = _dragStartIndex.Value;
                    SaveUndoState(); // Save state when drag begins

                    // Apply pending selection so the drag shows correct colors
                    if (_pendingSelectedPoint.HasValue)
                    {
                        _selectedPoint = _pendingSelectedPoint.Value;
                        _rangeSelection = _pendingRangeSelection;
                        _pendingSelectedPoint = null;
                        _pendingRangeSelection = null;
                        UpdateSelectionVisuals();
                        DrawGraph();
                    }
                    _canvas.Cursor = Cursors.Hand;
                    // Fall through to active drag handling below
                }
                else
                {
                    // Horizontal movement — expand range selection
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
                        DrawGraph();
                    }
                    _canvas.Cursor = Cursors.Hand;
                    return;
                }
            }

            /* --- Pending drag threshold check --- */
            if (_dragStartIndex.HasValue && _dragStartPos.HasValue && _dragIndex == null)
            {
                double dx = pos.X - _dragStartPos.Value.X;
                double dy = pos.Y - _dragStartPos.Value.Y;
                if (Math.Sqrt(dx * dx + dy * dy) >= DragThresholdPx)
                {
                    _dragIndex = _dragStartIndex.Value;
                    SaveUndoState(); // Save state when drag begins

                    // Apply pending selection now so the drag shows correct colors
                    if (_pendingSelectedPoint.HasValue)
                    {
                        _selectedPoint = _pendingSelectedPoint.Value;
                        _rangeSelection = _pendingRangeSelection;
                        _pendingSelectedPoint = null; // Only apply once
                        _pendingRangeSelection = null;
                        if (!_clickStartCtrl)
                            UpdateSelectionVisuals();
                        else
                            RemoveSelectionVisuals();
                        DrawGraph();
                    }
                }
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
            // Hide movement tooltip on release
            if (_movementTooltip != null) _movementTooltip.IsOpen = false;

            // Apply pending selection from MouseDown.
            // No double-click handling — use Ctrl+D to clear selection.
            bool dragHappened = _dragIndex.HasValue;

            // Fire CurveChanged after a drag completes so the editor window can auto-save.
            if (dragHappened)
            {
                CurveChanged?.Invoke(this, EventArgs.Empty);
            }

            if (_pendingSelectedPoint.HasValue && !dragHappened)
            {
                // If Shift+drag already built a multi-point range in MouseMove, don't overwrite it.
                bool shiftDragBuiltRange = _clickStartShift && _rangeSelection != null && _rangeSelection.Count > 1;

                if (!shiftDragBuiltRange)
                {
                    if (_clickStartCtrl)
                    {
                        // Ctrl+click: select all, no range highlight background.
                        _selectedPoint = _pendingSelectedPoint.Value;
                        _rangeSelection = _pendingRangeSelection;
                        _isShiftSelecting = false;
                        RemoveSelectionVisuals(); // remove any previous highlight
                        DrawGraph();
                    }
                    else if (_clickStartShift)
                    {
                        _selectedPoint = _pendingSelectedPoint.Value;
                        _rangeSelection = _pendingRangeSelection;
                        _isShiftSelecting = false;
                        UpdateSelectionVisuals();
                        DrawGraph();
                    }
                    else if (_clickStartNearest.HasValue)
                    {
                        _selectedPoint = _pendingSelectedPoint.Value;
                        _rangeSelection = _pendingRangeSelection;
                        _isShiftSelecting = false;
                        UpdateSelectionVisuals();
                        DrawGraph();
                    }
                    CurveChanged?.Invoke(this, EventArgs.Empty);
                }
            }

            // Clean up drag state
            _dragIndex = null;
            _dragStartIndex = null;
            _dragStartPos = null;
            _isShiftSelecting = false;
            _dragStartOffsets.Clear();
            _pendingSelectedPoint = null;
            _pendingRangeSelection = null;
            _tooltip.IsOpen = false;
            if (_canvas.IsMouseCaptured)
                _canvas.ReleaseMouseCapture();
        }

        private void Canvas_MouseLeave(object sender, MouseEventArgs e)
        {
            _tooltip.IsOpen = false;
            if (_movementTooltip != null) _movementTooltip.IsOpen = false;
            if (_dragIndex.HasValue || _dragStartIndex.HasValue)
            {
                // Fire CurveChanged before clearing state so offsets are saved.
                CurveChanged?.Invoke(this, EventArgs.Empty);
                _dragIndex = null;
                _dragStartIndex = null;
                _dragStartPos = null;
                _isShiftSelecting = false;
                _pendingSelectedPoint = null;
                _pendingRangeSelection = null;
                _canvas.ReleaseMouseCapture();
            }
        }

 
        /// <summary>
        /// Handles keyboard shortcuts for point navigation and selection.
        /// Tab: next point, Shift+Tab: previous point.
        /// Up/Down: move all selected points by FrequencyStep.
        /// Shift+Up/Down: move single selected point by FrequencyStep.
        /// Ctrl+A: select all points.
        /// Ctrl+D: clear selection.
        /// Ctrl+Z: undo, Ctrl+Y: redo.
        /// Shift+Enter (double-press): flatten range to first point's frequency.
        /// </summary>
        private void Grid_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (_editorPoints.Count == 0) return;

            bool shift = Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift);
            bool ctrl = Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl);

            if (e.Key == Key.Tab && !shift)
            {
                // Tab: select next point
                if (_selectedPoint.HasValue)
                    _selectedPoint = (_selectedPoint.Value + 1) % _editorPoints.Count;
                else
                    _selectedPoint = 0;
                _rangeSelection = null;
                DrawGraph();
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
                DrawGraph();
                UpdateSelectionVisuals();
                e.Handled = true;
            }
            else if (e.Key == Key.Up)
            {
                if (_selectedPoint.HasValue)
                {
                    if (shift)
                    {
                        // Shift+Up: move single selected point
                        SaveUndoState();
                        MovePoint(_selectedPoint.Value, FrequencyStep);
                    }
                    else
                    {
                        // Up: move all selected points (or just the active one)
                        SaveUndoState();
                        var targets = _rangeSelection != null && _rangeSelection.Count > 1 ? _rangeSelection : new List<int> { _selectedPoint.Value };
                        foreach (int idx in targets)
                            MovePoint(idx, FrequencyStep);
                    }
                    DrawGraph();
                    UpdateSelectionVisuals();
                    e.Handled = true;
                }
            }
            else if (e.Key == Key.Down)
            {
                if (_selectedPoint.HasValue)
                {
                    if (shift)
                    {
                        // Shift+Down: move single selected point
                        SaveUndoState();
                        MovePoint(_selectedPoint.Value, -FrequencyStep);
                    }
                    else
                    {
                        // Down: move all selected points (or just the active one)
                        SaveUndoState();
                        var targets = _rangeSelection != null && _rangeSelection.Count > 1 ? _rangeSelection : new List<int> { _selectedPoint.Value };
                        foreach (int idx in targets)
                            MovePoint(idx, -FrequencyStep);
                    }
                    DrawGraph();
                    UpdateSelectionVisuals();
                    e.Handled = true;
                }
            }
            else if (e.Key == Key.Z && ctrl && !shift)
            {
                // Ctrl+Z: undo
                if (Undo())
                    e.Handled = true;
            }
            else if (e.Key == Key.Y && ctrl && !shift)
            {
                // Ctrl+Y: redo
                if (Redo())
                    e.Handled = true;
            }
            else if (e.Key == Key.A && ctrl)
            {
                // Ctrl+A: select all points
                _selectedPoint = 0;
                _rangeSelection = new List<int>(_editorPoints.Count);
                for (int i = 0; i < _editorPoints.Count; i++)
                    _rangeSelection.Add(i);
                UpdateSelectionVisuals();
                DrawGraph();
                e.Handled = true;
            }
            else if (e.Key == Key.D && ctrl)
            {
                // Ctrl+D: clear selection
                ClearSelection();
                DrawGraph();
                e.Handled = true;
            }
            else if (e.Key == Key.Enter && shift)
            {
                // Shift+Enter double-press: flatten range to first point's frequency
                if (_rangeSelection != null && _rangeSelection.Count >= 2)
                {
                    var now = DateTime.UtcNow;
                    if (_lastShiftEnterTime.HasValue && (now - _lastShiftEnterTime.Value).TotalMilliseconds < 500)
                    {
                        // Double press confirmed — flatten all points to first point's frequency
                        SaveUndoState();
                        int firstIdx = _rangeSelection[0];
                        int targetFreq = _editorPoints[firstIdx].EffectiveFrequencyMHz();
                        DiagnosticLogger.LogInfo($"VfCurveEditor: flatten { _rangeSelection.Count} points to {targetFreq}MHz");
                        foreach (int idx in _rangeSelection)
                        {
                            _editorPoints[idx].OffsetMHz = targetFreq - _editorPoints[idx].BaseFrequencyMHz;
                        }
                        DrawGraph();
                        UpdateSelectionVisuals();
                        CurveChanged?.Invoke(this, EventArgs.Empty);
                        _lastShiftEnterTime = null;
                        e.Handled = true;
                        return;
                    }
                    _lastShiftEnterTime = now;
                    DiagnosticLogger.LogInfo($"VfCurveEditor: Shift+Enter — press again within 500ms to flatten {_rangeSelection.Count} points");
                    e.Handled = true;
                }
            }
        }

        private DateTime? _lastShiftEnterTime;

        private void MovePoint(int index, int freqDelta)
        {
            int newFreq = _editorPoints[index].EffectiveFrequencyMHz() + freqDelta;
            newFreq = Math.Max(MinDraggableFreq, Math.Min(YAxisMax, newFreq));
            _editorPoints[index].OffsetMHz = newFreq - _editorPoints[index].BaseFrequencyMHz;
        }

        private void SaveUndoState()
        {
            var snapshot = new Dictionary<int, int>(_editorPoints.Count);
            for (int i = 0; i < _editorPoints.Count; i++)
                snapshot[i] = _editorPoints[i].OffsetMHz;
            _undoStack.Push(snapshot);
            _redoStack.Clear(); // New action clears redo stack
        }

        private bool Undo()
        {
            if (_undoStack.Count == 0) return false;
            var redoSnapshot = new Dictionary<int, int>(_editorPoints.Count);
            for (int i = 0; i < _editorPoints.Count; i++)
                redoSnapshot[i] = _editorPoints[i].OffsetMHz;
            _redoStack.Push(redoSnapshot);

            var prev = _undoStack.Pop();
            foreach (var kvp in prev)
                _editorPoints[kvp.Key].OffsetMHz = kvp.Value;
            DrawGraph();
            UpdateSelectionVisuals();
            return true;
        }

        private bool Redo()
        {
            if (_redoStack.Count == 0) return false;
            var undoSnapshot = new Dictionary<int, int>(_editorPoints.Count);
            for (int i = 0; i < _editorPoints.Count; i++)
                undoSnapshot[i] = _editorPoints[i].OffsetMHz;
            _undoStack.Push(undoSnapshot);

            var next = _redoStack.Pop();
            foreach (var kvp in next)
                _editorPoints[kvp.Key].OffsetMHz = kvp.Value;
            DrawGraph();
            UpdateSelectionVisuals();
            return true;
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
