using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Universal_x86_Tuning_Utility.Models;
using Universal_x86_Tuning_Utility.Services;
using Wpf.Ui;
using Wpf.Ui.Abstractions.Controls;

namespace Universal_x86_Tuning_Utility.ViewModels
{
    public partial class DashboardViewModel :
        ObservableObject,
        INavigationAware
    {
        private readonly INavigationService _navigationService;
        private readonly IHardwareMonitoringService _hardwareMonitoring;
        private readonly UniwillECService? _uniwillEc;
        private readonly WaterCoolerService _waterCoolerService;
        private readonly FlydigiCoolerService _flydigiService;
        private readonly Bs1Service _bs1Service;
        private readonly DispatcherTimer _refreshTimer;
        private IDisposable? _lease;

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
        }

        private void OnRefreshTimerTick(object? sender, EventArgs e)
        {
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

        public event EventHandler<HardwareMetricsSnapshot>? MetricsUpdated;
        public event EventHandler<DeviceMetricsSnapshot>? DeviceMetricsUpdated;

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
                case "premade":
                case "custom":
                case "adaptive":
                case "games":
                case "auto":
                case "info":
                    NavigateWithinApplication(destination);
                    break;

                case "help":
                    OpenUrl("https://github.com/JamesCJ60/Universal-x86-Tuning-Utility/wiki");
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
                }

                _lease = _hardwareMonitoring.Acquire(
                    HardwareMonitoringCategory.Cpu |
                    HardwareMonitoringCategory.Gpu |
                    HardwareMonitoringCategory.Memory |
                    HardwareMonitoringCategory.Battery);

                // Start the timer on the UI thread
                Application.Current.Dispatcher.Invoke(() =>
                {
                    _refreshTimer.IsEnabled = true;
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
    }
}
