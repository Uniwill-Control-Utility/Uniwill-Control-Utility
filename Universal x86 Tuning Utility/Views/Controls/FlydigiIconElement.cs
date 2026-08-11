using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Shapes;
using Wpf.Ui.Controls;

namespace Universal_x86_Tuning_Utility.Views.Controls;

/// <summary>
/// A Path-based IconElement for the Flydigi logo.
/// The Fill binds to the inherited Foreground so it follows theme changes.
/// Works both inside NavigationViewItem and CardExpander contexts.
/// </summary>
public sealed class FlydigiIconElement : IconElement
{
    private readonly Path _path;

    public FlydigiIconElement()
    {
        var geometry = Geometry.Parse(
            "M19.015,7.83 L0,0 L15.659,23.488 L16.777,21.251 L10.066,10.066 L15.659,11.185 " +
            "L20.287,18.706 L16.777,25.726 L22.369,40.267 L27.962,25.726 L25.609,21.02 " +
            "L23.645,24.163 L24.607,25.726 L22.369,40.267 L20.132,25.726 L29.079,11.185 " +
            "L34.673,10.066 L27.962,21.251 L29.08,23.488 L45,0 L25.985,7.829 " +
            "L22.63,14.54 L19.274,7.83 Z");

        _path = new Path
        {
            Data = geometry,
            Stretch = Stretch.Uniform,
            Width = 24,
            Height = 24,
            SnapsToDevicePixels = true
        };
    }

    protected override UIElement InitializeChildren()
    {
        // Bind to Foreground of NavigationViewItem ancestor (navigation pane).
        // When not inside a NavigationViewItem (e.g., CardExpander), use the
        // CardExpander's Foreground as fallback via a second ancestor binding.
        var navBinding = new Binding("Foreground")
        {
            RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(NavigationViewItem), 1),
            Mode = BindingMode.OneWay
        };

        var cardBinding = new Binding("Foreground")
        {
            RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(CardExpander), 1),
            Mode = BindingMode.OneWay
        };

        var multiBinding = new MultiBinding
        {
            Bindings = { navBinding, cardBinding },
            Converter = new FirstNonNullConverter()
        };

        _path.SetBinding(Path.FillProperty, multiBinding);
        return _path;
    }

    /// <summary>Returns the first non-null value from the values array.</summary>
    private sealed class FirstNonNullConverter : IMultiValueConverter
    {
        public object? Convert(object[] values, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            foreach (var v in values)
                if (v is Brush)
                    return v;
            return Brushes.Gray;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, System.Globalization.CultureInfo culture)
            => Array.Empty<object>();
    }
}
