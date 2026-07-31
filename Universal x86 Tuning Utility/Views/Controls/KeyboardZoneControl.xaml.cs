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
