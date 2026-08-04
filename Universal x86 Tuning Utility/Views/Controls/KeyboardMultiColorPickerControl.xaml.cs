using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Wpf.Ui.Controls;
using Color = System.Windows.Media.Color;

namespace Universal_x86_Tuning_Utility.Views.Controls;

/// <summary>
/// Multi-color picker for keyboard multi-color effects. Shows a fixed number of color
/// swatches — the user can edit each color but cannot add or remove swatches.
/// Call SetColors(colors, count) to set how many swatches to display (4 or 7).
/// </summary>
public partial class KeyboardMultiColorPickerControl : UserControl
{
    private const int MaxColors = 7;

    // Default rainbow palette
    private static readonly Color[] s_defaultColors =
    {
        Color.FromRgb(255, 0, 0),     // Red
        Color.FromRgb(255, 123, 0),   // Orange
        Color.FromRgb(255, 183, 0),   // Yellow
        Color.FromRgb(0, 255, 0),     // Green
        Color.FromRgb(0, 255, 255),   // Cyan
        Color.FromRgb(0, 0, 255),     // Blue
        Color.FromRgb(139, 0, 255),   // Purple
    };

    /// <summary>All selected colors in order.</summary>
    public List<Color> Colors
    {
        get => ColorChipsPanel.Children.OfType<ColorPickerControl>()
            .Select(c => c.SelectedColor)
            .ToList();
    }

    public event EventHandler? ColorsChanged;

    public KeyboardMultiColorPickerControl()
    {
        InitializeComponent();

        // Initialize with default 7-color rainbow palette
        SetColors(s_defaultColors, MaxColors);
    }

    /* ------------------------------------------------------------------ */
    /*  Public API                                                         */
    /* ------------------------------------------------------------------ */

    /// <summary>Load colors into the control with a fixed swatch count (4 or 7).</summary>
    public void SetColors(IEnumerable<Color> colors, int count)
    {
        count = Math.Clamp(count, 1, MaxColors);
        var list = colors.ToList();

        // Pad with defaults if fewer than requested count
        while (list.Count < count)
            list.Add(s_defaultColors[list.Count % s_defaultColors.Length]);

        // Trim if more
        if (list.Count > count)
            list = list.Take(count).ToList();

        ColorChipsPanel.Children.Clear();

        foreach (var c in list)
            AddColorInternal(c);
    }

    /* ------------------------------------------------------------------ */
    /*  Internal                                                           */
    /* ------------------------------------------------------------------ */

    private ColorPickerControl AddColorInternal(Color color)
    {
        var picker = new ColorPickerControl
        {
            SelectedColor = color,
            Margin = new Thickness(0, 0, 4, 0)
        };
        picker.ColorChangedDelayed += (s, e) => ColorsChanged?.Invoke(this, EventArgs.Empty);
        ColorChipsPanel.Children.Add(picker);
        return picker;
    }
}
