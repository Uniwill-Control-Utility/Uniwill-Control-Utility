using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Uniwill_Control_Utility.Models;
using Uniwill_Control_Utility.Services;
using Wpf.Ui;
using Wpf.Ui.Abstractions.Controls;

namespace Uniwill_Control_Utility.ViewModels
{
    public partial class DashboardViewModel :
        ObservableObject,
        INavigationAware
    {
        private const string WikiUrl = "https://github.com/jgudec/Uniwill-Control-Utility/wiki";

        private readonly INavigationService _navigationService;
        private readonly IHardwareMonitoringService _hardwareMonitoring;
        private readonly UniwillECService? _uniwillEc;
        private readonly WaterCoolerService _waterCoolerService;
        private readonly FlydigiCoolerService _flydigiService;
        private readonly Bs1Service _bs1Service;
        private readonly DispatcherTimer _refreshTimer;
        private readonly DispatcherTimer _batteryEcTimer;
        private IDisposable? _lease;
        private int _batteryHealthPercent;
        private int _batteryTemperatureC;
        private int _batteryChargeLimit;

        public DashboardViewModel(
            INavigationService navigationService,
            IHardwareMonitoringService hardwareMonitoring,
            UniwillECService? uniwillEc,
            WaterCoolerService waterCoolerService,
            FlydigiCoolerService flydigiService,
            Bs1Service bs1Service)
        {
            _navigationService = navigationService
                ?? throw new ArgumentNullException(nameof(navigationService));
            _hardwareMonitoring = hardwareMonitoring
                ?? throw new ArgumentNullException(nameof(hardwareMonitoring));
            _uniwillEc = uniwillEc;
            _waterCoolerService = waterCoolerService;
            _flydigiService = flydigiService;
            _bs1Service = bs1Service;

            _refreshTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1),
                IsEnabled = false
            };
            _refreshTimer.Tick += OnRefreshTimerTick;

            _batteryEcTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(10),
                IsEnabled = false
            };
            _batteryEcTimer.Tick += OnBatteryEcTimerTick;
        }

        private void OnRefreshTimerTick(object? sender, EventArgs e)
        {
            // Skip polling when the app is minimized — no point reading hardware
            // while the user isn't looking at the UI.
            if (Application.Current.MainWindow?.WindowState == WindowState.Minimized)
                return;

            try
            {
                HardwareMetricsSnapshot snapshot = _hardwareMonitoring.ReadSnapshot();

                // Enrich with EC fan speed data if available
                if (_uniwillEc is not null)
                {
                    try
                    {
                        int cpuPwm = _uniwillEc.GetFanPwm(0);
                        int gpuPwm = _uniwillEc.GetFanPwm(1);
                        snapshot = snapshot with
                        {
                            CpuFanSpeed = (int)Math.Round(cpuPwm * 100.0 / 200.0),
                            GpuFanSpeed = (int)Math.Round(gpuPwm * 100.0 / 200.0)
                        };
                    }
                    catch { /* EC read failed, fan speed stays 0 */ }
                }

                // Enrich with cached battery EC data (refreshed every 10s)
                snapshot = snapshot with
                {
                    BatteryHealthPercent = _batteryHealthPercent,
                    BatteryTemperatureC = _batteryTemperatureC,
                    BatteryChargeLimit = _batteryChargeLimit
                };

                MetricsUpdated?.Invoke(this, snapshot);
            }
            catch
            {
                // Silently ignore transient hardware reading errors
            }

            try
            {
                DeviceMetricsSnapshot deviceSnapshot = BuildDeviceSnapshot();
                DeviceMetricsUpdated?.Invoke(this, deviceSnapshot);
            }
            catch
            {
                // Silently ignore transient device reading errors
            }
        }

        private void OnBatteryEcTimerTick(object? sender, EventArgs e)
        {
            // Skip polling when the app is minimized.
            if (Application.Current.MainWindow?.WindowState == WindowState.Minimized)
                return;

            if (_uniwillEc is null)
                return;

            // Only refresh temperature on the timer; health and charge limit
            // are fetched once on navigation and cached.
            try
            {
                int temp = _uniwillEc.ReadBatteryTemperatureC();
                _batteryTemperatureC = temp;
            }
            catch { /* EC read failed, keep cached value */ }
        }

        public event EventHandler<HardwareMetricsSnapshot>? MetricsUpdated;
        public event EventHandler<DeviceMetricsSnapshot>? DeviceMetricsUpdated;

        /// <summary>
        /// Returns the navigation service for use by dashboard controls.
        /// </summary>
        public INavigationService GetNavigationService() => _navigationService;

        private DeviceMetricsSnapshot BuildDeviceSnapshot()
        {
            // Hydro UI (LCT watercooler)
            bool hydroConnected = _waterCoolerService.IsConnected;
            int hydroPumpVoltage = 0;
            int hydroFanSpeed = 0;
            int hydroFanRpm = 0;

            if (hydroConnected)
            {
                try
                {
                    var wcSettings = _waterCoolerService.GetSettings();
                    hydroPumpVoltage = wcSettings.GetPumpVoltage() switch
                    {
                        PumpVoltage.V7 => 7,
                        PumpVoltage.V8 => 8,
                        PumpVoltage.V11 => 11,
                        _ => 0
                    };
                    hydroFanSpeed = (int)wcSettings.GetFanSpeed();
                }
                catch { /* non-critical */ }
            }

            // Flydigi cooler (BS2/BS2 Pro/BS3/BS3 Pro)
            bool flydigiConnected = _flydigiService.IsConnected;
            int flydigiFanRpm = 0;
            string flydigiRgbMode = string.Empty;
            string flydigiModelName = string.Empty;

            if (flydigiConnected)
            {
                try
                {
                    var fanData = _flydigiService.FanRpmData;
                    flydigiFanRpm = fanData?.CurrentRpm ?? 0;
                    if (flydigiFanRpm == 0)
                        flydigiFanRpm = fanData?.TargetRpm ?? 0;

                    var bsSettings = _flydigiService.GetSettings();
                    flydigiRgbMode = bsSettings.RgbMode ?? string.Empty;

                    flydigiModelName = _flydigiService.ConnectedDeviceInfo?.ModelName ?? string.Empty;
                }
                catch { /* non-critical */ }
            }

            // Track whether we're on BS1 (BLE) or BS2+ (HID) for RPM range
            bool flydigiIsBs1 = false;

            // If no BS2+ device is connected, check for BS1 (BLE)
            if (!flydigiConnected && _bs1Service.IsConnected)
            {
                flydigiConnected = true;
                flydigiIsBs1 = true;
                flydigiModelName = "BS1";
                try
                {
                    var bs1Settings = _bs1Service.GetSettings();
                    // BS1 doesn't have real-time RPM feedback, show target RPM from current mode
                    flydigiFanRpm = bs1Settings.ManualRpm;
                    flydigiRgbMode = "N/A"; // BS1 doesn't have RGB
                }
                catch { /* non-critical */ }
            }

            return new DeviceMetricsSnapshot
            {
                HydroUiConnected = hydroConnected,
                HydroUiPumpVoltage = hydroPumpVoltage,
                HydroUiFanSpeed = hydroFanSpeed,
                HydroUiFanRpm = hydroFanRpm,
                FlydigiConnected = flydigiConnected,
                FlydigiFanRpm = flydigiFanRpm,
                FlydigiIsBs1 = flydigiIsBs1,
                FlydigiRgbMode = flydigiRgbMode,
                FlydigiModelName = flydigiModelName
            };
        }

        [RelayCommand]
        private void Navigate(string? destination)
        {
            if (string.IsNullOrWhiteSpace(destination))
            {
                Debug.WriteLine(
                    "Dashboard navigation was requested without a destination.");

                return;
            }

            switch (destination)
            {
                case "adaptive":
                    NavigateWithinApplication(destination);
                    break;

                case "help":
                    OpenUrl(WikiUrl);
                    break;

                default:
                    Debug.WriteLine(
                        $"Unknown dashboard destination: {destination}");

                    break;
            }
        }

        public Task OnNavigatedToAsync()
        {
            Debug.WriteLine(
                $"INFO | {nameof(DashboardViewModel)} navigated to.");

            // Initialize EC and acquire hardware monitoring lease on a background
            // thread to avoid blocking the UI while probes run.
            _ = System.Threading.Tasks.Task.Run(() =>
            {
                if (_uniwillEc is not null)
                {
                    try
                    {
                        _uniwillEc.Initialize();
                    }
                    catch { /* EC not available on this hardware */ }

                    // Auto-apply the saved fan curve on startup. The app always launches
                    // on Dashboard, so this is the right place to apply the last saved curve.
                    try
                    {
                        var settings = FanControlSettingsService.Load();
                        Models.EcFanCurve cpuCurve;
                        Models.EcFanCurve gpuCurve;

                        if (settings.UnifiedMode)
                        {
                            var unifiedCurve = settings.UnifiedPreset == "Custom" && settings.UnifiedDuties?.Length == 11
                                ? BuildCustomCurve(settings.UnifiedDuties)
                                : GetPresetCurve(settings.UnifiedPreset);
                            cpuCurve = unifiedCurve;
                            gpuCurve = unifiedCurve;
                        }
                        else
                        {
                            cpuCurve = settings.CpuPreset == "Custom" && settings.CpuDuties?.Length == 11
                                ? BuildCustomCurve(settings.CpuDuties)
                                : GetPresetCurve(settings.CpuPreset);
                            gpuCurve = settings.GpuPreset == "Custom" && settings.GpuDuties?.Length == 11
                                ? BuildCustomCurve(settings.GpuDuties)
                                : GetPresetCurve(settings.GpuPreset);
                        }

                        var cpuTemps = settings.UnifiedMode
                            ? (settings.UnifiedTempThresholds ?? Models.EcFanCurve.CpuTemperatures)
                            : (settings.CpuTempThresholds ?? Models.EcFanCurve.CpuTemperatures);
                        var gpuTemps = settings.UnifiedMode
                            ? cpuTemps
                            : (settings.GpuTempThresholds ?? Models.EcFanCurve.GpuTemperatures);

                        _uniwillEc.ApplyFanCurve(cpuCurve, gpuCurve, cpuTemps, gpuTemps);
                        Debug.WriteLine("INFO | DashboardViewModel: Fan curve auto-applied on startup");
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"WARN | DashboardViewModel: Fan curve auto-apply failed: {ex.Message}");
                    }
                }

                _lease = _hardwareMonitoring.Acquire(
                    HardwareMonitoringCategory.Cpu |
                    HardwareMonitoringCategory.Gpu |
                    HardwareMonitoringCategory.Memory |
                    HardwareMonitoringCategory.Battery);

                // Fetch static battery data (health, charge limit) once on navigation.
                // Temperature is refreshed on the timer below.
                if (_uniwillEc is not null)
                {
                    try
                    {
                        var telemetry = _uniwillEc.ReadBatteryTelemetry();
                        if (telemetry is not null)
                        {
                            _batteryHealthPercent = telemetry.Value.HealthPercent;
                            _batteryTemperatureC = telemetry.Value.TemperatureC;
                        }
                    }
                    catch { /* EC read failed */ }

                    try
                    {
                        _batteryChargeLimit = _uniwillEc.GetBatteryMode();
                    }
                    catch { /* EC read failed */ }
                }

                // Start the timers on the UI thread
                Application.Current.Dispatcher.Invoke(() =>
                {
                    _refreshTimer.IsEnabled = true;
                    _batteryEcTimer.IsEnabled = true;
                    OnRefreshTimerTick(null, EventArgs.Empty);
                });
            });

            return Task.CompletedTask;
        }

        public Task OnNavigatedFromAsync()
        {
            Debug.WriteLine(
                $"INFO | {nameof(DashboardViewModel)} navigated from.");

            _refreshTimer.IsEnabled = false;
            _batteryEcTimer.IsEnabled = false;

            // Dispose lease on a background thread to avoid _computer.Close()
            // blocking the UI during navigation.
            var lease = _lease;
            _lease = null;

            if (lease is not null)
            {
                _ = System.Threading.Tasks.Task.Run(() => lease.Dispose());
            }

            return Task.CompletedTask;
        }

        private void NavigateWithinApplication(
            string targetPageTag)
        {
            bool succeeded =
                _navigationService.Navigate(targetPageTag);

            if (!succeeded)
            {
                Debug.WriteLine(
                    $"Dashboard navigation failed for tag " +
                    $"'{targetPageTag}'. Ensure the matching " +
                    $"NavigationViewItem has that TargetPageTag.");
            }
        }

        private static void OpenUrl(string url)
        {
            try
            {
                Process.Start(
                    new ProcessStartInfo
                    {
                        FileName = url,
                        UseShellExecute = true
                    });
            }
            catch (Exception exception)
            {
                Debug.WriteLine(
                    $"Could not open '{url}': {exception}");
            }
        }

        #region Fan Curve Startup Helpers

        private static Models.EcFanCurve GetPresetCurve(string? name) => name switch
        {
            "Silent" => Models.EcFanCurve.CreateSilent(),
            "Balanced" => Models.EcFanCurve.CreateBalanced(),
            "Performance" => Models.EcFanCurve.CreatePerformance(),
            "Full Speed" => Models.EcFanCurve.CreateFullSpeed(),
            "Off" => Models.EcFanCurve.CreateOff(),
            _ => Models.EcFanCurve.CreateBalanced()
        };

        private static Models.EcFanCurve BuildCustomCurve(int[] duties)
        {
            var curve = new Models.EcFanCurve { Name = "Custom" };
            curve.Duties.Clear();
            foreach (var d in duties)
                curve.Duties.Add(Math.Clamp(d, 0, 100));
            return curve;
        }

        #endregion
    }
}
