using System;
using System.Drawing;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Color = System.Windows.Media.Color;

namespace Uniwill_Control_Utility.Views.Controls;

/// <summary>
/// Color picker popup control adapted from LenovoLegionToolkit.WPF.Controls.ColorPickerControl.
/// Shows a circular swatch button that opens a SquarePicker popup with hex/RGB inputs.
/// </summary>
public partial class ColorPickerControl : UserControl
{
    private static readonly Regex HexRegex = new("^#(?:[0-9A-Fa-f]{3}){2}$");

    private bool _isEditing;

    public static readonly DependencyProperty SelectedColorProperty =
        DependencyProperty.Register(nameof(SelectedColor), typeof(Color), typeof(ColorPickerControl),
            new PropertyMetadata(Colors.Aqua, OnSelectedColorChanged));

    public Color SelectedColor
    {
        get => (Color)GetValue(SelectedColorProperty);
        set => SetValue(SelectedColorProperty, value);
    }

    private static void OnSelectedColorChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ColorPickerControl control && e.NewValue is Color color)
        {
            control._button.Background = new SolidColorBrush(color);

            // Sync the internal SquarePicker so it displays the correct color
            if (control._colorPicker is not null)
            {
                control._colorPicker.SelectedColor = color;
            }

            // Sync the RGB/hex text fields
            if (control._redNumberBox is not null)
            {
                control._redNumberBox.Text = color.R.ToString();
            }
            if (control._greenNumberBox is not null)
            {
                control._greenNumberBox.Text = color.G.ToString();
            }
            if (control._blueNumberBox is not null)
            {
                control._blueNumberBox.Text = color.B.ToString();
            }
            if (control._hexTextBox is not null)
            {
                control._hexTextBox.Text = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
            }
        }
    }

    public event EventHandler? ColorChangedContinuous;
    public event EventHandler? ColorChangedDelayed;

    /// <summary> Fired when the deletion X button is clicked (in deletion mode). </summary>
    public event EventHandler? DeleteRequested;

    public ColorPickerControl()
    {
        InitializeComponent();
        SelectedColor = Colors.Aqua;
    }

    /// <summary>Opens the color picker popup programmatically.</summary>
    public void OpenPopup() => _popup.IsOpen = true;

    /// <summary>Closes the color picker popup programmatically.</summary>
    public void ClosePopup() => _popup.IsOpen = false;

    /// <summary>Sync popup content with SelectedColor when the popup opens.</summary>
    private void _popup_Opened(object? sender, EventArgs e)
    {
        // The Popup's child visual tree is lazily rendered on first open.
        // Sync the internal SquarePicker and text fields with the current SelectedColor
        // so they reflect any programmatic changes made before the popup was opened.
        var color = SelectedColor;
        if (_colorPicker is not null)
            _colorPicker.SelectedColor = color;

        _redNumberBox.Text = color.R.ToString();
        _greenNumberBox.Text = color.G.ToString();
        _blueNumberBox.Text = color.B.ToString();
        _hexTextBox.Text = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
    }

    /// <summary>Sets the popup's placement target externally (for use when the control is invisible).</summary>
    public void SetPopupPlacementTarget(UIElement target)
    {
        _popup.PlacementTarget = target;
    }

    /// <summary>Sets the popup's horizontal offset.</summary>
    public void SetPopupOffset(double h, double v)
    {
        _popup.HorizontalOffset = h;
        _popup.VerticalOffset = v;
    }

    /// <summary>Show or hide the deletion X overlay button.</summary>
    public void SetDeleteMode(bool enabled)
    {
        _deleteButton.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        _deleteButton.Opacity = enabled ? 1.0 : 0.0;
        _deleteButton.IsHitTestVisible = enabled;
        _button.IsHitTestVisible = !enabled;
    }

    private void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        DeleteRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Sets whether the popup stays open after losing focus.</summary>
    public void SetPopupStaysOpen(bool staysOpen)
    {
        _popup.StaysOpen = staysOpen;
    }

    private bool CanHandleEvent =>
        !_isEditing
        && _colorPicker is not null
        && _redNumberBox is not null
        && _greenNumberBox is not null
        && _blueNumberBox is not null
        && _hexTextBox is not null;

    private void Button_Click(object sender, RoutedEventArgs e)
    {
        _popup.IsOpen = true;
        e.Handled = true;
    }

    private void ColorPicker_MouseUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        ColorChangedDelayed?.Invoke(this, EventArgs.Empty);
    }

    private void ColorPicker_MouseDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
    }

    private void ColorPicker_ColorChanged(object sender, RoutedEventArgs e)
    {
        if (!CanHandleEvent)
            return;

        _isEditing = true;

        var color = _colorPicker.SelectedColor;
        _button.Background = new SolidColorBrush(color);

        _redNumberBox.Text = color.R.ToString();
        _greenNumberBox.Text = color.G.ToString();
        _blueNumberBox.Text = color.B.ToString();
        _hexTextBox.Text = $"#{color.R:X2}{color.G:X2}{color.B:X2}";

        SelectedColor = color;

        ColorChangedContinuous?.Invoke(this, EventArgs.Empty);

        _isEditing = false;
    }

    private void NumberBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!CanHandleEvent)
            return;

        _isEditing = true;

        var r = ToByte(_redNumberBox.Text);
        var g = ToByte(_greenNumberBox.Text);
        var b = ToByte(_blueNumberBox.Text);
        var color = Color.FromRgb(r, g, b);

        _button.Background = new SolidColorBrush(color);
        _hexTextBox.Text = $"#{r:X2}{g:X2}{b:X2}";

        if (Mouse.LeftButton != MouseButtonState.Pressed && Mouse.RightButton != MouseButtonState.Pressed)
        {
            _colorPicker.SelectedColor = color;
            SelectedColor = color;
            ColorChangedDelayed?.Invoke(this, EventArgs.Empty);
        }

        _isEditing = false;
    }

    private void HexTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!CanHandleEvent)
            return;

        if (!HexRegex.Match(_hexTextBox.Text).Success)
            return;

        _isEditing = true;

        try
        {
            var c = ColorTranslator.FromHtml(_hexTextBox.Text);
            var color = Color.FromRgb(c.R, c.G, c.B);

            _button.Background = new SolidColorBrush(color);
            _redNumberBox.Text = color.R.ToString();
            _greenNumberBox.Text = color.G.ToString();
            _blueNumberBox.Text = color.B.ToString();

            if (Mouse.LeftButton != MouseButtonState.Pressed && Mouse.RightButton != MouseButtonState.Pressed)
            {
                _colorPicker.SelectedColor = color;
                SelectedColor = color;
                ColorChangedDelayed?.Invoke(this, EventArgs.Empty);
            }
        }
        catch { /* Ignore invalid hex */ }

        _isEditing = false;
    }

    private void OK_Click(object sender, RoutedEventArgs e) => _popup.IsOpen = false;

    private static byte ToByte(string s)
    {
        if (string.IsNullOrWhiteSpace(s))
            return 0;

        if (!int.TryParse(s, out var value))
            return 0;

        return (byte)Math.Clamp(value, 0, 255);
    }
}
