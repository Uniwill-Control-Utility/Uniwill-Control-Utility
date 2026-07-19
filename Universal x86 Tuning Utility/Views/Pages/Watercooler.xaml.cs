using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Universal_x86_Tuning_Utility.Models;
using Universal_x86_Tuning_Utility.Services;

namespace Universal_x86_Tuning_Utility.Views.Pages
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

        /// <summary>True while OnWatercoolerPresetApplied is updating UI controls. Suppresses selection-changed side effects.</summary>
        private bool _isSyncingFromPreset;

        public Watercooler()
        {
            InitializeComponent();
            InitializePage();
            Loaded += Watercooler_Loaded;
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

                // Get DeviceApplier for centralized device commands and override management
                _deviceApplier = App.GetService<DeviceApplier>();

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

            _waterCoolerService.ConnectionStateChanged += OnConnectionStateChanged;
            _waterCoolerService.StatusChanged += OnStatusChanged;

            // Subscribe to DeviceApplier events for override state and preset applications
            if (_deviceApplier != null)
            {
                _deviceApplier.WatercoolerOverrideChanged += OnWatercoolerOverrideChanged;
                _deviceApplier.WatercoolerPresetApplied += OnWatercoolerPresetApplied;
            }

            // Apply current override state (may already be overridden from startup or Adaptive page)
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

            // Restore RGB mode selection
            var rgbMode = settings.GetRgbMode();
            cbxRgbMode.SelectedIndex = GetRgbModeIndex(rgbMode);

            // Restore RGB color selection
            var rgbColor = settings.GetRgbColor();
            cbxRgbColor.SelectedIndex = GetRgbColorIndex(rgbColor);

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
        }

        private int GetPumpVoltageIndex(PumpVoltage voltage) => voltage switch
        {
            PumpVoltage.Off => 0,
            PumpVoltage.V7 => 1,
            PumpVoltage.V8 => 2,
            PumpVoltage.V11 => 3,
            _ => 0
        };

        private int GetFanSpeedIndex(FanSpeed speed) => speed switch
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

        private int GetRgbModeIndex(RgbState mode) => mode switch
        {
            RgbState.Off => 0,
            RgbState.Static => 1,
            RgbState.Breathe => 2,
            RgbState.Colorful => 3,
            RgbState.BreatheColor => 4,
            _ => 0
        };

        private int GetRgbColorIndex(RgbColor color) => color switch
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
            return (cbxRgbMode.SelectedIndex + 1) switch
            {
                1 => RgbState.Off,
                2 => RgbState.Static,
                3 => RgbState.Breathe,
                4 => RgbState.Colorful,
                _ => RgbState.BreatheColor
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
            cbxPumpVoltage.IsEnabled = enabled;
            cbxFanSpeed.IsEnabled = enabled;
            cbxRgbMode.IsEnabled = enabled;
            cbxRgbColor.IsEnabled = enabled;
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

            await _waterCoolerService.DisconnectAsync();
            SetControlsEnabled(false);
        }

        #endregion

        #region Control Handlers

        private async void cbxPumpVoltage_SelectionChanged(object sender, EventArgs e)
        {
            if (_isSyncingFromPreset) return;
            if (_waterCoolerService == null || !_waterCoolerService.IsConnected) return;

            var voltage = GetSelectedPumpVoltage();
            await _waterCoolerService.WritePumpModeAsync(voltage);
            _waterCoolerService.UpdatePumpVoltage(voltage);
        }

        private async void cbxFanSpeed_SelectionChanged(object sender, EventArgs e)
        {
            if (_isSyncingFromPreset) return;
            if (_waterCoolerService == null || !_waterCoolerService.IsConnected) return;

            var speed = GetSelectedFanSpeed();
            await _waterCoolerService.WriteFanModeAsync(speed);
            _waterCoolerService.UpdateFanSpeed(speed);
        }

        private async void cbxRgbMode_SelectionChanged(object sender, EventArgs e)
        {
            if (_isSyncingFromPreset) return;
            if (_waterCoolerService == null || !_waterCoolerService.IsConnected) return;

            var mode = GetSelectedRgbMode();
            var color = GetSelectedRgbColor();
            await _waterCoolerService.WriteRgbModeAsync(mode, color);
            _waterCoolerService.UpdateRgbMode(mode);
        }

        private async void cbxRgbColor_SelectionChanged(object sender, EventArgs e)
        {
            if (_isSyncingFromPreset) return;
            if (_waterCoolerService == null || !_waterCoolerService.IsConnected) return;

            var mode = GetSelectedRgbMode();
            var color = GetSelectedRgbColor();
            await _waterCoolerService.WriteRgbModeAsync(mode, color);
            _waterCoolerService.UpdateRgbColor(color);
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

                        // Update page title with detected device name
                        if (_waterCoolerService?.ConnectedDeviceName != null
                            && !string.IsNullOrEmpty(_waterCoolerService.ConnectedDeviceName))
                        {
                            tbPageTitle.Text = $"{_waterCoolerService.ConnectedDeviceName} Control";
                        }
                        break;

                    case WaterCoolerService.WatercoolerConnectionState.Disconnected:
                        spConnectedState.Visibility = Visibility.Collapsed;
                        spDisconnectedState.Visibility = Visibility.Visible;
                        btnConnect.Content = "Connect";
                        btnConnect.IsEnabled = _selectedDeviceAddress != null;
                        SetControlsEnabled(false);
                        spControls.Visibility = Visibility.Collapsed;

                        // Reset device image to default (Mk1)
                        UpdateDeviceImage();

                        // Reset page title
                        tbPageTitle.Text = "LCT Watercooler Control";
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
        /// Updates the device image based on the connected device's model.
        /// Shows Mk2 image for LCT22002, Mk1 for everything else.
        /// </summary>
        private void UpdateDeviceImage()
        {
            string imageFile = "mk2.png"; // Default fallback

            if (_waterCoolerService?.ConnectedDeviceName != null)
            {
                string name = _waterCoolerService.ConnectedDeviceName;
                if (name.Contains("LCT22002", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("Mk2", StringComparison.OrdinalIgnoreCase))
                {
                    imageFile = "mk2.png";
                }
            }

            imgDevice.Source = new BitmapImage(
                new Uri($"pack://application:,,,/Assets/HydroUI/{imageFile}", UriKind.Absolute));
        }

        private void Page_Unloaded(object sender, RoutedEventArgs e)
        {
            if (_waterCoolerService != null)
            {
                _waterCoolerService.ConnectionStateChanged -= OnConnectionStateChanged;
                _waterCoolerService.StatusChanged -= OnStatusChanged;
            }

            // Unsubscribe from DeviceApplier events
            if (_deviceApplier != null)
            {
                _deviceApplier.WatercoolerOverrideChanged -= OnWatercoolerOverrideChanged;
                _deviceApplier.WatercoolerPresetApplied -= OnWatercoolerPresetApplied;
            }

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
            else if (_waterCoolerService?.IsConnected == true)
            {
                // DeviceApplier already re-applied settings to the device in DisableWatercoolerOverrideAsync.
                // We just need to sync the UI to reflect the restored settings.
                SyncUiFromSettings();
                SetControlsEnabled(true);
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
                cbxRgbMode.SelectedIndex = GetRgbModeIndex(settings.GetRgbMode());
                cbxRgbColor.SelectedIndex = GetRgbColorIndex(settings.GetRgbColor());
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
                Appearance = Wpf.Ui.Controls.ControlAppearance.Primary,
                Icon = new Wpf.Ui.Controls.SymbolIcon(Wpf.Ui.Controls.SymbolRegular.Warning24),
                IsCloseButtonEnabled = false,
                Timeout = TimeSpan.FromHours(1), // effectively infinite — dismissed on page Unloaded
            };
            _adaptiveSnackbar.Show(true);
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
            cbxRgbMode.SelectedIndex = GetRgbModeIndex(e.RgbMode);
            cbxRgbColor.SelectedIndex = GetRgbColorIndex(e.RgbColor);
        }
    }
}
