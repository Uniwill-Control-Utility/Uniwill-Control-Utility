using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Universal_x86_Tuning_Utility.Models;
using Universal_x86_Tuning_Utility.Properties;
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
        private string? _selectedDeviceAddress;
        private bool _isInitialized;
        private Wpf.Ui.Controls.Snackbar? _adaptiveSnackbar;
        private bool _adaptiveSnackbarShown;
        /// <summary>True while the override was previously active (regardless of whether the snackbar was visually shown).</summary>
        private bool _wasPreviouslyOverridden;
        private System.Threading.Timer? _adaptiveCheckTimer;

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

            // Check Adaptive Mode override state first — it gates whether controls are enabled
            CheckAdaptiveModeState();

            // Reflect current connection state if already connected
            if (_waterCoolerService.IsConnected)
            {
                OnConnectionStateChanged(null, WaterCoolerService.WatercoolerConnectionState.Connected);
            }

            // Start polling for Adaptive Mode state (checks every 2 seconds)
            StartAdaptiveCheck();
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
                if (!IsAdaptiveOverrideActive())
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
            if (_waterCoolerService == null || !_waterCoolerService.IsConnected) return;
            if (IsAdaptiveOverrideActive()) return;

            var voltage = GetSelectedPumpVoltage();
            await _waterCoolerService.WritePumpModeAsync(voltage);
            _waterCoolerService.UpdatePumpVoltage(voltage);
        }

        private async void cbxFanSpeed_SelectionChanged(object sender, EventArgs e)
        {
            if (_waterCoolerService == null || !_waterCoolerService.IsConnected) return;
            if (IsAdaptiveOverrideActive()) return;

            var speed = GetSelectedFanSpeed();
            await _waterCoolerService.WriteFanModeAsync(speed);
            _waterCoolerService.UpdateFanSpeed(speed);
        }

        private async void cbxRgbMode_SelectionChanged(object sender, EventArgs e)
        {
            if (_waterCoolerService == null || !_waterCoolerService.IsConnected) return;
            if (IsAdaptiveOverrideActive()) return;

            var mode = GetSelectedRgbMode();
            var color = GetSelectedRgbColor();
            await _waterCoolerService.WriteRgbModeAsync(mode, color);
            _waterCoolerService.UpdateRgbMode(mode);
        }

        private async void cbxRgbColor_SelectionChanged(object sender, EventArgs e)
        {
            if (_waterCoolerService == null || !_waterCoolerService.IsConnected) return;
            if (IsAdaptiveOverrideActive()) return;

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
            string imageFile = "mk1.png"; // Default fallback

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

            _adaptiveCheckTimer?.Dispose();
            _adaptiveCheckTimer = null;

            // Reset snackbar/override state so it can show again on next page visit
            _adaptiveSnackbarShown = false;
            _wasPreviouslyOverridden = false;
            _adaptiveSnackbar = null;
        }

        #endregion

        /* ------------------------------------------------------------------ */
        /*  Adaptive Mode Override Detection                                   */
        /* ------------------------------------------------------------------ */

        private void StartAdaptiveCheck()
        {
            _adaptiveCheckTimer = new System.Threading.Timer(
                _ =>
                {
                    var dispatcher = Application.Current?.Dispatcher;
                    if (dispatcher == null || dispatcher.HasShutdownStarted)
                        return;
                    try
                    {
                        dispatcher.Invoke(CheckAdaptiveModeState);
                    }
                    catch (System.Threading.Tasks.TaskCanceledException)
                    {
                        // Dispatcher is shutting down, ignore
                    }
                },
                null, TimeSpan.Zero, TimeSpan.FromSeconds(2));
        }

        private void CheckAdaptiveModeState()
        {
            bool adaptiveRunning = Settings.Default.isAdaptiveModeRunning;
            bool wcEnabled = Settings.Default.AdaptiveWcEnabled;
            bool shouldOverride = adaptiveRunning && wcEnabled;

            overlayAdaptiveWarning.Visibility = shouldOverride ? Visibility.Visible : Visibility.Collapsed;

            if (shouldOverride)
            {
                SetControlsEnabled(false);

                // Show snackbar on transition from "no override" to "override"
                if (!_adaptiveSnackbarShown)
                {
                    _adaptiveSnackbarShown = true;
                    ShowAdaptiveSnackbar();
                }
                _wasPreviouslyOverridden = true;
            }
            else
            {
                // Only enable controls if connected
                if (_waterCoolerService?.IsConnected == true)
                {
                    SetControlsEnabled(true);
                }

                // Hide snackbar on transition from "override" to "no override"
                if (_adaptiveSnackbarShown)
                {
                    _adaptiveSnackbarShown = false;
                    HideAdaptiveSnackbar();
                }

                // Re-apply the Watercooler page's saved settings now that control is returned.
                // Only on transition, not every tick.
                if (_wasPreviouslyOverridden)
                {
                    _wasPreviouslyOverridden = false;
                    if (_waterCoolerService?.IsConnected == true)
                    {
                        try
                        {
                            var pumpVoltage = GetSelectedPumpVoltage();
                            if (pumpVoltage != PumpVoltage.Off)
                                _ = _waterCoolerService.WritePumpModeAsync(pumpVoltage);

                            var fanSpeed = GetSelectedFanSpeed();
                            if (fanSpeed != FanSpeed.Off)
                                _ = _waterCoolerService.WriteFanModeAsync(fanSpeed);

                            var rgbMode = GetSelectedRgbMode();
                            var rgbColor = GetSelectedRgbColor();
                            if (rgbMode != RgbState.Off)
                                _ = _waterCoolerService.WriteRgbModeAsync(rgbMode, rgbColor);
                        }
                        catch { /* non-critical on override-lift */ }
                    }
                }
            }
        }

        private void ShowAdaptiveSnackbar()
        {
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

        /* ------------------------------------------------------------------ */
        /*  Override Guards                                                    */
        /* ------------------------------------------------------------------ */

        private bool IsAdaptiveOverrideActive()
        {
            return Settings.Default.isAdaptiveModeRunning && Settings.Default.AdaptiveWcEnabled;
        }
    }
}
