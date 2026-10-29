using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Uniwill_Control_Utility.Models;
using Uniwill_Control_Utility.Services;
using Wpf.Ui;
using Wpf.Ui.Abstractions.Controls;
using UCU.Contracts.Capabilities;

namespace Uniwill_Control_Utility.ViewModels
{
    public partial class DashboardViewModel :
        ObservableObject,
        INavigationAware
    {
        private const string WikiUrl = "https://github.com/jgudec/Uniwill-Control-Utility/wiki";

        private readonly INavigationService _navigationService;
        private readonly IHardwareMonitoringService _hardwareMonitoring;
        private readonly EcTelemetryService _ecTelemetry;
        private readonly IBatteryCapability? _battery;
        private readonly IEngineCapability? _engine;
        private readonly HydroUIService _hydroUIService;
        private readonly FlydigiCoolerHidService _flydigiService;
        private readonly FlydigiCoolerBleService _bs1Service;
        private readonly Services.DashboardFirstFrameSignal _firstFrameSignal;
        private int _coreFrameSignalled;
        private int _ecChainDone;
        private readonly DispatcherTimer _refreshTimer;
        private readonly DispatcherTimer _batteryEcTimer;
        private IDisposable? _lease;
        private int _batteryHealthPercent;
        private int _batteryTemperatureC;
        private int _batteryChargeLimit;
        private int _batteryRefreshInProgress;

        public DashboardViewModel(
            INavigationService navigationService,
            IHardwareMonitoringService hardwareMonitoring,
            EcTelemetryService ecTelemetry,
            IBatteryCapability? battery,
            IEngineCapability? engine,
            HydroUIService hydroUIService,
            FlydigiCoolerHidService flydigiService,
            FlydigiCoolerBleService bs1Service,
            Services.DashboardFirstFrameSignal firstFrameSignal)
        {
            _navigationService = navigationService
                ?? throw new ArgumentNullException(nameof(navigationService));
            _hardwareMonitoring = hardwareMonitoring
                ?? throw new ArgumentNullException(nameof(hardwareMonitoring));
            _ecTelemetry = ecTelemetry
                ?? throw new ArgumentNullException(nameof(ecTelemetry));
            _battery = battery;
            _engine = engine;
            _hydroUIService = hydroUIService;
            _flydigiService = flydigiService;
            _bs1Service = bs1Service;
            _firstFrameSignal = firstFrameSignal;

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
                if (_battery is not null)
                {
                    EcFanDutySnapshot ecSnapshot = _ecTelemetry.ReadFanDuty();
                    snapshot = snapshot with
                    {
                        CpuFanSpeed = ecSnapshot.CpuFanPercent,
                        GpuFanSpeed = ecSnapshot.GpuFanPercent
                    };
                }

                // Enrich with the engine's kernel-side readings (see ApplyEngineMetrics).
                EngineMetricsSnapshot engineMetrics = _ecTelemetry.ReadEngineMetrics();
                snapshot = ApplyEngineMetrics(snapshot, engineMetrics);
                Scripts.Misc.CachedSystemInfo.PublishRamSpeedIfMissing(engineMetrics.MemorySpeedMts);

                // Enrich with cached battery EC data (refreshed every 10s)
                snapshot = snapshot with
                {
                    BatteryHealthPercent = _batteryHealthPercent,
                    BatteryTemperatureC = _batteryTemperatureC,
                    BatteryChargeLimit = _batteryChargeLimit
                };

                MetricsUpdated?.Invoke(this, snapshot);

                // The startup splash's final stage is gated on this frame
                // (Services.DashboardFirstFrameSignal): the window exists
                // behind the splash, and the splash releases when the
                // dashboard has real numbers, not when the window was
                // created. The condition is the OS-path data (memory is
                // always readable; CpuUsage too) - NOT the LHM temperatures:
                // on a guarded machine the probe parks, temperatures stay 0
                // for as long as it is wedged, and a temps-gated splash
                // expels at its budget with the window still empty
                // (field-proven 2026-10-29: the empty-window report
                // persisted under a temps-gated gate). The temperatures
                // stream in on later ticks when the probe opens.
                if ((snapshot.SystemMemoryTotalGb > 0 || snapshot.CpuTemperature > 0) &&
                    Volatile.Read(ref _ecChainDone) == 1)
                {
                    _firstFrameSignal.SignalFirstFrame();
                }
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

        /// <summary>
        /// Overlay of the engine's kernel-side readings onto the app's snapshot,
        /// filling only fields the app's own probe left at 0 (zero is the
        /// snapshot's "unavailable" convention, and a live in-process reading
        /// always beats the engine's cache - the dev app runs elevated, opens the
        /// sensor drivers itself, and its numbers are the fresher ones). GPU
        /// power/clock are deliberately absent: the GPU stays app-side (NVAPI
        /// decision), no engine reading exists to overlay.
        /// </summary>
        internal static HardwareMetricsSnapshot ApplyEngineMetrics(
            HardwareMetricsSnapshot snapshot, EngineMetricsSnapshot engine)
        {
            return snapshot with
            {
                CpuPowerWatts = FillMissing(snapshot.CpuPowerWatts, engine.CpuPowerWatts),
                CpuClockMhz = FillMissing(snapshot.CpuClockMhz, engine.CpuClockMhz),
                MemoryTemperature0 = FillMissing(snapshot.MemoryTemperature0, engine.MemoryTemperature0),
                MemoryTemperature1 = FillMissing(snapshot.MemoryTemperature1, engine.MemoryTemperature1),
            };
        }

        private static int FillMissing(int own, int engine) => own > 0 ? own : engine;

        private async void OnBatteryEcTimerTick(object? sender, EventArgs e)
        {
            // Skip polling when the app is minimized.
            if (Application.Current.MainWindow?.WindowState == WindowState.Minimized)
                return;

            if (_battery is null)
                return;

            if (Interlocked.Exchange(ref _batteryRefreshInProgress, 1) != 0)
                return;

            // Only refresh temperature on the timer; health and charge limit
            // are fetched once on navigation and cached.
            try
            {
                int temp = await _battery.GetTemperatureAsync().ConfigureAwait(false);
                _batteryTemperatureC = temp;
            }
            catch { /* EC read failed, keep cached value */ }
            finally
            {
                Volatile.Write(ref _batteryRefreshInProgress, 0);
            }
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
            bool hydroConnected = _hydroUIService.IsConnected;
            bool hydroSmartModeActive = _hydroUIService.IsSmartModeActive;
            int hydroPumpVoltage = 0;
            int hydroFanSpeed = 0;
            int hydroFanRpm = 0;

            if (hydroConnected)
            {
                try
                {
                    if (hydroSmartModeActive)
                    {
                        hydroPumpVoltage = _hydroUIService.SmartModeCurrentPump switch
                        {
                            PumpVoltage.V7 => 7,
                            PumpVoltage.V8 => 8,
                            PumpVoltage.V11 => 11,
                            _ => 0
                        };
                        var smartFan = _hydroUIService.SmartModeCurrentFan;
                        hydroFanSpeed = smartFan == Models.FanSpeed.Off ? 0 : (int)smartFan;
                    }
                    else
                    {
                        var wcSettings = _hydroUIService.GetSettings();
                        hydroPumpVoltage = wcSettings.GetPumpVoltage() switch
                        {
                            PumpVoltage.V7 => 7,
                            PumpVoltage.V8 => 8,
                            PumpVoltage.V11 => 11,
                            _ => 0
                        };
                        var fanSpeed = wcSettings.GetFanSpeed();
                        hydroFanSpeed = fanSpeed == Models.FanSpeed.Off ? 0 : (int)fanSpeed;
                    }
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
                HydroUiSmartModeActive = hydroSmartModeActive,
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

            // Phase 1 (fast): acquire the LHM lease on a background thread so the
            // UI never freezes, then start the metrics timer.
            _ = System.Threading.Tasks.Task.Run(() =>
            {
                _lease = _hardwareMonitoring.Acquire(
                    HardwareMonitoringCategory.Cpu |
                    HardwareMonitoringCategory.Gpu |
                    HardwareMonitoringCategory.Memory |
                    HardwareMonitoringCategory.Battery);

                // Enable the timers on the UI thread but don't call OnRefreshTimerTick
                // inline — let the first timer tick fire naturally after ~1s. Calling
                // UpdateHardware() on the UI thread during startup blocks WPF's layout
                // pass and delays the dashboard page from rendering.
                Application.Current.Dispatcher.Invoke(() =>
                {
                    _refreshTimer.IsEnabled = true;
                    _batteryEcTimer.IsEnabled = true;
                });

                // Start Hydro UI Smart Mode now that temperature data is available.
                // This is done here (not in TryAutoConnectAsync) because during
                // pre-loading, LibreHardwareMonitor may not have valid temperature
                // readings yet — causing Smart Mode to initialize with a filtered
                // temperature of 0 and take 8+ seconds to ramp up on a hot CPU.
                // By the time the dashboard navigates, LHM has valid sensor data.
                try
                {
                    var snapshot = _hardwareMonitoring.ReadSnapshot();
                    bool hasTemp = snapshot.CpuTemperature > 0 || snapshot.GpuTemperature > 0;

                    if (hasTemp && _hydroUIService.IsConnected)
                    {
                        var settings = _hydroUIService.GetSettings();
                        if (settings.SmartModeEnabled && !_hydroUIService.IsSmartModeActive)
                        {
                            _hydroUIService.StartSmartMode();
                        }
                    }
                }
                catch { /* non-critical — Smart Mode will retry on next navigation */ }
            });

            // Phase 2 (slow): EC handshake and battery telemetry read run in parallel
            // on a separate background thread. The EC data (fan PWM, battery telemetry)
            // enriches snapshots on subsequent ticks but is not required for core metrics.
            // Fan curve and battery charge limit apply moved to MainWindow.mainWindow_Loaded
            // so they only fire once at app launch, not on every Dashboard navigation.
            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    if (_battery is null || _engine is null)
                        return;

                    // Availability check opens the engine (the RPC gate initialises
                    // lazily, so this is the call that wakes it).
                    await _engine.IsHardwareAvailableAsync().ConfigureAwait(false);

                    // Fetch static battery data (health, charge limit) once on navigation.
                    // Temperature is refreshed on the timer below.
                    try
                    {
                        var telemetry = await _battery.GetTelemetryAsync().ConfigureAwait(false);
                        if (telemetry is not null)
                        {
                            _batteryHealthPercent = telemetry.HealthPercent;
                            _batteryTemperatureC = telemetry.TemperatureC;
                        }
                    }
                    catch { /* EC read failed */ }

                    // Cache the saved battery charge limit for display on the dashboard.
                    // (The actual EC write happens in MainWindow.mainWindow_Loaded.)
                    try
                    {
                        _batteryChargeLimit = Properties.Settings.Default.BatteryMode;
                    }
                    catch { /* settings read failed */ }
                }
                finally
                {
                    // The EC chain is over (data landed or every read failed):
                    // with the first real snapshot frame, this is the dashboard's
                    // first data frame - the splash's release condition. Machines
                    // without an EC (no battery/engine) complete here instantly.
                    Volatile.Write(ref _ecChainDone, 1);
                }
            });

            return Task.CompletedTask;
        }

        public Task OnNavigatedFromAsync()
        {
            Debug.WriteLine(
                $"INFO | {nameof(DashboardViewModel)} navigated from.");

            // Stop the polling timers so we don't read hardware while invisible.
            _refreshTimer.IsEnabled = false;
            _batteryEcTimer.IsEnabled = false;

            // Keep the hardware monitoring lease alive. Disposing it closes the
            // LibreHardwareMonitor Computer instance, which forces a full hardware
            // re-probe (enumeration + sensor tree walk) on the next navigation back.
            // Keeping the lease active means the sensor data is cached and the next
            // OnNavigatedToAsync can resume polling immediately with no re-probe cost.
            // The lease is only disposed when the app exits (via DI container teardown).

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
