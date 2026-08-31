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

namespace Uniwill_Control_Utility.Views.Controls;

/// <summary>
/// Multi-color picker for Rotation RGB mode.
/// Supports 1-6 colors displayed as ColorPickerControl swatches with add/remove buttons.
/// Each swatch opens its own color picker popup when clicked.
/// Based on LenovoLegionToolkit.WPF.Controls.MultiColorPickerControl.
/// </summary>
public partial class MultiColorPickerControl : UserControl
{
    private const int MaxColors = 6;

    private readonly Random _randomColor = new();
    private Border? _removeButton;
    private bool _isDeleteMode;

    /// <summary>All selected colors in order.</summary>
    public List<Color> Colors
    {
        get => ColorChipsPanel.Children.OfType<ColorPickerControl>()
            .Select(c => c.SelectedColor)
            .ToList();
    }

    public event EventHandler? ColorsChanged;

    public MultiColorPickerControl()
    {
        InitializeComponent();

        // Start with one default color (white)
        AddColor(System.Windows.Media.Colors.White);
    }

    /* ------------------------------------------------------------------ */
    /*  Public API                                                         */
    /* ------------------------------------------------------------------ */

    /// <summary>Load a list of colors (1-6) into the control.</summary>
    public void SetColors(IEnumerable<Color> colors)
    {
        var list = colors.ToList();
        if (list.Count == 0)
            list.Add(System.Windows.Media.Colors.White);

        ColorChipsPanel.Children.Clear();

        foreach (var c in list.Take(MaxColors))
            AddColorInternal(c);

        UpdateAddRemoveButtons();
    }

    /* ------------------------------------------------------------------ */
    /*  Add / Remove                                                       */
    /* ------------------------------------------------------------------ */

    private void AddColor(Color color)
    {
        if (ColorChipsPanel.Children.OfType<ColorPickerControl>().Count() >= MaxColors)
            return;

        AddColorInternal(color);
        UpdateAddRemoveButtons();
        ColorsChanged?.Invoke(this, EventArgs.Empty);
    }

    private ColorPickerControl AddColorInternal(Color color)
    {
        var picker = new ColorPickerControl
        {
            SelectedColor = color,
            Margin = new Thickness(0, 0, 4, 0)
        };
        picker.ColorChangedDelayed += (s, e) => ColorsChanged?.Invoke(this, EventArgs.Empty);
        picker.DeleteRequested += (s, e) =>
        {
            if (s is ColorPickerControl cp)
                RemoveColor(cp);
            // Stay in deletion mode so user can remove more
        };
        ColorChipsPanel.Children.Add(picker);
        return picker;
    }

    private void RemoveColor(ColorPickerControl picker)
    {
        ColorChipsPanel.Children.Remove(picker);
        UpdateAddRemoveButtons();
        ColorsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void EnterDeleteMode()
    {
        _isDeleteMode = true;
        foreach (var chip in ColorChipsPanel.Children.OfType<ColorPickerControl>())
            chip.SetDeleteMode(true);
        // Hide both add and remove buttons during deletion mode
        UpdateAddRemoveButtons();

        // Attach handler to root window so clicks anywhere outside the chip row exit deletion mode
        var root = Window.GetWindow(this);
        if (root != null)
            root.PreviewMouseDown += OnRootPreviewMouseDown;
    }

    private void ExitDeleteMode()
    {
        _isDeleteMode = false;
        foreach (var chip in ColorChipsPanel.Children.OfType<ColorPickerControl>())
            chip.SetDeleteMode(false);
        // Restore buttons
        UpdateAddRemoveButtons();

        // Detach root handler
        var root = Window.GetWindow(this);
        if (root != null)
            root.PreviewMouseDown -= OnRootPreviewMouseDown;
    }

    /* ------------------------------------------------------------------ */
    /*  Add/Remove button management                                       */
    /* ------------------------------------------------------------------ */

    private void UpdateAddRemoveButtons()
    {
        // Remove existing add/remove buttons
        var toRemove = ColorChipsPanel.Children.OfType<Border>()
            .Where(b => b.Tag is string tag && (tag == "AddBtn" || tag == "RemoveBtn"))
            .ToList();
        foreach (var b in toRemove)
            ColorChipsPanel.Children.Remove(b);

        int chipCount = ColorChipsPanel.Children.OfType<ColorPickerControl>().Count();

        // Add button shown when under max and NOT in deletion mode
        if (chipCount < MaxColors && !_isDeleteMode)
        {
            var addBtn = CreateIconButton(SymbolPlus, "AddBtn", OnAddClicked);
            ColorChipsPanel.Children.Add(addBtn);
        }

        // Remove button shown when 2+ colors and NOT in deletion mode
        if (chipCount >= 2 && !_isDeleteMode)
        {
            _removeButton = CreateIconButton(SymbolMinus, "RemoveBtn", OnRemoveClicked);
            ColorChipsPanel.Children.Add(_removeButton);
        }
    }

    private static Border CreateIconButton(Geometry geometry, string tag, RoutedEventHandler clicked)
    {
        var button = new System.Windows.Controls.Button
        {
            Width = 38,
            Height = 38,
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
            Tag = tag,
            Content = new Path
            {
                Data = geometry,
                Stroke = (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"],
                StrokeThickness = 2,
                Stretch = Stretch.Uniform,
                Width = 16,
                Height = 16
            }
        };
        button.Click += clicked;

        var border = new Border
        {
            Child = button,
            Margin = new Thickness(4, 0, 0, 0),
            Tag = tag
        };

        return border;
    }

    private void OnAddClicked(object sender, RoutedEventArgs e)
    {
        var randomColor = Color.FromRgb(
            (byte)_randomColor.Next(256),
            (byte)_randomColor.Next(256),
            (byte)_randomColor.Next(256));

        AddColor(randomColor);
    }

    private void OnRemoveClicked(object sender, RoutedEventArgs e)
    {
        EnterDeleteMode();
    }

    private void OnRootPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        // If clicking outside the chip row border, exit deletion mode
        if (!IsInsideChipRow(e.OriginalSource))
            ExitDeleteMode();
    }

    // Walk both visual and logical parents to check if source is inside ChipRowBorder
    private bool IsInsideChipRow(object source)
    {
        if (source is DependencyObject obj)
        {
            // Walk visual tree
            DependencyObject current = obj;
            while (current != null)
            {
                if (current == ChipRowBorder)
                    return true;
                current = VisualTreeHelper.GetParent(current);
            }
            // Walk logical tree as fallback
            current = obj;
            while (current != null)
            {
                if (current == ChipRowBorder)
                    return true;
                current = LogicalTreeHelper.GetParent(current);
            }
        }
        return false;
    }

    /* ------------------------------------------------------------------ */
    /*  Geometry resources                                                 */
    /* ------------------------------------------------------------------ */

    private static Geometry SymbolPlus => Geometry.Parse("M20 10V14H14V20H10V14H4V10H10V4H14V10Z");

    private static Geometry SymbolMinus => Geometry.Parse("M4 10V14H20V10Z");
}
