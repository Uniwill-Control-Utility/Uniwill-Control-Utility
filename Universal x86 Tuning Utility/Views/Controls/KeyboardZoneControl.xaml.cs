using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Universal_x86_Tuning_Utility.Views.Controls
{
    /// <summary>
    /// A single key in the per-key keyboard visualizer.
    /// Shows a color swatch with the key label and supports click selection.
    /// </summary>
    public partial class KeyboardZoneControl : UserControl
    {
        public static readonly DependencyProperty ZoneIndexProperty =
            DependencyProperty.Register(nameof(ZoneIndex), typeof(int), typeof(KeyboardZoneControl),
                new PropertyMetadata(0));

        public int ZoneIndex
        {
            get => (int)GetValue(ZoneIndexProperty);
            set => SetValue(ZoneIndexProperty, value);
        }

        /// <summary>
        /// Display string for the zone index in tooltips.
        /// </summary>
        public string ZoneIndexDisplay => $"Zone {ZoneIndex}";

        public static readonly DependencyProperty LabelProperty =
            DependencyProperty.Register(nameof(Label), typeof(string), typeof(KeyboardZoneControl),
                new PropertyMetadata(string.Empty));

        public string Label
        {
            get => (string)GetValue(LabelProperty);
            set => SetValue(LabelProperty, value);
        }

        public static readonly DependencyProperty IsInvertedLShapeProperty =
            DependencyProperty.Register(nameof(IsInvertedLShape), typeof(bool), typeof(KeyboardZoneControl),
                new PropertyMetadata(false, OnIsInvertedLShapeChanged));

        private static void OnIsInvertedLShapeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is KeyboardZoneControl control)
                control.ApplyClip();
        }

        public bool IsInvertedLShape
        {
            get => (bool)GetValue(IsInvertedLShapeProperty);
            set => SetValue(IsInvertedLShapeProperty, value);
        }

        private void ApplyClip()
        {
            if (IsInvertedLShape && _button is not null)
            {
                double w = _button.ActualWidth;
                double h = _button.ActualHeight;

                // If the control hasn't been measured yet, defer to Loaded.
                if (w > 0 && h > 0)
                {
                    ApplyClipGeometry(w, h);
                }
                else
                {
                    RoutedEventHandler handler = null;
                    handler = (s, e) =>
                    {
                        _button.Loaded -= handler;
                        ApplyClipGeometry(_button.ActualWidth, _button.ActualHeight);
                    };
                    _button.Loaded += handler;
                }
            }
        }

        private void ApplyClipGeometry(double w, double h)
        {
            double indent = 8;
            double innerRadius = 2;
            double cornerRadius = 4;

            // Clip the entire button with an L-shaped geometry.
            // Three rounded corners (all convex/outward):
            //   1. bottom-left (4px, like a normal key)
            //   2. inner corner (2px, bulging outward into cutout)
            //   3. left edge bite (4px, like a normal key)
            var geometry = new System.Windows.Media.StreamGeometry();
            using (var ctx = geometry.Open())
            {
                ctx.BeginFigure(new System.Windows.Point(0, 0), true, true);
                ctx.LineTo(new System.Windows.Point(w, 0), true, false);                                       // top edge
                ctx.LineTo(new System.Windows.Point(w, h), true, false);                                       // right edge
                ctx.LineTo(new System.Windows.Point(indent + cornerRadius, h), true, false);                   // bottom edge to arc
                ctx.ArcTo(new System.Windows.Point(indent, h - cornerRadius),                                 // rounded bottom-left (convex, 4px)
                    new System.Windows.Size(cornerRadius, cornerRadius), 0, false,
                    System.Windows.Media.SweepDirection.Clockwise, true, false);
                ctx.LineTo(new System.Windows.Point(indent, h / 2 + innerRadius), true, false);                // indent up to arc
                ctx.ArcTo(new System.Windows.Point(indent - innerRadius, h / 2),                               // rounded inner corner (convex, 2px)
                    new System.Windows.Size(innerRadius, innerRadius), 0, false,
                    System.Windows.Media.SweepDirection.Counterclockwise, true, false);
                ctx.LineTo(new System.Windows.Point(cornerRadius, h / 2), true, false);                        // bite left to arc
                ctx.ArcTo(new System.Windows.Point(0, h / 2 - cornerRadius),                                 // rounded left-edge bite (convex, 4px)
                    new System.Windows.Size(cornerRadius, cornerRadius), 0, false,
                    System.Windows.Media.SweepDirection.Clockwise, true, false);
                ctx.LineTo(new System.Windows.Point(0, 0), true, false);                                       // left edge
            }
            geometry.Freeze();
            _button.Clip = geometry;
        }

        public static readonly DependencyProperty ZoneBrushProperty =
            DependencyProperty.Register(nameof(ZoneBrush), typeof(Brush), typeof(KeyboardZoneControl),
                new PropertyMetadata(Brushes.White, OnZoneBrushChanged));

        private static void OnZoneBrushChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is KeyboardZoneControl control)
                control.UpdateLabelForeground();
        }

        public Brush ZoneBrush
        {
            get => (Brush)GetValue(ZoneBrushProperty);
            set => SetValue(ZoneBrushProperty, value);
        }

        public static readonly DependencyProperty IsSelectedProperty =
            DependencyProperty.Register(nameof(IsSelected), typeof(bool), typeof(KeyboardZoneControl),
                new PropertyMetadata(false, OnIsSelectedChanged));

        private static void OnIsSelectedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is KeyboardZoneControl control)
                control._button.IsChecked = (bool)e.NewValue;
        }

        public bool IsSelected
        {
            get => (bool)GetValue(IsSelectedProperty);
            set => SetValue(IsSelectedProperty, value);
        }

        /// <summary>
        /// The RGB color value for this zone.
        /// </summary>
        public Color? ZoneColor
        {
            get => (ZoneBrush as SolidColorBrush)?.Color;
            set
            {
                if (value.HasValue)
                    ZoneBrush = new SolidColorBrush(value.Value);
            }
        }

        public static readonly DependencyProperty PickerBrushProperty =
            DependencyProperty.Register(nameof(PickerBrush), typeof(Brush), typeof(KeyboardZoneControl),
                new PropertyMetadata(Brushes.White));

        public Brush PickerBrush
        {
            get => (Brush)GetValue(PickerBrushProperty);
            set => SetValue(PickerBrushProperty, value);
        }

        public event RoutedEventHandler? Click;

        public KeyboardZoneControl()
        {
            InitializeComponent();
            _button.IsChecked = false;
        }

        private void _button_Click(object sender, RoutedEventArgs e)
        {
            Click?.Invoke(this, e);
        }

        private void UpdateLabelForeground()
        {
            // Label foreground adjustment is handled by the XAML template.
            // The TextBlock uses a fixed white foreground which works on most colors.
        }
    }
}
