using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Uniwill_Control_Utility.Scripts;
using Uniwill_Control_Utility.Views.Controls;
using Uniwill_Control_Utility.Models;
using Uniwill_Control_Utility.Services;

namespace Uniwill_Control_Utility.Views.Pages
{
    /// <summary>
    /// Interaction logic for FlydigiCooler page.
    /// Provides fan speed, gear presets, auto curve control, RGB, and device settings
    /// for Flydigi BS series cooling pads.
    /// </summary>
    public partial class FlydigiCooler : Page, IDisposable
    {
        private bool _isInitialized;
        private bool _disposed;
        private FlydigiCoolerHidService? _coolerService;
        private FlydigiCoolerBleService? _bs1Service;
        private DeviceApplier? _deviceApplier;
        private MultiColorPickerControl? mcRotationColors;
        private Wpf.Ui.Controls.Snackbar? _adaptiveSnackbar;

        /// <summary>True when a BS1 (BLE-only) device is connected. False for BS2+ HID devices.</summary>
        private bool _isBs1Device;

        private FlydigiSmartControl? _smartControl;
        private FlydigiBs1SmartControl? _bs1SmartControl;
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

        private const string Bs1ImageUri = "pack://application:,,,/Assets/Flydigi/bs1.png";
        private const string FlydigiAssetPrefix = "pack://application:,,,/Assets/Flydigi/";

        // Device RPM limits
        private const double Bs1MaxRpm = 3000;
        private const double HidMaxRpm = 4000;

        // Glow animation parameters
        private const double GlowOpacityMin = 0.2;
        private const double GlowOpacityMax = 0.5;
        private const double GlowOpacityStep = 0.008;
        private const int GlowPulseIntervalMs = 80;

        // Fan mode indices (cbxFanMode)
        private const int FanModeManual = 0;
        private const int FanModeGear = 1;
        private const int FanModeAuto = 2;

        // RGB mode indices (cbxRgbMode)
        private const int RgbModeOff = 0;
        private const int RgbModeSmartTemp = 1;
        private const int RgbModeStatic = 2;
        private const int RgbModeRotation = 3;
        private const int RgbModeFlowing = 4;
        private const int RgbModeBreathing = 5;

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
                _coolerService = App.GetService<FlydigiCoolerHidService>();
                _bs1Service = App.GetService<FlydigiCoolerBleService>();

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

                // Subscribe to embedded curve editor changes for auto-persist
                FlydigiCurveEditor.CurveChanged += OnFlydigiCurveEditorCurveChanged;

                // Get DeviceApplier for centralized device commands and override management
                _deviceApplier = App.GetService<DeviceApplier>();

                LoadSettingsToUI();

                // Update page title with detected cooler model name
                UpdatePageTitle();

                // Update device image based on device type
                if (_isBs1Device)
                {
                    var bs1Source = new BitmapImage(new Uri(Bs1ImageUri));
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

                // Subscribe to settings changed events so the page responds to changes
                // from external callers (e.g., tray menu).
                if (_coolerService != null)
                    _coolerService.SettingsChanged += OnFlydigiSettingsChanged;
                if (_bs1Service != null)
                    _bs1Service.SettingsChanged += OnFlydigiSettingsChanged;

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

                // Restart glow pulse on every navigation back — Page_Unloaded stops it.
                StartGlowPulse();
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

                // Hide device-specific settings rows and dividers — BS1 has no device settings
                DeviceSettingsDivider.Visibility = Visibility.Collapsed;
                RowPowerOnStart.Visibility = Visibility.Collapsed;
                SmartStartStopDivider.Visibility = Visibility.Collapsed;
                RowSmartStartStop.Visibility = Visibility.Collapsed;
                GearLightDivider.Visibility = Visibility.Collapsed;
                RowGearLight.Visibility = Visibility.Collapsed;
                TimeScheduleDivider.Visibility = Visibility.Collapsed;
                RowTimeSchedule.Visibility = Visibility.Collapsed;
                LearningDivider.Visibility = Visibility.Collapsed;
                RowLearningEngine.Visibility = Visibility.Collapsed;

                // Hide sub-gear selector — BS1 has no sub-levels
                spGearSubLevel.Visibility = Visibility.Collapsed;

                // Adjust RPM range to BS1 max
                sliderRpm.Maximum = Bs1MaxRpm;
                nudAvoidanceStart.Maximum = Bs1MaxRpm;
                nudAvoidanceEnd.Maximum = Bs1MaxRpm;

                // Clamp any existing manual RPM that's out of range
                if (sliderRpm.Value > Bs1MaxRpm)
                    sliderRpm.Value = Bs1MaxRpm;
            }
            else
            {
                // Restore HID device UI (BS2/BS2 Pro/BS3/BS3 Pro)
                cardRgb.Visibility = Visibility.Visible;

                // Restore device-specific settings rows and dividers
                DeviceSettingsDivider.Visibility = Visibility.Visible;
                RowPowerOnStart.Visibility = Visibility.Visible;
                SmartStartStopDivider.Visibility = Visibility.Visible;
                RowSmartStartStop.Visibility = Visibility.Visible;
                GearLightDivider.Visibility = Visibility.Visible;
                RowGearLight.Visibility = Visibility.Visible;
                TimeScheduleDivider.Visibility = Visibility.Visible;
                RowTimeSchedule.Visibility = Visibility.Visible;
                LearningDivider.Visibility = Visibility.Visible;
                RowLearningEngine.Visibility = Visibility.Visible;

                spGearSubLevel.Visibility = Visibility.Visible;

                // Restore RPM range to HID max
                sliderRpm.Maximum = HidMaxRpm;
                nudAvoidanceStart.Maximum = HidMaxRpm;
                nudAvoidanceEnd.Maximum = HidMaxRpm;
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
            var enteringAuto = selectedIndex == FanModeAuto;
            var enteringManual = selectedIndex == FanModeManual;
            var enteringGear = selectedIndex == FanModeGear;
            var wasAuto = _smartControl != null || _bs1SmartControl != null;

            spManual.Visibility = enteringManual ? Visibility.Visible : Visibility.Collapsed;

            // Gear Presets row (no divider — mutually exclusive with Manual/Auto)
            spGear.Visibility = enteringGear ? Visibility.Visible : Visibility.Collapsed;

            // Auto (Curve) row (no divider — mutually exclusive)
            spAuto.Visibility = enteringAuto ? Visibility.Visible : Visibility.Collapsed;

            // Show embedded curve editor only when in Auto mode AND Custom profile is selected
            var isCustomAuto = enteringAuto && _activeProfile?.Name == "Custom";
            var editorVisible = isCustomAuto ? Visibility.Visible : Visibility.Collapsed;
            FanCurveEditorDivider.Visibility = editorVisible;
            FlydigiCurveEditor.Visibility = editorVisible;

            if (isCustomAuto)
            {
                FlydigiCurveEditor.SetCurve(_activeProfile!);
            }

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
        /*  CardExpander click handling                                        */
        /* ------------------------------------------------------------------ */

        /// <summary>
        /// Prevents clicks on ComboBoxes nested inside CardExpander headers from
        /// also toggling the expander. The ComboBox processes the click first (bubbling),
        /// and we block it from reaching the ToggleButton above.
        /// </summary>
        private void ComboBoxInCardExpander_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
        }

        /* ------------------------------------------------------------------ */
        /*  Manual RPM                                                         */
        /* ------------------------------------------------------------------ */

        private void sliderRpm_ValueChanged(object? sender, RoutedPropertyChangedEventArgs<double> e)
        {
            // Suppress side effects when UI is being synced from a preset event
            if (_isSyncingFromPreset)
                return;

            // Update the RPM value label
            RpmValueText.Text = ((int)sliderRpm.Value).ToString();

            // Debounce: wait 300ms after user stops dragging the slider
            ResetDebounceTimer(ref _rpmApplyTimer, 300, ApplyRpmAsync);
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

            var rpm = (ushort)sliderRpm.Value;

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
                _bs1SmartControl = new FlydigiBs1SmartControl(_bs1Service, _tempProvider);
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
                try { _smartControl.Stop(); } catch (Exception) { /* Non-critical: best-effort cleanup during teardown */ }
                try { _smartControl.Dispose(); } catch (Exception) { /* Non-critical: best-effort cleanup during teardown */ }
                _smartControl = null;
            }

            if (_bs1SmartControl != null)
            {
                try { _bs1SmartControl.Stop(); } catch (Exception) { /* Non-critical: best-effort cleanup during teardown */ }
                try { _bs1SmartControl.Dispose(); } catch (Exception) { /* Non-critical: best-effort cleanup during teardown */ }
                _bs1SmartControl = null;
            }

            if (_tempProvider != null)
            {
                try { _tempProvider.Dispose(); } catch (Exception) { /* Non-critical: best-effort cleanup during teardown */ }
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
                if (cbxFanMode.SelectedIndex == FanModeAuto && _smartControl == null && _bs1SmartControl == null && _activeProfile != null)
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
                cbxFanMode.SelectedIndex = Math.Clamp(settings.FanMode, FanModeManual, FanModeAuto);
                sliderRpm.Value = settings.ManualRpm;
                RpmValueText.Text = settings.ManualRpm.ToString();
                cbxGearSubLevel.SelectedIndex = Math.Clamp(settings.ManualGear - 1, 0, 3);

                cbxRgbMode.SelectedIndex = GetRgbModeIndex(settings.RgbMode);
                UpdateRgbColorVisibility();
                nudRgbR.Value = settings.R;
                nudRgbG.Value = settings.G;
                nudRgbB.Value = settings.B;
                RgbColorPicker.SelectedColor = Color.FromRgb(settings.R, settings.G, settings.B);
                sliderRgbBrightness.Value = settings.Brightness;
                RgbBrightnessValueText.Text = settings.Brightness.ToString();
            }
            finally
            {
                _isSyncingFromPreset = false;
            }
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
                    case FanModeManual: // Manual RPM
                        if (settings.ManualRpm > 0)
                            await _coolerService.WriteRealtimeRpmAsync(settings.ManualRpm);
                        break;
                    case FanModeGear: // Gear
                        if (settings.ManualGear > 0)
                            await _coolerService.WriteGearAsync((byte)settings.ManualGear);
                        break;
                    case FanModeAuto: // Curve — handled by FlydigiSmartControl (restart in ApplyOverrideState)
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
                Appearance = Wpf.Ui.Controls.ControlAppearance.Info,
                Icon = new Wpf.Ui.Controls.SymbolIcon { Symbol = Wpf.Ui.Controls.SymbolRegular.Info16, FontSize = 16, Margin = new System.Windows.Thickness(0, 0, 10, 0) },
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
        /// Syncs page UI when settings change from external callers (e.g., tray menu).
        /// </summary>
        private void OnFlydigiSettingsChanged(object? sender, EventArgs e)
        {
            this.Dispatcher.Invoke(() =>
            {
                if (_isSyncingFromPreset) return;

                _isSyncingFromPreset = true;
                try
                {
                    if (_isBs1Device && _bs1Service != null)
                    {
                        var settings = _bs1Service.GetSettings();
                        cbxFanMode.SelectedIndex = settings.FanMode;
                        // BS1 has no RGB — skip RGB sync
                    }
                    else if (_coolerService != null)
                    {
                        var settings = _coolerService.GetSettings();
                        cbxFanMode.SelectedIndex = settings.FanMode;
                        cbxRgbMode.SelectedIndex = GetRgbModeIndex(settings.RgbMode);
                    }
                    else
                    {
                        return;
                    }
                }
                finally
                {
                    _isSyncingFromPreset = false;
                }

                UpdateFanModeUI();
            });
        }

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
            {
                sliderRpm.Value = e.Rpm.Value;
                RpmValueText.Text = e.Rpm.Value.ToString();
            }

            cbxRgbMode.SelectedIndex = GetRgbModeIndex(e.RgbMode);
            UpdateRgbColorVisibility();
            nudRgbR.Value = e.R;
            nudRgbG.Value = e.G;
            nudRgbB.Value = e.B;
            RgbColorPicker.SelectedColor = Color.FromRgb(e.R, e.G, e.B);
            sliderRgbBrightness.Value = e.Brightness;
            RgbBrightnessValueText.Text = e.Brightness.ToString();
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

                // Show/hide embedded curve editor for Custom profile
                var isCustom = profile.Name == "Custom";
                var editorVisible = isCustom ? Visibility.Visible : Visibility.Collapsed;
                FanCurveEditorDivider.Visibility = editorVisible;
                FlydigiCurveEditor.Visibility = editorVisible;

                if (isCustom)
                {
                    FlydigiCurveEditor.SetCurve(profile);
                }

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
            var quiet = FlydigiFanCurveProfile.CreateQuiet();
            var balanced = FlydigiFanCurveProfile.CreateBalanced();
            var performance = FlydigiFanCurveProfile.CreatePerformance();
            var freezy = FlydigiFanCurveProfile.CreateFreezy();

            cbxCurveProfile.Items.Add(new ComboBoxItem { Content = silent.Name, Tag = silent });
            cbxCurveProfile.Items.Add(new ComboBoxItem { Content = quiet.Name, Tag = quiet });
            cbxCurveProfile.Items.Add(new ComboBoxItem { Content = balanced.Name, Tag = balanced });
            cbxCurveProfile.Items.Add(new ComboBoxItem { Content = performance.Name, Tag = performance });
            cbxCurveProfile.Items.Add(new ComboBoxItem { Content = freezy.Name, Tag = freezy });

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
                catch (Exception)
                {
                    // Non-critical: corrupted custom curve JSON — ignore and let user recreate
                }
            }

            // Default to Balanced (index 2)
            cbxCurveProfile.SelectedIndex = 2;
            _activeProfile = balanced;
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

        private void RgbColorPicker_ColorChangedDelayed(object? sender, EventArgs e)
        {
            // Suppress side effects when UI is being synced from a preset event
            if (_isSyncingFromPreset)
                return;

            // Sync hidden NumberBoxes so ApplyRgbAsync reads the same values
            var color = RgbColorPicker.SelectedColor;
            nudRgbR.Value = color.R;
            nudRgbG.Value = color.G;
            nudRgbB.Value = color.B;

            // Debounce: wait 500ms after user stops adjusting (RGB upload is expensive)
            ResetDebounceTimer(ref _rgbApplyTimer, 500, ApplyRgbAsync);
        }

        private void SliderRgbSpeed_ValueChanged(object? sender, RoutedPropertyChangedEventArgs<double> e)
        {
            // Update label
            RgbSpeedValueText.Text = GetRgbSpeedNameFromSlider(sliderRgbSpeed.Value);
            _rgbApplyTimer?.Dispose();
            _rgbApplyTimer = null;
            ApplyRgbAsync();
        }

        private void SliderRgbRotationSpeed_ValueChanged(object? sender, RoutedPropertyChangedEventArgs<double> e)
        {
            RgbRotationSpeedValueText.Text = GetRgbSpeedNameFromSlider(sliderRgbRotationSpeed.Value);
            _rgbApplyTimer?.Dispose();
            _rgbApplyTimer = null;
            ApplyRgbAsync();
        }

        private void SliderRgbBrightness_ValueChanged(object? sender, RoutedPropertyChangedEventArgs<double> e)
        {
            RgbBrightnessValueText.Text = ((int)sliderRgbBrightness.Value).ToString();
            ResetDebounceTimer(ref _rgbApplyTimer, 300, ApplyRgbAsync);
        }

        private void SliderRgbRotationBrightness_ValueChanged(object? sender, RoutedPropertyChangedEventArgs<double> e)
        {
            RgbRotationBrightnessValueText.Text = ((int)sliderRgbRotationBrightness.Value).ToString();
            ResetDebounceTimer(ref _rgbApplyTimer, 300, ApplyRgbAsync);
        }

        private void OnRotationColorsChanged(object? sender, EventArgs e)
        {
            // Debounce rotation color changes (uploads are expensive)
            ResetDebounceTimer(ref _rgbApplyTimer, 500, ApplyRgbAsync);
        }

        private void OnFlydigiCurveEditorCurveChanged(object? sender, EventArgs e)
        {
            // Auto-persist the custom curve whenever the user adjusts a slider.
            // The profile is already set as _activeProfile, we just need to save the edited points.
            if (_activeProfile == null) return;

            var editedProfile = FlydigiCurveEditor.GetCurve();
            // Update _activeProfile in place so the combo box item Tag stays current
            _activeProfile.Points = editedProfile.Points;

            // Persist custom curve JSON
            if (_isBs1Device && _bs1Service != null)
            {
                var settings = _bs1Service.GetSettings();
                settings.CustomCurveJson = _activeProfile.ToJSON();
                _bs1Service.PersistSettings();
            }
            else if (_coolerService != null)
            {
                var settings = _coolerService.GetSettings();
                settings.CustomCurveJson = _activeProfile.ToJSON();
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

        private async void ApplyRgbAsync()
        {
            _rgbApplyTimer?.Dispose();
            _rgbApplyTimer = null;

            if (_coolerService == null || !_coolerService.IsConnected) return;

            var modeIndex = cbxRgbMode.SelectedIndex;
            var mode = GetRgbModeName(modeIndex);

            // Guard: if NumberBox values are still null, the page hasn't finished loading yet
            if (nudRgbR.Value == null || nudRgbG.Value == null || nudRgbB.Value == null)
                return;

            try
            {
                switch (modeIndex)
                {
                    case RgbModeOff: // Off
                        await _coolerService.WriteRgbOffAsync();
                        break;

                    case RgbModeSmartTemp: // Smart-Temp
                        await _coolerService.WriteRgbSmartTempAsync();
                        break;

                    case RgbModeStatic: // Static
                        {
                            var r = (byte)nudRgbR.Value;
                            var g = (byte)nudRgbG.Value;
                            var b = (byte)nudRgbB.Value;
                            var brightness = (byte)sliderRgbBrightness.Value;
                            await _coolerService.WriteRgbStaticAsync(r, g, b, brightness);
                        }
                        break;

                    case RgbModeRotation: // Rotation (multi-color)
                        {
                            var colors = mcRotationColors!.GetColors();
                            var speed = GetRgbRotationSpeedName();
                            var brightness = (byte)sliderRgbRotationBrightness.Value;
                            await _coolerService.WriteRgbRotationMultiAsync(colors, speed, brightness);

                            // Save rotation-specific settings
                            var rotSettings = _coolerService.GetSettings();
                            rotSettings.RotationSpeed = speed;
                            rotSettings.RotationBrightness = brightness;
                            rotSettings.RotationColors = string.Join(",", colors.Select(c => $"#{c.R:X2}{c.G:X2}{c.B:X2}"));
                            _coolerService.PersistSettings();
                        }
                        break;

                    case RgbModeFlowing: // Flowing
                        {
                            var speed = GetRgbSpeedName();
                            var brightness = (byte)sliderRgbBrightness.Value;
                            await _coolerService.WriteRgbFlowingAsync(speed, brightness);
                        }
                        break;

                    case RgbModeBreathing: // Breathing
                        {
                            var r = (byte)nudRgbR.Value;
                            var g = (byte)nudRgbG.Value;
                            var b = (byte)nudRgbB.Value;
                            var brightness = (byte)sliderRgbBrightness.Value;
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
                settings.Brightness = (byte)sliderRgbBrightness.Value;
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

            // Off message
            spRgbOff.Visibility = selectedIndex is RgbModeOff ? Visibility.Visible : Visibility.Collapsed;

            // Smart-Temp message
            spRgbSmartTemp.Visibility = selectedIndex is RgbModeSmartTemp ? Visibility.Visible : Visibility.Collapsed;

            // Color picker: Static, Breathing
            var staticBreathVisible = selectedIndex is RgbModeStatic or RgbModeBreathing ? Visibility.Visible : Visibility.Collapsed;
            spRgbColorPicker.Visibility = staticBreathVisible;
            RgbStaticBrightnessDivider.Visibility = staticBreathVisible;

            // Multi-color row: Rotation — no divider, stacks with Color
            var rotationVisible = selectedIndex is RgbModeRotation ? Visibility.Visible : Visibility.Collapsed;
            spRgbMultiColor.Visibility = rotationVisible;

            // Rotation Speed row + divider
            RgbRotationSpeedDivider.Visibility = rotationVisible;
            spRgbRotationSpeed.Visibility = rotationVisible;

            // Rotation Brightness row + divider
            RgbRotationBrightnessDivider.Visibility = rotationVisible;
            spRgbRotationBrightness.Visibility = rotationVisible;

            // Flowing mode: Speed row + divider before it (brightness is shared)
            var flowingVisible = selectedIndex is RgbModeFlowing ? Visibility.Visible : Visibility.Collapsed;
            RgbFlowingSpeedDivider.Visibility = flowingVisible;
            spRgbSpeed.Visibility = flowingVisible;

            // Shared brightness: Static, Breathing, Flowing
            var brightnessVisible = selectedIndex is RgbModeStatic or RgbModeFlowing or RgbModeBreathing ? Visibility.Visible : Visibility.Collapsed;
            spRgbBrightness.Visibility = brightnessVisible;
        }

        private static string GetRgbModeName(int index) => index switch
        {
            RgbModeOff => "Off",
            RgbModeSmartTemp => "SmartTemp",
            RgbModeStatic => "Static",
            RgbModeRotation => "Rotation",
            RgbModeFlowing => "Flowing",
            RgbModeBreathing => "Breathing",
            _ => "Off"
        };

        private string GetRgbSpeedName()
        {
            return GetRgbSpeedNameFromSlider(sliderRgbSpeed.Value);
        }

        private string GetRgbRotationSpeedName()
        {
            return GetRgbSpeedNameFromSlider(sliderRgbRotationSpeed.Value);
        }

        /// <summary>Convert slider value (0=Slow, 1=Medium, 2=Fast) to display name.</summary>
        private static string GetRgbSpeedNameFromSlider(double value) => ((int)value) switch
        {
            0 => "Slow",
            2 => "Fast",
            _ => "Medium"
        };

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
            ushort startRpm = (ushort)(nudAvoidanceStart.Value ?? 2000);
            ushort endRpm = (ushort)(nudAvoidanceEnd.Value ?? 2500);

            // Update BS2+ service and smart control
            if (_coolerService != null)
            {
                var settings = _coolerService.GetSettings();
                settings.AvoidanceEnabled = true;
                settings.AvoidanceStartRpm = startRpm;
                settings.AvoidanceEndRpm = endRpm;
                _coolerService.PersistSettings();
                if (_smartControl != null)
                    _smartControl.Settings = settings;
            }

            // Update BS1 service and smart control
            if (_bs1Service != null)
            {
                var bs1Settings = _bs1Service.GetSettings();
                bs1Settings.AvoidanceEnabled = true;
                bs1Settings.AvoidanceStartRpm = startRpm;
                bs1Settings.AvoidanceEndRpm = endRpm;
                _bs1Service.PersistSettings();
                if (_bs1SmartControl != null)
                    _bs1SmartControl.Settings = bs1Settings;
            }

            // Show Avoidance Range row + divider
            AvoidanceRangeDivider.Visibility = Visibility.Visible;
            AvoidanceRangeRow.Visibility = Visibility.Visible;
        }

        private void tsAvoidance_Unchecked(object sender, RoutedEventArgs e)
        {
            // Update BS2+ service and smart control
            if (_coolerService != null)
            {
                var settings = _coolerService.GetSettings();
                settings.AvoidanceEnabled = false;
                _coolerService.PersistSettings();
                if (_smartControl != null)
                    _smartControl.Settings = settings;
            }

            // Update BS1 service and smart control
            if (_bs1Service != null)
            {
                var bs1Settings = _bs1Service.GetSettings();
                bs1Settings.AvoidanceEnabled = false;
                _bs1Service.PersistSettings();
                if (_bs1SmartControl != null)
                    _bs1SmartControl.Settings = bs1Settings;
            }

            // Hide Avoidance Range row + divider
            AvoidanceRangeDivider.Visibility = Visibility.Collapsed;
            AvoidanceRangeRow.Visibility = Visibility.Collapsed;
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
            // Update BS2+ service and smart control
            if (_coolerService != null)
            {
                var settings = _coolerService.GetSettings();
                if (nudAvoidanceStart.Value.HasValue)
                    settings.AvoidanceStartRpm = (ushort)nudAvoidanceStart.Value.Value;
                if (nudAvoidanceEnd.Value.HasValue)
                    settings.AvoidanceEndRpm = (ushort)nudAvoidanceEnd.Value.Value;
                _coolerService.PersistSettings();
                if (_smartControl != null)
                    _smartControl.Settings = settings;
            }

            // Update BS1 service and smart control
            if (_bs1Service != null)
            {
                var bs1Settings = _bs1Service.GetSettings();
                if (nudAvoidanceStart.Value.HasValue)
                    bs1Settings.AvoidanceStartRpm = (ushort)nudAvoidanceStart.Value.Value;
                if (nudAvoidanceEnd.Value.HasValue)
                    bs1Settings.AvoidanceEndRpm = (ushort)nudAvoidanceEnd.Value.Value;
                _bs1Service.PersistSettings();
                if (_bs1SmartControl != null)
                    _bs1SmartControl.Settings = bs1Settings;
            }
        }

       /* ------------------------------------------------------------------ */
        /*  Time Schedule Handlers                                             */
        /* ------------------------------------------------------------------ */

        private void tsTimeSchedule_Checked(object sender, RoutedEventArgs e)
        {
            if (_coolerService != null)
            {
                var settings = _coolerService.GetSettings();
                settings.TimeScheduleEnabled = true;
                _coolerService.PersistSettings();
            }

            TimeScheduleEntriesDivider.Visibility = Visibility.Visible;
            TimeScheduleEntriesRow.Visibility = Visibility.Visible;
            icTimeScheduleEntries.Visibility = Visibility.Visible;
        }

        private void tsTimeSchedule_Unchecked(object sender, RoutedEventArgs e)
        {
            if (_coolerService != null)
            {
                var settings = _coolerService.GetSettings();
                settings.TimeScheduleEnabled = false;
                _coolerService.PersistSettings();
            }

            TimeScheduleEntriesDivider.Visibility = Visibility.Collapsed;
            TimeScheduleEntriesRow.Visibility = Visibility.Collapsed;
            icTimeScheduleEntries.Visibility = Visibility.Collapsed;
        }

        private void btnAddScheduleEntry_Click(object sender, RoutedEventArgs e)
        {
            if (_coolerService == null) return;

            var entry = new TimeCurveEntry { StartTime = "08:00", EndTime = "18:00", ProfileName = "Balanced" };
            var settings = _coolerService.GetSettings();
            settings.TimeScheduleEntries.Add(entry);
            _coolerService.PersistSettings();

            // Refresh the items control
            icTimeScheduleEntries.ItemsSource = settings.TimeScheduleEntries;
        }

        private void btnRemoveScheduleEntry_Click(object sender, RoutedEventArgs e)
        {
            if (_coolerService == null || sender is not Button btn) return;
            var entry = btn.DataContext as TimeCurveEntry;
            if (entry == null) return;

            var settings = _coolerService.GetSettings();
            settings.TimeScheduleEntries.Remove(entry);
            _coolerService.PersistSettings();

            icTimeScheduleEntries.ItemsSource = settings.TimeScheduleEntries;
        }

        private void ScheduleEntry_ValueChanged(object? sender, RoutedEventArgs e)
        {
            if (_coolerService == null || sender is not Wpf.Ui.Controls.NumberBox nb) return;
            var entry = nb.DataContext as TimeCurveEntry;
            if (entry == null || !nb.Value.HasValue) return;

            // Update the entry's start or end time based on which number box fired.
            // The name isn't directly available, so we check the parent grid.
            var grid = FindParent<Grid>(nb);
            if (grid == null) return;

            var nbStart = grid.FindName("nbStartHour") as Wpf.Ui.Controls.NumberBox;
            var nbEnd = grid.FindName("nbEndHour") as Wpf.Ui.Controls.NumberBox;

            if (nb == nbStart && nbStart.Value.HasValue)
                entry.StartTime = $"{nbStart.Value.Value:D2}:00";
            else if (nb == nbEnd && nbEnd.Value.HasValue)
                entry.EndTime = $"{nbEnd.Value.Value:D2}:00";

            _coolerService.PersistSettings();
        }

        private void ScheduleProfile_SelectionChanged(object sender, EventArgs e)
        {
            if (_coolerService == null) return;
            _coolerService.PersistSettings();
        }

        /* ------------------------------------------------------------------ */
        /*  Learning Engine Handlers                                           */
        /* ------------------------------------------------------------------ */

        private void tsLearning_Checked(object sender, RoutedEventArgs e)
        {
            if (_coolerService != null)
            {
                var settings = _coolerService.GetSettings();
                settings.LearningEnabled = true;
                _coolerService.PersistSettings();
            }

            LearningBiasDivider.Visibility = Visibility.Visible;
            RowLearningBias.Visibility = Visibility.Visible;
        }

        private void tsLearning_Unchecked(object sender, RoutedEventArgs e)
        {
            if (_coolerService != null)
            {
                var settings = _coolerService.GetSettings();
                settings.LearningEnabled = false;
                _coolerService.PersistSettings();
            }

            LearningBiasDivider.Visibility = Visibility.Collapsed;
            RowLearningBias.Visibility = Visibility.Collapsed;
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
        }

        /// <summary>Walk up the visual tree to find a parent of the specified type.</summary>
        private static T? FindParent<T>(DependencyObject child) where T : DependencyObject
        {
            var parent = VisualTreeHelper.GetParent(child);
            while (parent != null)
            {
                if (parent is T result)
                    return result;
                parent = VisualTreeHelper.GetParent(parent);
            }
            return null;
        }

        /* ------------------------------------------------------------------ */
        /*  Event Callbacks                                                    */
        /* ------------------------------------------------------------------ */

        private void OnConnectionStateChanged(object? sender, bool connected)
        {
            try
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
            catch { /* Dispatcher shutting down during app exit - safe to ignore */ }
        }

        private void OnStatusChanged(object? sender, string message)
        {
            // Status is now shown via icon in the UI, no text update needed.
        }

        private void OnFanDataReceived(object? sender, FanRpmData data)
        {
            try
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    // Only show RPM data when actually connected
                    if (spConnectedState.Visibility == Visibility.Visible)
                        tbCurrentRpm.Text = $"{data.CurrentRpm} RPM";
                });
            }
            catch { /* Dispatcher shutting down during app exit - safe to ignore */ }
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
            rectGlowOverlay.Opacity = GlowOpacityMin;
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
                            if (current < GlowOpacityMax)
                                rectGlowOverlay.Opacity = current + GlowOpacityStep;
                            else
                                _glowPulseDirection = false;
                        }
                        else
                        {
                            if (current > GlowOpacityMin)
                                rectGlowOverlay.Opacity = current - GlowOpacityStep;
                            else
                                _glowPulseDirection = true;
                        }
                    });
                }
                catch (Exception)
                {
                    // Non-critical: dispatcher shut down or element detached — stop the timer
                    _glowActive = false;
                }
            }, null, GlowPulseIntervalMs, GlowPulseIntervalMs);
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
            catch (Exception)
            {
                // Non-critical: dispatcher shut down during glow stop
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
            catch (Exception) { /* Non-critical: element detached or accent color unavailable */ }
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
            catch (Exception) { /* Non-critical: registry read failed, fall through to default */ }

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
                    ImageSource = new BitmapImage(new Uri(FlydigiAssetPrefix + imageFile, UriKind.Absolute)),
                    Stretch = Stretch.Uniform,
                    AlignmentX = AlignmentX.Center,
                    AlignmentY = AlignmentY.Center,
                };
                rectGlowOverlay.OpacityMask = maskBrush;
            }
            catch (Exception) { /* Non-critical: element detached during glow mask update */ }
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
                    ? Math.Min(bs1Settings.ManualRpm, (ushort)Bs1MaxRpm)
                    : Bs1DefaultGearRpm.Gear0_Quiet;
                sliderRpm.Value = bs1Rpm;
                RpmValueText.Text = ((int)bs1Rpm).ToString();

                // Auto-connect
                tsAutoConnect.IsChecked = bs1Settings.AutoConnect;

                // Advanced settings (avoidance, temp source)
                tsAvoidance.IsChecked = bs1Settings.AvoidanceEnabled;
                nudAvoidanceStart.Value = bs1Settings.AvoidanceStartRpm;
                nudAvoidanceEnd.Value = bs1Settings.AvoidanceEndRpm;

                // Show/hide Avoidance Range based on toggle state
                var bs1AvoidanceVisible = bs1Settings.AvoidanceEnabled ? Visibility.Visible : Visibility.Collapsed;
                AvoidanceRangeDivider.Visibility = bs1AvoidanceVisible;
                AvoidanceRangeRow.Visibility = bs1AvoidanceVisible;

                cbxTempSource.SelectedIndex = bs1Settings.TempSource.ToLowerInvariant() switch
                {
                    "cpu" => 1,
                    "gpu" => 2,
                    _ => 0
                };

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
                int bs1FanMode = Math.Clamp(bs1Settings.FanMode, FanModeManual, FanModeAuto);
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

            // Time schedule
            tsTimeSchedule.IsChecked = settings.TimeScheduleEnabled;
            if (settings.TimeScheduleEnabled)
            {
                TimeScheduleEntriesDivider.Visibility = Visibility.Visible;
                TimeScheduleEntriesRow.Visibility = Visibility.Visible;
                icTimeScheduleEntries.Visibility = Visibility.Visible;
                icTimeScheduleEntries.ItemsSource = settings.TimeScheduleEntries;
            }

            // Learning engine
            tsLearning.IsChecked = settings.LearningEnabled;
            if (settings.LearningEnabled)
            {
                LearningBiasDivider.Visibility = Visibility.Visible;
                RowLearningBias.Visibility = Visibility.Visible;
            }
            var learningBiasIndex = settings.LearningBias.ToLowerInvariant() switch
            {
                "cooling" => 1,
                "quiet" => 2,
                _ => 0
            };
            cbxLearningBias.SelectedIndex = learningBiasIndex;

            // RGB settings
            var rgbModeIndex = GetRgbModeIndex(settings.RgbMode);
            cbxRgbMode.SelectedIndex = rgbModeIndex;
            UpdateRgbColorVisibility();

            nudRgbR.Value = settings.R;
            nudRgbG.Value = settings.G;
            nudRgbB.Value = settings.B;
            RgbColorPicker.SelectedColor = Color.FromRgb(settings.R, settings.G, settings.B);

            var speedIndex = GetRgbSpeedIndex(settings.RgbSpeed);
            sliderRgbSpeed.Value = speedIndex;
            RgbSpeedValueText.Text = settings.RgbSpeed;

            sliderRgbBrightness.Value = settings.Brightness;
            RgbBrightnessValueText.Text = settings.Brightness.ToString();

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
                sliderRgbRotationSpeed.Value = rotSpeedIndex;
                RgbRotationSpeedValueText.Text = settings.RotationSpeed;

                // Load brightness
                sliderRgbRotationBrightness.Value = settings.RotationBrightness;
                RgbRotationBrightnessValueText.Text = settings.RotationBrightness.ToString();
            }

            // Advanced settings
            tsAvoidance.IsChecked = settings.AvoidanceEnabled;
            nudAvoidanceStart.Value = settings.AvoidanceStartRpm;
            nudAvoidanceEnd.Value = settings.AvoidanceEndRpm;

            // Show/hide Avoidance Range based on toggle state
            var avoidanceVisible = settings.AvoidanceEnabled ? Visibility.Visible : Visibility.Collapsed;
            AvoidanceRangeDivider.Visibility = avoidanceVisible;
            AvoidanceRangeRow.Visibility = avoidanceVisible;

            var tempSourceIndex = settings.TempSource.ToLowerInvariant() switch
            {
                "cpu" => 1,
                "gpu" => 2,
                _ => 0
            };
            cbxTempSource.SelectedIndex = tempSourceIndex;

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
            var fanMode = Math.Clamp(settings.FanMode, FanModeManual, FanModeAuto);
            cbxFanMode.SelectedIndex = fanMode;
            UpdateFanModeUI();

            // Manual RPM
            if (settings.ManualRpm > 0)
            {
                sliderRpm.Value = settings.ManualRpm;
                RpmValueText.Text = settings.ManualRpm.ToString();
            }

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

                // Notify hardware detector FIRST so UpdatePageTitle reads the correct model name.
                // For HID devices, prefer the ConnectedDeviceInfo from the service (which reads
                // the ProductId from the actual connected device) rather than the cached detector,
                // which may return a stale or wrong model when multiple devices are paired.
                string model;
                if (_isBs1Device)
                {
                    model = "BS1";
                }
                else if (_coolerService?.ConnectedDeviceInfo?.ModelName is { } hidModel)
                {
                    model = hidModel;
                }
                else
                {
                    model = FlydigiHardwareDetector.GetDetectedModelName();
                }
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
                InfoBarConnection.IsOpen = false;
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
                        try { _bs1SmartControl.Stop(); } catch (Exception) { /* Non-critical: best-effort cleanup during disconnect */ }
                        try { _bs1SmartControl.Dispose(); } catch (Exception) { /* Non-critical: best-effort cleanup during disconnect */ }
                        _bs1SmartControl = null;
                    }

                    // Use the ConnectedDeviceInfo from the service for accurate model name
                    string model = _coolerService?.ConnectedDeviceInfo?.ModelName
                        ?? FlydigiHardwareDetector.GetDetectedModelName();
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
                        try { _smartControl.Stop(); } catch (Exception) { /* Non-critical: best-effort cleanup during disconnect */ }
                        try { _smartControl.Dispose(); } catch (Exception) { /* Non-critical: best-effort cleanup during disconnect */ }
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
                InfoBarConnection.IsOpen = true;
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
                new Uri(FlydigiAssetPrefix + imageFile, UriKind.Absolute));
            imgDevice.Source = source;
            UpdateGlowOpacityMask(imageFile);
        }

        /// <summary>
        /// Sets the device image to the BS1-specific asset.
        /// </summary>
        private void UpdateDeviceImageBs1()
        {
            var source = new BitmapImage(
                new Uri(Bs1ImageUri, UriKind.Absolute));
            imgDevice.Source = source;
            UpdateGlowOpacityMask("bs1.png");
        }

        /// <summary>
        /// Updates the page title with the detected cooler model name.
        /// - No device: "Flydigi Control"
        /// - BS1: "Flydigi BS1 Control"
        /// - BS2+: "Flydigi BS2 PRO Control"
        /// </summary>
        private void UpdatePageTitle()
        {
            string modelName = FlydigiHardwareDetector.GetConnectedModelName();
            if (modelName.StartsWith("Flydigi ", StringComparison.OrdinalIgnoreCase))
                tbPageTitle.Text = $"{modelName} Control";
            else
                tbPageTitle.Text = $"Flydigi {modelName} Control";
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
                        case FanModeManual: // Manual RPM
                            if (settings.ManualRpm > 0)
                                await _bs1Service.WriteRpmAsync(settings.ManualRpm);
                            break;
                        case FanModeGear: // Gear Presets
                            if (settings.ManualGear > 0)
                                await _bs1Service.WriteGearAsync((byte)settings.ManualGear);
                            break;
                        case FanModeAuto: // Auto Curve - StartAutoControl will handle this
                            StartAutoControlBs1();
                            break;
                    }
                }
                else if (_coolerService != null && _coolerService.IsConnected)
                {
                    var settings = _coolerService.GetSettings();
                    switch (settings.FanMode)
                    {
                        case FanModeManual: // Manual RPM
                            if (settings.ManualRpm > 0)
                                await _coolerService.WriteRealtimeRpmAsync(settings.ManualRpm);
                            break;
                        case FanModeGear: // Gear Presets
                            if (settings.ManualGear > 0)
                                await _coolerService.WriteGearAsync((byte)settings.ManualGear);
                            break;
                        case FanModeAuto: // Auto Curve - start smart control on connect
                            StartAutoControl();
                            break;
                    }
                }
            }
            catch (Exception)
            {
                // Non-critical: device may not be ready yet on connect
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
                try
                {
                    checkDispatcher.Invoke(() => isConnected = (spConnectedState.Visibility == Visibility.Visible));
                }
                catch { /* Dispatcher shutting down during app exit */ return; }

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

                try
                {
                    dispatcher.Invoke(() =>
                    {
                        tbTemperature.Text = temp.HasValue ? $"{temp.Value:F1}°C" : "N/A";
                    });
                }
                catch { /* Dispatcher shutting down during app exit - safe to ignore */ }
            }
            catch (Exception)
            {
                // Non-critical: temperature reading failures shouldn't crash the UI
                var dispatcher = Application.Current?.Dispatcher;
                if (dispatcher != null)
                {
                    try { dispatcher.Invoke(() => tbTemperature.Text = "N/A"); }
                    catch { /* Dispatcher shutting down during app exit */ }
                }
            }
        }

        private void SetControlsEnabled(bool enabled)
        {
            cbxFanMode.IsEnabled = enabled;
            sliderRpm.IsEnabled = enabled;
            btnGearQuiet.IsEnabled = enabled;
            btnGearStandard.IsEnabled = enabled;
            btnGearStrong.IsEnabled = enabled;
            btnGearOverclock.IsEnabled = enabled;
            cbxGearSubLevel.IsEnabled = enabled;
            cbxCurveProfile.IsEnabled = enabled;
            cbxRgbMode.IsEnabled = enabled;
            RgbColorPicker.IsEnabled = enabled;
            sliderRgbBrightness.IsEnabled = enabled;
            sliderRgbSpeed.IsEnabled = enabled;
            if (mcRotationColors is not null) mcRotationColors.IsEnabled = enabled;
            sliderRgbRotationSpeed.IsEnabled = enabled;
            sliderRgbRotationBrightness.IsEnabled = enabled;
            tsAutoConnect.IsEnabled = enabled;
            tsPowerOnStart.IsEnabled = enabled;
            cbxSmartStartStop.IsEnabled = enabled;
            tsGearLight.IsEnabled = enabled;
            tsAvoidance.IsEnabled = enabled;
            nudAvoidanceStart.IsEnabled = enabled;
            nudAvoidanceEnd.IsEnabled = enabled;
            cbxTempSource.IsEnabled = enabled;
            tsTimeSchedule.IsEnabled = enabled;
            btnAddScheduleEntry.IsEnabled = enabled;
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
            "slow" => 0,
            "medium" => 1,
            "fast" => 2,
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

                // Scan for HID devices (BS2+) — uses BLE ad scan fallback if HID enum finds nothing
                if (_coolerService != null)
                {
                    var hidDevices = await FlydigiCoolerHidService.DiscoverDevicesAsync();
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
                            Content = $"{Bs1DeviceInfo.ModelName} ({device.Name})",
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
                    MessageBox.Show("No Flydigi devices found.\n\n" +
                        "Make sure the device is powered on.\n" +
                        "BS1: ensure Bluetooth is enabled.\n" +
                        "BS2/BS3: connect the device in Windows Bluetooth Settings first.",
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
        /// Runs a fast background discovery for HID and BLE devices.
        /// Uses instant HID enumeration for BS2+ and paired-device lookup for BS1
        /// (no slow BLE advertisement scan).  If exactly one device is found,
        /// connects automatically; otherwise populates the combobox for the user.
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

                    // 1. HID enumeration (BS2+) — instant, finds connected devices
                    if (_coolerService != null)
                    {
                        var hidDevices = FlydigiCoolerHidService.DiscoverDevices();
                        foreach (var device in hidDevices)
                        {
                            discoveredDevices.Add((device.ModelName, (object)device));
                        }
                    }

                    // 2. BS1 fast path — try last-known address, then paired-device enumeration
                    if (_bs1Service != null)
                    {
                        var bs1Devices = await TryDiscoverBs1FastAsync();
                        foreach (var device in bs1Devices)
                        {
                            discoveredDevices.Add(($"{Bs1DeviceInfo.ModelName} ({device.Name})", (object)device));
                        }
                    }

                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        cbxDevices.Items.Clear();

                        if (discoveredDevices.Count == 1)
                        {
                            // Auto-connect to the single discovered device
                            cbxDevices.Items.Add(new ComboBoxItem
                            {
                                Content = discoveredDevices[0].Content,
                                Tag = discoveredDevices[0].Tag
                            });
                            cbxDevices.SelectedIndex = 0;
                            btnScan.IsEnabled = true;
                            btnScan.Content = "Scan";

                            // If already connected (e.g., auto-connect from previous session),
                            // just update the UI instead of trying to connect again.
                            bool alreadyConnected = (_coolerService?.IsConnected == true) ||
                                                    (_bs1Service?.IsConnected == true);
                            if (alreadyConnected)
                            {
                                bool bs1Connected = _bs1Service?.IsConnected == true;
                                _isBs1Device = bs1Connected;
                                UpdateConnectionUI(true, bs1Connected);
                            }
                            else
                            {
                                _ = Task.Run(() => ConnectToDeviceAsync());
                            }
                        }
                        else if (discoveredDevices.Count > 1)
                        {
                            foreach (var dev in discoveredDevices)
                            {
                                cbxDevices.Items.Add(new ComboBoxItem
                                {
                                    Content = dev.Content,
                                    Tag = dev.Tag
                                });
                            }
                            cbxDevices.SelectedIndex = 0;
                            btnScan.IsEnabled = true;
                            btnScan.Content = "Scan";
                        }
                        else
                        {
                            btnScan.IsEnabled = true;
                            btnScan.Content = "Scan";
                        }
                    });
                }
                catch (Exception)
                {
                    // Non-critical: auto-scan failure — restore UI to idle state
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        btnScan.IsEnabled = true;
                        btnScan.Content = "Scan";
                    });
                }
            });
        }

        /// <summary>
        /// Fast BS1 discovery without BLE advertisement scanning.
        /// Tries last-known address first, then enumerates paired BLE devices.
        /// </summary>
        private async Task<List<Bs1DeviceInfo>> TryDiscoverBs1FastAsync()
        {
            var devices = new List<Bs1DeviceInfo>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 1. Try last-known device address (instant)
            var bs1Settings = _bs1Service!.GetSettings();
            if (!string.IsNullOrEmpty(bs1Settings.LastDeviceAddress))
            {
                try
                {
                    var bleDevice = await TryGetBluetoothLEDeviceAsync(bs1Settings.LastDeviceAddress);
                    if (bleDevice != null)
                    {
                        var name = bleDevice.Name ?? Bs1DeviceInfo.ModelName;
                        if (IsBs1AdvertisingName(name))
                        {
                            if (seen.Add(bs1Settings.LastDeviceAddress))
                            {
                                devices.Add(new Bs1DeviceInfo
                                {
                                    Address = bs1Settings.LastDeviceAddress,
                                    Name = name,
                                    Rssi = 0
                                });
                            }
                        }
                        bleDevice.Dispose();
                    }
                }
                catch { /* Last-known device unavailable */ }
            }

            // 2. Enumerate paired BLE devices and filter for BS1 names (fast, no ad scan)
            try
            {
                var selector = global::Windows.Devices.Bluetooth.BluetoothLEDevice.GetDeviceSelectorFromPairingState(true);
                var pairedDevices = await global::Windows.Devices.Enumeration.DeviceInformation.FindAllAsync(selector);
                foreach (var info in pairedDevices)
                {
                    var name = info.Name ?? string.Empty;
                    if (!string.IsNullOrEmpty(name) && IsBs1AdvertisingName(name))
                    {
                        if (seen.Add(info.Id))
                        {
                            devices.Add(new Bs1DeviceInfo
                            {
                                Address = info.Id,
                                Name = name,
                                Rssi = 0
                            });
                        }
                    }
                }
            }
            catch { /* Paired device enumeration failed — non-critical */ }

            return devices;
        }

        /// <summary>
        /// Resolves a saved BS1 address (either DeviceId or Bluetooth address) to a BluetoothLEDevice.
        /// </summary>
        private static async Task<global::Windows.Devices.Bluetooth.BluetoothLEDevice?> TryGetBluetoothLEDeviceAsync(string address)
        {
            bool isDeviceId = address.StartsWith("\\\\?\\") || address.Contains('#');

            if (isDeviceId)
            {
                return await global::Windows.Devices.Bluetooth.BluetoothLEDevice.FromIdAsync(address).AsTask();
            }
            else
            {
                string clean = address.Replace(":", "").Replace("-", "");
                if (clean.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                    clean = clean.Substring(2);

                if (ulong.TryParse(clean, System.Globalization.NumberStyles.HexNumber, null, out ulong btAddress))
                {
                    return await global::Windows.Devices.Bluetooth.BluetoothLEDevice.FromBluetoothAddressAsync(btAddress).AsTask();
                }
            }

            return null;
        }

        /// <summary>
        /// Checks if a BLE advertising name matches a known Flydigi BS1 device.
        /// </summary>
        private static bool IsBs1AdvertisingName(string name)
        {
            string lower = name.ToLowerInvariant();
            if (lower.Contains("bs2") || lower.Contains("bs3"))
                return false;
            return lower.Contains("bs1") ||
                   (lower.Contains("flydigi") && lower.Contains("cooling pad"));
        }

        /// <summary>
        /// Connects to the device selected in the combobox (used for auto-connect after discovery).
        /// Runs silently without blocking the UI or showing "Connecting..." states.
        /// </summary>
        private async Task ConnectToDeviceAsync()
        {
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                if (cbxDevices.SelectedItem is not ComboBoxItem selectedItem)
                    return;

                if (selectedItem.Tag is FlydigiCoolerDeviceInfo hidInfo && _coolerService != null && !_coolerService.IsConnected)
                {
                    _ = _coolerService.ConnectAsync(hidInfo.DevicePath);
                }
                else if (selectedItem.Tag is Bs1DeviceInfo bleInfo && _bs1Service != null && !_bs1Service.IsConnected)
                {
                    _ = _bs1Service.ConnectAsync(bleInfo.Address);
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
                    await _bs1Service.DisconnectByUser();
                else if (_coolerService != null)
                    _coolerService.DisconnectByUser();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Disconnection error: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /* ------------------------------------------------------------------ */
        /*  IDisposable                                                        */
        /* ------------------------------------------------------------------ */

        /// <summary>
        /// Stops all background timers so they don't fire <c>Dispatcher.Invoke</c> calls
        /// on a dying dispatcher during app shutdown. Safe to call multiple times.
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            StopTemperaturePolling();
            StopGlowPulse();
            _rpmApplyTimer?.Dispose();
            _rpmApplyTimer = null;
            _rgbApplyTimer?.Dispose();
            _rgbApplyTimer = null;
        }
    }
}
