using System;
using System.Collections.Generic;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Uniwill_Control_Utility.Models;
using Uniwill_Control_Utility.Properties;
using Uniwill_Control_Utility.Services;
using Uniwill_Control_Utility.Services.Performance;

namespace Uniwill_Control_Utility.Views.Pages
{
    public partial class OverlaySettingsPage : Page
    {
        private readonly OverlayManagerService _overlay;
        private readonly HydroUIService? _hydroUi;
        private readonly FlydigiCoolerHidService? _flydigi;
        private readonly DispatcherTimer _rtssTimer;
        private bool _saveInProgress;

        // References to peripheral checkboxes so they can be shown/hidden reactively.
        private CheckBox? _hydroUiCheckBox;
        private CheckBox? _flydigiCheckBox;

        public OverlaySettingsPage(
            OverlayManagerService overlay,
            HydroUIService? hydroUi = null,
            FlydigiCoolerHidService? flydigi = null)
        {
            _overlay = overlay ?? throw new ArgumentNullException(nameof(overlay));
            _hydroUi = hydroUi;
            _flydigi = flydigi;

            InitializeComponent();

            // Suppress auto-save while populating controls from stored settings.
            _saveInProgress = true;
            try
            {
                LoadOptions(_overlay.CurrentOptions);
            }
            finally
            {
                _saveInProgress = false;
            }

            // Wire up UI update handlers and auto-save after initial load is complete.
            EnabledCheckBox.Checked += (_, __) => SaveOptions();
            EnabledCheckBox.Unchecked += (_, __) => SaveOptions();
            StartupCheckBox.Checked += (_, __) => SaveOptions();
            StartupCheckBox.Unchecked += (_, __) => SaveOptions();

            RefreshIntervalNumberBox.ValueChanged += (_, __) => SaveOptions();

            // Poll RTSS status every 2 seconds.
            _rtssTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _rtssTimer.Tick += (_, __) => UpdateRtssStatus();
            _rtssTimer.Start();
            UpdateRtssStatus();

            Loaded += (_, __) =>
            {
                _rtssTimer.Start();
                WirePeripheralConnectionEvents();

                // Feature settings may have changed while this page was in the
                // background (e.g. user toggled Hydro UI or Flydigi in Settings).
                // Rebuild the metrics list so newly-enabled checkboxes appear.
                RefreshPeripheralCheckboxes();
            };
            Unloaded += (_, __) =>
            {
                _rtssTimer.Stop();
                UnwirePeripheralConnectionEvents();
            };
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

        #region Options Load / Save

        private void LoadOptions(OverlayOptions options)
        {
            EnabledCheckBox.IsChecked = options.Enabled;
            StartupCheckBox.IsChecked = options.ShowOnStartup;
            ToggleOverlayHotkeyTextBox.Text = options.ToggleOverlayHotkey ?? string.Empty;
            ToggleContentsHotkeyTextBox.Text = options.ToggleContentsHotkey ?? string.Empty;
            RefreshIntervalNumberBox.Value = options.RefreshIntervalMs > 0
                ? options.RefreshIntervalMs
                : 500;

            BuildMetricsList(options);
        }

        private OverlayOptions ReadOptions()
        {
            OverlayOptions current = _overlay.CurrentOptions;

            int refreshInterval = (int)Math.Round(
                RefreshIntervalNumberBox.Value ?? 250);

            if (refreshInterval <= 0)
                refreshInterval = 250;

            // Read tree state into per-metric toggles.
            var toggles = ReadTreeToggles();

            return current with
            {
                Enabled = EnabledCheckBox.IsChecked == true,
                ShowOnStartup = StartupCheckBox.IsChecked == true,
                ToggleOverlayHotkey = ToggleOverlayHotkeyTextBox.Text.Trim(),
                ToggleContentsHotkey = ToggleContentsHotkeyTextBox.Text.Trim(),
                RefreshIntervalMs = refreshInterval,

                // FPS
                ShowFps = toggles["ShowFps"],
                ShowFpsOnePercentLow = toggles["ShowFpsOnePercentLow"],
                ShowFpsPointOnePercentLow = toggles["ShowFpsPointOnePercentLow"],

                // Frame times
                ShowFrameTimeCurrent = toggles["ShowFrameTimeCurrent"],
                ShowFrameTimeOnePercent = toggles["ShowFrameTimeOnePercent"],
                ShowFrameTimePointOnePercent = toggles["ShowFrameTimePointOnePercent"],

                // GPU
                ShowGpuTemperature = toggles["ShowGpuTemperature"],
                ShowGpuUsage = toggles["ShowGpuUsage"],
                ShowGpuPower = toggles["ShowGpuPower"],
                ShowGpuClock = toggles["ShowGpuClock"],
                ShowGpuVram = toggles["ShowGpuVram"],

                // CPU
                ShowCpuTemperature = toggles["ShowCpuTemperature"],
                ShowCpuUsage = toggles["ShowCpuUsage"],
                ShowCpuPower = toggles["ShowCpuPower"],
                ShowCpuClock = toggles["ShowCpuClock"],

                // RAM
                ShowMemory = toggles["ShowMemory"],

                // Battery
                ShowBatteryPercent = toggles["ShowBatteryPercent"],
                ShowBatteryStatus = toggles["ShowBatteryStatus"],
                ShowBatteryTime = toggles["ShowBatteryTime"],
                ShowBatteryPower = toggles["ShowBatteryPower"],

                // Peripherals
                ShowHydroUi = toggles["ShowHydroUi"],
                ShowFlydigi = toggles["ShowFlydigi"]
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

        private bool SaveOptionsWithValidation()
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

        #endregion

        #region Hotkey

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

            SaveOptions();
        }

        #endregion

        #region Metrics CheckBox List

        private void BuildMetricsList(OverlayOptions options)
        {
            MetricsCheckPanel.Children.Clear();
            _hydroUiCheckBox = null;
            _flydigiCheckBox = null;

            AddMetricCheckBox("Current FPS", "ShowFps", options.ShowFps);
            AddMetricCheckBox("1% Low FPS", "ShowFpsOnePercentLow", options.ShowFpsOnePercentLow);
            AddMetricCheckBox("0.1% Low FPS", "ShowFpsPointOnePercentLow", options.ShowFpsPointOnePercentLow);
            AddMetricCheckBox("Frame time (current)", "ShowFrameTimeCurrent", options.ShowFrameTimeCurrent);
            AddMetricCheckBox("Frame time (1%)", "ShowFrameTimeOnePercent", options.ShowFrameTimeOnePercent);
            AddMetricCheckBox("Frame time (0.1%)", "ShowFrameTimePointOnePercent", options.ShowFrameTimePointOnePercent);
            AddMetricCheckBox("GPU temperature", "ShowGpuTemperature", options.ShowGpuTemperature);
            AddMetricCheckBox("GPU usage", "ShowGpuUsage", options.ShowGpuUsage);
            AddMetricCheckBox("GPU power", "ShowGpuPower", options.ShowGpuPower);
            AddMetricCheckBox("GPU clock", "ShowGpuClock", options.ShowGpuClock);
            AddMetricCheckBox("GPU VRAM", "ShowGpuVram", options.ShowGpuVram);
            AddMetricCheckBox("CPU temperature", "ShowCpuTemperature", options.ShowCpuTemperature);
            AddMetricCheckBox("CPU usage", "ShowCpuUsage", options.ShowCpuUsage);
            AddMetricCheckBox("CPU power", "ShowCpuPower", options.ShowCpuPower);
            AddMetricCheckBox("CPU clock", "ShowCpuClock", options.ShowCpuClock);
            AddMetricCheckBox("RAM used / total", "ShowMemory", options.ShowMemory);
            AddMetricCheckBox("Battery percentage", "ShowBatteryPercent", options.ShowBatteryPercent);
            AddMetricCheckBox("Battery charging status", "ShowBatteryStatus", options.ShowBatteryStatus);
            AddMetricCheckBox("Battery time remaining", "ShowBatteryTime", options.ShowBatteryTime);
            AddMetricCheckBox("Battery power draw", "ShowBatteryPower", options.ShowBatteryPower);

            // Hydro UI checkbox — hidden if feature disabled in Settings or device not connected.
            bool hydroUiFeatureEnabled = Settings.Default.EnableHydroUiNavigation;
            if (hydroUiFeatureEnabled)
            {
                _hydroUiCheckBox = AddMetricCheckBox("Hydro UI pump & fan", "ShowHydroUi", options.ShowHydroUi);
                UpdateHydroUiCheckBoxVisibility();
            }

            // Flydigi checkbox — hidden if feature disabled in Settings or device not connected.
            bool flydigiFeatureEnabled = Settings.Default.EnableFlydigi;
            if (flydigiFeatureEnabled)
            {
                _flydigiCheckBox = AddMetricCheckBox("Flydigi cooler RPM", "ShowFlydigi", options.ShowFlydigi);
                UpdateFlydigiCheckBoxVisibility();
            }
        }

        private CheckBox AddMetricCheckBox(string label, string optionKey, bool isChecked)
        {
            var cb = new CheckBox
            {
                IsChecked = isChecked,
                Content = label,
                Margin = new Thickness(0, 4, 0, 4),
                VerticalAlignment = VerticalAlignment.Center,
                Tag = optionKey
            };
            cb.Checked += (_, __) => SaveOptions();
            cb.Unchecked += (_, __) => SaveOptions();
            MetricsCheckPanel.Children.Add(cb);
            return cb;
        }

        private Dictionary<string, bool> ReadTreeToggles()
        {
            var result = new Dictionary<string, bool>();

            foreach (var child in MetricsCheckPanel.Children)
            {
                if (child is CheckBox cb && cb.Tag is string key)
                {
                    result[key] = cb.IsChecked == true;
                }
            }

            return result;
        }

        #endregion

        #region Peripheral Connection Events

        private void WirePeripheralConnectionEvents()
        {
            if (_hydroUi is not null)
                _hydroUi.ConnectionStateChanged += HydroUi_ConnectionStateChanged;

            if (_flydigi is not null)
                _flydigi.ConnectionStateChanged += Flydigi_ConnectionStateChanged;
        }

        private void UnwirePeripheralConnectionEvents()
        {
            if (_hydroUi is not null)
                _hydroUi.ConnectionStateChanged -= HydroUi_ConnectionStateChanged;

            if (_flydigi is not null)
                _flydigi.ConnectionStateChanged -= Flydigi_ConnectionStateChanged;
        }

        private void HydroUi_ConnectionStateChanged(object? sender, HydroUIService.WatercoolerConnectionState e)
        {
            // Dispatcher.Invoke not needed — ConnectionStateChanged fires on the UI thread.
            UpdateHydroUiCheckBoxVisibility();
        }

        private void Flydigi_ConnectionStateChanged(object? sender, bool e)
        {
            UpdateFlydigiCheckBoxVisibility();
        }

        private void UpdateHydroUiCheckBoxVisibility()
        {
            if (_hydroUiCheckBox is null)
                return;

            bool connected = _hydroUi?.IsConnected == true;
            _hydroUiCheckBox.Visibility = connected ? Visibility.Visible : Visibility.Collapsed;
        }

        private void UpdateFlydigiCheckBoxVisibility()
        {
            if (_flydigiCheckBox is null)
                return;

            bool connected = _flydigi?.IsConnected == true;
            _flydigiCheckBox.Visibility = connected ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>
        /// Ensures peripheral checkboxes exist when their feature is enabled in
        /// Settings, and hides them when the device is disconnected. Called on
        /// every Loaded so that toggles made in Settings while this page was in
        /// the background take effect without restarting the app.
        /// </summary>
        private void RefreshPeripheralCheckboxes()
        {
            OverlayOptions options = _overlay.CurrentOptions;

            // Hydro UI — create checkbox if feature enabled but checkbox missing.
            if (Settings.Default.EnableHydroUiNavigation && _hydroUiCheckBox is null)
            {
                _saveInProgress = true;
                try { BuildMetricsList(options); }
                finally { _saveInProgress = false; }
                return; // BuildMetricsList creates both, no need to check Flydigi separately.
            }

            // Flydigi — same logic.
            if (Settings.Default.EnableFlydigi && _flydigiCheckBox is null)
            {
                _saveInProgress = true;
                try { BuildMetricsList(options); }
                finally { _saveInProgress = false; }
                return;
            }

            // Both checkboxes already exist (or both features disabled) — just
            // update visibility based on connection state.
            UpdateHydroUiCheckBoxVisibility();
            UpdateFlydigiCheckBoxVisibility();
        }

        #endregion

        #region Button Handlers

        private void VisibilityButton_Click(object sender, RoutedEventArgs e)
        {
            if (SaveOptionsWithValidation())
                _overlay.ToggleOverlay();
        }

        private void PreviewButton_Click(object sender, RoutedEventArgs e)
        {
            if (SaveOptionsWithValidation())
                _overlay.ShowOverlay();
        }

        private void HideButton_Click(object sender, RoutedEventArgs e)
        {
            _overlay.HideOverlay();
        }

        #endregion
    }
}
