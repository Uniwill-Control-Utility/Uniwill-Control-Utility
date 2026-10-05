using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Uniwill_Control_Utility.Models;
using Uniwill_Control_Utility.Services;

namespace Uniwill_Control_Utility.Views.Pages
{
    /// <summary>
    /// Interaction logic for Watercooler page.
    /// Provides pump voltage, fan speed, and RGB lighting controls for LCT water coolers.
    /// </summary>
    public partial class Watercooler : Page
    {
        private WaterCoolerService? _waterCoolerService;
        private DeviceApplier? _deviceApplier;
        private string? _selectedDeviceAddress;
        private bool _isInitialized;
        private Wpf.Ui.Controls.Snackbar? _adaptiveSnackbar;

        // Glow pulse timer fields
        private System.Threading.Timer? _glowPulseTimer;
        private bool _glowPulseDirection;
        private volatile bool _glowActive;

        // Temperature update timer
        private System.Threading.Timer? _tempUpdateTimer;

        /// <summary>True while OnWatercoolerPresetApplied is updating UI controls. Suppresses selection-changed side effects.</summary>
        private bool _isSyncingFromPreset;

        /// <summary>True while LoadSmartModeSettings is populating controls. Suppresses SelectionChanged saves.</summary>
        private bool _isSyncingSmartMode;

        /// <summary>True while syncing UI from external settings change (tray menu). Suppresses SelectionChanged saves.</summary>
        private bool _isSyncingFromExternal;

        public Watercooler()
        {
            InitializeComponent();
            InitializePage();
            Loaded += Watercooler_Loaded;
            IsVisibleChanged += Watercooler_IsVisibleChanged;
        }

        private void InitializePage()
        {
            if (_isInitialized) return;
            _isInitialized = true;

            try
            {
                _waterCoolerService = App.GetService<WaterCoolerService>();
                if (_waterCoolerService == null)
                {
                    MessageBox.Show(
                        "Watercooler service is not available.\nPlease restart the application.",
                        "Service Error",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                    return;
                }

                // Subscribe to service events so the page responds to connection changes
                // even before it's navigated to (e.g., auto-connect at startup).
                _waterCoolerService.ConnectionStateChanged += OnConnectionStateChanged;
                _waterCoolerService.StatusChanged += OnStatusChanged;
                _waterCoolerService.FlowStatusChanged += OnFlowStatusChanged;
                _waterCoolerService.FirmwareVersionReceived += OnFirmwareVersionReceived;
                _waterCoolerService.SettingsChanged += OnWaterCoolerSettingsChanged;

                // Get DeviceApplier for centralized device commands and override management
                _deviceApplier = App.GetService<DeviceApplier>();

                // Subscribe to DeviceApplier events early so the page responds to override
                // state changes even before it's navigated to (e.g., auto-start at startup).
                if (_deviceApplier != null)
                {
                    _deviceApplier.WatercoolerOverrideChanged += OnWatercoolerOverrideChanged;
                    _deviceApplier.WatercoolerPresetApplied += OnWatercoolerPresetApplied;
                }

                LoadSettings();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Failed to initialize watercooler page: {ex.Message}",
                    "Initialization Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private void Watercooler_Loaded(object sender, RoutedEventArgs e)
        {
            if (_waterCoolerService == null) return;

            // Service event subscriptions are in InitializePage() (constructor) so they're
            // active even before the first navigation. No need to subscribe here.

            // Apply current override state (may already be overridden from startup or Adaptive page).
            // DeviceApplier event subscription is done in InitializePage() so we receive events
            // even before this page is navigated to.
            bool isOverridden = _deviceApplier?.IsWatercoolerOverridden == true;
            ApplyOverrideState(isOverridden);

            // If override is active, sync UI from the last-applied preset.
            if (isOverridden && _deviceApplier?.LastAppliedWatercoolerPreset is { } preset)
            {
                SyncUiFromPreset(preset);
            }

            // Reflect current connection state if already connected
            if (_waterCoolerService.IsConnected)
            {
                OnConnectionStateChanged(null, WaterCoolerService.WatercoolerConnectionState.Connected);
            }
        }

       /// <summary>
        /// Re-syncs UI state on every navigation back to this page.
        /// Page.Loaded only fires once (WPF-UI caches scoped page instances),
        /// so IsVisibleChanged is the reliable hook for per-navigation logic.
        /// </summary>
        private void Watercooler_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (!this.IsVisible || _waterCoolerService == null) return;

            // Re-sync connection state in case it changed while this page was hidden.
            // The event-driven OnConnectionStateChanged handles live changes, but if
            // a state transition occurred and completed while we were away, we need
            // to reflect the current state on return.
            if (_waterCoolerService.IsConnected)
            {
                OnConnectionStateChanged(null, WaterCoolerService.WatercoolerConnectionState.Connected);
                // Also re-sync the current flow status in case it changed while away.
                UpdateFlowStatusUi(_waterCoolerService.FlowStatus);

                // Re-sync firmware version — the query may have completed before this
                // page was first navigated to, so the event was missed.
                if (_waterCoolerService.FirmwareVersion is string version)
                    OnFirmwareVersionReceived(null, version);
            }
            else
            {
                OnConnectionStateChanged(null, WaterCoolerService.WatercoolerConnectionState.Disconnected);
            }

            // Re-apply override state (Adaptive Mode may have been toggled while away)
            bool isOverridden = _deviceApplier?.IsWatercoolerOverridden == true;
            ApplyOverrideState(isOverridden);

            if (isOverridden && _deviceApplier?.LastAppliedWatercoolerPreset is { } preset)
            {
                SyncUiFromPreset(preset);
            }
        }

        private void LoadSettings()
        {
            if (_waterCoolerService == null) return;

            var settings = _waterCoolerService.GetSettings();

            // Restore pump voltage selection
            var pumpVoltage = settings.GetPumpVoltage();
            cbxPumpVoltage.SelectedIndex = GetPumpVoltageIndex(pumpVoltage);

            // Restore fan speed selection
            var fanSpeed = settings.GetFanSpeed();
            cbxFanSpeed.SelectedIndex = GetFanSpeedIndex(fanSpeed);

            // Restore RGB toggle and mode/color selection
            var rgbMode = settings.GetRgbMode();
            tsRgbEnabled.IsChecked = settings.RgbEnabled && rgbMode != RgbState.Off;
            cbxRgbMode.SelectedIndex = GetRgbModeIndex(rgbMode);

            // Restore RGB color selection
            var rgbColor = settings.GetRgbColor();
            cbxRgbColor.SelectedIndex = GetRgbColorIndex(rgbColor);

            // Update color picker visibility based on loaded mode
            UpdateRgbColorVisibility();

            // Expand/collapse RGB card based on enabled state
            RgbCardExpander.IsExpanded = tsRgbEnabled.IsChecked == true;

            // Restore auto-connect toggle
            tsAutoConnect.IsChecked = settings.AutoConnect;

            // Check if the service is already connected (e.g., via auto-connect on startup)
            if (_waterCoolerService.IsConnected)
            {
                _selectedDeviceAddress = settings.LastDeviceAddress;
                OnConnectionStateChanged(null, WaterCoolerService.WatercoolerConnectionState.Connected);
            }
            else
            {
                // Pre-populate from last known device so Connect/Disconnect works without re-scanning
                if (!string.IsNullOrEmpty(settings.LastDeviceAddress))
                    _selectedDeviceAddress = settings.LastDeviceAddress;

                SetControlsEnabled(false);
            }

            // Load Smart Mode settings
            LoadSmartModeSettings();
        }

        private static int GetPumpVoltageIndex(PumpVoltage voltage) => voltage switch
        {
            PumpVoltage.Off => 0,
            PumpVoltage.V7 => 1,
            PumpVoltage.V8 => 2,
            PumpVoltage.V11 => 3,
            _ => 0
        };

        private static int GetFanSpeedIndex(FanSpeed speed) => speed switch
        {
            FanSpeed.Off => 0,
            FanSpeed.Percent25 => 1,
            FanSpeed.Percent50 => 2,
            FanSpeed.Percent75 => 3,
            FanSpeed.Percent90 => 4,
            FanSpeed.Percent95 => 5,
            FanSpeed.Percent100 => 6,
            _ => 0
        };

        private static int GetRgbModeIndex(RgbState mode) => mode switch
        {
            RgbState.Static => 0,
            RgbState.Breathe => 1,
            RgbState.Colorful => 2,
            RgbState.BreatheColor => 3,
            _ => 0
        };

        private static int GetRgbColorIndex(RgbColor color) => color switch
        {
            RgbColor.Red => 0,
            RgbColor.Green => 1,
            RgbColor.Blue => 2,
            RgbColor.White => 3,
            _ => 0
        };

        private PumpVoltage GetSelectedPumpVoltage()
        {
            return (cbxPumpVoltage.SelectedIndex + 1) switch
            {
                1 => PumpVoltage.Off,
                2 => PumpVoltage.V7,
                3 => PumpVoltage.V8,
                _ => PumpVoltage.V11
            };
        }

        private FanSpeed GetSelectedFanSpeed()
        {
            return (cbxFanSpeed.SelectedIndex + 1) switch
            {
                1 => FanSpeed.Off,
                2 => FanSpeed.Percent25,
                3 => FanSpeed.Percent50,
                4 => FanSpeed.Percent75,
                5 => FanSpeed.Percent90,
                6 => FanSpeed.Percent95,
                _ => FanSpeed.Percent100
            };
        }

        private RgbState GetSelectedRgbMode()
        {
            // ComboBox indices: 0=Static, 1=Breathe, 2=Colorful, 3=Breathe Color
            return cbxRgbMode.SelectedIndex switch
            {
                0 => RgbState.Static,
                1 => RgbState.Breathe,
                2 => RgbState.Colorful,
                3 => RgbState.BreatheColor,
                _ => RgbState.Static
            };
        }

        private RgbColor GetSelectedRgbColor()
        {
            return (cbxRgbColor.SelectedIndex + 1) switch
            {
                1 => RgbColor.Red,
                2 => RgbColor.Green,
                3 => RgbColor.Blue,
                _ => RgbColor.White
            };
        }

        private void SetControlsEnabled(bool enabled)
        {
            spControls.IsEnabled = enabled;
        }

        #region Device Discovery & Connection

        private async void btnScan_Click(object sender, RoutedEventArgs e)
        {
            if (_waterCoolerService == null) return;

            btnScan.IsEnabled = false;
            cbxDevices.Items.Clear();

            try
            {
                var devices = await _waterCoolerService.DiscoverDevicesAsync(10000);

                foreach (var device in devices)
                    cbxDevices.Items.Add(device);

                if (cbxDevices.Items.Count > 0)
                    cbxDevices.SelectedIndex = 0;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Scan failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                btnScan.IsEnabled = true;
            }
        }

        private void cbxDevices_SelectionChanged(object sender, EventArgs e)
        {
            if (cbxDevices.SelectedItem is WaterCoolerDeviceInfo device)
            {
                _selectedDeviceAddress = device.Address;
                btnConnect.IsEnabled = _waterCoolerService == null || !_waterCoolerService.IsConnected;
            }
        }

        private async void btnConnect_Click(object sender, RoutedEventArgs e)
        {
            if (_waterCoolerService == null || _selectedDeviceAddress == null) return;

            btnConnect.IsEnabled = false;
            btnConnect.Content = "Connecting...";

            var connected = await _waterCoolerService.ConnectAsync(_selectedDeviceAddress);

            if (connected)
            {
                // Pause after connection for device BLE stack to stabilize.
                await Task.Delay(500);

                // Restore saved settings to device (skip if Adaptive Mode is overriding)
                if (_deviceApplier?.IsWatercoolerOverridden != true)
                {
                    // WriteWithResponse ensures reliable delivery — no artificial delays needed.
                    try
                    {
                        var pumpVoltage = GetSelectedPumpVoltage();
                        if (pumpVoltage != PumpVoltage.Off)
                            await _waterCoolerService.WritePumpModeAsync(pumpVoltage);

                        var fanSpeed = GetSelectedFanSpeed();
                        if (fanSpeed != FanSpeed.Off)
                            await _waterCoolerService.WriteFanModeAsync(fanSpeed);

                        var rgbMode = GetSelectedRgbMode();
                        var rgbColor = GetSelectedRgbColor();
                        if (rgbMode != RgbState.Off)
                            await _waterCoolerService.WriteRgbModeAsync(rgbMode, rgbColor);
                    }
                    catch { /* non-critical during connect */ }
                }
            }
            else
            {
                btnConnect.Content = "Connect";
                btnConnect.IsEnabled = true;
            }
        }

        private async void btnDisconnect_Click(object sender, RoutedEventArgs e)
        {
            if (_waterCoolerService == null) return;

            _waterCoolerService.SetUserDisconnected();
            await _waterCoolerService.DisconnectAsync();
            SetControlsEnabled(false);
        }

        #endregion

        #region Control Handlers

        private async void cbxPumpVoltage_SelectionChanged(object sender, EventArgs e)
        {
            if (_isSyncingFromPreset || _isSyncingFromExternal) return;
            if (_waterCoolerService == null || !_waterCoolerService.IsConnected) return;

            var voltage = GetSelectedPumpVoltage();
            await _waterCoolerService.WritePumpModeAsync(voltage);
            _waterCoolerService.UpdatePumpVoltage(voltage);
        }

        private async void cbxFanSpeed_SelectionChanged(object sender, EventArgs e)
        {
            if (_isSyncingFromPreset || _isSyncingFromExternal) return;
            if (_waterCoolerService == null || !_waterCoolerService.IsConnected) return;

            var speed = GetSelectedFanSpeed();
            await _waterCoolerService.WriteFanModeAsync(speed);
            _waterCoolerService.UpdateFanSpeed(speed);
        }

        private async void cbxRgbMode_SelectionChanged(object sender, EventArgs e)
        {
            UpdateRgbColorVisibility();

            if (_isSyncingFromPreset || _isSyncingFromExternal) return;
            if (_waterCoolerService == null || !_waterCoolerService.IsConnected) return;

            var mode = GetSelectedRgbMode();
            var color = GetSelectedRgbColor();
            await _waterCoolerService.WriteRgbModeAsync(mode, color);
            _waterCoolerService.UpdateRgbMode(mode);
        }

        /// <summary>Show the color row (and divider) only for modes that accept a custom color (Static, Breathe).</summary>
        private void UpdateRgbColorVisibility()
        {
            // Static=0, Breathe=1 accept color; Colorful=2, BreatheColor=3 do not
            bool showColor = cbxRgbMode.SelectedIndex is 0 or 1;
            RgbDivider.Visibility = showColor ? Visibility.Visible : Visibility.Collapsed;
            RgbColorRow.Visibility = showColor ? Visibility.Visible : Visibility.Collapsed;
        }

        private async void cbxRgbColor_SelectionChanged(object sender, EventArgs e)
        {
            if (_isSyncingFromPreset || _isSyncingFromExternal) return;
            if (_waterCoolerService == null || !_waterCoolerService.IsConnected) return;

            var mode = GetSelectedRgbMode();
            var color = GetSelectedRgbColor();
            await _waterCoolerService.WriteRgbModeAsync(mode, color);
            _waterCoolerService.UpdateRgbColor(color);
        }

       private void tsRgbEnabled_Checked(object sender, RoutedEventArgs e)
        {
            if (_isSyncingFromPreset || _isSyncingFromExternal) return;
            if (_waterCoolerService == null) return;
            var settings = _waterCoolerService.GetSettings();
            settings.RgbEnabled = true;
            _waterCoolerService.SaveSettings();

            RgbCardExpander.IsExpanded = true;

            // Apply current effect/color to device
            if (_waterCoolerService.IsConnected)
            {
                var mode = GetSelectedRgbMode();
                var color = GetSelectedRgbColor();
                _ = _waterCoolerService.WriteRgbModeAsync(mode, color);
            }
        }

        private async void tsRgbEnabled_Unchecked(object sender, RoutedEventArgs e)
        {
            if (_isSyncingFromPreset || _isSyncingFromExternal) return;
            if (_waterCoolerService == null) return;
            var settings = _waterCoolerService.GetSettings();
            settings.RgbEnabled = false;
            _waterCoolerService.SaveSettings();

            RgbCardExpander.IsExpanded = false;

            // Turn off RGB on device
            if (_waterCoolerService.IsConnected)
            {
                await _waterCoolerService.WriteRgbModeAsync(RgbState.Off, RgbColor.Red);
            }
        }

        private void tsAutoConnect_Checked(object sender, RoutedEventArgs e)
        {
            if (_waterCoolerService != null)
                _waterCoolerService.UpdateAutoConnect(true);
        }

        private void tsAutoConnect_Unchecked(object sender, RoutedEventArgs e)
        {
            if (_waterCoolerService != null)
                _waterCoolerService.UpdateAutoConnect(false);
        }

        #region Smart Mode Handlers

        private void tsSmartMode_Checked(object sender, RoutedEventArgs e)
        {
            if (_isSyncingFromPreset || _isSyncingFromExternal) return;
            if (_waterCoolerService == null) return;

            var settings = _waterCoolerService.GetSettings();
            settings.SmartModeEnabled = true;
            SaveSmartModeSettings();

            _waterCoolerService.StartSmartMode();
            // Hide manual pump and fan control while smart mode is active
            PumpVoltageCard.Visibility = Visibility.Collapsed;
            FanSpeedCard.Visibility = Visibility.Collapsed;
        }

        private async void tsSmartMode_Unchecked(object sender, RoutedEventArgs e)
        {
            if (_isSyncingFromPreset || _isSyncingFromExternal) return;
            if (_waterCoolerService == null) return;

            var settings = _waterCoolerService.GetSettings();
            settings.SmartModeEnabled = false;
            _waterCoolerService.SaveSettings();

            _waterCoolerService.StopSmartMode();
            PumpVoltageCard.Visibility = Visibility.Visible;
            FanSpeedCard.Visibility = Visibility.Visible;

            // Re-apply the current combobox values so pump/fan don't stay at the
            // last smart-mode speed after disabling.
            if (_waterCoolerService.IsConnected)
            {
                var voltage = GetSelectedPumpVoltage();
                var fan = GetSelectedFanSpeed();
                try
                {
                    if (voltage != PumpVoltage.Off)
                        await _waterCoolerService.WritePumpModeAsync(voltage);
                    if (fan != FanSpeed.Off)
                        await _waterCoolerService.WriteFanModeAsync(fan);
                }
                catch { /* non-critical — device may have disconnected */ }
            }
        }

        private void cbxSmartModeTempSource_SelectionChanged(object sender, EventArgs e)
        {
            SaveSmartModeSettings();
        }

        private void cbxSmartModeSpeed_SelectionChanged(object sender, EventArgs e)
        {
            SaveSmartModeSettings();
        }

        private void tbSmartModeThresholds_LostFocus(object sender, RoutedEventArgs e)
        {
            SaveSmartModeSettings();
        }

        private async void tsSmartModeTemperatureRgb_Checked(object sender, RoutedEventArgs e)
        {
            SaveSmartModeSettings();
            // Hide and disable manual RGB controls — Smart Mode takes over
            RgbCardExpander.IsEnabled = false;
            RgbCardExpander.Visibility = Visibility.Collapsed;
            // Turn off manual RGB so Smart Mode's writes aren't overwritten
            if (_waterCoolerService?.IsConnected == true)
            {
                await _waterCoolerService.WriteRgbModeAsync(RgbState.Off, RgbColor.Red);
            }
            if (_waterCoolerService?.IsSmartModeActive == true)
                _waterCoolerService.StartSmartMode(); // restart to pick up new setting
        }

        private void tsSmartModeTemperatureRgb_Unchecked(object sender, RoutedEventArgs e)
        {
            SaveSmartModeSettings();
            // Restore manual RGB controls
            RgbCardExpander.IsEnabled = true;
            RgbCardExpander.Visibility = Visibility.Visible;
            if (_waterCoolerService?.IsSmartModeActive == true)
                _waterCoolerService.StartSmartMode(); // restart to pick up new setting
        }

        private void TextBox_NumericOnly(object sender, TextCompositionEventArgs e)
        {
            e.Handled = !Regex.IsMatch(e.Text, @"^\d+$");
        }

        private void SaveSmartModeSettings()
        {
            if (_waterCoolerService == null) return;
            // Skip save if controls are being populated from loaded settings (prevents circular save).
            if (_isSyncingSmartMode) return;
            var settings = _waterCoolerService.GetSettings();

            // Temperature source: 0=max, 1=cpu, 2=gpu
            settings.SmartModeTempSource = cbxSmartModeTempSource.SelectedIndex switch
            {
                1 => "cpu",
                2 => "gpu",
                _ => "max"
            };

            // Thresholds
            if (int.TryParse(tbSmartModeLow.Text, out int low))
                settings.SmartModeLowThreshold = Math.Clamp(low, 0, 99);
            if (int.TryParse(tbSmartModeHigh.Text, out int high))
                settings.SmartModeHighThreshold = Math.Clamp(high, 0, 99);

            // Ensure low <= high
            if (settings.SmartModeLowThreshold > settings.SmartModeHighThreshold)
            {
                (settings.SmartModeLowThreshold, settings.SmartModeHighThreshold) =
                    (settings.SmartModeHighThreshold, settings.SmartModeLowThreshold);
                tbSmartModeLow.Text = settings.SmartModeLowThreshold.ToString();
                tbSmartModeHigh.Text = settings.SmartModeHighThreshold.ToString();
            }

            // Pump speeds: 0=Off, 1=V7, 2=V8, 3=V11
            settings.SmartModeIdlePump = (cbxSmartModeIdlePump.SelectedIndex + 1) switch
            {
                1 => "Off",
                2 => "V7",
                3 => "V8",
                _ => "V11"
            };
            settings.SmartModeBoostPump = (cbxSmartModeBoostPump.SelectedIndex + 1) switch
            {
                1 => "Off",
                2 => "V7",
                3 => "V8",
                _ => "V11"
            };

            // Fan speeds: 0=Off, 1=25%, 2=50%, 3=75%, 4=90%, 5=95%, 6=100%
            settings.SmartModeIdleFan = (cbxSmartModeIdleFan.SelectedIndex + 1) switch
            {
                1 => "Off",
                2 => "Percent25",
                3 => "Percent50",
                4 => "Percent75",
                5 => "Percent90",
                6 => "Percent95",
                _ => "Percent100"
            };
            settings.SmartModeBoostFan = (cbxSmartModeBoostFan.SelectedIndex + 1) switch
            {
                1 => "Off",
                2 => "Percent25",
                3 => "Percent50",
                4 => "Percent75",
                5 => "Percent90",
                6 => "Percent95",
                _ => "Percent100"
            };

            settings.SmartModeTemperatureRgb = tsSmartModeTemperatureRgb.IsChecked == true;

            _waterCoolerService.SaveSettings();
        }

        private void LoadSmartModeSettings()
        {
            if (_waterCoolerService == null) return;
            var settings = _waterCoolerService.GetSettings();

            // Prevent SelectionChanged handlers from saving back to disk while we populate controls.
            _isSyncingSmartMode = true;
            try
            {
                // Toggle state
                tsSmartMode.IsChecked = settings.SmartModeEnabled;

                // Temperature source
                cbxSmartModeTempSource.SelectedIndex = settings.SmartModeTempSource switch
                {
                    "cpu" => 1,
                    "gpu" => 2,
                    _ => 0
                };

                // Thresholds
                tbSmartModeLow.Text = settings.SmartModeLowThreshold.ToString();
                tbSmartModeHigh.Text = settings.SmartModeHighThreshold.ToString();

                // Pump speeds
                cbxSmartModeIdlePump.SelectedIndex = settings.GetSmartModeIdlePump() switch
                {
                    PumpVoltage.Off => 0,
                    PumpVoltage.V7 => 1,
                    PumpVoltage.V8 => 2,
                    _ => 3
                };
                cbxSmartModeBoostPump.SelectedIndex = settings.GetSmartModeBoostPump() switch
                {
                    PumpVoltage.Off => 0,
                    PumpVoltage.V7 => 1,
                    PumpVoltage.V8 => 2,
                    _ => 3
                };

                // Fan speeds
                cbxSmartModeIdleFan.SelectedIndex = settings.GetSmartModeIdleFan() switch
                {
                    FanSpeed.Off => 0,
                    FanSpeed.Percent25 => 1,
                    FanSpeed.Percent50 => 2,
                    FanSpeed.Percent75 => 3,
                    FanSpeed.Percent90 => 4,
                    FanSpeed.Percent95 => 5,
                    _ => 6
                };
                cbxSmartModeBoostFan.SelectedIndex = settings.GetSmartModeBoostFan() switch
                {
                    FanSpeed.Off => 0,
                    FanSpeed.Percent25 => 1,
                    FanSpeed.Percent50 => 2,
                    FanSpeed.Percent75 => 3,
                    FanSpeed.Percent90 => 4,
                    FanSpeed.Percent95 => 5,
                    _ => 6
                };

                // Temperature-based RGB toggle
                tsSmartModeTemperatureRgb.IsChecked = settings.SmartModeTemperatureRgb;

                // Hide manual RGB controls if temperature-based RGB is active
                if (settings.SmartModeTemperatureRgb)
                {
                    RgbCardExpander.IsEnabled = false;
                    RgbCardExpander.Visibility = Visibility.Collapsed;
                }

                // Apply smart mode enabled state
                if (settings.SmartModeEnabled && _waterCoolerService.IsConnected)
                {
                    _waterCoolerService.StartSmartMode();
                    PumpVoltageCard.Visibility = Visibility.Collapsed;
                    FanSpeedCard.Visibility = Visibility.Collapsed;
                }
            }
            finally
            {
                _isSyncingSmartMode = false;
            }
        }

        #endregion

        #endregion

        #region Event Handlers

        private void OnConnectionStateChanged(object? sender, WaterCoolerService.WatercoolerConnectionState state)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                switch (state)
                {
                    case WaterCoolerService.WatercoolerConnectionState.Connected:
                        spConnectedState.Visibility = Visibility.Visible;
                        spDisconnectedState.Visibility = Visibility.Collapsed;
                        SetControlsEnabled(true);
                        spControls.Visibility = Visibility.Visible;

                        // Update device image based on detected model
                        UpdateDeviceImage();

                        // Start glow effect on connect
                        StartGlowPulse();
                        ApplyAccentGlow();

                        // Start temperature update timer
                        _tempUpdateTimer = new System.Threading.Timer(_ =>
                        {
                            try
                            {
                                Application.Current.Dispatcher.Invoke(() =>
                                {
                                    var provider = new FlydigiTemperatureProvider();
                                    tbCpuTemp.Text = provider.GetCpuTemperature()?.ToString("F0") + "°C" ?? "--";
                                    tbGpuTemp.Text = provider.GetGpuTemperature()?.ToString("F0") + "°C" ?? "--";
                                    provider.Dispose();
                                });
                            }
                            catch { /* temperature unavailable */ }
                        }, null, TimeSpan.Zero, TimeSpan.FromSeconds(2));

                        // Update page title with detected device name
                        if (_waterCoolerService?.ConnectedDeviceName != null
                            && !string.IsNullOrEmpty(_waterCoolerService.ConnectedDeviceName))
                        {
                            tbPageTitle.Text = $"{_waterCoolerService.ConnectedDeviceName} Control";
                        }

                        // Restart Smart Mode if it was enabled
                        var settings = _waterCoolerService?.GetSettings();
                        if (settings?.SmartModeEnabled == true)
                        {
                            _waterCoolerService.StartSmartMode();
                            PumpVoltageCard.Visibility = Visibility.Collapsed;
                            FanSpeedCard.Visibility = Visibility.Collapsed;
                            tsSmartMode.IsChecked = true;
                        }

                        // Show bottom bar on connect
                        bottomBar.Visibility = Visibility.Visible;

                        // Reset standby button state
                        UpdateStandbyButton(false);

                        // Re-sync flow status from service — it may already be in a
                        // non-Off state if Smart Mode restarted and turned the pump on.
                        UpdateFlowStatusUi(_waterCoolerService.FlowStatus);
                        break;

                    case WaterCoolerService.WatercoolerConnectionState.Disconnected:
                        // Reset flow status UI to Off on disconnect so the card doesn't
                        // show stale state from the previous connection.
                        UpdateFlowStatusUi(FlowStatus.Off);

                        spConnectedState.Visibility = Visibility.Collapsed;
                        spDisconnectedState.Visibility = Visibility.Visible;
                        btnConnect.Content = "Connect";
                        btnConnect.IsEnabled = _selectedDeviceAddress != null;
                        SetControlsEnabled(false);
                        spControls.Visibility = Visibility.Collapsed;

                        // Stop glow effect on disconnect
                        StopGlowPulse();

                        // Stop temperature update timer
                        _tempUpdateTimer?.Dispose();
                        _tempUpdateTimer = null;
                        tbCpuTemp.Text = "--";
                        tbGpuTemp.Text = "--";

                        // Reset device image to default (Mk1)
                        UpdateDeviceImage();

                        // Reset page title
                        tbPageTitle.Text = "Hydro UI";

                        // Re-show manual pump and fan control on disconnect (smart mode is stopped by service)
                        PumpVoltageCard.Visibility = Visibility.Visible;
                        FanSpeedCard.Visibility = Visibility.Visible;

                        // Hide bottom bar on disconnect
                        bottomBar.Visibility = Visibility.Collapsed;
                        break;

                    case WaterCoolerService.WatercoolerConnectionState.Scanning:
                        break;
                }
            });
        }

        private void OnStatusChanged(object? sender, string status)
        {
            // Status messages are transient (e.g. "Sending pump command..."),
            // so we don't overwrite the connection state display.
        }

        /// <summary>
        /// Syncs page UI when settings change from external callers (e.g., tray menu).
        /// </summary>
        private void OnWaterCoolerSettingsChanged(object? sender, EventArgs e)
        {
            if (_waterCoolerService == null) return;

            this.Dispatcher.Invoke(() =>
            {
                _isSyncingFromExternal = true;
                try
                {
                    var settings = _waterCoolerService.GetSettings();

                    // Pump voltage
                    var pumpVoltage = settings.GetPumpVoltage();
                    cbxPumpVoltage.SelectedIndex = GetPumpVoltageIndex(pumpVoltage);

                    // Fan speed
                    var fanSpeed = settings.GetFanSpeed();
                    cbxFanSpeed.SelectedIndex = GetFanSpeedIndex(fanSpeed);

                    // RGB mode
                    var rgbMode = settings.GetRgbMode();
                    cbxRgbMode.SelectedIndex = GetRgbModeIndex(rgbMode);

                    // RGB color
                    var rgbColor = settings.GetRgbColor();
                    cbxRgbColor.SelectedIndex = GetRgbColorIndex(rgbColor);

                    // RGB enabled toggle
                    tsRgbEnabled.IsChecked = settings.RgbEnabled && rgbMode != RgbState.Off;

                    // Smart Mode toggle
                    tsSmartMode.IsChecked = settings.SmartModeEnabled;

                    // Update UI visibility based on Smart Mode state
                    if (settings.SmartModeEnabled)
                    {
                        PumpVoltageCard.Visibility = Visibility.Collapsed;
                        FanSpeedCard.Visibility = Visibility.Collapsed;
                    }
                    else
                    {
                        PumpVoltageCard.Visibility = Visibility.Visible;
                        FanSpeedCard.Visibility = Visibility.Visible;
                    }

                    // Update color picker visibility based on loaded mode
                    UpdateRgbColorVisibility();
                }
                finally
                {
                    _isSyncingFromExternal = false;
                }
            });
        }

        /// <summary>
        /// Updates the device image based on the connected device's model.
        /// Shows Mk2 image for LCT22002, Mk1 for everything else.
        /// </summary>
        private void UpdateDeviceImage()
        {
            string imageFile = "mk2.png"; // Default fallback

            if (_waterCoolerService?.ConnectedDeviceName != null)
            {
                string name = _waterCoolerService.ConnectedDeviceName;
                if (name.Contains("LCT22001", StringComparison.OrdinalIgnoreCase) || name.Contains("LCT22002", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("Mk2", StringComparison.OrdinalIgnoreCase))
                {
                    imageFile = "mk2.png";
                } else if (name.Contains("LCT21001", StringComparison.OrdinalIgnoreCase) || name.Contains("LCT21002", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("Mk1", StringComparison.OrdinalIgnoreCase) )
                {
                    imageFile = "mk1.png";
                }
            }

            imgDevice.Source = new BitmapImage(
                new Uri($"pack://application:,,,/Assets/HydroUI/{imageFile}", UriKind.Absolute));
        }

        private void Page_Unloaded(object sender, RoutedEventArgs e)
        {
            // NOTE: Do NOT unsubscribe from service events here. WPF-UI caches scoped page
            // instances — the constructor (and InitializePage) runs only once. Unsubscribing
            // on Unloaded would break all live event delivery after the first navigation away
            // and back, because the page is reused instead of recreated.
            // The page is GC'd when the app exits and the DI container disposes.

            // Stop glow pulse timer to prevent leaks
            StopGlowPulse();

            // Stop temperature update timer
            _tempUpdateTimer?.Dispose();
            _tempUpdateTimer = null;

            // Reset snackbar state so it can show again on next page visit
            _adaptiveSnackbar = null;
        }

        #endregion

        /* ------------------------------------------------------------------ */
        /*  Adaptive Mode Override (Event-Driven)                              */
        /* ------------------------------------------------------------------ */

        /// <summary>
        /// Event handler for <see cref="DeviceApplier.WatercoolerOverrideChanged"/>.
        /// </summary>
        private void OnWatercoolerOverrideChanged(object? sender, bool isOverridden)
        {
            ApplyOverrideState(isOverridden);
        }

        /// <summary>
        /// Applies the override state: shows/hides snackbar, enables/disables controls,
        /// and syncs UI when override is lifted.
        /// </summary>
        private void ApplyOverrideState(bool isOverridden)
        {
            overlayAdaptiveWarning.Visibility = isOverridden ? Visibility.Visible : Visibility.Collapsed;

            if (isOverridden)
            {
                SetControlsEnabled(false);
                ShowAdaptiveSnackbar();
            }
            else
            {
                // Override lifted — hide snackbar and overlay regardless of connection state.
                HideAdaptiveSnackbar();

                if (_waterCoolerService?.IsConnected == true)
                {
                    // DeviceApplier already re-applied settings to the device in DisableWatercoolerOverrideAsync.
                    // We just need to sync the UI to reflect the restored settings.
                    SyncUiFromSettings();
                    SetControlsEnabled(true);
                }
            }
        }

        /// <summary>
        /// Re-applies the Watercooler page's saved settings to the device after override is lifted.
        /// </summary>
        private void ReapplyUserSettingsToDevice()
        {
            if (_waterCoolerService == null || !_waterCoolerService.IsConnected)
                return;

            try
            {
                var settings = _waterCoolerService.GetSettings();
                var pumpVoltage = settings.GetPumpVoltage();
                var fanSpeed = settings.GetFanSpeed();
                var rgbMode = settings.GetRgbMode();
                var rgbColor = settings.GetRgbColor();

                if (pumpVoltage != PumpVoltage.Off)
                    _ = _waterCoolerService.WritePumpModeAsync(pumpVoltage);

                if (fanSpeed != FanSpeed.Off)
                    _ = _waterCoolerService.WriteFanModeAsync(fanSpeed);

                if (rgbMode != RgbState.Off)
                    _ = _waterCoolerService.WriteRgbModeAsync(rgbMode, rgbColor);
            }
            catch { /* non-critical on override-lift */ }
        }

        /// <summary>
        /// Syncs UI controls from the service's restored settings after override is lifted.
        /// </summary>
        private void SyncUiFromSettings()
        {
            if (_waterCoolerService == null) return;
            var settings = _waterCoolerService.GetSettings();

            _isSyncingFromPreset = true;
            try
            {
                cbxPumpVoltage.SelectedIndex = GetPumpVoltageIndex(settings.GetPumpVoltage());
                cbxFanSpeed.SelectedIndex = GetFanSpeedIndex(settings.GetFanSpeed());
                var rgbMode = settings.GetRgbMode();
                tsRgbEnabled.IsChecked = settings.RgbEnabled && rgbMode != RgbState.Off;
                cbxRgbMode.SelectedIndex = GetRgbModeIndex(rgbMode);
                cbxRgbColor.SelectedIndex = GetRgbColorIndex(settings.GetRgbColor());
                UpdateRgbColorVisibility();
                RgbCardExpander.IsExpanded = tsRgbEnabled.IsChecked == true;
            }
            finally
            {
                _isSyncingFromPreset = false;
            }
        }

        private void ShowAdaptiveSnackbar()
        {
            // Hide existing snackbar before showing a new one
            if (_adaptiveSnackbar != null)
                SnackbarPresenter.HideCurrent();

            _adaptiveSnackbar = new Wpf.Ui.Controls.Snackbar(SnackbarPresenter)
            {
                Title = "Adaptive Mode Override",
                Content = "Adaptive Mode is currently controlling the watercooler. Controls on this page are disabled.",
                Appearance = Wpf.Ui.Controls.ControlAppearance.Info,
                Icon = new Wpf.Ui.Controls.FontIcon { FontFamily = new System.Windows.Media.FontFamily("Segoe Fluent Icons"), Glyph = "\uF167", FontSize = 16, Margin = new System.Windows.Thickness(0, 0, 10, 0) },
                IsCloseButtonEnabled = false,
                Timeout = TimeSpan.FromHours(1), // effectively infinite — dismissed on page Unloaded
            };

            // Defer showing until the presenter is in the visual tree.
            // The page may be eagerly instantiated before navigation, in which case
            // the SnackbarPresenter isn't loaded yet and Show() would silently fail.
            if (SnackbarPresenter.IsLoaded)
                _adaptiveSnackbar.Show(true);
            else
                SnackbarPresenter.Loaded += (s, e) => _adaptiveSnackbar?.Show(true);
        }

        private void HideAdaptiveSnackbar()
        {
            SnackbarPresenter.HideCurrent();
            _adaptiveSnackbar = null;
        }

        /// <summary>
        /// Event handler for <see cref="DeviceApplier.WatercoolerPresetApplied"/>.
        /// Syncs the Watercooler page's UI controls to reflect the profile's values.
        /// </summary>
        private void OnWatercoolerPresetApplied(object? sender, WatercoolerPresetAppliedEventArgs e)
        {
            _isSyncingFromPreset = true;
            try
            {
                SyncUiFromPreset(e);
            }
            finally
            {
                _isSyncingFromPreset = false;
            }
        }

        /// <summary>
        /// Syncs UI controls from a preset (used by both the event handler and Page_Loaded).
        /// </summary>
        private void SyncUiFromPreset(WatercoolerPresetAppliedEventArgs e)
        {
            cbxPumpVoltage.SelectedIndex = GetPumpVoltageIndex(e.PumpVoltage);
            cbxFanSpeed.SelectedIndex = GetFanSpeedIndex(e.FanSpeed);
            tsRgbEnabled.IsChecked = e.RgbMode != RgbState.Off;
            cbxRgbMode.SelectedIndex = GetRgbModeIndex(e.RgbMode);
            cbxRgbColor.SelectedIndex = GetRgbColorIndex(e.RgbColor);
            UpdateRgbColorVisibility();
            RgbCardExpander.IsExpanded = tsRgbEnabled.IsChecked == true;
        }

        /* ------------------------------------------------------------------ */
        /*  Flow Status                                                        */
        /* ------------------------------------------------------------------ */

        private FlowStatus _latestFlowStatus;
        private bool _flowStatusUpdatePending;

        private void OnFlowStatusChanged(object? sender, FlowStatus status)
        {
            // Coalesce rapid status changes: stash the latest value and queue at most
            // one Dispatcher action. If an update is already pending, the incoming event
            // only overwrites the cached value — the pending action will read it when it runs.
            // This prevents icon/text desync when e.g. Off → Starting → Ok fires rapidly.
            _latestFlowStatus = status;

            if (_flowStatusUpdatePending) return;
            _flowStatusUpdatePending = true;

            Application.Current.Dispatcher.InvokeAsync(
                (Action)(() =>
                {
                    UpdateFlowStatusUi(_latestFlowStatus);
                    _flowStatusUpdatePending = false;
                }),
                DispatcherPriority.Normal);
        }

        private void UpdateFlowStatusUi(FlowStatus status)
        {
            MahApps.Metro.IconPacks.PackIconLucideKind kind;
            string text;
            Brush foreground;

            (kind, text, foreground) = status switch
            {
                FlowStatus.Ok => (MahApps.Metro.IconPacks.PackIconLucideKind.CircleCheck, "Flow: OK",
                    new SolidColorBrush(Color.FromRgb(0, 0x9A, 0x00))),
                FlowStatus.Fault => (MahApps.Metro.IconPacks.PackIconLucideKind.CircleAlert, "Flow: Fault",
                    new SolidColorBrush(Color.FromRgb(0xFF, 0x00, 0x00))),
                FlowStatus.Starting => (MahApps.Metro.IconPacks.PackIconLucideKind.Loader, "Flow: Starting...",
                    new SolidColorBrush(Color.FromRgb(0xFF, 0x8C, 0x00))),
                FlowStatus.Off => (MahApps.Metro.IconPacks.PackIconLucideKind.DropletOff, "Flow: Off",
                    new SolidColorBrush((Color)(Application.Current.FindResource("TextFillColorSecondaryBrush") is SolidColorBrush b ? b.Color : Colors.Gray))),
                _ => (MahApps.Metro.IconPacks.PackIconLucideKind.Waves, "Flow: Unknown",
                    new SolidColorBrush((Color)(Application.Current.FindResource("TextFillColorSecondaryBrush") is SolidColorBrush b ? b.Color : Colors.Gray)))
            };

            FlowStatusIcon.Kind = kind;
            FlowStatusText.Text = text;
            FlowStatusIcon.Foreground = foreground;
        }

        /* ------------------------------------------------------------------ */
        /*  Firmware Version                                                   */
        /* ------------------------------------------------------------------ */

        private void OnFirmwareVersionReceived(object? sender, string version)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                // Extract version number from "MCU F/W Version: 2.0.0.4" or "CoolingSystem FW V2.0.0.4"
                string display = version;
                var colonIdx = version.LastIndexOf(':');
                if (colonIdx >= 0 && colonIdx < version.Length - 1)
                    display = version[(colonIdx + 1)..].Trim();
                else if (version.IndexOf('V', StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    var vIdx = version.LastIndexOf('V');
                    if (vIdx >= 0 && vIdx < version.Length - 1)
                        display = "V" + version[(vIdx + 1)..].Trim();
                }

                FirmwareText.Text = "FW " + display;
                FirmwareBadge.Visibility = Visibility.Visible;
            });
        }

        /* ------------------------------------------------------------------ */
        /*  Standby / Resume                                                   */
        /* ------------------------------------------------------------------ */

        private async void btnStandby_Click(object sender, RoutedEventArgs e)
        {
            if (_waterCoolerService == null || !_waterCoolerService.IsConnected) return;

            if (_waterCoolerService.IsStandby)
            {
                // Resume
                await _waterCoolerService.ExitStandbyAsync();
                UpdateStandbyButton(false);
            }
            else
            {
                // Enter standby
                await _waterCoolerService.EnterStandbyAsync();
                UpdateStandbyButton(true);
            }
        }

        private void UpdateStandbyButton(bool isStandby)
        {
            if (isStandby)
            {
                StandbyIcon.Glyph = "\uE768"; // Sun/Resume
                StandbyText.Text = "Resume";
                btnStandby.ToolTip = "Resume all outputs.";
            }
            else
            {
                StandbyIcon.Glyph = "\uE769"; // Power
                StandbyText.Text = "Standby";
                btnStandby.ToolTip = "Pause all outputs while keeping BLE connected.";
            }
        }

        /* ------------------------------------------------------------------ */
        /*  Water Filling Cycle                                                */
        /* ------------------------------------------------------------------ */

        private void btnWaterFill_Click(object sender, RoutedEventArgs e)
        {
            if (_waterCoolerService == null || !_waterCoolerService.IsConnected) return;

            var dialog = new Views.Windows.WaterFillDialog(_waterCoolerService);
            dialog.ShowDialog();
        }

        /* ------------------------------------------------------------------ */
        /*  Device Glow                                                        */
        /* ------------------------------------------------------------------ */

        /// <summary>Starts the subtle breathing glow animation when a device connects.</summary>
        private void StartGlowPulse()
        {
            _glowPulseTimer?.Dispose();
            _glowActive = true;
            _glowPulseDirection = true;
            rectGlowOverlay.Opacity = 0.2;
            _glowPulseTimer = new System.Threading.Timer(_ =>
            {
                if (!_glowActive)
                    return;
                try
                {
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        if (!_glowActive)
                            return;
                        var current = rectGlowOverlay.Opacity;
                        if (_glowPulseDirection)
                        {
                            if (current < 0.5)
                                rectGlowOverlay.Opacity = current + 0.008;
                            else
                                _glowPulseDirection = false;
                        }
                        else
                        {
                            if (current > 0.2)
                                rectGlowOverlay.Opacity = current - 0.008;
                            else
                                _glowPulseDirection = true;
                        }
                    });
                }
                catch
                {
                    // Dispatcher shut down or element detached — stop the timer
                    _glowActive = false;
                }
            }, null, 80, 80);
        }

        /// <summary>Stops the glow pulse and hides the glow when disconnected.</summary>
        private void StopGlowPulse()
        {
            _glowActive = false;
            var timer = _glowPulseTimer;
            _glowPulseTimer = null;
            timer?.Dispose();
            try
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    rectGlowOverlay.Opacity = 0;
                });
            }
            catch
            {
                // Dispatcher shut down — ignore
            }
        }

        /// <summary>Applies the Windows accent color as a decorative glow behind the cooler image.</summary>
        private void ApplyAccentGlow()
        {
            try
            {
                var accentColor = GetWindowsAccentColor();
                rectGlowOverlay.Fill = new SolidColorBrush(accentColor);
            }
            catch { /* element detached or color unavailable */ }
        }

        /// <summary>Reads the Windows 10/11 accent color from the registry.</summary>
        private static Color GetWindowsAccentColor()
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
                if (key?.GetValue("AccentColor") is long accentLong)
                {
                    // Registry stores as 0x00BBGGRR (little-endian BGR)
                    var b = (byte)((accentLong >> 16) & 0xFF);
                    var g = (byte)((accentLong >> 8) & 0xFF);
                    var r = (byte)(accentLong & 0xFF);
                    return Color.FromRgb(r, g, b);
                }
            }
            catch { /* fall through to default */ }

            // Fallback: WPF-UI default blue
            return Color.FromRgb(0, 0x7A, 0xCC);
        }
    }
}
