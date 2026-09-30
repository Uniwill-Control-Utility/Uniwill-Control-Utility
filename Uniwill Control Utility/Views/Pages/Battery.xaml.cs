using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Uniwill_Control_Utility.Properties;
using Uniwill_Control_Utility.Services;
using Wpf.Ui.Abstractions.Controls;

namespace Uniwill_Control_Utility.Views.Pages
{
    public partial class Battery : Page, INavigationAware
    {
        private const int RefreshIntervalSeconds = 5;
        private const int ApplyDelaySeconds = 2;
        private const int BatteryModeLimit = 2;
        // After a user change, skip syncing the combo box from EC for this many
        // milliseconds to avoid reading back the stale value before the EC commits.
        private const int ApplyGraceMs = 10000; // 10 seconds

        private readonly UniwillECService? _ecService;
        private readonly DispatcherTimer _refreshTimer = new()
        {
            Interval = TimeSpan.FromSeconds(RefreshIntervalSeconds),
            IsEnabled = false
        };
        private bool _isEcAvailable;
        private bool _isLoaded;

        // Delayed refresh timer created after each user change. Tracked as a field
        // so it can be stopped on Page_Unloaded — the NavigationView caches the page,
        // so a leaked timer would fire on the hidden page and overwrite state.
        private DispatcherTimer? _delayTimer;

        // Shared across page instances (AddScoped creates a new page on each navigation).
        // Without this, navigating away and back resets the grace period to zero,
        // causing the 5-second timer to read a stale EC value and overwrite the UI.
        private static DateTime _lastUserApplyTime;

     public Battery()
        {
            InitializeComponent();

            _refreshTimer.Tick += OnRefreshTimerTick;

            _ecService = App.GetService<UniwillECService>();
            if (_ecService is not null)
            {
                try
                {
                    _isEcAvailable = _ecService.Initialize();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Battery] Failed to initialize EC service: {ex.Message}");
                    _isEcAvailable = false;
                }
            }
        }

      /// <inheritdoc />
        /// Only fires on the very first navigation (page creation).
        public async Task OnNavigatedToAsync()
        {
            SyncFromEcAndStartTimer();
        }

        /// <inheritdoc />
        public async Task OnNavigatedFromAsync()
        {
            _refreshTimer.IsEnabled = false;
            _delayTimer?.Stop();
            _delayTimer = null;
            _isLoaded = false;
        }

       private void Page_Loaded(object sender, RoutedEventArgs e)
        {
            if (!_isEcAvailable)
            {
                BatteryAvailable.Visibility = Visibility.Collapsed;
                BatteryUnavailable.Visibility = Visibility.Visible;
            }
        }

        private void Page_Unloaded(object sender, RoutedEventArgs e)
        {
            _refreshTimer.IsEnabled = false;
            _delayTimer?.Stop();
            _delayTimer = null;
            _isLoaded = false;
        }

      /// <summary>
        /// Syncs combo box from EC, applies startup correction, restarts timer.
        /// Called from OnNavigatedToAsync (first navigation to this cached page).
        /// The tbChargeLimit textblock is XAML-bound to the combo box so it always
        /// reflects the current selection without needing separate code-behind updates.
        /// </summary>
        private void SyncFromEcAndStartTimer()
        {
            if (!_isEcAvailable)
            {
                BatteryAvailable.Visibility = Visibility.Collapsed;
                BatteryUnavailable.Visibility = Visibility.Visible;
                return;
            }

            BatteryAvailable.Visibility = Visibility.Visible;
            BatteryUnavailable.Visibility = Visibility.Collapsed;

            // Sync combo box from EC state without triggering SelectionChanged.
            int currentMode;
            try
            {
                currentMode = _ecService!.GetBatteryMode();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Battery] Failed to read mode from EC: {ex.Message}");
                currentMode = Settings.Default.BatteryMode;
            }

            cbChargingLimit.SelectionChanged -= cbChargingLimit_SelectionChanged;
            cbChargingLimit.SelectedIndex = currentMode;
            cbChargingLimit.SelectionChanged += cbChargingLimit_SelectionChanged;

            // If saved setting differs from EC, apply saved setting.
            int savedMode = Settings.Default.BatteryMode;
            if (savedMode != currentMode)
            {
                try
                {
                    _ecService.SetBatteryMode(savedMode);
                    _lastUserApplyTime = DateTime.UtcNow;
                    Debug.WriteLine($"[Battery] Startup: EC mode ({currentMode}) differed from saved ({savedMode}), applied saved");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Battery] Startup apply failed: {ex.Message}");
                }
            }

            // Sync UI and restart timer.
            RefreshAll();
            _refreshTimer.IsEnabled = true;
            _isLoaded = true;
        }

        private void cbChargingLimit_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_isLoaded)
                return;

            int mode = cbChargingLimit.SelectedIndex;
            if (mode < 0 || mode > BatteryModeLimit)
                return;

            try
            {
                _ecService!.SetBatteryMode(mode);
                Settings.Default.BatteryMode = mode;
                Settings.Default.Save();
                _lastUserApplyTime = DateTime.UtcNow;
                Debug.WriteLine($"[Battery] Applied charging limit: {mode switch { 0 => "100%", 1 => "80%", 2 => "60%", _ => "unknown" }} (mode {mode})");

                // Delay refresh to let the EC process the change (timer auto-starts and self-stops in callback).
                _delayTimer?.Stop(); // Cancel any pending delay from a previous change
                _delayTimer = new DispatcherTimer(TimeSpan.FromSeconds(ApplyDelaySeconds), DispatcherPriority.Normal, (s, e) =>
                {
                    RefreshAll();
                    ((DispatcherTimer)s!).Stop();
                    _delayTimer = null;
                }, Dispatcher);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Battery] Failed to apply mode: {ex.Message}");
            }
        }

        private void OnRefreshTimerTick(object? sender, EventArgs e)
        {
            // Skip polling when the app is minimized.
            if (Application.Current.MainWindow?.WindowState == WindowState.Minimized)
                return;

            RefreshAll();
        }

        private void btnRefresh_Click(object sender, RoutedEventArgs e)
        {
            RefreshAll();
        }

       /// <summary>
        /// Refreshes both the charge limit (from EC) and all telemetry.
        /// The tbChargeLimit textblock is XAML-bound to cbChargingLimit.SelectedIndex,
        /// so we only need to sync the combo box here.
        /// </summary>
        private void RefreshAll()
        {
            if (_ecService is null) return;

            // Skip syncing the combo box if the user recently changed the setting —
            // the EC may not have committed yet.
            bool withinGrace = (DateTime.UtcNow - _lastUserApplyTime).TotalMilliseconds < ApplyGraceMs;

            try
            {
                int ecMode = _ecService.GetBatteryMode();
                if (!withinGrace)
                {
                    // Sync combo box to EC state without triggering SelectionChanged
                    // (which would write the read-back value back to the EC).
                    cbChargingLimit.SelectionChanged -= cbChargingLimit_SelectionChanged;
                    try
                    {
                        cbChargingLimit.SelectedIndex = ecMode;
                    }
                    finally
                    {
                        cbChargingLimit.SelectionChanged += cbChargingLimit_SelectionChanged;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Battery] Failed to read charge limit from EC: {ex.Message}");
            }

            RefreshTelemetry();
        }

        private void RefreshTelemetry()
        {
            if (_ecService is null) return;

            try
            {
                var telemetry = _ecService.ReadBatteryTelemetry();
                if (telemetry is not null)
                    Application.Current.Dispatcher.Invoke(() => UpdateTelemetryUi(telemetry.Value));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Battery] Failed to read telemetry: {ex.Message}");
            }
        }

        private void UpdateTelemetryUi(UniwillECService.BatteryTelemetry telemetry)
        {
            if (telemetry.IsAcPowered)
            {
                iconPowerSource.Glyph = "\xE9A3"; // plug
                tbPowerSource.Text = "AC Power";
            }
            else
            {
                iconPowerSource.Glyph = "\xF2BF"; // battery
                tbPowerSource.Text = "Battery";
            }

            tbStateOfCharge.Text = telemetry.StateOfCharge >= 0 ? $"{telemetry.StateOfCharge}%" : "N/A";
            tbHealth.Text = telemetry.HealthPercent > 0 ? $"{telemetry.HealthPercent}%" : "N/A";
            tbTemperature.Text = telemetry.TemperatureC >= 0 ? $"{telemetry.TemperatureC} °C" : "N/A";
            tbCycleCount.Text = telemetry.CycleCount >= 0 ? telemetry.CycleCount.ToString() : "N/A";
            tbVoltage.Text = telemetry.PresentVoltageMv > 0 ? $"{telemetry.PresentVoltageMv} mV" : "N/A";
            int chargeRate = telemetry.ChargeRateMa;
            double chargeWatts = Math.Abs(chargeRate * (double)telemetry.PresentVoltageMv / 1_000_000.0);
            tbChargeRate.Text = $"{chargeRate} mA ({chargeWatts:F1} W)";
        }
    }
}
