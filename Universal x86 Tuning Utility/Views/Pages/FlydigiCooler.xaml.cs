using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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
        private Bs1Service? _bs1Service;
        private DeviceApplier? _deviceApplier;
        private MultiColorPickerControl? mcRotationColors;
        private Wpf.Ui.Controls.Snackbar? _adaptiveSnackbar;

        /// <summary>True when a BS1 (BLE-only) device is connected. False for BS2+ HID devices.</summary>
        private bool _isBs1Device;

        private FlydigiSmartControl? _smartControl;
        private Bs1SmartControl? _bs1SmartControl;
        private FlydigiTemperatureProvider? _tempProvider;
        private System.Threading.Timer? _tempTimer;

        // Debounce timers for auto-apply
        private System.Threading.Timer? _rpmApplyTimer;
        private System.Threading.Timer? _rgbApplyTimer;

        /// <summary>True once initial settings (RGB, RPM) have been applied to the device after first connect.</summary>
        private bool _hasAppliedInitialSettings;

        /// <summary>True once UpdateConnectionUI(true) has run for the current connection session. Prevents redundant BLE writes on page navigation.</summary>
        private bool _hasAppliedConnectionUI;

        /// <summary>The currently selected fan curve profile for auto control.</summary>
        private FlydigiFanCurveProfile? _activeProfile;

        /// <summary>True while OnFlydigiPresetApplied is updating UI controls. Suppresses selection-changed side effects.</summary>
        private bool _isSyncingFromPreset;

        /// <summary>Discrete animation timer for the glow breathing effect.</summary>
        private System.Threading.Timer? _glowPulseTimer;
        private bool _glowPulseDirection = true;
        private volatile bool _glowActive;

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
                _bs1Service = App.GetService<Bs1Service>();

                if (_coolerService == null && _bs1Service == null)
                {
                    return;
                }

                // Determine active device type from which service is actually connected
                bool initBs1Connected = _bs1Service?.IsConnected == true;
                bool initHidConnected = _coolerService?.IsConnected == true;
                _isBs1Device = initBs1Connected && !initHidConnected;

                // Create multi-color picker programmatically (XAML codegen doesn't generate the field)
                mcRotationColors = new MultiColorPickerControl();
                mcRotationColors.ColorsChanged += OnRotationColorsChanged;
                mcRotationColorsHost.Children.Add(mcRotationColors);

                // Get DeviceApplier for centralized device commands and override management
                _deviceApplier = App.GetService<DeviceApplier>();

                LoadSettingsToUI();

                // Update page title with detected cooler model name
                UpdatePageTitle();

                // Update device image based on device type
                if (_isBs1Device)
                {
                    var bs1Source = new BitmapImage(new Uri("pack://application:,,,/Assets/Flydigi/bs1.png"));
                    imgDevice.Source = bs1Source;
                    UpdateGlowOpacityMask("bs1.png");
                }

                // Apply BS1-specific UI restrictions (no RGB, no device settings, no sub-gear)
                ApplyDeviceTypeUI();

                // Subscribe to service events early so the page responds to connection
                // events even before it's navigated to (e.g., auto-connect at startup).
                SubscribeToServiceEvents();

                // Subscribe to DeviceApplier events for override state and preset applications
                if (_deviceApplier != null)
                {
                    _deviceApplier.FlydigiOverrideChanged += OnFlydigiOverrideChanged;
                    _deviceApplier.FlydigiPresetApplied += OnFlydigiPresetApplied;
                }

                // Reflect any already-connected state (auto-connect may have fired before page load)
                bool bs1Connected = _bs1Service?.IsConnected == true;
                bool hidConnected = _coolerService?.IsConnected == true;
                if (bs1Connected || hidConnected)
                {
                    _isBs1Device = bs1Connected;
                    UpdateConnectionUI(true, bs1Connected);
                }
            }
            catch (Exception ex)
            {
                // Log but don't block page load
                System.Diagnostics.Debug.WriteLine($"FlydigiCooler init error: {ex.Message}");
            }
        }

        /// <summary>
        /// Subscribes to connection/status/fan events on the active service.
        /// Called once from InitializePage() — not on every page navigation.
        /// </summary>
        private void SubscribeToServiceEvents()
        {
            if (_bs1Service != null)
            {
                _bs1Service.ConnectionStateChanged += OnConnectionStateChanged;
                _bs1Service.StatusChanged += OnStatusChanged;
                _bs1Service.FanDataReceived += OnFanDataReceived;
            }

            if (_coolerService != null)
            {
                _coolerService.ConnectionStateChanged += OnConnectionStateChanged;
                _coolerService.StatusChanged += OnStatusChanged;
                _coolerService.FanDataReceived += OnFanDataReceived;
            }
        }

        private void FlydigiCooler_Loaded(object sender, RoutedEventArgs e)
        {
            // Lightweight: only reflect current state on navigation.
            // Heavy event subscription is done once in InitializePage().

            // Apply current override state (may already be overridden from startup or Adaptive page)
            // Only if the override state actually changed since last check.
            // Use the DeviceApplier flag as the source of truth — the overlay/snackbar may
            // be stale after Unloaded cleared _adaptiveSnackbar but left the overlay visible.
            bool isOverridden = _deviceApplier?.IsFlydigiOverridden == true;
            bool isUiOverridden = _adaptiveSnackbar != null;
            if (isOverridden != isUiOverridden)
            {
                ApplyOverrideState(isOverridden);
            }

            // If override is active, sync UI from the last-applied preset so the user sees
            // the profile's values even if they navigate to this page after the override fires.
            if (isOverridden && _deviceApplier?.LastAppliedFlydigiPreset is { } preset)
            {
                SyncUiFromPreset(preset);
            }

            // Apply saved RGB settings only on first connect, not on every page navigation
            // Skip if Adaptive Mode is overriding (the profile owns the device)
            bool bs1Connected = _bs1Service?.IsConnected == true;
            bool hidConnected = _coolerService?.IsConnected == true;
            bool isConnected = bs1Connected || hidConnected;

            if (isConnected && !_hasAppliedInitialSettings &&
                _deviceApplier?.IsFlydigiOverridden != true)
            {
                ApplyRgbAsync();
                _hasAppliedInitialSettings = true;
            }

            // Reflect current connection state if already connected.
            // Skip the heavy UpdateConnectionUI() if we already ran it for this
            // connection session — it triggers BLE writes that cause visible lag.
            if (isConnected)
            {
                _isBs1Device = bs1Connected;

                // Only run the full connection UI update on the first navigation
                // after connect (e.g., auto-connect before page was visited).
                if (!_hasAppliedConnectionUI)
                {
                    System.Diagnostics.Debug.WriteLine($"[FlydigiCooler_Loaded] Calling UpdateConnectionUI, bs1Connected={bs1Connected}, current cbxFanMode.SelectedIndex={cbxFanMode.SelectedIndex}");
                    UpdateConnectionUI(true, bs1Connected);
                    System.Diagnostics.Debug.WriteLine($"[FlydigiCooler_Loaded] After UpdateConnectionUI, cbxFanMode.SelectedIndex={cbxFanMode.SelectedIndex}");
                }

                // Keep temperature polling alive — restart only if timer was disposed
                if (_tempTimer == null)
                {
                    StartTemperaturePolling();
                }
            }
            else
            {
                SetControlsEnabled(false);

                 // Auto-scan once when entering the page with no active connection
                RunAutoScanAsync();
            }
        }

        private void Page_Unloaded(object? sender, RoutedEventArgs e)
        {
            // Note: service events are subscribed once in InitializePage() so they remain
            // active even when the page is not visible (e.g., auto-connect at startup).
            // We don't unsubscribe because the page lifetime matches the app lifetime.

            // DeviceApplier events also stay subscribed — the page lifetime matches the
            // app lifetime. Unsubscribing here caused a permanent disconnect: the constructor
            // subscribes once, the first navigation away unsubscribes, and Loaded never
            // re-subscribes. After that, the page never receives override state changes.

            // Keep auto-control and temperature polling alive across page navigation.
            // The page lifetime matches the app lifetime — stopping/restarting these
            // on every navigation causes visible lag and unnecessary BLE traffic.

            // Keep debounce timers alive (they're lightweight and self-managing)
            // Reset snackbar state so it can show again on next page visit
            _adaptiveSnackbar = null;

            // Stop glow animation timer to avoid running in unloaded state
            StopGlowPulse();
        }

        /* ------------------------------------------------------------------ */
        /*  Device Type UI Adjustments                                         */
        /* ------------------------------------------------------------------ */

        /// <summary>
        /// Applies UI visibility changes based on whether the connected device is a BS1 (BLE-only)
        /// or BS2+ (HID). BS1 has no RGB, no device settings, no sub-gear levels, and a lower
        /// max RPM (3000 vs 4000).
        /// </summary>
        private void ApplyDeviceTypeUI()
        {
            if (_isBs1Device)
            {
                // Hide RGB section — BS1 has no RGB
                cardRgb.Visibility = Visibility.Collapsed;

                // Hide Device Settings section — BS1 has no device settings
                cardDeviceSettings.Visibility = Visibility.Collapsed;

                // Hide sub-gear selector — BS1 has no sub-levels
                spGearSubLevel.Visibility = Visibility.Collapsed;

                // Adjust RPM range to BS1 max (3000)
                sliderRpm.Maximum = 3000;
                nudRpm.Maximum = 3000;
                nudAvoidanceStart.Maximum = 3000;
                nudAvoidanceEnd.Maximum = 3000;

                // Update RPM label
                foreach (var child in spManual.Children)
                {
                    if (child is TextBlock tb && tb.Text?.Contains("4000") == true)
                        tb.Text = "RPM (1300 - 3000)";
                }

                // Clamp any existing manual RPM that's out of range
                if (nudRpm.Value > 3000)
                    nudRpm.Value = 3000;
            }
            else
            {
                // Restore HID device UI (BS2/BS2 Pro/BS3/BS3 Pro)
                cardRgb.Visibility = Visibility.Visible;
                cardDeviceSettings.Visibility = Visibility.Visible;
                spGearSubLevel.Visibility = Visibility.Visible;

                // Restore RPM range to HID max (4000)
                sliderRpm.Maximum = 4000;
                nudRpm.Maximum = 4000;
                nudAvoidanceStart.Maximum = 4000;
                nudAvoidanceEnd.Maximum = 4000;

                // Restore RPM label
                foreach (var child in spManual.Children)
                {
                    if (child is TextBlock tb && tb.Text?.Contains("3000") == true)
                        tb.Text = "RPM (1300 - 4000)";
                }
            }
        }

        /* ------------------------------------------------------------------ */
        /*  Fan Mode Switching                                                 */
        /* ------------------------------------------------------------------ */

        private void cbxFanMode_SelectionChanged(object sender, EventArgs e)
        {
            // Suppress side effects when UI is being synced from a preset event
            if (_isSyncingFromPreset)
                return;

            UpdateFanModeUI();

            // Persist fan mode to settings
            if (_isBs1Device && _bs1Service != null)
            {
                var settings = _bs1Service.GetSettings();
                settings.FanMode = cbxFanMode.SelectedIndex;
                System.Diagnostics.Debug.WriteLine($"[BS1 SelectionChanged] Saving FanMode={cbxFanMode.SelectedIndex}");
                _bs1Service.PersistSettings();
            }
            else if (_coolerService != null)
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
            var wasAuto = _smartControl != null || _bs1SmartControl != null;

            spManual.Visibility = enteringManual ? Visibility.Visible : Visibility.Collapsed;
            spGear.Visibility = selectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
            spAuto.Visibility = enteringAuto ? Visibility.Visible : Visibility.Collapsed;

            if (enteringAuto && !wasAuto)
            {
                if (_isBs1Device)
                    StartAutoControlBs1();
                else
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
            // Suppress side effects when UI is being synced from a preset event
            if (_isSyncingFromPreset)
                return;

            // Debounce: wait 300ms after user stops dragging the slider
            ResetDebounceTimer(ref _rpmApplyTimer, 300, ApplyRpmAsync);
        }

        private void nudRpm_ValueChanged(object? sender, RoutedEventArgs e)
        {
            // Suppress side effects when UI is being synced from a preset event
            if (_isSyncingFromPreset)
                return;

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

            var rpm = nudRpm.Value.HasValue ? (ushort)nudRpm.Value.Value : (ushort)0;

            try
            {
                if (_isBs1Device && _bs1Service != null && _bs1Service.IsConnected)
                {
                    await _bs1Service.WriteRpmAsync(rpm);

                    var settings = _bs1Service.GetSettings();
                    settings.ManualRpm = rpm;
                    _bs1Service.PersistSettings();
                }
                else if (_coolerService != null && _coolerService.IsConnected)
                {
                    await _coolerService.WriteRealtimeRpmAsync(rpm);

                    var settings = _coolerService.GetSettings();
                    settings.ManualRpm = rpm;
                    _coolerService.PersistSettings();
                }
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
            try
            {
                if (_isBs1Device && _bs1Service != null && _bs1Service.IsConnected)
                {
                    await _bs1Service.WriteGearAsync(gear);
                    var settings = _bs1Service.GetSettings();
                    settings.ManualGear = gear;
                    _bs1Service.PersistSettings();
                }
                else if (_coolerService != null && _coolerService.IsConnected)
                {
                    await _coolerService.WriteGearAsync(gear);
                    var settings = _coolerService.GetSettings();
                    settings.ManualGear = gear;
                    _coolerService.PersistSettings();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to set gear: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void cbxGearSubLevel_SelectionChanged(object sender, EventArgs e)
        {
            if (_isSyncingFromPreset) return;
            if (_coolerService == null || !_coolerService.IsConnected) return;

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

        private void StartAutoControlBs1()
        {
            if (_bs1Service == null || !_bs1Service.IsConnected)
                return;

            if (_activeProfile == null)
                return;

            try
            {
                StopAutoControl();

                _tempProvider ??= new FlydigiTemperatureProvider();
                _bs1SmartControl = new Bs1SmartControl(_bs1Service, _tempProvider);
                _bs1SmartControl.ActiveProfile = _activeProfile;
                _bs1SmartControl.Settings = _bs1Service.GetSettings();
                _bs1SmartControl.TempSource = _bs1Service.GetSettings().TempSource;

                _bs1SmartControl.Start();
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

            if (_bs1SmartControl != null)
            {
                try { _bs1SmartControl.Stop(); } catch { /* ignore */ }
                try { _bs1SmartControl.Dispose(); } catch { /* ignore */ }
                _bs1SmartControl = null;
            }

            if (_tempProvider != null)
            {
                try { _tempProvider.Dispose(); } catch { /* ignore */ }
                _tempProvider = null;
            }
        }

        /* ------------------------------------------------------------------ */
        /*  Adaptive Mode Override (Event-Driven)                              */
        /* ------------------------------------------------------------------ */

        /// <summary>
        /// Event handler for <see cref="DeviceApplier.FlydigiOverrideChanged"/>.
        /// Applies or lifts the override UI state (snackbar, control enablement, auto control).
        /// </summary>
        private void OnFlydigiOverrideChanged(object? sender, bool isOverridden)
        {
            ApplyOverrideState(isOverridden);
        }

        /// <summary>
        /// Applies the override state: shows/hides snackbar, enables/disables controls,
        /// stops/starts auto control, and re-applies user settings when override is lifted.
        /// </summary>
        private void ApplyOverrideState(bool isOverridden)
        {
            overlayAdaptiveWarning.Visibility = isOverridden ? Visibility.Visible : Visibility.Collapsed;

            if (isOverridden)
            {
                SetControlsEnabled(false);
                StopAutoControl();
                ShowAdaptiveSnackbar();
            }
            else
            {
                // Override lifted — hide snackbar and overlay regardless of connection state.
                HideAdaptiveSnackbar();

                if (_coolerService?.IsConnected == true || _bs1Service?.IsConnected == true)
                {
                    // DeviceApplier already re-applied settings to the device in DisableFlydigiOverrideAsync.
                    // We just need to sync the UI to reflect the restored settings.
                    SyncUiFromSettings();

                    SetControlsEnabled(true);

                    // Restart glow if it was stopped
                    StartGlowPulse();
                    ApplyAccentGlow();

                // Restart auto control if we're in Auto mode and it was stopped by the override
                if (cbxFanMode.SelectedIndex == 2 && _smartControl == null && _bs1SmartControl == null && _activeProfile != null)
                {
                    if (_isBs1Device)
                        StartAutoControlBs1();
                    else
                        StartAutoControl();
                }
                }
            }
        }

        /// <summary>
        /// Syncs UI controls from the service's restored settings after override is lifted.
        /// Uses _isSyncingFromPreset guard to prevent selection-changed callbacks.
        /// </summary>
        private void SyncUiFromSettings()
        {
            if (_coolerService == null) return;
            var settings = _coolerService.GetSettings();

            _isSyncingFromPreset = true;
            try
            {
                cbxFanMode.SelectedIndex = Math.Clamp(settings.FanMode, 0, 2);
                nudRpm.Value = settings.ManualRpm;
                cbxGearSubLevel.SelectedIndex = Math.Clamp(settings.ManualGear - 1, 0, 3);

                cbxRgbMode.SelectedIndex = GetRgbModeIndex(settings.RgbMode);
                UpdateRgbColorVisibility();
                nudRgbR.Value = settings.R;
                nudRgbG.Value = settings.G;
                nudRgbB.Value = settings.B;
                nudRgbBrightness.Value = settings.Brightness;
            }
            finally
            {
                _isSyncingFromPreset = false;
            }
        }

        /// <summary>
        /// Re-applies the Flydigi page's saved settings to the device after override is lifted.
        /// This ensures the device reflects the page's user settings, not whatever
        /// Adaptive Mode last commanded.
        /// </summary>
        private void ReapplyUserSettingsToDevice()
        {
            if (_coolerService == null || !_coolerService.IsConnected)
                return;

            var settings = _coolerService.GetSettings();

            // Apply RGB from restored settings
            try
            {
                ApplyRgbFromSettings(settings);
            }
            catch { /* non-critical on override-lift */ }

            // Apply fan from restored settings
            try
            {
                ApplyFanFromSettings(settings);
            }
            catch { /* non-critical on override-lift */ }
        }

        /// <summary>
        /// Applies RGB to the device from the given settings object.
        /// </summary>
        private async Task ApplyRgbFromSettings(Bs2ProSettings settings)
        {
            if (_coolerService == null || !_coolerService.IsConnected)
                return;

            try
            {
                switch (settings.RgbMode)
                {
                    case "Off":
                        await _coolerService.WriteRgbOffAsync();
                        break;
                    case "Static":
                        await _coolerService.WriteRgbStaticAsync(settings.R, settings.G, settings.B, settings.Brightness);
                        break;
                    case "Breathing":
                        await _coolerService.WriteRgbBreathingAsync(settings.R, settings.G, settings.B, settings.Brightness);
                        break;
                    case "SmartTemp":
                        await _coolerService.WriteRgbSmartTempAsync();
                        break;
                    case "Flowing":
                        await _coolerService.WriteRgbFlowingAsync(settings.RgbSpeed, settings.Brightness);
                        break;
                    case "Rotation":
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
                                await _coolerService.WriteRgbRotationMultiAsync(colors, settings.RotationSpeed, settings.RotationBrightness);
                        }
                        else
                            await _coolerService.WriteRgbRotationAsync(settings.R, settings.G, settings.B, settings.RotationSpeed, settings.RotationBrightness);
                        break;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"FlydigiCooler: Failed to re-apply RGB on override-lift: {ex.Message}");
            }
        }

        /// <summary>
        /// Applies fan to the device from the given settings object.
        /// </summary>
        private async Task ApplyFanFromSettings(Bs2ProSettings settings)
        {
            if (_coolerService == null || !_coolerService.IsConnected)
                return;

            try
            {
                switch (settings.FanMode)
                {
                    case 0: // Manual RPM
                        if (settings.ManualRpm > 0)
                            await _coolerService.WriteRealtimeRpmAsync(settings.ManualRpm);
                        break;
                    case 1: // Gear
                        if (settings.ManualGear > 0)
                            await _coolerService.WriteGearAsync((byte)settings.ManualGear);
                        break;
                    case 2: // Curve — handled by FlydigiSmartControl (restart in ApplyOverrideState)
                        break;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"FlydigiCooler: Failed to re-apply fan on override-lift: {ex.Message}");
            }
        }

        /// <summary>
        /// Creates and shows the Adaptive Mode override snackbar.
        /// Defers showing until the SnackbarPresenter is in the visual tree to avoid
        /// the snackbar being created before the page is navigated to.
        /// </summary>
        private void ShowAdaptiveSnackbar()
        {
            // Hide existing snackbar before showing a new one
            if (_adaptiveSnackbar != null)
                SnackbarPresenter.HideCurrent();

            _adaptiveSnackbar = new Wpf.Ui.Controls.Snackbar(SnackbarPresenter)
            {
                Title = "Adaptive Mode Override",
                Content = "Adaptive Mode is currently controlling the Flydigi cooler. Controls on this page are currently disabled.",
                Appearance = Wpf.Ui.Controls.ControlAppearance.Primary,
                Icon = new Wpf.Ui.Controls.SymbolIcon(Wpf.Ui.Controls.SymbolRegular.Warning24),
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

        /// <summary>
        /// Hides the Adaptive Mode override snackbar.
        /// </summary>
        private void HideAdaptiveSnackbar()
        {
            SnackbarPresenter.HideCurrent();
            _adaptiveSnackbar = null;
        }

        /// <summary>
        /// Event handler for <see cref="DeviceApplier.FlydigiPresetApplied"/>.
        /// Syncs the Flydigi page's UI controls to reflect the profile's values.
        /// Uses _isSyncingFromPreset flag to suppress selection-changed side effects.
        /// </summary>
        private void OnFlydigiPresetApplied(object? sender, FlydigiPresetAppliedEventArgs e)
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
        private void SyncUiFromPreset(FlydigiPresetAppliedEventArgs e)
        {
            cbxFanMode.SelectedIndex = GetFanModeIndex(e.FanMode);
            if (e.Gear.HasValue)
                cbxGearSubLevel.SelectedIndex = Math.Clamp(e.Gear.Value - 1, 0, 3);
            if (e.Rpm.HasValue)
                nudRpm.Value = e.Rpm.Value;

            cbxRgbMode.SelectedIndex = GetRgbModeIndex(e.RgbMode);
            UpdateRgbColorVisibility();
            nudRgbR.Value = e.R;
            nudRgbG.Value = e.G;
            nudRgbB.Value = e.B;
            nudRgbBrightness.Value = e.Brightness;
        }

        private static int GetFanModeIndex(string mode) => mode switch
        {
            "Off" => -1,       // No "Off" in the Flydigi page combo box
            "Gear" => 1,        // Gear Presets
            "Rpm" => 0,         // Manual
            "Curve" => 2,       // Auto (Curve)
            _ => -1
        };

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
                if (_isInitialized)
                {
                    if (_isBs1Device && _bs1Service != null)
                    {
                        var settings = _bs1Service.GetSettings();
                        settings.SelectedCurveProfile = profile.Name;
                        _bs1Service.PersistSettings();
                    }
                    else if (_coolerService != null)
                    {
                        var settings = _coolerService.GetSettings();
                        settings.SelectedCurveProfile = profile.Name;
                        _coolerService.PersistSettings();
                    }
                }

                // Re-apply if auto control is active
                if (_smartControl != null)
                {
                    _smartControl.ActiveProfile = _activeProfile;
                }
                else if (_bs1SmartControl != null)
                {
                    _bs1SmartControl.ActiveProfile = _activeProfile;
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
            string? customCurveJson = null;
            if (_isBs1Device && _bs1Service != null)
            {
                customCurveJson = _bs1Service.GetSettings().CustomCurveJson;
            }
            else if (_coolerService != null)
            {
                customCurveJson = _coolerService.GetSettings().CustomCurveJson;
            }

            if (!string.IsNullOrEmpty(customCurveJson))
            {
                try
                {
                    var custom = FlydigiFanCurveProfile.FromJSON(customCurveJson);
                    cbxCurveProfile.Items.Add(new ComboBoxItem { Content = custom.Name, Tag = custom });
                }
                catch
                {
                    // Corrupted JSON — ignore and let user recreate
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
            if (_isBs1Device && _bs1Service != null)
            {
                var settings = _bs1Service.GetSettings();
                settings.CustomCurveJson = _activeProfile.ToJSON();
                settings.SelectedCurveProfile = "Custom";
                _bs1Service.PersistSettings();
            }
            else if (_coolerService != null)
            {
                var settings = _coolerService.GetSettings();
                settings.CustomCurveJson = _activeProfile.ToJSON();
                settings.SelectedCurveProfile = "Custom";
                _coolerService.PersistSettings();
            }

            // Re-apply if auto control is active
            if (_bs1SmartControl != null)
            {
                _bs1SmartControl.ActiveProfile = _activeProfile;
            }
            else if (_smartControl != null)
            {
                _smartControl.ActiveProfile = _activeProfile;
            }
        }

        /* ------------------------------------------------------------------ */
        /*  RGB Control                                                        */
        /* ------------------------------------------------------------------ */

        private void cbxRgbMode_SelectionChanged(object sender, EventArgs e)
        {
            // Suppress side effects when UI is being synced from a preset event
            if (_isSyncingFromPreset)
                return;

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
            // Suppress side effects when UI is being synced from a preset event
            if (_isSyncingFromPreset)
                return;

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
            if (_isBs1Device && _bs1Service != null)
            {
                var settings = _bs1Service.GetSettings();
                settings.AutoConnect = true;
                _bs1Service.PersistSettings();
            }
            else if (_coolerService != null)
            {
                var settings = _coolerService.GetSettings();
                settings.AutoConnect = true;
                _coolerService.PersistSettings();
            }
        }

        private void tsAutoConnect_Unchecked(object sender, RoutedEventArgs e)
        {
            if (_isBs1Device && _bs1Service != null)
            {
                var settings = _bs1Service.GetSettings();
                settings.AutoConnect = false;
                _bs1Service.PersistSettings();
            }
            else if (_coolerService != null)
            {
                var settings = _coolerService.GetSettings();
                settings.AutoConnect = false;
                _coolerService.PersistSettings();
            }
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
                bool isBs1Sender = sender == _bs1Service;
                UpdateConnectionUI(connected, isBs1Sender);
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
                // Only show RPM data when actually connected
                if (spConnectedState.Visibility == Visibility.Visible)
                    tbCurrentRpm.Text = $"{data.CurrentRpm} RPM";
            });
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

        /// <summary>Updates the glow overlay's OpacityMask to match the device image.</summary>
        private void UpdateGlowOpacityMask(string imageFile)
        {
            try
            {
                var maskBrush = new ImageBrush
                {
                    ImageSource = new BitmapImage(new Uri($"pack://application:,,,/Assets/Flydigi/{imageFile}", UriKind.Absolute)),
                    Stretch = Stretch.Uniform,
                    AlignmentX = AlignmentX.Center,
                    AlignmentY = AlignmentY.Center,
                };
                rectGlowOverlay.OpacityMask = maskBrush;
            }
            catch { /* element detached */ }
        }

        /* ------------------------------------------------------------------ */
        /*  Helpers                                                            */
        /* ------------------------------------------------------------------ */

        private void LoadSettingsToUI()
        {
            // BS1-specific settings load (simplified — no RGB, no device settings, no learning)
            if (_isBs1Device && _bs1Service != null)
            {
                var bs1Settings = _bs1Service.GetSettings();

                // Manual RPM (clamped to BS1 range)
                double bs1Rpm = bs1Settings.ManualRpm > 0
                    ? Math.Min((int)bs1Settings.ManualRpm, 3000)
                    : Bs1DefaultGearRpm.Gear0_Quiet;
                nudRpm.Value = bs1Rpm;
                sliderRpm.Value = bs1Rpm;

                // Auto-connect
                tsAutoConnect.IsChecked = bs1Settings.AutoConnect;

                // Advanced settings (avoidance, temp source)
                tsAvoidance.IsChecked = bs1Settings.AvoidanceEnabled;
                nudAvoidanceStart.Value = bs1Settings.AvoidanceStartRpm;
                nudAvoidanceEnd.Value = bs1Settings.AvoidanceEndRpm;

                cbxTempSource.SelectedIndex = bs1Settings.TempSource.ToLowerInvariant() switch
                {
                    "cpu" => 1,
                    "gpu" => 2,
                    _ => 0
                };

                // Hide learning for BS1 (not supported)
                tsLearning.Visibility = Visibility.Collapsed;
                var learningBiasPanel = tsLearning.Parent as StackPanel;
                if (learningBiasPanel != null)
                    learningBiasPanel.Visibility = Visibility.Collapsed;

                // Curve profile — restore BEFORE fan mode so _activeProfile is ready
                // when the fan mode combo box fires SelectionChanged and tries to start
                // auto control (BS1 Auto is app-side, unlike BS2+ which does it on-device).
                var bs1SavedProfile = bs1Settings.SelectedCurveProfile;
                _isInitialized = false;
                LoadCurveProfiles();
                if (!string.IsNullOrEmpty(bs1SavedProfile))
                {
                    for (int i = 0; i < cbxCurveProfile.Items.Count; i++)
                    {
                        if (cbxCurveProfile.Items[i] is ComboBoxItem ci && ci.Content?.ToString() == bs1SavedProfile)
                        {
                            cbxCurveProfile.SelectedIndex = i;
                            _activeProfile = ci.Tag as FlydigiFanCurveProfile;
                            break;
                        }
                    }
                }
                _isInitialized = true;

                // Fan mode — must come AFTER curve profile so that _activeProfile is
                // populated when SelectionChanged → StartAutoControlBs1() runs.
                int bs1FanMode = Math.Clamp(bs1Settings.FanMode, 0, 2);
                System.Diagnostics.Debug.WriteLine($"[BS1 LoadSettingsToUI] FanMode from settings={bs1Settings.FanMode}, clamped={bs1FanMode}, _activeProfile={(_activeProfile?.Name ?? "null")}");
                cbxFanMode.SelectedIndex = bs1FanMode;

                return;
            }

            // BS2+ settings load
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

        private void UpdateConnectionUI(bool connected, bool isBs1Sender = false)
        {
            if (connected)
            {
                // Determine device type from the event sender (which service fired this event)
                _isBs1Device = isBs1Sender;

                // Notify hardware detector FIRST so UpdatePageTitle reads the correct model name
                string model = _isBs1Device ? "BS1" : FlydigiHardwareDetector.GetDetectedModelName();
                FlydigiHardwareDetector.SetConnectedDeviceType(
                    _isBs1Device ? ConnectedDeviceType.BS1 : ConnectedDeviceType.Hid, model);

                // Reload settings from the active device so the page shows correct device-specific
                // settings (e.g., AutoConnect toggle) when switching between BS1 and HID
                LoadSettingsToUI();

                // Apply device-type-specific UI (hide/restore RGB, RPM range, etc.)
                // Called for both BS1 and HID so that UI restores properly when switching devices
                ApplyDeviceTypeUI();

                // Update page title with connected device name
                UpdatePageTitle();

                spConnectedState.Visibility = Visibility.Visible;
                spDisconnectedState.Visibility = Visibility.Collapsed;
                btnScan.IsEnabled = false;
                SetControlsEnabled(true);

                // Check if Adaptive Mode is overriding — this may re-disable controls
                bool isOverriddenOnConnect = _deviceApplier?.IsFlydigiOverridden == true;
                ApplyOverrideState(isOverriddenOnConnect);

                // If override is active, sync UI from the last-applied preset so the user sees
                // the profile's values, not the Flydigi page's saved settings from LoadSettingsToUI.
                if (isOverriddenOnConnect && _deviceApplier?.LastAppliedFlydigiPreset is { } preset)
                {
                    SyncUiFromPreset(preset);
                }

                if (_coolerService?.ConnectedDeviceInfo != null)
                    UpdateDeviceImage(_coolerService.ConnectedDeviceInfo.ProductId);

                // Update device image for BS1 (use bs1.png)
                if (_isBs1Device)
                    UpdateDeviceImageBs1();

                // Start standalone temperature polling
                StartTemperaturePolling();

                // Start glow effect
                StartGlowPulse();
                ApplyAccentGlow();

                // Restore button text (may have been left as "Connecting..." from the click handler)
                btnDisconnect.Content = "Disconnect";

                // Show control panels when connected
                spControls.Visibility = Visibility.Visible;

                // Ensure the fan mode sub-panels (Manual/Gear/Auto) are visible
                // matching the currently selected mode. This is needed because the
                // combo box index is restored from settings before connect, but the
                // sub-panels are hidden inside spControls which was collapsed.
                UpdateFanModeUI();

                // Apply saved RPM/gear on connect so the device doesn't stay at its
                // last power-on speed (e.g. 1300) when the user has a different saved value.
                // Skip if Adaptive Mode is overriding — the profile owns the device.
                if (_deviceApplier?.IsFlydigiOverridden != true)
                    ApplySavedFanSettingsOnConnect();

                // Mark so subsequent page navigations skip this heavy work
                _hasAppliedConnectionUI = true;
            }
            else
            {
                // The sender service disconnected. Check if the other service is still connected.
                bool bs1StillConnected = _bs1Service?.IsConnected == true;
                bool hidStillConnected = _coolerService?.IsConnected == true;

                if (isBs1Sender && hidStillConnected)
                {
                    // BS1 disconnected but HID is still connected — switch to HID
                    _isBs1Device = false;
                    _hasAppliedConnectionUI = false;

                    // Stop BS1 smart control, keep temperature polling
                    if (_bs1SmartControl != null)
                    {
                        try { _bs1SmartControl.Stop(); } catch { /* ignore */ }
                        try { _bs1SmartControl.Dispose(); } catch { /* ignore */ }
                        _bs1SmartControl = null;
                    }

                    string model = FlydigiHardwareDetector.GetDetectedModelName();
                    FlydigiHardwareDetector.SetConnectedDeviceType(ConnectedDeviceType.Hid, model);
                    ApplyDeviceTypeUI(); // Restore HID UI (RGB, etc.)

                    // Re-run the connect UI for the HID device
                    UpdateConnectionUI(true, false);
                    return;
                }
                else if (!isBs1Sender && bs1StillConnected)
                {
                    // HID disconnected but BS1 is still connected — switch to BS1
                    _isBs1Device = true;
                    _hasAppliedConnectionUI = false;

                    // Stop HID smart control
                    if (_smartControl != null)
                    {
                        try { _smartControl.Stop(); } catch { /* ignore */ }
                        try { _smartControl.Dispose(); } catch { /* ignore */ }
                        _smartControl = null;
                    }

                    FlydigiHardwareDetector.SetConnectedDeviceType(ConnectedDeviceType.BS1, "BS1");
                    ApplyDeviceTypeUI(); // Apply BS1 restrictions

                    // Re-run the connect UI for BS1
                    UpdateConnectionUI(true, true);
                    return;
                }

                // Neither service is connected — true disconnect
                _hasAppliedConnectionUI = false;

                FlydigiHardwareDetector.SetConnectedDeviceType(ConnectedDeviceType.None);

                // Stop polling first to prevent timer from overwriting cleared values
                StopAutoControl();
                StopTemperaturePolling();

                // Stop glow effect
                StopGlowPulse();

                spConnectedState.Visibility = Visibility.Collapsed;
                spDisconnectedState.Visibility = Visibility.Visible;
                tbCurrentRpm.Text = "--";
                tbTemperature.Text = "--";
                SetControlsEnabled(false);

                UpdateDeviceImage(0);

                btnConnect.Content = "Connect";
                btnScan.IsEnabled = true;

                spControls.Visibility = Visibility.Collapsed;

                _hasAppliedInitialSettings = false;
                _isBs1Device = false;

                UpdatePageTitle();

                // Auto-scan once after disconnect so the user can immediately reconnect
                RunAutoScanAsync();
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

            var source = new BitmapImage(
                new Uri($"pack://application:,,,/Assets/Flydigi/{imageFile}", UriKind.Absolute));
            imgDevice.Source = source;
            UpdateGlowOpacityMask(imageFile);
        }

        /// <summary>
        /// Sets the device image to the BS1-specific asset.
        /// </summary>
        private void UpdateDeviceImageBs1()
        {
            var source = new BitmapImage(
                new Uri("pack://application:,,,/Assets/Flydigi/bs1.png", UriKind.Absolute));
            imgDevice.Source = source;
            UpdateGlowOpacityMask("bs1.png");
        }

        /// <summary>
        /// Updates the page title with the detected cooler model name.
        /// - No device: "Flydigi Cooler Control"
        /// - BS1: "Flydigi BS1 Cooler Control"
        /// - BS2+: "Flydigi BS2 PRO Cooler Control"
        /// </summary>
        private void UpdatePageTitle()
        {
            string modelName = FlydigiHardwareDetector.GetConnectedModelName();
            if (modelName.StartsWith("Flydigi ", StringComparison.OrdinalIgnoreCase))
                tbPageTitle.Text = $"{modelName} Control";
            else
                tbPageTitle.Text = $"Flydigi {modelName} Cooler Control";
        }

        /// <summary>
        /// Applies the saved fan settings (RPM or gear) to the device on connect
        /// so it doesn't stay at whatever speed it was at when powered on.
        /// </summary>
        private async void ApplySavedFanSettingsOnConnect()
        {
            try
            {
                if (_isBs1Device && _bs1Service != null && _bs1Service.IsConnected)
                {
                    var settings = _bs1Service.GetSettings();
                    System.Diagnostics.Debug.WriteLine($"[BS1 ApplySavedFanSettingsOnConnect] FanMode={settings.FanMode}, _activeProfile={(_activeProfile?.Name ?? "null")}");
                    switch (settings.FanMode)
                    {
                        case 0: // Manual RPM
                            if (settings.ManualRpm > 0)
                                await _bs1Service.WriteRpmAsync(settings.ManualRpm);
                            break;
                        case 1: // Gear Presets
                            if (settings.ManualGear > 0)
                                await _bs1Service.WriteGearAsync((byte)settings.ManualGear);
                            break;
                        case 2: // Auto Curve - StartAutoControl will handle this
                            StartAutoControlBs1();
                            break;
                    }
                }
                else if (_coolerService != null && _coolerService.IsConnected)
                {
                    var settings = _coolerService.GetSettings();
                    switch (settings.FanMode)
                    {
                        case 0: // Manual RPM
                            if (settings.ManualRpm > 0)
                                await _coolerService.WriteRealtimeRpmAsync(settings.ManualRpm);
                            break;
                        case 1: // Gear Presets
                            if (settings.ManualGear > 0)
                                await _coolerService.WriteGearAsync((byte)settings.ManualGear);
                            break;
                        case 2: // Auto Curve - already handled by StartAutoControl in temperature polling
                            break;
                    }
                }
            }
            catch
            {
                // Non-critical: device may not be ready yet
            }
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

            // Don't update temperature if disconnected
            var checkDispatcher = Application.Current?.Dispatcher;
            if (checkDispatcher != null)
            {
                bool isConnected = false;
                checkDispatcher.Invoke(() => isConnected = (spConnectedState.Visibility == Visibility.Visible));
                if (!isConnected)
                    return;
            }

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
            btnScan.IsEnabled = false;
            btnScan.Content = "Scanning...";

            try
            {
                cbxDevices.Items.Clear();

                // Scan for HID devices (BS2+)
                if (_coolerService != null)
                {
                    var hidDevices = _coolerService.DiscoverDevices();
                    foreach (var device in hidDevices)
                    {
                        cbxDevices.Items.Add(new ComboBoxItem
                        {
                            Content = device.ModelName,
                            Tag = device
                        });
                    }
                }

                // Scan for BLE devices (BS1)
                if (_bs1Service != null)
                {
                    var bleDevices = await _bs1Service.DiscoverDevicesAsync(5000);
                    foreach (var device in bleDevices)
                    {
                        cbxDevices.Items.Add(new ComboBoxItem
                        {
                            Content = $"{device.ModelName} ({device.Name})",
                            Tag = device
                        });
                    }
                }

                if (cbxDevices.Items.Count > 0)
                {
                    cbxDevices.SelectedIndex = 0;
                }
                else
                {
                    MessageBox.Show("No Flydigi devices found.\nMake sure the cooling pad is connected (USB for BS2+, BLE for BS1).",
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

        /// <summary>
        /// Runs a background scan for HID and BLE devices, populating the device combobox
        /// with proper Tag objects so Connect can resolve the correct device.
        /// </summary>
        private void RunAutoScanAsync()
        {
            _ = Task.Run(async () =>
            {
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    btnScan.IsEnabled = false;
                    btnScan.Content = "Scanning...";
                });

                try
                {
                    List<(string Content, object Tag)> discoveredDevices = new();

                    // Scan for HID devices (BS2+)
                    if (_coolerService != null)
                    {
                        var hidDevices = _coolerService.DiscoverDevices();
                        foreach (var device in hidDevices)
                        {
                            discoveredDevices.Add((device.ModelName, (object)device));
                        }
                    }

                    // Scan for BLE devices (BS1)
                    if (_bs1Service != null)
                    {
                        var bleDevices = await _bs1Service.DiscoverDevicesAsync(5000);
                        foreach (var device in bleDevices)
                        {
                            discoveredDevices.Add(($"{device.ModelName} ({device.Name})", (object)device));
                        }
                    }

                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        cbxDevices.Items.Clear();
                        foreach (var dev in discoveredDevices)
                        {
                            cbxDevices.Items.Add(new ComboBoxItem
                            {
                                Content = dev.Content,
                                Tag = dev.Tag
                            });
                        }

                        if (cbxDevices.Items.Count > 0)
                            cbxDevices.SelectedIndex = 0;

                        btnScan.IsEnabled = true;
                        btnScan.Content = "Scan";
                    });
                }
                catch
                {
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        btnScan.IsEnabled = true;
                        btnScan.Content = "Scan";
                    });
                }
            });
        }

        private async void btnConnect_Click(object sender, RoutedEventArgs e)
        {
            if (_coolerService == null && _bs1Service == null) return;

            btnConnect.Content = "Connecting...";
            btnConnect.IsEnabled = false;

            try
            {
                bool connected = false;
                bool isHidTarget = false; // Track which device type the user selected

                // Determine device type from selected item
                if (cbxDevices.SelectedItem is ComboBoxItem selectedItem)
                {
                    if (selectedItem.Tag is FlydigiCoolerDeviceInfo hidInfo)
                    {
                        // HID device (BS2+)
                        isHidTarget = true;
                        if (_coolerService != null)
                            connected = await _coolerService.ConnectAsync(hidInfo.DevicePath);
                    }
                    else if (selectedItem.Tag is Bs1DeviceInfo bleInfo)
                    {
                        // BLE device (BS1)
                        if (_bs1Service != null)
                        {
                            connected = await _bs1Service.ConnectAsync(bleInfo.Address);
                            if (connected)
                            {
                                _isBs1Device = true;
                                ApplyDeviceTypeUI();
                            }
                        }
                    }
                }

                // Fallback: try last known device of the SAME type the user selected.
                // Do not cross-fallback (HID failure should not fall back to BLE, and vice versa).
                if (!connected && isHidTarget && _coolerService != null &&
                    !string.IsNullOrEmpty(_coolerService.GetSettings().LastDevicePath))
                {
                    connected = await _coolerService.ConnectAsync(_coolerService.GetSettings().LastDevicePath);
                }

                if (!connected && !isHidTarget && _bs1Service != null)
                {
                    var bs1Settings = _bs1Service.GetSettings();
                    if (!string.IsNullOrEmpty(bs1Settings.LastDeviceAddress))
                    {
                        connected = await _bs1Service.ConnectAsync(bs1Settings.LastDeviceAddress);
                        if (connected)
                        {
                            _isBs1Device = true;
                            ApplyDeviceTypeUI();
                        }
                    }
                }

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
            finally
            {
                btnConnect.Content = "Connect";
                btnConnect.IsEnabled = true;
            }
        }

        private async void btnDisconnect_Click(object sender, RoutedEventArgs e)
        {
            btnDisconnect.Content = "Disconnecting...";

            try
            {
                if (_isBs1Device && _bs1Service != null)
                    await _bs1Service.DisconnectAsync();
                else if (_coolerService != null)
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
