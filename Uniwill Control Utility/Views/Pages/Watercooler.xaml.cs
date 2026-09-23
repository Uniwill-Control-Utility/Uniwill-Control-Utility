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

            // Restore RGB mode selection
            var rgbMode = settings.GetRgbMode();
            cbxRgbMode.SelectedIndex = GetRgbModeIndex(rgbMode);

            // Restore RGB color selection
            var rgbColor = settings.GetRgbColor();
            cbxRgbColor.SelectedIndex = GetRgbColorIndex(rgbColor);

            // Update color picker visibility based on loaded mode
            UpdateRgbColorVisibility();

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
            RgbState.Off => 0,
            RgbState.Static => 1,
            RgbState.Breathe => 2,
            RgbState.Colorful => 3,
            RgbState.BreatheColor => 4,
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
            UpdateRgbColorVisibility();

            if (_isSyncingFromPreset) return;
            if (_waterCoolerService == null || !_waterCoolerService.IsConnected) return;

            var mode = GetSelectedRgbMode();
            var color = GetSelectedRgbColor();
            await _waterCoolerService.WriteRgbModeAsync(mode, color);
            _waterCoolerService.UpdateRgbMode(mode);
        }

        /// <summary>Show the color row only for modes that accept a custom color (Static, Breathe).</summary>
        private void UpdateRgbColorVisibility()
        {
            // Off=0, Static=1, Breathe=2, Colorful=3, Breathe Color=4
            bool showColor = cbxRgbMode.SelectedIndex is 1 or 2;
            RgbColorRow.Visibility = showColor ? Visibility.Visible : Visibility.Collapsed;
            // Collapse the content area when no color row is visible, hiding the divider too.
            RgbCardExpander.IsExpanded = showColor;
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

        /// <summary>Hide the RGB CardExpander chevron so the card stays non-collapsible by arrow.</summary>
        private void RgbCardExpander_Loaded(object sender, RoutedEventArgs e)
        {
            Application.Current.Dispatcher.BeginInvoke(
                (Action)(() =>
                {
                    var toggle = RgbCardExpander.Template?.FindName("ExpanderToggleButton", RgbCardExpander) as ToggleButton;
                    if (toggle?.Template is not null)
                    {
                        var chevronGrid = toggle.Template.FindName("ChevronGrid", toggle) as FrameworkElement;
                        if (chevronGrid is not null)
                        {
                            chevronGrid.Visibility = Visibility.Collapsed;
                            return;
                        }
                    }

                    WalkAndHideChevron(RgbCardExpander);
                }),
                DispatcherPriority.Loaded);
        }

        private void WalkAndHideChevron(DependencyObject parent)
        {
            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is Wpf.Ui.Controls.SymbolIcon)
                {
                    ((FrameworkElement)child).Visibility = Visibility.Collapsed;
                    return;
                }
                WalkAndHideChevron(child);
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
            if (_waterCoolerService == null) return;

            var settings = _waterCoolerService.GetSettings();
            settings.SmartModeEnabled = true;
            SaveSmartModeSettings();

            _waterCoolerService.StartSmartMode();
            // Disable manual pump and fan control while smart mode is active
            cbxPumpVoltage.IsEnabled = false;
            cbxFanSpeed.IsEnabled = false;
        }

        private async void tsSmartMode_Unchecked(object sender, RoutedEventArgs e)
        {
            if (_waterCoolerService == null) return;

            var settings = _waterCoolerService.GetSettings();
            settings.SmartModeEnabled = false;
            _waterCoolerService.SaveSettings();

            _waterCoolerService.StopSmartMode();
            cbxPumpVoltage.IsEnabled = true;
            cbxFanSpeed.IsEnabled = true;

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

        private void TextBox_NumericOnly(object sender, TextCompositionEventArgs e)
        {
            e.Handled = !Regex.IsMatch(e.Text, @"^\d+$");
        }

        private void SaveSmartModeSettings()
        {
            if (_waterCoolerService == null) return;
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

            _waterCoolerService.SaveSettings();
        }

        private void LoadSmartModeSettings()
        {
            if (_waterCoolerService == null) return;
            var settings = _waterCoolerService.GetSettings();

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

            // Apply smart mode enabled state
            if (settings.SmartModeEnabled && _waterCoolerService.IsConnected)
            {
                _waterCoolerService.StartSmartMode();
                cbxPumpVoltage.IsEnabled = false;
                cbxFanSpeed.IsEnabled = false;
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
                            cbxPumpVoltage.IsEnabled = false;
                            cbxFanSpeed.IsEnabled = false;
                            tsSmartMode.IsChecked = true;
                        }
                        break;

                    case WaterCoolerService.WatercoolerConnectionState.Disconnected:
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

                        // Re-enable manual pump control on disconnect (smart pump is stopped by service)
                        cbxPumpVoltage.IsEnabled = true;
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
            // Unsubscribe from service events to allow this scoped instance to be GC'd.
            // The next navigation creates a fresh instance whose constructor re-subscribes.
            if (_waterCoolerService != null)
            {
                _waterCoolerService.ConnectionStateChanged -= OnConnectionStateChanged;
                _waterCoolerService.StatusChanged -= OnStatusChanged;
            }

            // Unsubscribe from DeviceApplier events.
            if (_deviceApplier != null)
            {
                _deviceApplier.WatercoolerOverrideChanged -= OnWatercoolerOverrideChanged;
                _deviceApplier.WatercoolerPresetApplied -= OnWatercoolerPresetApplied;
            }

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
            cbxRgbMode.SelectedIndex = GetRgbModeIndex(e.RgbMode);
            cbxRgbColor.SelectedIndex = GetRgbColorIndex(e.RgbColor);
            UpdateRgbColorVisibility();
        }

        /* ------------------------------------------------------------------ */
        /*  RGB CardExpander Chevron Hide                                      */
        /* ------------------------------------------------------------------ */

        /// <summary>Hide the CardExpander chevron so the card stays always-expanded.</summary>
 
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
