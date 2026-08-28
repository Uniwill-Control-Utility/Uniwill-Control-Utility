using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Universal_x86_Tuning_Utility.Properties;
using Universal_x86_Tuning_Utility.Services;

namespace Universal_x86_Tuning_Utility.Views.Pages
{
    public partial class Battery : Page
    {
        private readonly UniwillECService? _ecService;
        private readonly DispatcherTimer _refreshTimer = new()
        {
            Interval = TimeSpan.FromSeconds(5),
            IsEnabled = false
        };
        private bool _isEcAvailable;
        private bool _isLoaded;

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
                catch
                {
                    _isEcAvailable = false;
                }
            }
        }

        private void Page_Loaded(object sender, RoutedEventArgs e)
        {
            if (!_isEcAvailable)
            {
                BatteryAvailable.Visibility = Visibility.Collapsed;
                BatteryUnavailable.Visibility = Visibility.Visible;
                return;
            }

            BatteryAvailable.Visibility = Visibility.Visible;
            BatteryUnavailable.Visibility = Visibility.Collapsed;

            // Read current mode from EC
            int currentMode;
            try
            {
                currentMode = _ecService!.GetBatteryMode();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Battery] Failed to read mode from EC: {ex.Message}");
                currentMode = (int)Settings.Default.BatteryMode;
            }

            // Set combo box to match EC state
            cbChargingLimit.SelectedIndex = currentMode;

            // If saved setting differs from EC, apply saved setting
            int savedMode = (int)Settings.Default.BatteryMode;
            if (savedMode != currentMode)
            {
                try
                {
                    _ecService.SetBatteryMode(savedMode);
                    Debug.WriteLine($"[Battery] Startup: EC mode ({currentMode}) differed from saved ({savedMode}), applied saved");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Battery] Startup apply failed: {ex.Message}");
                }
            }

            RefreshAll();
            _refreshTimer.IsEnabled = true;
            _isLoaded = true;
        }

        private void Page_Unloaded(object sender, RoutedEventArgs e)
        {
            _refreshTimer.IsEnabled = false;
        }

        private void cbChargingLimit_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_isLoaded)
                return;

            int mode = cbChargingLimit.SelectedIndex;
            if (mode < 0 || mode > 2)
                return;

            try
            {
                _ecService!.SetBatteryMode(mode);
                Settings.Default.BatteryMode = mode;
                Settings.Default.Save();
                Debug.WriteLine($"[Battery] Applied charging limit: {mode switch { 0 => "100%", 1 => "80%", 2 => "60%", _ => "unknown" }} (mode {mode})");

                // Re-read EC state to confirm
                RefreshAll();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Battery] Failed to apply mode: {ex.Message}");
            }
        }

        private void OnRefreshTimerTick(object? sender, EventArgs e)
        {
            RefreshAll();
        }

        private void btnRefresh_Click(object sender, RoutedEventArgs e)
        {
            RefreshAll();
        }

        /// <summary>
        /// Refreshes both the charge limit (from EC) and all telemetry.
        /// </summary>
        private void RefreshAll()
        {
            if (_ecService is null) return;

            // Re-read charge limit from EC
            try
            {
                int ecMode = _ecService.GetBatteryMode();
                Application.Current.Dispatcher.Invoke(() =>
                {
                    // Sync combo box to EC state
                    cbChargingLimit.SelectedIndex = ecMode;
                    tbChargeLimit.Text = $"{ecMode switch { 0 => "100", 1 => "80", 2 => "60", _ => "--" }}%";
                });
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
