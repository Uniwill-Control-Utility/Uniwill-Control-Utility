using System;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Uniwill_Control_Utility.Models;
using Uniwill_Control_Utility.Services;
using Uniwill_Control_Utility.Services.Performance;

namespace Uniwill_Control_Utility.Views.Pages
{
    public partial class OverlaySettingsPage : Page
    {
        private readonly OverlayManagerService _overlay;
        private readonly DispatcherTimer _rtssTimer;
        private bool _saveInProgress;

        public OverlaySettingsPage(OverlayManagerService overlay)
        {
            _overlay = overlay ?? throw new ArgumentNullException(nameof(overlay));

            InitializeComponent();
            ScaleSlider.ValueChanged += (_, __) => ScaleValueText.Text = ScaleSlider.Value.ToString("P0");
            BackgroundOpacitySlider.ValueChanged += (_, __) => BackgroundOpacityValueText.Text = BackgroundOpacitySlider.Value.ToString("P0");
            ContentOpacitySlider.ValueChanged += (_, __) => ContentOpacityValueText.Text = ContentOpacitySlider.Value.ToString("P0");
            LoadOptions(_overlay.CurrentOptions);
            ScaleValueText.Text = ScaleSlider.Value.ToString("P0");
            BackgroundOpacityValueText.Text = BackgroundOpacitySlider.Value.ToString("P0");
            ContentOpacityValueText.Text = ContentOpacitySlider.Value.ToString("P0");

            // Poll RTSS status every 2 seconds.
            _rtssTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _rtssTimer.Tick += (_, __) => UpdateRtssStatus();
            _rtssTimer.Start();
            UpdateRtssStatus();

            Loaded += (_, __) => _rtssTimer.Start();
            Unloaded += (_, __) => _rtssTimer.Stop();
        }

        private void UpdateRtssStatus()
        {
            if (RtssReader.IsRunning())
            {
                RtssStatusIcon.Glyph = "\uE930"; // Positive
                RtssStatusIcon.Foreground = Application.Current.FindResource("SystemFillColorSuccessBrush") as System.Windows.Media.Brush;
                RtssStatusText.Text = "RTSS running";
                RtssStatusText.Foreground = Application.Current.FindResource("TextFillColorSecondaryBrush") as System.Windows.Media.Brush;
            }
            else
            {
                RtssStatusIcon.Glyph = "\uE7BA"; // Warning
                RtssStatusIcon.Foreground = Application.Current.FindResource("SystemFillColorCautionBrush") as System.Windows.Media.Brush;
                RtssStatusText.Text = "RTSS not running";
                RtssStatusText.Foreground = Application.Current.FindResource("SystemFillColorCautionBrush") as System.Windows.Media.Brush;
            }
        }

        private void LoadOptions(OverlayOptions options)
        {
            EnabledCheckBox.IsChecked = options.Enabled;
            StartupCheckBox.IsChecked = options.ShowOnStartup;
            ToggleOverlayHotkeyTextBox.Text = options.ToggleOverlayHotkey ?? string.Empty;
            ToggleContentsHotkeyTextBox.Text = options.ToggleContentsHotkey ?? string.Empty;
            RefreshIntervalNumberBox.Value = options.RefreshIntervalMs > 0
                ? options.RefreshIntervalMs
                : 500;
            ScaleSlider.Value = IsFinitePositive(options.Scale)
                ? options.Scale
                : 1.0;
            BackgroundOpacitySlider.Value = IsFinite(options.BackgroundOpacity)
                ? Math.Clamp(options.BackgroundOpacity, 0.0, 1.0)
                : 0.95;
            ContentOpacitySlider.Value = IsFinite(options.ContentOpacity)
                ? Math.Clamp(options.ContentOpacity, 0.0, 1.0)
                : 1.0;
            ShowFpsCheckBox.IsChecked = options.ShowFps;
            ShowFrameTimesCheckBox.IsChecked = options.ShowFrameTimes;
            ShowGpuCheckBox.IsChecked = options.ShowGpu;
            ShowCpuCheckBox.IsChecked = options.ShowCpu;
            ShowMemoryCheckBox.IsChecked = options.ShowMemory;
           ShowBatteryCheckBox.IsChecked = options.ShowBattery;
            ShowGraphsCheckBox.IsChecked = options.ShowGraphs;
            HideWhenNoGameCheckBox.IsChecked = options.HideWhenNoGame;
        }

        private void HotkeyTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            e.Handled = true;

            if (sender is not TextBox textBox)
                return;

            Key key = e.Key == Key.System
                ? e.SystemKey
                : e.Key;

            if (key is
                Key.LeftCtrl or
                Key.RightCtrl or
                Key.LeftAlt or
                Key.RightAlt or
                Key.LeftShift or
                Key.RightShift or
                Key.LWin or
                Key.RWin)
            {
                return;
            }

            ModifierKeys modifiers = System.Windows.Input.Keyboard.Modifiers;
            var value = new StringBuilder();

            if (modifiers.HasFlag(ModifierKeys.Control))
                value.Append("Ctrl+");

            if (modifiers.HasFlag(ModifierKeys.Alt))
                value.Append("Alt+");

            if (modifiers.HasFlag(ModifierKeys.Shift))
                value.Append("Shift+");

            value.Append(key);

            textBox.Text = value.ToString();
            textBox.CaretIndex = textBox.Text.Length;
        }

        private OverlayOptions ReadOptions()
        {
            OverlayOptions current = _overlay.CurrentOptions;

            int refreshInterval = (int)Math.Round(
                RefreshIntervalNumberBox.Value ?? 500);

            if (refreshInterval <= 0)
                refreshInterval = 500;

            double scale = ScaleSlider.Value;

            if (!IsFinitePositive(scale))
                scale = 1.0;

            double backgroundOpacity = BackgroundOpacitySlider.Value;
            double contentOpacity = ContentOpacitySlider.Value;

            if (!IsFinite(backgroundOpacity))
                backgroundOpacity = 0.95;
            if (!IsFinite(contentOpacity))
                contentOpacity = 1.0;

            backgroundOpacity = Math.Clamp(backgroundOpacity, 0.0, 1.0);
            contentOpacity = Math.Clamp(contentOpacity, 0.0, 1.0);

            return current with
            {
                Enabled = EnabledCheckBox.IsChecked == true,
                ShowOnStartup = StartupCheckBox.IsChecked == true,
                ToggleOverlayHotkey = ToggleOverlayHotkeyTextBox.Text.Trim(),
                ToggleContentsHotkey = ToggleContentsHotkeyTextBox.Text.Trim(),
                RefreshIntervalMs = refreshInterval,
                Scale = scale,
                BackgroundOpacity = backgroundOpacity,
                ContentOpacity = contentOpacity,
                ShowFps = ShowFpsCheckBox.IsChecked == true,
                ShowFrameTimes = ShowFrameTimesCheckBox.IsChecked == true,
                ShowGpu = ShowGpuCheckBox.IsChecked == true,
                ShowCpu = ShowCpuCheckBox.IsChecked == true,
                ShowMemory = ShowMemoryCheckBox.IsChecked == true,
                ShowBattery = ShowBatteryCheckBox.IsChecked == true,
                ShowGraphs = ShowGraphsCheckBox.IsChecked == true,
                HideWhenNoGame = HideWhenNoGameCheckBox.IsChecked == true
            };
        }

        private bool SaveOptions()
        {
            if (_saveInProgress)
                return false;

            _saveInProgress = true;

            try
            {
                OverlayOptions options = ReadOptions();

                if (!ValidateOptions(options))
                    return false;

                _overlay.ApplyOptions(options);
                return true;
            }
            catch (Exception exception)
            {
                MessageBox.Show(
                    LocalizationService.Format("The overlay settings could not be saved.\n\n{0}", exception.Message),
                    LocalizationService.Get("Overlay settings error"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);

                return false;
            }
            finally
            {
                _saveInProgress = false;
            }
        }

        private static bool ValidateOptions(OverlayOptions options)
        {
            bool overlayHotkeyValid = OverlayManagerService.IsValidHotkey(
                options.ToggleOverlayHotkey);

            bool contentsHotkeyValid = OverlayManagerService.IsValidHotkey(
                options.ToggleContentsHotkey);

            bool hotkeysAreDifferent = !string.Equals(
                options.ToggleOverlayHotkey,
                options.ToggleContentsHotkey,
                StringComparison.OrdinalIgnoreCase);

            if (overlayHotkeyValid &&
                contentsHotkeyValid &&
                hotkeysAreDifferent)
            {
                return true;
            }

            MessageBox.Show(
                LocalizationService.Get("Choose two different shortcuts that each include a non-modifier key."),
                LocalizationService.Get("Invalid overlay shortcut"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);

            return false;
        }

        private void ApplyButton_Click(object sender, RoutedEventArgs e)
        {
            SaveOptions();
        }

        private void VisibilityButton_Click(object sender, RoutedEventArgs e)
        {
            if (SaveOptions())
                _overlay.ToggleOverlay();
        }

        private void PreviewButton_Click(object sender, RoutedEventArgs e)
        {
            if (SaveOptions())
                _overlay.ShowOverlay();
        }

        private void HideButton_Click(object sender, RoutedEventArgs e)
        {
            _overlay.HideOverlay();
        }

        private void ContentsVisibilityButton_Click(object sender, RoutedEventArgs e)
        {
            if (SaveOptions())
                _overlay.ToggleContents();
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) &&
                   !double.IsInfinity(value);
        }

        private static bool IsFinitePositive(double value)
        {
            return IsFinite(value) && value > 0;
        }
    }
}
