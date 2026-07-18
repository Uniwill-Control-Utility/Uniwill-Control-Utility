using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Universal_x86_Tuning_Utility.Properties;
using Universal_x86_Tuning_Utility.Scripts;
using Universal_x86_Tuning_Utility.Views.Controls;
using Universal_x86_Tuning_Utility.Models;
using Universal_x86_Tuning_Utility.Services;

namespace Universal_x86_Tuning_Utility.Views.Pages
{
    /// <summary>
    /// Interaction logic for FlydigiCooler page.
    /// Provides fan speed, gear presets, auto curve control, RGB, and device settings
    /// for Flydigi BS series cooling pads.
    /// </summary>
    public partial class FlydigiCooler : Page
    {
        private bool _isInitialized;
        private FlydigiCoolerService? _coolerService;
        private MultiColorPickerControl? mcRotationColors;
        private Wpf.Ui.Controls.Snackbar? _adaptiveSnackbar;
        private bool _adaptiveSnackbarShown;
        /// <summary>True while the override was previously active (regardless of whether the snackbar was visually shown).</summary>
        private bool _wasPreviouslyOverridden;

        private FlydigiSmartControl? _smartControl;
        private FlydigiTemperatureProvider? _tempProvider;
        private System.Threading.Timer? _tempTimer;
        private System.Threading.Timer? _adaptiveCheckTimer;

        // Debounce timers for auto-apply
        private System.Threading.Timer? _rpmApplyTimer;
        private System.Threading.Timer? _rgbApplyTimer;

        /// <summary>True once initial settings (RGB, RPM) have been applied to the device after first connect.</summary>
        private bool _hasAppliedInitialSettings;

        /// <summary>The currently selected fan curve profile for auto control.</summary>
        private FlydigiFanCurveProfile? _activeProfile;

        public FlydigiCooler()
        {
            InitializeComponent();
            InitializePage();
        }

        /* ------------------------------------------------------------------ */
        /*  Lifecycle                                                          */
        /* ------------------------------------------------------------------ */

        private void InitializePage()
        {
            if (_isInitialized) return;
            _isInitialized = true;

            try
            {
                _coolerService = App.GetService<FlydigiCoolerService>();
                if (_coolerService == null)
                {
                    return;
                }

                // Create multi-color picker programmatically (XAML codegen doesn't generate the field)
                mcRotationColors = new MultiColorPickerControl();
                mcRotationColors.ColorsChanged += OnRotationColorsChanged;
                mcRotationColorsHost.Children.Add(mcRotationColors);

                LoadSettingsToUI();

                // Update page title with detected cooler model name
                tbPageTitle.Text = $"Flydigi {FlydigiHardwareDetector.GetDetectedModelName()} Control";
            }
            catch (Exception ex)
            {
                // Log but don't block page load
                System.Diagnostics.Debug.WriteLine($"FlydigiCooler init error: {ex.Message}");
            }
        }

        private void FlydigiCooler_Loaded(object sender, RoutedEventArgs e)
        {
            if (_coolerService == null) return;

            _coolerService.ConnectionStateChanged += OnConnectionStateChanged;
            _coolerService.StatusChanged += OnStatusChanged;
            _coolerService.FanDataReceived += OnFanDataReceived;

            // Check Adaptive Mode override state first — it gates whether we apply settings
            CheckAdaptiveModeState();

            // Apply saved RGB settings only on first connect, not on every page navigation
            if (_coolerService.IsConnected && !_hasAppliedInitialSettings)
            {
                ApplyRgbAsync();
                _hasAppliedInitialSettings = true;
            }

            // Reflect current connection state
            if (_coolerService.IsConnected)
            {
                UpdateConnectionUI(true);
                StartTemperaturePolling();
            }
            else
            {
                SetControlsEnabled(false);
            }

            // Start polling for Adaptive Mode state (checks every 2 seconds)
            StartAdaptiveCheck();
        }

        private void Page_Unloaded(object? sender, RoutedEventArgs e)
        {
            if (_coolerService != null)
            {
                _coolerService.ConnectionStateChanged -= OnConnectionStateChanged;
                _coolerService.StatusChanged -= OnStatusChanged;
                _coolerService.FanDataReceived -= OnFanDataReceived;
            }

            StopTemperaturePolling();
            StopAutoControl();

            _rpmApplyTimer?.Dispose();
            _rpmApplyTimer = null;
            _rgbApplyTimer?.Dispose();
            _rgbApplyTimer = null;
            _adaptiveCheckTimer?.Dispose();
            _adaptiveCheckTimer = null;

            // Reset snackbar/override state so it can show again on next page visit
            _adaptiveSnackbarShown = false;
            _wasPreviouslyOverridden = false;
            _adaptiveSnackbar = null;
        }

        /* ------------------------------------------------------------------ */
        /*  Fan Mode Switching                                                 */
        /* ------------------------------------------------------------------ */

        private void cbxFanMode_SelectionChanged(object sender, EventArgs e)
        {
            UpdateFanModeUI();

            // Persist fan mode to settings
            if (_coolerService != null)
            {
                var settings = _coolerService.GetSettings();
                settings.FanMode = cbxFanMode.SelectedIndex;
                _coolerService.PersistSettings();
            }
        }

        private void UpdateFanModeUI()
        {
            var selectedIndex = cbxFanMode.SelectedIndex;
            var enteringAuto = selectedIndex == 2;
            var enteringManual = selectedIndex == 0;
            var wasAuto = _smartControl != null;

            spManual.Visibility = enteringManual ? Visibility.Visible : Visibility.Collapsed;
            spGear.Visibility = selectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
            spAuto.Visibility = enteringAuto ? Visibility.Visible : Visibility.Collapsed;

            if (enteringAuto && !wasAuto)
            {
                StartAutoControl();
            }
            else if (!enteringAuto && wasAuto)
            {
                StopAutoControl();
            }

            // When entering Manual mode, immediately apply the saved RPM so the device
            // doesn't stay at whatever Auto was commanding.
            if (enteringManual)
            {
                ApplyRpmAsync();
            }
        }

        /* ------------------------------------------------------------------ */
        /*  Manual RPM                                                         */
        /* ------------------------------------------------------------------ */

        private void sliderRpm_ValueChanged(object? sender, RoutedPropertyChangedEventArgs<double> e)
        {
            // Debounce: wait 300ms after user stops dragging the slider
            ResetDebounceTimer(ref _rpmApplyTimer, 300, ApplyRpmAsync);
        }

        private void nudRpm_ValueChanged(object? sender, RoutedEventArgs e)
        {
            // Immediate apply when user types a value and commits
            _rpmApplyTimer?.Dispose();
            _rpmApplyTimer = null;
            ApplyRpmAsync();
        }

        private static void ResetDebounceTimer(ref System.Threading.Timer? timer, int delayMs, Action callback)
        {
            timer?.Dispose();
            timer = new System.Threading.Timer(_ => Application.Current.Dispatcher.Invoke(callback), null, delayMs, System.Threading.Timeout.Infinite);
        }

        private async void ApplyRpmAsync()
        {
            _rpmApplyTimer?.Dispose();
            _rpmApplyTimer = null;

            if (_coolerService == null || !_coolerService.IsConnected) return;

            // Guard: don't apply settings when Adaptive Mode is overriding
            if (Settings.Default.isAdaptiveModeRunning && Settings.Default.AdaptiveBs2ProEnabled)
                return;

            var rpm = (ushort)nudRpm.Value;

            try
            {
                await _coolerService.WriteRealtimeRpmAsync(rpm);

                // Persist RPM to settings
                var settings = _coolerService.GetSettings();
                settings.ManualRpm = rpm;
                _coolerService.PersistSettings();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to set RPM: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /* ------------------------------------------------------------------ */
        /*  Gear Presets                                                       */
        /* ------------------------------------------------------------------ */

        private async void btnGearQuiet_Click(object sender, RoutedEventArgs e)
        {
            await ApplyGearAsync(1); // Gear 1 = Quiet
        }

        private async void btnGearStandard_Click(object sender, RoutedEventArgs e)
        {
            await ApplyGearAsync(2); // Gear 2 = Standard
        }

        private async void btnGearStrong_Click(object sender, RoutedEventArgs e)
        {
            await ApplyGearAsync(3); // Gear 3 = Strong
        }

        private async void btnGearOverclock_Click(object sender, RoutedEventArgs e)
        {
            await ApplyGearAsync(4); // Gear 4 = Overclock
        }

        private async Task ApplyGearAsync(byte gear)
        {
            if (_coolerService == null || !_coolerService.IsConnected) return;

            // Guard: don't apply settings when Adaptive Mode is overriding
            if (Settings.Default.isAdaptiveModeRunning && Settings.Default.AdaptiveBs2ProEnabled)
                return;

            try
            {
                await _coolerService.WriteGearAsync(gear);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to set gear: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void cbxGearSubLevel_SelectionChanged(object sender, EventArgs e)
        {
            if (_coolerService == null || !_coolerService.IsConnected) return;

            // Guard: don't apply settings when Adaptive Mode is overriding
            if (Settings.Default.isAdaptiveModeRunning && Settings.Default.AdaptiveBs2ProEnabled)
                return;

            var subLevel = cbxGearSubLevel.SelectedIndex; // 0=Low, 1=Medium, 2=High

            // Determine which gear is currently selected from settings
            var gearIndex = (byte)Math.Max(0, _coolerService.GetSettings().ManualGear - 1);

            var rpm = GetGearRpm(gearIndex, subLevel);

            try
            {
                await _coolerService.WriteGearRpmAsync(gearIndex, rpm);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to set gear RPM: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Looks up the default RPM for a gear index (0-3) and sub-level (0-2).
        /// </summary>
        private static ushort GetGearRpm(byte gearIndex, int subLevel)
        {
            return (gearIndex, subLevel) switch
            {
                (0, 0) => Bs2ProDefaultGearRpm.Gear0Low,
                (0, 1) => Bs2ProDefaultGearRpm.Gear0Medium,
                (0, 2) => Bs2ProDefaultGearRpm.Gear0High,
                (1, 0) => Bs2ProDefaultGearRpm.Gear1Low,
                (1, 1) => Bs2ProDefaultGearRpm.Gear1Medium,
                (1, 2) => Bs2ProDefaultGearRpm.Gear1High,
                (2, 0) => Bs2ProDefaultGearRpm.Gear2Low,
                (2, 1) => Bs2ProDefaultGearRpm.Gear2Medium,
                (2, 2) => Bs2ProDefaultGearRpm.Gear2High,
                (3, 0) => Bs2ProDefaultGearRpm.Gear3Low,
                (3, 1) => Bs2ProDefaultGearRpm.Gear3Medium,
                (3, 2) => Bs2ProDefaultGearRpm.Gear3High,
                _ => Bs2ProDefaultGearRpm.Gear0Medium
            };
        }

        /* ------------------------------------------------------------------ */
        /*  Auto Control (Smart Control)                                       */
        /* ------------------------------------------------------------------ */

        private void StartAutoControl()
        {
            if (_coolerService == null || !_coolerService.IsConnected)
                return;

            if (_activeProfile == null)
                return;

            try
            {
                StopAutoControl();

                _tempProvider = new FlydigiTemperatureProvider();
                _smartControl = new FlydigiSmartControl(_coolerService, _tempProvider);
                _smartControl.ActiveProfile = _activeProfile;
                _smartControl.Settings = _coolerService.GetSettings();
                _smartControl.TempSource = _coolerService.GetSettings().TempSource;

                _smartControl.Start();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to start auto control: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void StopAutoControl()
        {
            if (_smartControl != null)
            {
                try { _smartControl.Stop(); } catch { /* ignore */ }
                try { _smartControl.Dispose(); } catch { /* ignore */ }
                _smartControl = null;
            }

            if (_tempProvider != null)
            {
                try { _tempProvider.Dispose(); } catch { /* ignore */ }
                _tempProvider = null;
            }
        }

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
            bool bs2ProEnabled = Settings.Default.AdaptiveBs2ProEnabled;
            bool shouldOverride = adaptiveRunning && bs2ProEnabled;

            overlayAdaptiveWarning.Visibility = shouldOverride ? Visibility.Visible : Visibility.Collapsed;

            if (shouldOverride)
            {
                SetControlsEnabled(false);
                StopAutoControl();

                // Show snackbar on transition from "no override" to "override"
                if (!_adaptiveSnackbarShown)
                {
                    _adaptiveSnackbarShown = true;
                    ShowAdaptiveSnackbar();
                }
                _wasPreviouslyOverridden = true;
            }
            else if (_coolerService?.IsConnected == true)
            {
                // Hide snackbar on transition from "override" to "no override"
                if (_adaptiveSnackbarShown)
                {
                    _adaptiveSnackbarShown = false;
                    HideAdaptiveSnackbar();
                }

                // Re-apply the Flydigi page's saved settings now that control is returned.
                // This ensures the device reflects the page's settings, not whatever
                // Adaptive Mode last commanded. Only on transition, not every tick.
                if (_wasPreviouslyOverridden)
                {
                    _wasPreviouslyOverridden = false;
                    ApplyRgbAsync();
                    if (cbxFanMode.SelectedIndex == 0)
                    {
                        ApplyRpmAsync();
                    }
                }

                SetControlsEnabled(true);

                // Restart auto control if we're in Auto mode and it was stopped by the override
                if (cbxFanMode.SelectedIndex == 2 && _smartControl == null && _activeProfile != null)
                {
                    StartAutoControl();
                }
            }
        }

        /// <summary>
        /// Creates and shows the Adaptive Mode override snackbar.
        /// </summary>
        private void ShowAdaptiveSnackbar()
        {
            _adaptiveSnackbar = new Wpf.Ui.Controls.Snackbar(SnackbarPresenter)
            {
                Title = "Adaptive Mode Override",
                Content = "Adaptive Mode is currently controlling the Flydigi cooler. Controls on this page are currently disabled.",
                Appearance = Wpf.Ui.Controls.ControlAppearance.Primary,
                Icon = new Wpf.Ui.Controls.SymbolIcon(Wpf.Ui.Controls.SymbolRegular.Warning24),
                IsCloseButtonEnabled = false,
                Timeout = TimeSpan.FromHours(1), // effectively infinite — dismissed on page Unloaded
            };
            _adaptiveSnackbar.Show(true);
        }

        /// <summary>
        /// Hides the Adaptive Mode override snackbar.
        /// </summary>
        private void HideAdaptiveSnackbar()
        {
            SnackbarPresenter.HideCurrent();
            _adaptiveSnackbar = null;
        }

        /* ------------------------------------------------------------------ */
        /*  Curve Profile Management                                           */
        /* ------------------------------------------------------------------ */

        private void LoadCurveProfiles()
        {
            PopulateCurveProfiles();
        }

        private void cbxCurveProfile_SelectionChanged(object sender, EventArgs e)
        {
            if (cbxCurveProfile.SelectedItem is ComboBoxItem item && item.Tag is FlydigiFanCurveProfile profile)
            {
                _activeProfile = profile;

                // Persist selected profile name (skip during initial population)
                if (_isInitialized && _coolerService != null)
                {
                    var settings = _coolerService.GetSettings();
                    settings.SelectedCurveProfile = profile.Name;
                    _coolerService.PersistSettings();
                }

                // Re-apply if auto control is active
                if (_smartControl != null)
                {
                    _smartControl.ActiveProfile = _activeProfile;
                }
            }
        }

        private void PopulateCurveProfiles()
        {
            cbxCurveProfile.Items.Clear();

            var silent = FlydigiFanCurveProfile.CreateSilent();
            var balanced = FlydigiFanCurveProfile.CreateBalanced();
            var performance = FlydigiFanCurveProfile.CreatePerformance();

            cbxCurveProfile.Items.Add(new ComboBoxItem { Content = silent.Name, Tag = silent });
            cbxCurveProfile.Items.Add(new ComboBoxItem { Content = balanced.Name, Tag = balanced });
            cbxCurveProfile.Items.Add(new ComboBoxItem { Content = performance.Name, Tag = performance });

            // Load saved custom curve if it exists
            if (_coolerService != null)
            {
                var settings = _coolerService.GetSettings();
                if (!string.IsNullOrEmpty(settings.CustomCurveJson))
                {
                    try
                    {
                        var custom = FlydigiFanCurveProfile.FromJSON(settings.CustomCurveJson);
                        cbxCurveProfile.Items.Add(new ComboBoxItem { Content = custom.Name, Tag = custom });
                    }
                    catch
                    {
                        // Corrupted JSON — ignore and let user recreate
                    }
                }
            }

            // Default to Balanced (index 1)
            cbxCurveProfile.SelectedIndex = 1;
            _activeProfile = balanced;
        }

        private void btnEditCurve_Click(object sender, RoutedEventArgs e)
        {
            // Start editing from the currently selected profile (or a fresh balanced default)
            var seedProfile = _activeProfile ?? FlydigiFanCurveProfile.CreateBalanced();

            var dialog = new Views.Windows.FlydigiCurveEditorWindow(seedProfile);
            if (dialog.ShowDialog() != true || dialog.EditedProfile == null)
                return;

            _activeProfile = dialog.EditedProfile;

            // Append or replace the "Custom" entry at the end of the list
            bool replaced = false;
            for (int i = 0; i < cbxCurveProfile.Items.Count; i++)
            {
                if (cbxCurveProfile.Items[i] is ComboBoxItem existing && existing.Tag is FlydigiFanCurveProfile p && p.Name == "Custom")
                {
                    cbxCurveProfile.Items[i] = new ComboBoxItem { Content = _activeProfile.Name, Tag = _activeProfile };
                    replaced = true;
                    break;
                }
            }
            if (!replaced)
            {
                cbxCurveProfile.Items.Add(new ComboBoxItem { Content = _activeProfile.Name, Tag = _activeProfile });
            }

            // Select the Custom entry
            cbxCurveProfile.SelectedIndex = cbxCurveProfile.Items.Count - 1;

            // Persist custom curve JSON
            if (_coolerService != null)
            {
                var settings = _coolerService.GetSettings();
                settings.CustomCurveJson = _activeProfile.ToJSON();
                settings.SelectedCurveProfile = "Custom";
                _coolerService.PersistSettings();
            }

            // Re-apply if auto control is active
            if (_smartControl != null)
            {
                _smartControl.ActiveProfile = _activeProfile;
            }
        }

        /* ------------------------------------------------------------------ */
        /*  RGB Control                                                        */
        /* ------------------------------------------------------------------ */

        private void cbxRgbMode_SelectionChanged(object sender, EventArgs e)
        {
            UpdateRgbColorVisibility();

            // Apply immediately for all modes when connected and initialized
            if (_isInitialized && _coolerService?.IsConnected == true)
            {
                var modeIndex = cbxRgbMode.SelectedIndex;
                if (modeIndex >= 0)
                    ApplyRgbAsync();
            }
        }

        private void RgbSlider_ValueChanged(object? sender, RoutedPropertyChangedEventArgs<double> e)
        {
            // Debounce: wait 500ms after user stops adjusting (RGB upload is expensive)
            ResetDebounceTimer(ref _rgbApplyTimer, 500, ApplyRgbAsync);
        }

        private void cbxRgbSpeed_SelectionChanged(object? sender, EventArgs e)
        {
            // Immediate apply on speed change
            _rgbApplyTimer?.Dispose();
            _rgbApplyTimer = null;
            ApplyRgbAsync();
        }

        private void OnRotationColorsChanged(object? sender, EventArgs e)
        {
            // Debounce rotation color changes (uploads are expensive)
            ResetDebounceTimer(ref _rgbApplyTimer, 500, ApplyRgbAsync);
        }

        private async void ApplyRgbAsync()
        {
            _rgbApplyTimer?.Dispose();
            _rgbApplyTimer = null;

            if (_coolerService == null || !_coolerService.IsConnected) return;

            // Guard: don't apply settings when Adaptive Mode is overriding
            if (Settings.Default.isAdaptiveModeRunning && Settings.Default.AdaptiveBs2ProEnabled)
                return;

            var modeIndex = cbxRgbMode.SelectedIndex;
            var mode = GetRgbModeName(modeIndex);

            // Guard: if NumberBox values are still null, the page hasn't finished loading yet
            if (nudRgbR.Value == null || nudRgbG.Value == null || nudRgbB.Value == null || nudRgbBrightness.Value == null)
                return;

            try
            {
                switch (modeIndex)
                {
                    case 0: // Off
                        await _coolerService.WriteRgbOffAsync();
                        break;

                    case 1: // Smart-Temp
                        await _coolerService.WriteRgbSmartTempAsync();
                        break;

                    case 2: // Static
                        {
                            var r = (byte)nudRgbR.Value;
                            var g = (byte)nudRgbG.Value;
                            var b = (byte)nudRgbB.Value;
                            var brightness = (byte)nudRgbBrightness.Value;
                            await _coolerService.WriteRgbStaticAsync(r, g, b, brightness);
                        }
                        break;

                    case 3: // Rotation (multi-color)
                        {
                            var colors = mcRotationColors!.Colors;
                            var speed = mcRotationColors.SelectedSpeed;
                            var brightness = mcRotationColors.Brightness;
                            await _coolerService.WriteRgbRotationMultiAsync(colors, speed, brightness);

                            // Save rotation-specific settings
                            var rotSettings = _coolerService.GetSettings();
                            rotSettings.RotationSpeed = speed;
                            rotSettings.RotationBrightness = brightness;
                            rotSettings.RotationColors = string.Join(",", colors.Select(c => $"#{c.R:X2}{c.G:X2}{c.B:X2}"));
                            _coolerService.PersistSettings();
                        }
                        break;

                    case 4: // Flowing
                        {
                            var speed = GetRgbSpeedName();
                            var brightness = (byte)nudRgbBrightness.Value;
                            await _coolerService.WriteRgbFlowingAsync(speed, brightness);
                        }
                        break;

                    case 5: // Breathing
                        {
                            var r = (byte)nudRgbR.Value;
                            var g = (byte)nudRgbG.Value;
                            var b = (byte)nudRgbB.Value;
                            var brightness = (byte)nudRgbBrightness.Value;
                            await _coolerService.WriteRgbBreathingAsync(r, g, b, brightness);
                        }
                        break;
                }

                // Save RGB settings
                var settings = _coolerService.GetSettings();
                settings.RgbMode = mode;
                settings.R = (byte)nudRgbR.Value;
                settings.G = (byte)nudRgbG.Value;
                settings.B = (byte)nudRgbB.Value;
                settings.RgbSpeed = GetRgbSpeedName();
                settings.Brightness = (byte)nudRgbBrightness.Value;
                _coolerService.PersistSettings();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to apply RGB settings: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void UpdateRgbColorVisibility()
        {
            var selectedIndex = cbxRgbMode.SelectedIndex;

            // RGB sliders: Static (2), Breathing (5)
            spRgbSliders.Visibility = selectedIndex is 2 or 5
                ? Visibility.Visible
                : Visibility.Collapsed;

            // Multi-color picker: Rotation (3)
            mcRotationColorsHost.Visibility = selectedIndex is 3
                ? Visibility.Visible
                : Visibility.Collapsed;

            // Speed + Brightness: Flowing (4)
            spRgbSpeedBrightness.Visibility = selectedIndex is 4
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private static string GetRgbModeName(int index) => index switch
        {
            0 => "Off",
            1 => "SmartTemp",
            2 => "Static",
            3 => "Rotation",
            4 => "Flowing",
            5 => "Breathing",
            _ => "Off"
        };

        private string GetRgbSpeedName()
        {
            if (cbxRgbSpeed.SelectedItem is ComboBoxItem item)
                return item.Content?.ToString() ?? "Medium";
            return "Medium";
        }

        /* ------------------------------------------------------------------ */
        /*  Device Settings                                                    */
        /* ------------------------------------------------------------------ */

        private void tsAutoConnect_Checked(object sender, RoutedEventArgs e)
        {
            if (_coolerService == null) return;
            var settings = _coolerService.GetSettings();
            settings.AutoConnect = true;
            _coolerService.PersistSettings();
        }

        private void tsAutoConnect_Unchecked(object sender, RoutedEventArgs e)
        {
            if (_coolerService == null) return;
            var settings = _coolerService.GetSettings();
            settings.AutoConnect = false;
            _coolerService.PersistSettings();
        }

        private async void tsPowerOnStart_Checked(object sender, RoutedEventArgs e)
        {
            if (_coolerService == null || !_coolerService.IsConnected) return;
            await _coolerService.WritePowerOnStartAsync(true);

            var settings = _coolerService.GetSettings();
            settings.PowerOnStart = true;
            _coolerService.PersistSettings();
        }

        private async void tsPowerOnStart_Unchecked(object sender, RoutedEventArgs e)
        {
            if (_coolerService == null || !_coolerService.IsConnected) return;
            await _coolerService.WritePowerOnStartAsync(false);

            var settings = _coolerService.GetSettings();
            settings.PowerOnStart = false;
            _coolerService.PersistSettings();
        }

        private async void cbxSmartStartStop_SelectionChanged(object sender, EventArgs e)
        {
            if (_coolerService == null || !_coolerService.IsConnected) return;

            var mode = (byte)cbxSmartStartStop.SelectedIndex; // 0=Off, 1=Immediate, 2=Delayed
            await _coolerService.WriteSmartStartStopAsync(mode);

            var settings = _coolerService.GetSettings();
            settings.SmartStartStopMode = mode;
            _coolerService.PersistSettings();
        }

        private async void tsGearLight_Checked(object sender, RoutedEventArgs e)
        {
            if (_coolerService == null || !_coolerService.IsConnected) return;
            await _coolerService.WriteGearLightAsync(true);

            var settings = _coolerService.GetSettings();
            settings.GearLightEnabled = true;
            _coolerService.PersistSettings();
        }

        private async void tsGearLight_Unchecked(object sender, RoutedEventArgs e)
        {
            if (_coolerService == null || !_coolerService.IsConnected) return;
            await _coolerService.WriteGearLightAsync(false);

            var settings = _coolerService.GetSettings();
            settings.GearLightEnabled = false;
            _coolerService.PersistSettings();
        }

        /* ------------------------------------------------------------------ */
        /*  Advanced Settings                                                  */
        /* ------------------------------------------------------------------ */

        private void tsAvoidance_Checked(object sender, RoutedEventArgs e)
        {
            if (_coolerService == null) return;

            var settings = _coolerService.GetSettings();
            settings.AvoidanceEnabled = true;
            if (nudAvoidanceStart.Value.HasValue)
                settings.AvoidanceStartRpm = (ushort)nudAvoidanceStart.Value.Value;
            if (nudAvoidanceEnd.Value.HasValue)
                settings.AvoidanceEndRpm = (ushort)nudAvoidanceEnd.Value.Value;
            _coolerService.PersistSettings();

            // Update smart control settings if active
            if (_smartControl != null)
                _smartControl.Settings = settings;
        }

        private void tsAvoidance_Unchecked(object sender, RoutedEventArgs e)
        {
            if (_coolerService == null) return;

            var settings = _coolerService.GetSettings();
            settings.AvoidanceEnabled = false;
            _coolerService.PersistSettings();

            // Update smart control settings if active
            if (_smartControl != null)
                _smartControl.Settings = settings;
        }

        private void cbxTempSource_SelectionChanged(object sender, EventArgs e)
        {
            if (_coolerService == null) return;

            var source = cbxTempSource.SelectedIndex switch
            {
                0 => "max",
                1 => "cpu",
                2 => "gpu",
                _ => "max"
            };

            var settings = _coolerService.GetSettings();
            settings.TempSource = source;
            _coolerService.PersistSettings();

            // Update smart control if active
            if (_smartControl != null)
                _smartControl.TempSource = source;
        }

        private void nudAvoidance_ValueChanged(object? sender, RoutedEventArgs e)
        {
            if (_coolerService == null) return;

            var settings = _coolerService.GetSettings();
            if (nudAvoidanceStart.Value.HasValue)
                settings.AvoidanceStartRpm = (ushort)nudAvoidanceStart.Value.Value;
            if (nudAvoidanceEnd.Value.HasValue)
                settings.AvoidanceEndRpm = (ushort)nudAvoidanceEnd.Value.Value;
            _coolerService.PersistSettings();
        }

        private void tsLearning_Checked(object sender, RoutedEventArgs e)
        {
            if (_coolerService == null) return;
            var settings = _coolerService.GetSettings();
            settings.LearningEnabled = true;
            _coolerService.PersistSettings();
        }

        private void tsLearning_Unchecked(object sender, RoutedEventArgs e)
        {
            if (_coolerService == null) return;
            var settings = _coolerService.GetSettings();
            settings.LearningEnabled = false;
            _coolerService.PersistSettings();
        }

        private void cbxLearningBias_SelectionChanged(object sender, EventArgs e)
        {
            if (_coolerService == null) return;

            var bias = cbxLearningBias.SelectedIndex switch
            {
                0 => "balanced",
                1 => "cooling",
                2 => "quiet",
                _ => "balanced"
            };

            var settings = _coolerService.GetSettings();
            settings.LearningBias = bias;
            _coolerService.PersistSettings();
            // Store learning bias in settings for persistence
            // The FlydigiLearningEngine reads BiasMode from configuration
        }

        /* ------------------------------------------------------------------ */
        /*  Event Callbacks                                                    */
        /* ------------------------------------------------------------------ */

        private void OnConnectionStateChanged(object? sender, bool connected)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                UpdateConnectionUI(connected);
                // Apply initial settings on first connect (from the Connect button, not page navigation)
                if (connected && !_hasAppliedInitialSettings)
                {
                    ApplyRgbAsync();
                    _hasAppliedInitialSettings = true;
                }
            });
        }

        private void OnStatusChanged(object? sender, string message)
        {
            // Status is now shown via icon in the UI, no text update needed.
        }

        private void OnFanDataReceived(object? sender, FanRpmData data)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                tbCurrentRpm.Text = $"{data.CurrentRpm} RPM";
            });
        }

        /* ------------------------------------------------------------------ */
        /*  Helpers                                                            */
        /* ------------------------------------------------------------------ */

        private void LoadSettingsToUI()
        {
            if (_coolerService == null) return;

            var settings = _coolerService.GetSettings();

            // Device settings
            tsAutoConnect.IsChecked = settings.AutoConnect;
            tsPowerOnStart.IsChecked = settings.PowerOnStart;
            cbxSmartStartStop.SelectedIndex = settings.SmartStartStopMode;
            tsGearLight.IsChecked = settings.GearLightEnabled;

            // RGB settings
            var rgbModeIndex = GetRgbModeIndex(settings.RgbMode);
            cbxRgbMode.SelectedIndex = rgbModeIndex;
            UpdateRgbColorVisibility();

            nudRgbR.Value = settings.R;
            nudRgbG.Value = settings.G;
            nudRgbB.Value = settings.B;

            var speedIndex = GetRgbSpeedIndex(settings.RgbSpeed);
            cbxRgbSpeed.SelectedIndex = speedIndex;

            nudRgbBrightness.Value = settings.Brightness;

            // Load rotation-specific settings
            if (mcRotationColors is not null)
            {
                // Load colors
                if (!string.IsNullOrEmpty(settings.RotationColors))
                {
                    var colors = settings.RotationColors.Split(',')
                        .Select(h =>
                        {
                            var hex = h.Trim().Replace("#", "");
                            if (hex.Length == 6)
                                return Color.FromRgb(
                                    byte.Parse(hex.Substring(0, 2), System.Globalization.NumberStyles.HexNumber),
                                    byte.Parse(hex.Substring(2, 2), System.Globalization.NumberStyles.HexNumber),
                                    byte.Parse(hex.Substring(4, 2), System.Globalization.NumberStyles.HexNumber));
                            return (Color?)null;
                        })
                        .Where(c => c.HasValue)
                        .Select(c => c.Value)
                        .ToList();
                    if (colors.Count > 0)
                        mcRotationColors.SetColors(colors);
                }

                // Load speed
                var rotSpeedIndex = GetRgbSpeedIndex(settings.RotationSpeed);
                mcRotationColors.SetSpeedIndex(rotSpeedIndex);

                // Load brightness
                mcRotationColors.SetBrightness(settings.RotationBrightness);
            }

            // Advanced settings
            tsAvoidance.IsChecked = settings.AvoidanceEnabled;
            nudAvoidanceStart.Value = settings.AvoidanceStartRpm;
            nudAvoidanceEnd.Value = settings.AvoidanceEndRpm;

            var tempSourceIndex = settings.TempSource.ToLowerInvariant() switch
            {
                "cpu" => 1,
                "gpu" => 2,
                _ => 0
            };
            cbxTempSource.SelectedIndex = tempSourceIndex;

            // Learning settings
            tsLearning.IsChecked = settings.LearningEnabled;
            var biasIndex = settings.LearningBias.ToLowerInvariant() switch
            {
                "cooling" => 1,
                "quiet" => 2,
                _ => 0
            };
            cbxLearningBias.SelectedIndex = biasIndex;

            // Curve profile — restore before fan mode so _activeProfile is ready for auto control
            // Save the selected profile name BEFORE LoadCurveProfiles repopulates the combo box,
            // because PopulateCurveProfiles sets SelectedIndex=1 (Balanced) which would overwrite
            // settings.SelectedCurveProfile if _isInitialized were true.
            var savedCurveProfile = settings.SelectedCurveProfile;
            _isInitialized = false; // suppress SelectionChanged persistence during population
            LoadCurveProfiles();
            if (!string.IsNullOrEmpty(savedCurveProfile))
            {
                for (int i = 0; i < cbxCurveProfile.Items.Count; i++)
                {
                    if (cbxCurveProfile.Items[i] is ComboBoxItem ci && ci.Content?.ToString() == savedCurveProfile)
                    {
                        cbxCurveProfile.SelectedIndex = i;
                        _activeProfile = ci.Tag as FlydigiFanCurveProfile;
                        break;
                    }
                }
            }
            _isInitialized = true;

            // Fan mode
            var fanMode = Math.Clamp(settings.FanMode, 0, 2);
            cbxFanMode.SelectedIndex = fanMode;
            UpdateFanModeUI();

            // Manual RPM
            if (settings.ManualRpm > 0)
                nudRpm.Value = settings.ManualRpm;

            // Gear
            if (settings.ManualGear > 0)
                cbxGearSubLevel.SelectedIndex = settings.ManualGearSubLevel;

          }

        private void UpdateConnectionUI(bool connected)
        {
            if (connected)
            {
                spConnectedState.Visibility = Visibility.Visible;
                spDisconnectedState.Visibility = Visibility.Collapsed;
                btnScan.IsEnabled = false;
                SetControlsEnabled(true);

                // Check if Adaptive Mode is overriding — this may re-disable controls
                CheckAdaptiveModeState();

                if (_coolerService?.ConnectedDeviceInfo != null)
                    UpdateDeviceImage(_coolerService.ConnectedDeviceInfo.ProductId);

                // Start standalone temperature polling
                StartTemperaturePolling();

                // Restore button text (may have been left as "Connecting..." from the click handler)
                btnDisconnect.Content = "Disconnect";

                // Show control panels when connected
                spControls.Visibility = Visibility.Visible;
            }
            else
            {
                spConnectedState.Visibility = Visibility.Collapsed;
                spDisconnectedState.Visibility = Visibility.Visible;
                tbCurrentRpm.Text = "--";
                tbTemperature.Text = "--";
                SetControlsEnabled(false);

                // Reset device image to default
                UpdateDeviceImage(0);

                // Restore button text (may have been left as "Disconnecting...")
                btnConnect.Content = "Connect";
                btnScan.IsEnabled = true;

                // Hide control panels when disconnected
                spControls.Visibility = Visibility.Collapsed;

                // Stop auto control when disconnected
                StopAutoControl();
                StopTemperaturePolling();

                // Reset so settings re-apply on next connect
                _hasAppliedInitialSettings = false;
            }
        }

        /// <summary>
        /// Updates the device image based on the connected device's product ID.
        /// Falls back to the BS2 Pro image for unknown devices.
        /// </summary>
        private void UpdateDeviceImage(ushort productId)
        {
            string imageFile = productId switch
            {
                Bs2ProProductId.B2 => "bs2.png",
                Bs2ProProductId.B2Pro => "bs2-pro.png",
                Bs2ProProductId.B3 => "bs3.png",
                Bs2ProProductId.B3Pro => "bs3-pro.png",
                _ => "bs2-pro.png" // Default fallback
            };

            imgDevice.Source = new BitmapImage(
                new Uri($"pack://application:,,,/Assets/Flydigi/{imageFile}", UriKind.Absolute));
        }

        /* ------------------------------------------------------------------ */
        /*  Temperature Polling                                                */
        /* ------------------------------------------------------------------ */

        private void StartTemperaturePolling()
        {
            StopTemperaturePolling();

            // Read immediately, then every 2 seconds
            _tempTimer = new System.Threading.Timer(
                _ => UpdateTemperatureDisplay(),
                null,
                TimeSpan.Zero,
                TimeSpan.FromSeconds(2));
        }

        private void StopTemperaturePolling()
        {
            _tempTimer?.Dispose();
            _tempTimer = null;
        }

        private void UpdateTemperatureDisplay()
        {
            // Guard against timer firing after page unload / service disposal
            if (_coolerService == null)
                return;

            _tempProvider ??= new FlydigiTemperatureProvider();

            try
            {
                var settings = _coolerService.GetSettings();
                var source = settings?.TempSource.ToLowerInvariant() ?? "max";
                double? temp = source switch
                {
                    "cpu" => _tempProvider.GetCpuTemperature(),
                    "gpu" => _tempProvider.GetGpuTemperature(),
                    _ => _tempProvider.GetMaxTemperature()
                };

                var dispatcher = Application.Current?.Dispatcher;
                if (dispatcher == null)
                    return;

                dispatcher.Invoke(() =>
                {
                    tbTemperature.Text = temp.HasValue ? $"{temp.Value:F1}°C" : "N/A";
                });
            }
            catch
            {
                // Non-critical: temperature reading failures shouldn't crash the UI
                var dispatcher = Application.Current?.Dispatcher;
                if (dispatcher != null)
                    dispatcher.Invoke(() => tbTemperature.Text = "N/A");
            }
        }

        private void SetControlsEnabled(bool enabled)
        {
            cbxFanMode.IsEnabled = enabled;
            nudRpm.IsEnabled = enabled;
            sliderRpm.IsEnabled = enabled;
            btnGearQuiet.IsEnabled = enabled;
            btnGearStandard.IsEnabled = enabled;
            btnGearStrong.IsEnabled = enabled;
            btnGearOverclock.IsEnabled = enabled;
            cbxGearSubLevel.IsEnabled = enabled;
            cbxCurveProfile.IsEnabled = enabled;
            btnEditCurve.IsEnabled = enabled;
            cbxRgbMode.IsEnabled = enabled;
            nudRgbR.IsEnabled = enabled;
            nudRgbG.IsEnabled = enabled;
            nudRgbB.IsEnabled = enabled;
            sliderRgbR.IsEnabled = enabled;
            sliderRgbG.IsEnabled = enabled;
            sliderRgbB.IsEnabled = enabled;
            nudRgbBrightness.IsEnabled = enabled;
            sliderRgbBrightness.IsEnabled = enabled;
            cbxRgbSpeed.IsEnabled = enabled;
            if (mcRotationColors is not null) mcRotationColors.IsEnabled = enabled;
            tsAutoConnect.IsEnabled = enabled;
            tsPowerOnStart.IsEnabled = enabled;
            cbxSmartStartStop.IsEnabled = enabled;
            tsGearLight.IsEnabled = enabled;
            tsAvoidance.IsEnabled = enabled;
            nudAvoidanceStart.IsEnabled = enabled;
            nudAvoidanceEnd.IsEnabled = enabled;
            cbxTempSource.IsEnabled = enabled;
            tsLearning.IsEnabled = enabled;
            cbxLearningBias.IsEnabled = enabled;
        }

        private static int GetRgbModeIndex(string mode) => mode.ToLowerInvariant() switch
        {
            "off" => 0,
            "smarttemp" => 1,
            "static" => 2,
            "rotation" => 3,
            "flowing" => 4,
            "breathing" => 5,
            _ => 0
        };

        private static int GetRgbSpeedIndex(string speed) => speed.ToLowerInvariant() switch
        {
            "fast" => 0,
            "medium" => 1,
            "slow" => 2,
            _ => 1
        };

        /* ------------------------------------------------------------------ */
        /*  Connection Management                                              */
        /* ------------------------------------------------------------------ */

        private async void btnScan_Click(object sender, RoutedEventArgs e)
        {
            if (_coolerService == null) return;

            btnScan.IsEnabled = false;
            btnScan.Content = "Scanning...";

            try
            {
                var devices = _coolerService.DiscoverDevices();
                cbxDevices.Items.Clear();

                foreach (var device in devices)
                {
                    cbxDevices.Items.Add(new ComboBoxItem
                    {
                        Content = device.ModelName,
                        Tag = device
                    });
                }

                if (devices.Count > 0)
                {
                    cbxDevices.SelectedIndex = 0;
                }
                else
                {
                    MessageBox.Show("No Flydigi devices found.\nMake sure the cooling pad is connected via USB.",
                        "No Devices", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to scan for devices: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }

            btnScan.IsEnabled = true;
            btnScan.Content = "Scan";
        }

        private async void btnConnect_Click(object sender, RoutedEventArgs e)
        {
            if (_coolerService == null) return;

            // Get selected device path
            string? devicePath = null;
            if (cbxDevices.SelectedItem is ComboBoxItem selectedItem && selectedItem.Tag is FlydigiCoolerDeviceInfo info)
            {
                devicePath = info.DevicePath;
            }
            else if (!string.IsNullOrEmpty(_coolerService.GetSettings().LastDevicePath))
            {
                devicePath = _coolerService.GetSettings().LastDevicePath;
            }

            if (string.IsNullOrEmpty(devicePath))
            {
                MessageBox.Show("Please scan for devices first, then select a device to connect.",
                    "No Device Selected", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            btnConnect.Content = "Connecting...";

            try
            {
                var connected = await _coolerService.ConnectAsync(devicePath);
                if (!connected)
                {
                    MessageBox.Show("Failed to connect to the device.",
                        "Connection Failed", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Connection error: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void btnDisconnect_Click(object sender, RoutedEventArgs e)
        {
            if (_coolerService == null) return;

            btnDisconnect.Content = "Disconnecting...";

            try
            {
                _coolerService.DisconnectAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Disconnection error: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
