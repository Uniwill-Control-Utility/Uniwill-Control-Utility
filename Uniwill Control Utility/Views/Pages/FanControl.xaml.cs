using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Uniwill_Control_Utility.Models;
using Uniwill_Control_Utility.Properties;
using Uniwill_Control_Utility.Scripts.Misc;
using Uniwill_Control_Utility.Services;

namespace Uniwill_Control_Utility.Views.Pages
{
    public partial class FanControl : Page
    {
 
        private readonly IHardwareMonitoringService _hardwareMonitoring;
        private readonly EcTelemetryService _ecTelemetry;
        private readonly UniwillECService? _uniwillEc;
        private readonly FlydigiCoolerHidService _flydigiService;
        private readonly FlydigiCoolerBleService _bs1Service;
        private readonly DeviceApplier _deviceApplier;
        private bool _loadingSettings;
        private DispatcherTimer? _statusTimer;
        private IDisposable? _monitoringLease;
        private System.Timers.Timer? _applyDebounceTimer;
        private Wpf.Ui.Controls.Snackbar? _adaptiveSnackbar;
        private Wpf.Ui.Controls.Snackbar? _stockPresetSnackbar;

        // Smart Fan Mode (cross-sensor polling loop)
        private EcSmartFanControl? _smartFanControl;
        private FlydigiTemperatureProvider? _smartFanTempProvider;

        // Concurrency: serialize all EC access to prevent AccessViolationException
        // from concurrent native I/O on the same handle.
        private readonly SemaphoreSlim _ecSemaphore = new(1, 1);
        private CancellationTokenSource _pageCts = new();
        private bool _isUnloaded;

        // Tracks whether Smart Fan Mode was active before Turbo was enabled,
        // so we can restore it when Turbo is disabled.
        private bool _smartFanWasActiveBeforeTurbo;

        public FanControl(
            IHardwareMonitoringService hardwareMonitoring,
            DeviceApplier deviceApplier,
            EcTelemetryService ecTelemetry,
            UniwillECService? uniwillEc = null,
            FlydigiCoolerHidService? flydigiService = null,
            FlydigiCoolerBleService? bs1Service = null)
        {
            _hardwareMonitoring = hardwareMonitoring;
            _deviceApplier = deviceApplier;
            _ecTelemetry = ecTelemetry;
            _uniwillEc = uniwillEc;
            _flydigiService = flydigiService;
            _bs1Service = bs1Service;
            InitializeComponent();

            // Subscribe to DeviceApplier override events
            _deviceApplier.EcFanOverrideChanged += OnEcFanOverrideChanged;
            _deviceApplier.EcFanPresetApplied += OnEcFanPresetApplied;
            _deviceApplier.ManualControlLockChanged += OnManualControlLockChanged;

            // Subscribe to settings changes from external callers (e.g., tray menu)
            // so the page UI stays in sync when the user changes settings from niTray.
            FanControlSettingsService.SettingsChanged += OnFanSettingsChanged;

            // Subscribe to curve changes for banner updates and auto-apply debounce
            FanCurveEditor.CurveChanged += (s, e) =>
            {
                UpdateBanner();
                SaveCurrentSettings();
                ScheduleAutoApply();
                UpdateSmartFanCurve();
            };
            CpuFanCurveEditor.CurveChanged += (s, e) =>
            {
                UpdateBanner();
                SaveCurrentSettings();
                ScheduleAutoApply();
                UpdateSmartFanCurve();
            };
           GpuFanCurveEditor.CurveChanged += (s, e) =>
            {
                UpdateBanner();
                SaveCurrentSettings();
                ScheduleAutoApply();
                UpdateSmartFanCurve();
            };

            // Update banner when Flydigi connection state changes (affects cooler name)
            _flydigiService.ConnectionStateChanged += (s, e) => UpdateBanner();
            _bs1Service.ConnectionStateChanged += (s, e) => UpdateBanner();

            // Try to initialize Uniwill EC if injected
            bool uniwillAvailable = false;
            if (_uniwillEc is not null)
            {
                try
                {
                    uniwillAvailable = _uniwillEc.Initialize();
                }
                catch
                {
                    uniwillAvailable = false;
                }
            }

            if (uniwillAvailable)
            {
                SetStatusText(LocalizationService.Get("Ready"));
                UniwillAvailable.Visibility = Visibility.Visible;
                UniwillUnavailable.Visibility = Visibility.Collapsed;

                // Load persisted settings
                var settings = FanControlSettingsService.Load();
                _loadingSettings = true;
                ApplySettings(settings);
                InitializeTempThresholdInputs();
                _loadingSettings = false;

                // Start status polling timer (every 1 second)
                _statusTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(1)
                };
                _statusTimer.Tick += StatusTimer_Tick;
            }
            else
            {
                SetStatusText(LocalizationService.Get("EC hardware not available"));
                UniwillAvailable.Visibility = Visibility.Collapsed;
                UniwillUnavailable.Visibility = Visibility.Visible;
            }
        }

        private void SetStatusText(string message)
        {
            StatusText.Text = message;
        }

        /// <summary>
        /// Schedules a debounced auto-apply of the current fan curve after 5 seconds
        /// of slider inactivity. Each call resets the timer - the curve only applies
        /// after the user stops dragging sliders. Used only for slider edits.
        /// </summary>
        private void ScheduleAutoApply()
        {
            if (_loadingSettings) return;

            if (_applyDebounceTimer == null)
            {
                _applyDebounceTimer = new System.Timers.Timer(5000) { AutoReset = false };
                _applyDebounceTimer.Elapsed += (s, e) =>
                {
                    Dispatcher.Invoke(() => ApplyCurveInBackground());
                };
            }

            _applyDebounceTimer.Stop();
            _applyDebounceTimer.Start();
        }

        /// <summary>
        /// Applies the current fan curve to the EC immediately. Used for toggle
        /// switches and preset changes where the user expects instant feedback.
        /// Cancels any pending debounced apply from slider edits.
        /// </summary>
        private void ApplyCurveNow()
        {
            if (_loadingSettings) return;

            // Cancel any pending debounced apply so we don't get a duplicate
            _applyDebounceTimer?.Stop();

            // Apply directly — the caller has already updated all UI state.
            ApplyCurveInBackground();
        }

        #region Adaptive Mode Override

        private void OnEcFanOverrideChanged(object? sender, bool isOverridden)
        {
            ApplyOverrideState(isOverridden);

            // Adaptive Mode owns the overlay while it is active. When it lifts, the EC
            // manual-control gate may still be locked by a stock preset, which needs the
            // same overlay back with its own snackbar.
            if (!isOverridden && _deviceApplier.IsManualControlLocked)
                ApplyStockPresetState(true);
        }

        private void OnManualControlLockChanged(object? sender, bool isLocked)
        {
            ApplyStockPresetState(isLocked);
        }

        /// <summary>
        /// Called when Fan Control settings change from external sources (e.g., tray menu).
        /// Syncs the page UI to match the new persisted state without re-applying the
        /// curve to the EC (the external caller already did that).
        /// </summary>
        private void OnFanSettingsChanged(object? sender, EventArgs e)
        {
            // Skip if the page is not currently visible — Page_Loaded will reload.
            if (_isUnloaded) return;

            // Skip if we're currently loading settings (avoid feedback loop from
            // our own SaveCurrentSettings calls which also fire this event).
            if (_loadingSettings) return;

            Dispatcher.Invoke(() =>
            {
                try
                {
                    var settings = FanControlSettingsService.Load();

                    // Sync unified/split mode if it changed (e.g., from a custom preset apply)
                    bool unifiedWasChecked = tsUnifiedCurve.IsChecked == true;
                    if (unifiedWasChecked != settings.UnifiedMode)
                    {
                        // Full reload needed — apply all settings including curve editors
                        _loadingSettings = true;
                        ApplySettings(settings);
                        InitializeTempThresholdInputs();
                        _loadingSettings = false;
                        UpdateBanner();
                        return;
                    }

                    // Sync Smart Fan Mode toggle
                    _loadingSettings = true;
                    bool smartFanWasChecked = tsSmartFanMode.IsChecked == true;
                    tsSmartFanMode.IsChecked = settings.EcSmartFanMode;

                    // If Smart Fan Mode was toggled from the tray, update the control loop.
                    if (smartFanWasChecked != settings.EcSmartFanMode && _uniwillEc is not null)
                    {
                        if (settings.EcSmartFanMode)
                        {
                            // Enable Smart Fan Mode
                            if (_smartFanTempProvider == null)
                                _smartFanTempProvider = new FlydigiTemperatureProvider();

                            _smartFanControl = EcSmartFanControl.GetOrCreate(
                                _uniwillEc,
                                _smartFanTempProvider,
                                settings,
                                GetActiveCurve(),
                                GetActiveTemperatures());

                            if (!_smartFanControl.HasTemperatureSubscribers)
                            {
                                _smartFanControl.TemperatureUpdated += (s, ev) => { };
                            }

                            _smartFanControl.UpdateCurve(GetActiveCurve(), GetActiveTemperatures());
                            _smartFanControl.UpdateSettings(settings);
                            _smartFanControl.Start();
                            SmartFanCard.IsExpanded = true;
                        }
                        else
                        {
                            // Disable Smart Fan Mode
                            EcSmartFanControl.ClearInstance();
                            _smartFanControl = null;
                            SmartFanCard.IsExpanded = false;
                            // No need to ApplyCurveNow() — the tray handler already
                            // applied the normal fan curve when it stopped Smart Fan.
                        }
                    }

                    // Sync preset combo boxes if they changed (e.g., from a custom preset apply)
                    string unifiedPreset = GetSelectedPresetName(cbPreset);
                    string cpuPreset = GetSelectedPresetName(cbCpuPreset);
                    string gpuPreset = GetSelectedPresetName(cbGpuPreset);

                    bool presetChanged =
                        (settings.UnifiedMode && unifiedPreset != settings.UnifiedPreset) ||
                        (!settings.UnifiedMode && (cpuPreset != settings.CpuPreset || gpuPreset != settings.GpuPreset));

                    if (presetChanged)
                    {
                        // Reload curve editors with the new preset selections
                        ApplySettings(settings);
                        InitializeTempThresholdInputs();
                        UpdateBanner();
                    }

                    // Sync temp source dropdown
                    int tempSourceIndex = settings.EcSmartFanTempSource.ToLowerInvariant() switch
                    {
                        "cpu" => 1,
                        "gpu" => 2,
                        _ => 0
                    };
                    if (cbxSmartFanTempSource.SelectedIndex != tempSourceIndex)
                    {
                        cbxSmartFanTempSource.SelectedIndex = tempSourceIndex;
                    }

                    _loadingSettings = false;
                }
                catch { /* Non-critical: tray sync failure shouldn't crash the page */ }
            });
        }

        private void OnEcFanPresetApplied(object? sender, EcFanPresetAppliedEventArgs e)
        {
            // Only update UI if the page is currently loaded and visible.
            // If the page is unloaded (user navigated away), skip — the next
            // navigation will load settings from disk via ApplySettings.
            if (_isUnloaded) return;

            // Cancel any pending auto-apply from the debounce timer. The preset
            // was already applied to the EC by the caller (DeviceApplier).
            _applyDebounceTimer?.Stop();

            // Update UI controls to reflect the preset being applied.
            // Use _loadingSettings to prevent SelectionChanged handlers from
            // firing and corrupting state during the bulk update.
            _loadingSettings = true;
            try
            {
                if (e.UnifiedMode)
                {
                    tsUnifiedCurve.IsChecked = true;
                    UnifiedCard.IsExpanded = true;
                    SplitCurvePanel.Visibility = Visibility.Collapsed;

                    if (e.CustomDuties != null && e.CustomDuties.Length == 11)
                    {
                        cbPreset.SelectedIndex = 5; // Custom
                        var curve = new EcFanCurve { Name = e.PresetName };
                        curve.Duties.Clear();
                        foreach (var d in e.CustomDuties)
                            curve.Duties.Add(Math.Clamp(d, 0, 100));
                        FanCurveEditor.SetCurve(curve);
                        FanCurveEditor.IsReadOnly = false; // Custom is editable
                    }
                    else
                    {
                        // Map preset name to index
                        cbPreset.SelectedIndex = e.PresetName switch
                        {
                            "Silent" => 0,
                            "Performance" => 2,
                            "Full Speed" => 3,
                            "Off" => 4,
                            _ => 1 // Balanced
                        };
                        FanCurveEditor.SetCurve(GetPresetCurve(e.PresetName));
                        FanCurveEditor.IsReadOnly = true;
                    }

                    if (e.CpuTemps != null)
                        FanCurveEditor.Temperatures = e.CpuTemps;
                }
                else
                {
                    tsUnifiedCurve.IsChecked = false;
                    UnifiedCard.IsExpanded = false;
                    SplitCurvePanel.Visibility = Visibility.Visible;

                    // CPU
                    if (e.CpuPresetDuties != null && e.CpuPresetDuties.Length == 11)
                    {
                        cbCpuPreset.SelectedIndex = 5; // Custom
                        var cpuCurve = new EcFanCurve { Name = "Custom" };
                        cpuCurve.Duties.Clear();
                        foreach (var d in e.CpuPresetDuties)
                            cpuCurve.Duties.Add(Math.Clamp(d, 0, 100));
                        CpuFanCurveEditor.SetCurve(cpuCurve);
                        CpuFanCurveEditor.IsReadOnly = false; // Custom is editable
                    }

                    // GPU
                    if (e.GpuPresetDuties != null && e.GpuPresetDuties.Length == 11)
                    {
                        cbGpuPreset.SelectedIndex = 5; // Custom
                        var gpuCurve = new EcFanCurve { Name = "Custom" };
                        gpuCurve.Duties.Clear();
                        foreach (var d in e.GpuPresetDuties)
                            gpuCurve.Duties.Add(Math.Clamp(d, 0, 100));
                        GpuFanCurveEditor.SetCurve(gpuCurve);
                        GpuFanCurveEditor.IsReadOnly = false; // Custom is editable
                    }

                    if (e.CpuTemps != null)
                        CpuFanCurveEditor.Temperatures = e.CpuTemps;
                    if (e.GpuTemps != null)
                        GpuFanCurveEditor.Temperatures = e.GpuTemps;
                }

                UpdateBanner();
                // Update smart fan control with the new curve
                UpdateSmartFanCurve();
            }
            finally
            {
                _loadingSettings = false;
            }
        }

        private void ApplyOverrideState(bool isOverridden)
        {
            if (isOverridden)
            {
                overlayAdaptiveWarning.Visibility = Visibility.Visible;
                SetControlsEnabled(false);
                ShowAdaptiveSnackbar();
            }
            else
            {
                overlayAdaptiveWarning.Visibility = Visibility.Collapsed;
                SetControlsEnabled(true);
                HideAdaptiveSnackbar();
            }
        }

        private void ShowAdaptiveSnackbar()
        {
            if (_adaptiveSnackbar != null)
                SnackbarPresenter.HideCurrent();

            _adaptiveSnackbar = new Wpf.Ui.Controls.Snackbar(SnackbarPresenter)
            {
                Title = "Adaptive Mode Override",
                Content = "Adaptive Mode is currently controlling the EC fan curves. Controls on this page are currently disabled.",
                Appearance = Wpf.Ui.Controls.ControlAppearance.Info,
                Icon = new Wpf.Ui.Controls.FontIcon { FontFamily = new System.Windows.Media.FontFamily("Segoe Fluent Icons"), Glyph = "\uF167", FontSize = 16, Margin = new System.Windows.Thickness(0, 0, 10, 0) },
                IsCloseButtonEnabled = false,
                Timeout = TimeSpan.FromHours(1),
            };

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
        /// Applies the "EC manual control unavailable" state. The firmware makes manual
        /// EC control and the OEM Performance-button engine mutually exclusive, so while
        /// a stock preset is active the fan curves it accepts are the firmware's own.
        /// Adaptive Mode override owns the same overlay and wins when both would apply.
        /// </summary>
        private void ApplyStockPresetState(bool isLocked)
        {
            if (_deviceApplier.IsEcFanOverridden)
                return;

            if (isLocked)
            {
                overlayAdaptiveWarning.Visibility = Visibility.Visible;
                SetControlsEnabled(false);
                ShowStockPresetSnackbar();
            }
            else
            {
                overlayAdaptiveWarning.Visibility = Visibility.Collapsed;
                SetControlsEnabled(true);
                HideStockPresetSnackbar();
            }
        }

        private void ShowStockPresetSnackbar()
        {
            if (_stockPresetSnackbar != null)
                SnackbarPresenter.HideCurrent();

            string preset = PresetProfileApplier.DisplayName(Settings.Default.presetProfile);
            _stockPresetSnackbar = new Wpf.Ui.Controls.Snackbar(SnackbarPresenter)
            {
                Title = "Stock Preset Active",
                Content = $"{preset} preset applied — fan control customization is only possible with a Custom preset.",
                Appearance = Wpf.Ui.Controls.ControlAppearance.Info,
                Icon = new Wpf.Ui.Controls.FontIcon { FontFamily = new System.Windows.Media.FontFamily("Segoe Fluent Icons"), Glyph = "\uF167", FontSize = 16, Margin = new System.Windows.Thickness(0, 0, 10, 0) },
                IsCloseButtonEnabled = false,
                Timeout = TimeSpan.FromHours(1),
            };

            if (SnackbarPresenter.IsLoaded)
                _stockPresetSnackbar.Show(true);
            else
                SnackbarPresenter.Loaded += (s, e) => _stockPresetSnackbar?.Show(true);
        }

        private void HideStockPresetSnackbar()
        {
            SnackbarPresenter.HideCurrent();
            _stockPresetSnackbar = null;
        }

        private void SetControlsEnabled(bool enabled)
        {
            foreach (var child in UniwillAvailable.Children)
            {
                if (child is FrameworkElement fe)
                {
                    fe.IsEnabled = enabled;
                }
            }

            // Also disable/enable the bottom bar buttons
            btnResetFanState.IsEnabled = enabled;
            btnRestore.IsEnabled = enabled;
        }

        #endregion

        private async void ReadFanRpm()
        {
            try
            {
                if (_uniwillEc is null) return;

                int rpm = await _ecTelemetry.RefreshMainFanRpmAsync();
                if (!_isUnloaded && rpm > 0)
                    SetStatusText($"{rpm} RPM");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to read fan RPM: {ex.Message}");
            }
        }

        private void StatusTimer_Tick(object? sender, EventArgs e)
        {
            // Skip polling when the app is minimized — no point reading hardware
            // while the user isn't looking at the UI.
            if (Application.Current.MainWindow?.WindowState == WindowState.Minimized)
                return;

            try
            {
                // Get metrics from hardware monitoring (temp, usage, power, clock)
                var snapshot = _hardwareMonitoring.ReadSnapshot();

                // Enrich with the shared EC cache. Native EC reads run on a worker
                // and never block this DispatcherTimer callback.
                if (_uniwillEc is not null)
                {
                    EcFanDutySnapshot ecSnapshot = _ecTelemetry.ReadFanDuty();
                    snapshot = snapshot with
                    {
                        CpuFanSpeed = ecSnapshot.CpuFanPercent,
                        GpuFanSpeed = ecSnapshot.GpuFanPercent
                    };
                }

                // Update both controls from the single enriched snapshot
                CpuControl.UpdateMetrics(snapshot);
                GpuControl.UpdateMetrics(snapshot);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to read status: {ex.Message}");
            }
        }

      #region Info Banner

        private void UpdateBanner()
        {
            // Check all active curve editors for risk levels
            var bannerState = GetBannerState();

            string coolerName = GetCoolerName();

            Wpf.Ui.Controls.InfoBarSeverity newSeverity;
            string newMessage;

            switch (bannerState)
            {
                case BannerState.FanOff:
                    newSeverity = Wpf.Ui.Controls.InfoBarSeverity.Error;
                    newMessage =
                        $"An \"Off\" fan curve has been applied. Make sure that you are using a {coolerName} cooler to avoid long-term hardware damage.";
                    break;

                case BannerState.ZeroApplied:
                    newSeverity = Wpf.Ui.Controls.InfoBarSeverity.Error;
                    newMessage =
                        $"A zero-fan setting has been applied. Make sure that you are using a {coolerName} cooler to avoid long-term hardware damage.";
                    break;

                case BannerState.LowCurve:
                    newSeverity = Wpf.Ui.Controls.InfoBarSeverity.Warning;
                    newMessage =
                        $"A low fan curve has been applied. Make sure that you are using a {coolerName} cooler to avoid long-term hardware damage.";
                    break;

                default:
                    newSeverity = Wpf.Ui.Controls.InfoBarSeverity.Informational;
                    newMessage =
                        "Default fan curve profiles cannot be edited, switch to Custom in order to make a personalized curve.";
                    break;
            }

            // Only refresh if something actually changed — avoids unnecessary re-render.
            if (InfoBanner.Severity == newSeverity && InfoBanner.Message == newMessage)
                return;

            // WPF UI's InfoBar doesn't reliably re-render the Message content when
            // Severity and Message change on the same dispatcher tick. Close first,
            // then re-open on the next frame so the template picks up both properties.
            InfoBanner.IsOpen = false;
            InfoBanner.Severity = newSeverity;
            InfoBanner.Message = newMessage;

            Dispatcher.Invoke(() => InfoBanner.IsOpen = true,
                System.Windows.Threading.DispatcherPriority.Background);
        }

        private enum BannerState
        {
            Info,
            FanOff,
            ZeroApplied,
            LowCurve
        }

        private BannerState GetBannerState()
        {
            // Check if any active preset is "Off"
            bool hasOffProfile = false;

            if (tsUnifiedCurve.IsChecked == true)
            {
                if (GetSelectedPresetName(cbPreset) == "Off")
                    hasOffProfile = true;
            }
            else
            {
                if (GetSelectedPresetName(cbCpuPreset) == "Off" ||
                    GetSelectedPresetName(cbGpuPreset) == "Off")
                    hasOffProfile = true;
            }

            if (hasOffProfile)
                return BannerState.FanOff;

            // Check Custom curves for zero-fan or low-curve
            int[]? cpuDuties = null;
            int[]? gpuDuties = null;

            if (tsUnifiedCurve.IsChecked == true)
            {
                if (GetSelectedPresetName(cbPreset) == "Custom")
                {
                    cpuDuties = FanCurveEditor.GetCurve().Duties.ToArray();
                    gpuDuties = cpuDuties;
                }
            }
            else
            {
                if (GetSelectedPresetName(cbCpuPreset) == "Custom")
                    cpuDuties = CpuFanCurveEditor.GetCurve().Duties.ToArray();
                if (GetSelectedPresetName(cbGpuPreset) == "Custom")
                    gpuDuties = GpuFanCurveEditor.GetCurve().Duties.ToArray();
            }

            // Check for all-zero custom curve (zone 0 is always 0, so check zones 1-10)
            bool allZero = (cpuDuties != null && cpuDuties.Skip(1).All(d => d == 0)) &&
                           (gpuDuties != null && gpuDuties.Skip(1).All(d => d == 0));
            if (allZero)
                return BannerState.ZeroApplied;

            // Check for low curve (highest point <= 25%)
            bool hasLowCurve = false;
            if (cpuDuties != null && cpuDuties.Skip(1).Max() <= 25)
                hasLowCurve = true;
            if (gpuDuties != null && gpuDuties.Skip(1).Max() <= 25)
                hasLowCurve = true;
            if (hasLowCurve)
                return BannerState.LowCurve;

            return BannerState.Info;
        }

        private string GetCoolerName()
        {
            if (_flydigiService?.IsConnected == true || _bs1Service?.IsConnected == true)
                return "Flydigi";
            return "positive pressure";
        }

        #endregion

        #region Settings Persistence

        private void ApplySettings(FanControlSettings settings)
        {
            // Apply temperature thresholds if user customized them
            if (settings.CpuTempThresholds != null && settings.CpuTempThresholds.Length == 11)
            {
                CpuFanCurveEditor.Temperatures = settings.CpuTempThresholds;
            }
            else
            {
                CpuFanCurveEditor.Temperatures = EcFanCurve.CpuTemperatures;
            }

            if (settings.GpuTempThresholds != null && settings.GpuTempThresholds.Length == 11)
            {
                GpuFanCurveEditor.Temperatures = settings.GpuTempThresholds;
            }
            else
            {
                GpuFanCurveEditor.Temperatures = EcFanCurve.GpuTemperatures;
            }

            if (settings.UnifiedTempThresholds != null && settings.UnifiedTempThresholds.Length == 11)
            {
                FanCurveEditor.Temperatures = settings.UnifiedTempThresholds;
            }
            else
            {
                FanCurveEditor.Temperatures = EcFanCurve.CpuTemperatures;
            }

            // Restore saved preset selections and Custom duties.
            if (settings.UnifiedMode)
            {
                tsUnifiedCurve.IsChecked = true;
                UnifiedCard.IsExpanded = true;
                SplitCurvePanel.Visibility = Visibility.Collapsed;
                UnifiedTempsCard.Visibility = Visibility.Visible;
                SmartFanCard.Visibility = Visibility.Visible;
                LoadPresetIntoEditor(FanCurveEditor, cbPreset, settings.UnifiedPreset, settings.UnifiedDuties);
            }
            else
            {
                tsUnifiedCurve.IsChecked = false;
                UnifiedCard.IsExpanded = false;
                SplitCurvePanel.Visibility = Visibility.Visible;
                UnifiedTempsCard.Visibility = Visibility.Collapsed;
                SmartFanCard.Visibility = Visibility.Collapsed;
                LoadPresetIntoEditor(CpuFanCurveEditor, cbCpuPreset, settings.CpuPreset, settings.CpuDuties);
                LoadPresetIntoEditor(GpuFanCurveEditor, cbGpuPreset, settings.GpuPreset, settings.GpuDuties);
            }

            // Restore Smart Fan Mode UI state
            int tempSourceIndex = settings.EcSmartFanTempSource.ToLowerInvariant() switch
            {
                "cpu" => 1,
                "gpu" => 2,
                _ => 0 // "max"
            };
            cbxSmartFanTempSource.SelectedIndex = tempSourceIndex;
        }

        private void InitializeTempThresholdInputs()
        {
            PopulateTempGrid(ugUnifiedTemps, ugUnifiedTempInputs, FanCurveEditor.Temperatures, "Unified");
            PopulateTempGrid(ugCpuTemps, ugCpuTempInputs, CpuFanCurveEditor.Temperatures, "CPU");
            PopulateTempGrid(ugGpuTemps, ugGpuTempInputs, GpuFanCurveEditor.Temperatures, "GPU");
        }

        private void PopulateTempGrid(UniformGrid labelsGrid, UniformGrid inputsGrid, int[] temps, string prefix)
        {
            labelsGrid.Children.Clear();
            inputsGrid.Children.Clear();

            for (int i = 0; i < temps.Length; i++)
            {
                // Zone label
                var label = new TextBlock
                {
                    Text = i == 0 ? "OFF" : $"Z{i}",
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    FontSize = 10,
                    Foreground = (Brush?)Application.Current.Resources["TextFillColorTertiaryBrush"],
                };
                labelsGrid.Children.Add(label);

                // Temperature input
                var textBox = new TextBox
                {
                    Text = temps[i].ToString(),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Width = 40,
                    Padding = new Thickness(2, 2, 2, 2),
                    FontSize = 11,
                    HorizontalContentAlignment = HorizontalAlignment.Center,
                    Tag = new object[] { prefix, i },
                };
                textBox.LostKeyboardFocus += TempTextBox_LostFocus;
                inputsGrid.Children.Add(textBox);
            }
        }

        private void TempTextBox_LostFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            if (sender is not TextBox tb) return;
            if (tb.Tag is not object[] tag || tag.Length != 2) return;

            string prefix = (string)tag[0];
            int index = (int)tag[1];

            if (!int.TryParse(tb.Text, out int value))
            {
                // Revert to original value on invalid input
                tb.Text = "0";
                value = 0;
            }

            value = Math.Clamp(value, 0, 127);
            tb.Text = value.ToString();

            // Update the corresponding editor's temperatures array
            int[]? newTemps = null;
            if (prefix == "Unified")
                newTemps = (int[])FanCurveEditor.Temperatures.Clone();
            else if (prefix == "CPU")
                newTemps = (int[])CpuFanCurveEditor.Temperatures.Clone();
            else if (prefix == "GPU")
                newTemps = (int[])GpuFanCurveEditor.Temperatures.Clone();

            if (newTemps != null && index < newTemps.Length)
            {
                newTemps[index] = value;
                if (prefix == "Unified")
                    FanCurveEditor.Temperatures = newTemps;
                else if (prefix == "CPU")
                    CpuFanCurveEditor.Temperatures = newTemps;
                else
                    GpuFanCurveEditor.Temperatures = newTemps;
            }

            // Save settings when not loading
            if (!_loadingSettings)
                SaveCurrentSettings();
        }

        /// <summary>Reset unified temperature thresholds to XMG Control Center defaults.</summary>
        private void btnResetUnifiedTemps_Click(object sender, RoutedEventArgs e)
        {
            ResetTempsToDefault(FanCurveEditor, ugUnifiedTemps, ugUnifiedTempInputs);
        }

        /// <summary>Reset CPU temperature thresholds to XMG Control Center defaults.</summary>
        private void btnResetCpuTemps_Click(object sender, RoutedEventArgs e)
        {
            ResetTempsToDefault(CpuFanCurveEditor, ugCpuTemps, ugCpuTempInputs);
        }

        /// <summary>Reset GPU temperature thresholds to XMG Control Center defaults.</summary>
        private void btnResetGpuTemps_Click(object sender, RoutedEventArgs e)
        {
            ResetTempsToDefault(GpuFanCurveEditor, ugGpuTemps, ugGpuTempInputs);
        }

        private void ResetTempsToDefault(
            Uniwill_Control_Utility.Views.Controls.EcFanCurveEditor editor,
            UniformGrid labelsGrid, UniformGrid inputsGrid)
        {
            int[] defaults = EcFanCurve.DefaultTemperatures;
            editor.Temperatures = (int[])defaults.Clone();

            // Refresh the input boxes
            inputsGrid.Children.Clear();
            foreach (var temp in defaults)
            {
                var textBox = new TextBox
                {
                    Text = temp.ToString(),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Width = 40,
                    Padding = new Thickness(2, 2, 2, 2),
                    FontSize = 11,
                    HorizontalContentAlignment = HorizontalAlignment.Center,
                };
                // Re-derive prefix from which grid we're updating
                string prefix = labelsGrid == ugUnifiedTemps ? "Unified"
                              : labelsGrid == ugCpuTemps ? "CPU"
                              : "GPU";
                int index = inputsGrid.Children.Count;
                textBox.Tag = new object[] { prefix, index };
                textBox.LostKeyboardFocus += TempTextBox_LostFocus;
                inputsGrid.Children.Add(textBox);
            }

            if (!_loadingSettings)
                SaveCurrentSettings();
        }

        private static void LoadPresetIntoEditor(
            Uniwill_Control_Utility.Views.Controls.EcFanCurveEditor editor,
            ComboBox comboBox, string presetName, int[]? customDuties)
        {
            EcFanCurve curve;
            int selectedIndex;

            if (presetName == "Custom")
            {
                // Custom: use saved duties, or fall back to Performance as baseline.
                if (customDuties != null && customDuties.Length == 11)
                {
                    curve = new EcFanCurve { Name = "Custom" };
                    curve.Duties.Clear();
                    foreach (var d in customDuties)
                        curve.Duties.Add(Math.Clamp(d, 0, 100));
                }
                else
                {
                    curve = EcFanCurve.CreatePerformance();
                    curve.Name = "Custom";
                }
                selectedIndex = 5; // Custom
                editor.IsReadOnly = false;
            }
            else
            {
                // Default preset: load from factory definition, lock the editor.
                curve = presetName switch
                {
                    "Silent" => EcFanCurve.CreateSilent(),
                    "Performance" => EcFanCurve.CreatePerformance(),
                    "Full Speed" => EcFanCurve.CreateFullSpeed(),
                    "Off" => EcFanCurve.CreateOff(),
                    _ => EcFanCurve.CreateBalanced()
                };
                selectedIndex = presetName switch
                {
                    "Silent" => 0,
                    "Performance" => 2,
                    "Full Speed" => 3,
                    "Off" => 4,
                    _ => 1
                };
                editor.IsReadOnly = true;
            }

            editor.SetCurve(curve);
            comboBox.SelectedIndex = selectedIndex;
        }

        private void SaveCurrentSettings()
        {
            var settings = FanControlSettingsService.Load(); // merge with existing

            // Save unified mode toggle
            settings.UnifiedMode = tsUnifiedCurve.IsChecked == true;

            // Save preset selections
            settings.UnifiedPreset = GetSelectedPresetName(cbPreset);
            settings.CpuPreset = GetSelectedPresetName(cbCpuPreset);
            settings.GpuPreset = GetSelectedPresetName(cbGpuPreset);

            // Save Custom duties only when Custom is selected
            if (settings.UnifiedPreset == "Custom")
            {
                var curve = FanCurveEditor.GetCurve();
                settings.UnifiedDuties = curve.Duties.ToArray();
            }

            if (settings.CpuPreset == "Custom")
            {
                var curve = CpuFanCurveEditor.GetCurve();
                settings.CpuDuties = curve.Duties.ToArray();
            }

            if (settings.GpuPreset == "Custom")
            {
                var curve = GpuFanCurveEditor.GetCurve();
                settings.GpuDuties = curve.Duties.ToArray();
            }

            // Save temperature thresholds
            settings.CpuTempThresholds = CpuFanCurveEditor.Temperatures;
            settings.GpuTempThresholds = GpuFanCurveEditor.Temperatures;
            settings.UnifiedTempThresholds = FanCurveEditor.Temperatures;

            // Save Smart Fan Mode state
            settings.EcSmartFanMode = tsSmartFanMode.IsChecked == true;
            settings.EcSmartFanTempSource = _smartFanControl?.TempSource ?? settings.EcSmartFanTempSource;

            FanControlSettingsService.Save(settings);
        }

        private static string GetSelectedPresetName(ComboBox comboBox)
        {
            return (comboBox.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content?.ToString()
                   ?? "Balanced";
        }

        #endregion

        #region Preset Selection

        private void cbPreset_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (cbPreset.SelectedItem is not ComboBoxItem item) return;
            string? name = item.Content?.ToString();
            HandlePresetSelection(FanCurveEditor, name);
            if (!_loadingSettings)
            {
                SaveCurrentSettings();
                ApplyCurveNow();
                UpdateBanner();
            }
        }

        private void cbCpuPreset_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (cbCpuPreset.SelectedItem is not ComboBoxItem item) return;
            string? name = item.Content?.ToString();
            HandlePresetSelection(CpuFanCurveEditor, name);
            if (!_loadingSettings)
            {
                SaveCurrentSettings();
                ApplyCurveNow();
                UpdateBanner();
            }
        }

        private void cbGpuPreset_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (cbGpuPreset.SelectedItem is not ComboBoxItem item) return;
            string? name = item.Content?.ToString();
            HandlePresetSelection(GpuFanCurveEditor, name);
            if (!_loadingSettings)
            {
                SaveCurrentSettings();
                ApplyCurveNow();
                UpdateBanner();
            }
        }

        private void HandlePresetSelection(
            Uniwill_Control_Utility.Views.Controls.EcFanCurveEditor editor, string? name)
        {
            if (name == "Custom")
            {
                // Load saved Custom duties, or fall back to Performance as baseline.
                int[]? savedDuties = GetSavedCustomDuties(editor);
                if (savedDuties != null)
                {
                    var curve = new EcFanCurve { Name = "Custom" };
                    curve.Duties.Clear();
                    foreach (var d in savedDuties)
                        curve.Duties.Add(Math.Clamp(d, 0, 100));
                    editor.SetCurve(curve);
                }
                else
                {
                    editor.SetCurve(EcFanCurve.CreatePerformance());
                }
                editor.IsReadOnly = false;
            }
            else
            {
                editor.SetCurve(GetPresetCurve(name));
                editor.IsReadOnly = true;
            }
        }

        private int[]? GetSavedCustomDuties(Uniwill_Control_Utility.Views.Controls.EcFanCurveEditor editor)
        {
            var settings = FanControlSettingsService.Load();
            if (editor == FanCurveEditor)
                return settings.UnifiedDuties;
            if (editor == CpuFanCurveEditor)
                return settings.CpuDuties;
            if (editor == GpuFanCurveEditor)
                return settings.GpuDuties;
            return null;
        }

        private static EcFanCurve GetPresetCurve(string? name) => name switch
        {
            "Silent" => EcFanCurve.CreateSilent(),
            "Balanced" => EcFanCurve.CreateBalanced(),
            "Performance" => EcFanCurve.CreatePerformance(),
            "Full Speed" => EcFanCurve.CreateFullSpeed(),
            "Off" => EcFanCurve.CreateOff(),
            _ => EcFanCurve.CreateBalanced()
        };

        #endregion

         #region Unified Curve

      private void tsUnifiedCurve_Checked(object sender, RoutedEventArgs e)
        {
            // Skip if triggered by ApplySettings during startup
            if (_loadingSettings) return;

            // Read settings once, save CPU/GPU state, then load unified preset.
            var settings = FanControlSettingsService.Load();
            settings.CpuPreset = GetSelectedPresetName(cbCpuPreset);
            settings.GpuPreset = GetSelectedPresetName(cbGpuPreset);
            if (settings.CpuPreset == "Custom")
                settings.CpuDuties = CpuFanCurveEditor.GetCurve().Duties.ToArray();
            if (settings.GpuPreset == "Custom")
                settings.GpuDuties = GpuFanCurveEditor.GetCurve().Duties.ToArray();

            // Guard against SelectionChanged events firing and corrupting state
            _loadingSettings = true;
            try
            {
                // Load unified preset into the unified editor
                LoadPresetIntoEditor(FanCurveEditor, cbPreset, settings.UnifiedPreset, settings.UnifiedDuties);

                // Expand the card, hide split panel, show unified temps and smart fan card
                UnifiedCard.IsExpanded = true;
                SplitCurvePanel.Visibility = Visibility.Collapsed;
                UnifiedTempsCard.Visibility = Visibility.Visible;
                SmartFanCard.Visibility = Visibility.Visible;
            }
            finally
            {
                _loadingSettings = false;
            }

            settings.UnifiedMode = true;
            FanControlSettingsService.Save(settings);

            // Apply immediately — user expects instant feedback on toggle
            ApplyCurveNow();
            UpdateBanner();
        }

        private void tsUnifiedCurve_Unchecked(object sender, RoutedEventArgs e)
        {
            // Skip if triggered by ApplySettings during startup
            if (_loadingSettings) return;

            // Disable Smart Fan Mode if active (requires unified mode)
            _smartFanControl?.Stop();
            if (tsSmartFanMode.IsChecked == true)
            {
                _loadingSettings = true;
                tsSmartFanMode.IsChecked = false;
                _loadingSettings = false;

                var smartSettings = FanControlSettingsService.Load();
                smartSettings.EcSmartFanMode = false;
                FanControlSettingsService.Save(smartSettings);
            }

            // Read settings once, save unified state, then restore CPU/GPU.
            var settings = FanControlSettingsService.Load();
            settings.UnifiedPreset = GetSelectedPresetName(cbPreset);
            if (settings.UnifiedPreset == "Custom")
                settings.UnifiedDuties = FanCurveEditor.GetCurve().Duties.ToArray();

            // Guard against SelectionChanged events firing and corrupting state
            _loadingSettings = true;
            try
            {
                // Restore saved CPU/GPU state
                LoadPresetIntoEditor(CpuFanCurveEditor, cbCpuPreset, settings.CpuPreset, settings.CpuDuties);
                LoadPresetIntoEditor(GpuFanCurveEditor, cbGpuPreset, settings.GpuPreset, settings.GpuDuties);

                // Collapse the card, show split panel, hide unified temps and smart fan card
                UnifiedCard.IsExpanded = false;
                SplitCurvePanel.Visibility = Visibility.Visible;
                UnifiedTempsCard.Visibility = Visibility.Collapsed;
                SmartFanCard.Visibility = Visibility.Collapsed;
            }
            finally
            {
                _loadingSettings = false;
            }

            settings.UnifiedMode = false;
            FanControlSettingsService.Save(settings);

            // Apply immediately — user expects instant feedback on toggle
            ApplyCurveNow();
            UpdateBanner();
        }

        /// <summary>
        /// If the user clicked the chevron (not the toggle), sync the toggle to match.
        /// </summary>
        private void UnifiedCard_Expanded(object sender, RoutedEventArgs e)
        {
            if (tsUnifiedCurve.IsChecked != true)
            {
                // Chevron was clicked — sync toggle and apply unified logic
                tsUnifiedCurve.IsChecked = true;
                var cpuCurve = CpuFanCurveEditor.GetCurve();
                FanCurveEditor.SetCurve(cpuCurve);
                SplitCurvePanel.Visibility = Visibility.Collapsed;
                UnifiedTempsCard.Visibility = Visibility.Visible;
                SmartFanCard.Visibility = Visibility.Visible;
            }
        }

        /// <summary>
        /// If the user clicked the chevron (not the toggle), sync the toggle to match.
        /// </summary>
        private void UnifiedCard_Collapsed(object sender, RoutedEventArgs e)
        {
            if (tsUnifiedCurve.IsChecked != false)
            {
                // Chevron was clicked — sync toggle and apply split logic
                tsUnifiedCurve.IsChecked = false;
                var unifiedCurve = FanCurveEditor.GetCurve();
                CpuFanCurveEditor.SetCurve(unifiedCurve);
                SplitCurvePanel.Visibility = Visibility.Visible;
                UnifiedTempsCard.Visibility = Visibility.Collapsed;
                SmartFanCard.Visibility = Visibility.Collapsed;
            }
        }

        #endregion

       #region Apply / Restore

        /// <summary>
        /// Captures the current curve state and applies it to the EC in the background.
        /// Returns immediately — the UI never blocks.
        /// </summary>
        private void ApplyCurveInBackground()
        {
            if (_uniwillEc is null)
            {
                SetStatusText("EC not available");
                return;
            }

            // Skip static curve apply when Smart Fan Mode is active — the polling
            // loop manages the EC curve tables and would be overwritten.
            if (_smartFanControl?.IsActive == true)
                return;

            // Capture the curve data on the UI thread, then send to EC in the background.
            EcFanCurve cpuCurve;
            EcFanCurve gpuCurve;
            int[] cpuTemps;
            int[] gpuTemps;

            if (tsUnifiedCurve.IsChecked == true)
            {
                cpuCurve = FanCurveEditor.GetCurve();
                cpuCurve.Name = GetSelectedPresetName(cbPreset);
                gpuCurve = cpuCurve;
                cpuTemps = FanCurveEditor.Temperatures;
                gpuTemps = cpuTemps;
            }
            else
            {
                cpuCurve = CpuFanCurveEditor.GetCurve();
                cpuCurve.Name = GetSelectedPresetName(cbCpuPreset);
                gpuCurve = GpuFanCurveEditor.GetCurve();
                gpuCurve.Name = GetSelectedPresetName(cbGpuPreset);
                cpuTemps = CpuFanCurveEditor.Temperatures;
                gpuTemps = GpuFanCurveEditor.Temperatures;
            }

            SetStatusText("Applying fan curve...");

            _ = Task.Run(async () =>
            {
                try
                {
                    await _ecSemaphore.WaitAsync(_pageCts.Token);
                }
                catch (OperationCanceledException)
                {
                    return; // Page unloaded, skip apply
                }
                try
                {
                    _uniwillEc!.ApplyFanCurve(cpuCurve, gpuCurve, cpuTemps, gpuTemps);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"ERROR | FanControl: ApplyCurveInBackground failed: {ex}");
                    Dispatcher.Invoke(() =>
                    {
                        if (!_isUnloaded)
                            SetStatusText($"Failed: {ex.Message}");
                    });
                }
                finally
                {
                    _ecSemaphore.Release();
                }

                // Check cancellation before UI update
                if (_pageCts.IsCancellationRequested)
                {
                    return;
                }

                Dispatcher.Invoke(() =>
                {
                    if (_isUnloaded) return;
                    SaveCurrentSettings();
                    SetStatusText(LocalizationService.Get("Fan curve applied"));
                    UpdateBanner();
                });
            });
        }

        private void btnRestore_Click(object sender, RoutedEventArgs e)
        {
            if (_uniwillEc is null) return;

            SetStatusText(LocalizationService.Get("Restoring auto mode..."));

            _ = Task.Run(async () =>
            {
                try
                {
                    await _ecSemaphore.WaitAsync(_pageCts.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                try
                {
                    _uniwillEc!.RestoreAutoFanControl();
                }
                finally
                {
                    _ecSemaphore.Release();
                }

                if (_pageCts.IsCancellationRequested) return;

                Dispatcher.Invoke(() =>
                {
                    if (_isUnloaded) return;
                    ReadFanRpm();
                    SetStatusText(LocalizationService.Get("Auto mode restored"));

                    // Reset UI to Balanced presets
                    _loadingSettings = true;

                    // Disable unified mode if active
                    if (tsUnifiedCurve.IsChecked == true)
                    {
                        tsUnifiedCurve.IsChecked = false;
                        UnifiedCard.IsExpanded = false;
                        SplitCurvePanel.Visibility = Visibility.Visible;
                    }

                    // Set all combo boxes to Balanced
                    cbPreset.SelectedIndex = 1;
                    cbCpuPreset.SelectedIndex = 1;
                    cbGpuPreset.SelectedIndex = 1;

                    // Load Balanced curves into editors
                    CpuFanCurveEditor.SetCurve(EcFanCurve.CreateBalanced());
                    CpuFanCurveEditor.IsReadOnly = true;
                    GpuFanCurveEditor.SetCurve(EcFanCurve.CreateBalanced());
                    GpuFanCurveEditor.IsReadOnly = true;
                    FanCurveEditor.SetCurve(EcFanCurve.CreateBalanced());
                    FanCurveEditor.IsReadOnly = true;

                    _loadingSettings = false;

                    // Persist reset state
                    var settings = FanControlSettingsService.Load();
                    settings.UnifiedMode = false;
                    settings.UnifiedPreset = "Balanced";
                    settings.CpuPreset = "Balanced";
                    settings.GpuPreset = "Balanced";
                    settings.UnifiedDuties = null;
                    settings.CpuDuties = null;
                    settings.GpuDuties = null;
                    FanControlSettingsService.Save(settings);
                });
            });
        }

        #endregion

        #region Turbo

        private void tsTurbo_Checked(object sender, RoutedEventArgs e)
        {
            if (_uniwillEc is null) return;

            // Remember whether Smart Fan Mode was active so we can restore it
            // when Turbo is disabled. Do NOT overwrite the persisted setting —
            // only track in-memory so the toggle survives the Turbo session.
            _smartFanWasActiveBeforeTurbo = tsSmartFanMode.IsChecked == true;

            // Stop smart fan mode if active — turbo and smart fan are mutually exclusive
            _smartFanControl?.Stop();
            if (_smartFanWasActiveBeforeTurbo)
            {
                _loadingSettings = true;
                tsSmartFanMode.IsChecked = false;
                _loadingSettings = false;

                // Do NOT persist EcSmartFanMode = false — we want to restore it
                // when Turbo is disabled. Just stop the control loop in-memory.
            }

            SetStatusText(LocalizationService.Get("Enabling turbo mode..."));
            SetTurboSwitchColor(true);

            _ = Task.Run(async () =>
            {
                try
                {
                    await _ecSemaphore.WaitAsync(_pageCts.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                try
                {
                    await _uniwillEc!.ToggleTurboAsync();
                }
                catch (Exception ex)
                {
                }
                finally
                {
                    _ecSemaphore.Release();
                }

                if (_pageCts.IsCancellationRequested)
                {
                    return;
                }

                Dispatcher.Invoke(() =>
                {
                    if (_isUnloaded) return;
                    SetStatusText(LocalizationService.Get("Turbo mode ENABLED — fans at max duty"));
                    ReadFanRpm();
                });
            });
        }

        private void tsTurbo_Unchecked(object sender, RoutedEventArgs e)
        {
            if (_uniwillEc is null) return;

            // Restart smart fan mode if it was active before turbo was enabled.
            // Use the in-memory flag instead of persisted settings because Turbo
            // no longer overwrites EcSmartFanMode to false.
            if (_smartFanWasActiveBeforeTurbo)
            {
                _loadingSettings = true;
                tsSmartFanMode.IsChecked = true;
                _loadingSettings = false;
                _smartFanWasActiveBeforeTurbo = false;

                var settings = FanControlSettingsService.Load();

                if (_smartFanControl == null)
                {
                    _smartFanTempProvider = new FlydigiTemperatureProvider();
                    var curve = GetActiveCurve();
                    var temps = GetActiveTemperatures();
                    _smartFanControl = new EcSmartFanControl(
                        _uniwillEc,
                        _smartFanTempProvider,
                        settings,
                        curve,
                        temps);

                    _smartFanControl.TemperatureUpdated += (s, ev) =>
                    {
                        // Status text update removed — keep the "Smart Fan Mode enabled"
                        // message static instead of spamming temperature/duty updates.
                    };
                }

                _smartFanControl.UpdateCurve(GetActiveCurve(), GetActiveTemperatures());
                _smartFanControl.UpdateSettings(settings);
                _smartFanControl.Start();
            }

            SetStatusText(LocalizationService.Get("Disabling turbo mode..."));

            _ = Task.Run(async () =>
            {
                try
                {
                    await _ecSemaphore.WaitAsync(_pageCts.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                try
                {
                    _uniwillEc!.ToggleTurbo();
                }
                catch (Exception ex)
                {
                }
                finally
                {
                    _ecSemaphore.Release();
                }

                if (_pageCts.IsCancellationRequested)
                {
                    return;
                }

                Dispatcher.Invoke(() =>
                {
                    if (_isUnloaded) return;
                    SetStatusText(LocalizationService.Get("Turbo mode DISABLED — previous settings restored"));
                    SetTurboSwitchColor(false);
                    ReadFanRpm();
                });
            });
        }

        /// <summary>
        /// Sets an intense red color on the turbo toggle switch when enabled,
        /// disregarding the system theme's accent color.
        /// </summary>
        private void SetTurboSwitchColor(bool isOn)
        {
            if (isOn)
            {
                // Intense red that stands out regardless of theme
                tsTurbo.Foreground = new SolidColorBrush(Color.FromRgb(255, 30, 30));
            }
            else
            {
                // ClearValue removes the local value so the XAML DynamicResource
                // binding kicks back in. Setting to null doesn't work — it falls
                // through to the parent's inherited Foreground (black).
                tsTurbo.ClearValue(Control.ForegroundProperty);
            }
        }

        #endregion

       #region Smart Fan Mode

        private void tsSmartFanMode_Checked(object sender, RoutedEventArgs e)
        {
            if (_uniwillEc is null) return;

            // Skip if triggered by ApplySettings during startup
            if (_loadingSettings) return;

            // Stop turbo mode if active — smart fan mode and turbo are mutually exclusive
            if (tsTurbo.IsChecked == true)
            {
                tsTurbo.IsChecked = false;
            }

            var settings = FanControlSettingsService.Load();
            settings.EcSmartFanMode = true;
            FanControlSettingsService.Save(settings);

            // Use the app-scoped singleton — may already be running from
            // DashboardViewModel startup or MainWindow resume.
            var curve = GetActiveCurve();
            var temps = GetActiveTemperatures();

            // If no provider yet, create one (needed for first call to GetOrCreate)
            if (_smartFanTempProvider == null)
                _smartFanTempProvider = new FlydigiTemperatureProvider();

            _smartFanControl = EcSmartFanControl.GetOrCreate(
                _uniwillEc,
                _smartFanTempProvider,
                settings,
                curve,
                temps);

            // Subscribe to temperature updates (safe to add multiple times,
            // but we only want one subscription per page lifetime)
            if (!_smartFanControl.HasTemperatureSubscribers)
            {
                _smartFanControl.TemperatureUpdated += (s, ev) =>
                {
                    // Status text update removed — keep the "Smart Fan Mode enabled"
                    // message static instead of spamming temperature/duty updates.
                };
            }

            // Update curve and settings, then start (idempotent if already running)
            _smartFanControl.UpdateCurve(GetActiveCurve(), GetActiveTemperatures());
            _smartFanControl.UpdateSettings(settings);
            _smartFanControl.Start();

            // Expand the card
            SmartFanCard.IsExpanded = true;

            SetStatusText("Smart Fan Mode enabled");
        }

        private void tsSmartFanMode_Unchecked(object sender, RoutedEventArgs e)
        {
            // Skip if triggered by ApplySettings during startup
            if (_loadingSettings) return;

            // Collapse the card
            SmartFanCard.IsExpanded = false;

            var settings = FanControlSettingsService.Load();
            settings.EcSmartFanMode = false;
            FanControlSettingsService.Save(settings);

            // Stop the smart fan control loop and clear the singleton
            EcSmartFanControl.ClearInstance();
            _smartFanControl = null;

            // Re-apply the normal fan curves so the EC stops using the flat
            // smart fan tables and returns to the user's active curve.
            ApplyCurveNow();

            SetStatusText("Smart Fan Mode disabled");
        }

        private void cbxSmartFanTempSource_SelectionChanged(object sender, EventArgs e)
        {
            if (cbxSmartFanTempSource.SelectedItem is not System.Windows.Controls.ComboBoxItem item)
                return;

            var source = item.Content?.ToString()?.ToLowerInvariant() switch
            {
                "cpu" => "cpu",
                "gpu" => "gpu",
                _ => "max"
            };

            var settings = FanControlSettingsService.Load();
            settings.EcSmartFanTempSource = source;
            FanControlSettingsService.Save(settings);

            // Update smart control if active
            if (_smartFanControl != null)
            {
                _smartFanControl.TempSource = source;
                _smartFanControl.UpdateSettings(settings);
            }
        }

        /// <summary>
        /// If the user clicked the chevron (not the toggle), sync the toggle to match.
        /// </summary>
        private void SmartFanCard_Expanded(object sender, RoutedEventArgs e)
        {
            if (tsSmartFanMode.IsChecked != true)
            {
                tsSmartFanMode.IsChecked = true;
            }
        }

        /// <summary>
        /// If the user clicked the chevron (not the toggle), sync the toggle to match.
        /// </summary>
        private void SmartFanCard_Collapsed(object sender, RoutedEventArgs e)
        {
            if (tsSmartFanMode.IsChecked != false)
            {
                tsSmartFanMode.IsChecked = false;
            }
        }

        private void SmartFanCard_Loaded(object sender, RoutedEventArgs e)
        {
            // Hide the chevron so clicking the header still toggles but the arrow is invisible (same pattern as UnifiedCard)
            Application.Current.Dispatcher.BeginInvoke(
                (Action)(() =>
                {
                    var toggle = SmartFanCard.Template?.FindName("ExpanderToggleButton", SmartFanCard) as ToggleButton;
                    if (toggle?.Template is not null)
                    {
                        var chevronGrid = toggle.Template.FindName("ChevronGrid", toggle) as FrameworkElement;
                        if (chevronGrid is not null)
                        {
                            chevronGrid.Visibility = Visibility.Collapsed;
                            return;
                        }
                    }

                    WalkAndHideChevron(SmartFanCard);
                }),
                DispatcherPriority.Loaded);
        }

        /// <summary>
        /// Updates the smart fan control with the current curve when the user edits it.
        /// Safe to call when smart fan mode is inactive (no-op).
        /// </summary>
        private void UpdateSmartFanCurve()
        {
            _smartFanControl?.UpdateCurve(GetActiveCurve(), GetActiveTemperatures());
        }

        /// <summary>
        /// Gets the currently active curve based on unified/split mode.
        /// </summary>
        private EcFanCurve GetActiveCurve()
        {
            if (tsUnifiedCurve.IsChecked == true)
                return FanCurveEditor.GetCurve();
            // In split mode, use the CPU curve as the base for smart fan mode
            return CpuFanCurveEditor.GetCurve();
        }

        /// <summary>
        /// Gets the currently active temperature thresholds based on unified/split mode.
        /// </summary>
        private int[] GetActiveTemperatures()
        {
            if (tsUnifiedCurve.IsChecked == true)
                return FanCurveEditor.Temperatures;
            return CpuFanCurveEditor.Temperatures;
        }

        #endregion

        #region Reset Fan State

        private void btnResetFanState_Click(object sender, RoutedEventArgs e)
        {
            if (_uniwillEc is null) return;

            SetStatusText(LocalizationService.Get("Resetting fan state..."));

            _ = Task.Run(async () =>
            {
                try
                {
                    await _ecSemaphore.WaitAsync(_pageCts.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                try
                {
                    _uniwillEc!.ResetFanState();
                }
                finally
                {
                    _ecSemaphore.Release();
                }

                if (_pageCts.IsCancellationRequested) return;

                Dispatcher.Invoke(() =>
                {
                    if (_isUnloaded) return;
                    if (tsTurbo.IsChecked == true)
                    {
                        tsTurbo.IsChecked = false;
                        SetTurboSwitchColor(false);
                    }

                    SetStatusText(LocalizationService.Get("Fan state reset — fans at 100%"));
                    ReadFanRpm();
                });
            });
        }

        #endregion

        #region Page Lifecycle

        private void UnifiedCard_Loaded(object sender, RoutedEventArgs e)
        {
            // Defer until template is fully applied
            Application.Current.Dispatcher.BeginInvoke(
                (Action)(() =>
                {
                    // Find the ExpanderToggleButton template part, then locate the chevron inside it
                    var toggle = UnifiedCard.Template?.FindName("ExpanderToggleButton", UnifiedCard) as ToggleButton;
                    if (toggle?.Template is not null)
                    {
                        // The chevron grid lives inside the toggle button's template
                        var chevronGrid = toggle.Template.FindName("ChevronGrid", toggle) as FrameworkElement;
                        if (chevronGrid is not null)
                        {
                            chevronGrid.Visibility = Visibility.Collapsed;
                            return;
                        }
                    }

                    // Fallback: walk visual tree for the first SymbolIcon (the chevron)
                    WalkAndHideChevron(UnifiedCard);
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

        private void Page_Loaded(object sender, RoutedEventArgs e)
        {
            _isUnloaded = false;

            // Replace the canceled token source so background tasks aren't
            // immediately canceled on the next navigation back.
            _pageCts.Dispose();
            _pageCts = new CancellationTokenSource();

            // Reload settings from disk — the Presets page may have changed
            // the fan curve while we were unloaded (e.g., applying a power
            // profile maps to a fan preset). Reloading keeps the UI in sync.
            var settings = FanControlSettingsService.Load();
            _loadingSettings = true;
            ApplySettings(settings);
            InitializeTempThresholdInputs();
            _loadingSettings = false;

            _statusTimer?.Start();
            UpdateBanner();

            // Re-apply override state on every navigation back to this page.
            // The DeviceApplier event only fires when the state *changes*, so if we
            // navigated away while override was active (Unloaded hid the snackbar),
            // we need to restore the overlay + snackbar on return.
            bool isOverridden = _deviceApplier.IsEcFanOverridden;
            ApplyOverrideState(isOverridden);

            // Re-read the EC manual-control gate on navigation too — a preset may have
            // been applied while this page was hidden, and the event only fires on change.
            _deviceApplier.SyncManualControlLock();
            ApplyStockPresetState(_deviceApplier.IsManualControlLocked);

            // NOTE: Fan curve auto-apply is handled in DashboardViewModel.OnNavigatedToAsync
            // (the app always launches on Dashboard). This page does not auto-apply on
            // navigation — the user explicitly applies via the 5s debounce timer.

            // Acquire hardware monitoring lease on a background thread to avoid
            // blocking the UI while LibreHardwareMonitor probes hardware.
            // NOTE: Do NOT hold _ecSemaphore during Acquire — it probes hardware
            // sensors (not the EC) and can block for seconds, which would deadlock
            // all fan control operations waiting on the semaphore.
            if (_monitoringLease is null)
            {
                _ = Task.Run(() =>
                {
                    _monitoringLease = _hardwareMonitoring.Acquire(
                        HardwareMonitoringCategory.Cpu |
                        HardwareMonitoringCategory.Gpu);
                });
            }

            // Restart Smart Fan Mode if it was active before navigation away.
            if (settings.EcSmartFanMode && _uniwillEc is not null)
            {
                // If no provider yet, create one
                if (_smartFanTempProvider == null)
                    _smartFanTempProvider = new FlydigiTemperatureProvider();

                var curve = GetActiveCurve();
                var temps = GetActiveTemperatures();

                _smartFanControl = EcSmartFanControl.GetOrCreate(
                    _uniwillEc,
                    _smartFanTempProvider,
                    settings,
                    curve,
                    temps);

                // Subscribe to temperature updates if not already subscribed
                if (!_smartFanControl.HasTemperatureSubscribers)
                {
                    _smartFanControl.TemperatureUpdated += (s, ev) =>
                    {
                        // Status text update removed — keep the "Smart Fan Mode enabled"
                        // message static instead of spamming temperature/duty updates.
                    };
                }

                _smartFanControl.UpdateCurve(GetActiveCurve(), GetActiveTemperatures());
                _smartFanControl.UpdateSettings(settings);
                _smartFanControl.Start();

                // Restore toggle state and expand the card
                _loadingSettings = true;
                tsSmartFanMode.IsChecked = true;
                SmartFanCard.IsExpanded = true;
                _loadingSettings = false;
            }
        }

        private void Page_Unloaded(object? sender, EventArgs e)
        {
            _isUnloaded = true;
            _statusTimer?.Stop();

            // Clean up debounce timer
            _applyDebounceTimer?.Stop();
            _applyDebounceTimer?.Dispose();
            _applyDebounceTimer = null;

            // Hide adaptive snackbar
            HideAdaptiveSnackbar();
            HideStockPresetSnackbar();

            // Stop Smart Fan Mode polling loop (EC tables remain as safety net)
            _smartFanControl?.Stop();

            // Keep the hardware monitoring lease alive. Disposing it closes the
            // LibreHardwareMonitor Computer instance, forcing a full hardware re-probe
            // on the next navigation back. The lease stays active until app exit
            // (DI container teardown). The _statusTimer is already stopped above, so
            // no polling happens while the page is invisible.

            // Signal all waiting tasks to cancel.
            _pageCts.Cancel();

            // NOTE: Do NOT call RestoreAutoFanControl() here.
            // The user's fan curve should persist across app shutdowns.
            // On next startup, the curve is auto-applied in Page_Loaded.
        }

        #endregion
    }
}
