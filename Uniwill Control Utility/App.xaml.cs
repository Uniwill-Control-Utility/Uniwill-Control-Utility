using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using RyzenSmu;
using Serilog;
using Serilog.Sinks.SystemConsole.Themes;
using System;
using System.Configuration;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Uniwill_Control_Utility.Models;
using Uniwill_Control_Utility.Properties;
using Uniwill_Control_Utility.Scripts;
using Uniwill_Control_Utility.Scripts.Intel_Backend;
using Uniwill_Control_Utility.Scripts.Misc;
using Uniwill_Control_Utility.Services;
using Uniwill_Control_Utility.Services.Performance;
using Uniwill_Control_Utility.Views.Pages;
using Uniwill_Control_Utility.Views.Windows;
using Wpf.Ui;
using Wpf.Ui.Abstractions;

namespace Uniwill_Control_Utility
{
    public partial class App
    {
        // The.NET Generic Host provides dependency injection, configuration, logging, and other services.
        // https://docs.microsoft.com/dotnet/core/extensions/generic-host
        // https://docs.microsoft.com/dotnet/core/extensions/dependency-injection
        // https://docs.microsoft.com/dotnet/core/extensions/configuration
        // https://docs.microsoft.com/dotnet/core/extensions/logging
        private static IHost? _host;

        private static ILogger<App>? _logger;

        /// <summary>
        /// Gets registered service.
        /// </summary>
        /// <typeparam name="T">Type of the service to get.</typeparam>
        /// <returns>Instance of the service or <see langword="null"/>.</returns>
        public static T? GetService<T>()
            where T : class
        {
            return _host?.Services.GetService(typeof(T)) as T;
        }

        internal static readonly string version = "0.9.4.2";
        private Mutex? mutex;
        private const string MutexName = "UniwillControlUtility";

        internal static readonly string SCALE_MODELS_JSON_PATH = ".\\ScaleModels.json";
        internal static readonly string LOGS_FOLDER = ".\\logs\\";

        internal static string product = "";
        public static Task DisplaySetupTask { get; private set; } = Task.CompletedTask;
        private static readonly object _displaySetupTaskLock = new();

        /// <summary>
        /// True when the laptop's display panel is MiniLED.
        /// Set once at startup so pages can decide which controls to show.
        /// </summary>
        internal static bool HasMiniLEDPanel { get; private set; }

        /// <summary>
        /// Fired when hardware detection (WMI queries) completes.
        /// Subscribers can use this to populate UI that depends on Family.* statics.
        /// Detection now runs synchronously before the UI is built, so this event
        /// fires during page construction rather than as a late callback.
        /// </summary>
        internal static event Action? HardwareDetectionCompleted;

        /// <summary>
        /// Occurs when the application is loading.
        /// </summary>
        private async void OnStartup(object sender, StartupEventArgs e)
        {
            Environment.CurrentDirectory = AppDomain.CurrentDomain.BaseDirectory;

            LocalizationService.Initialize(Settings.Default.Language);

            // Ensure logs directory exists before configuring the logger
            if (!Directory.Exists(LOGS_FOLDER))
                Directory.CreateDirectory(LOGS_FOLDER);

            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.ControlledBy(Scripts.Misc.DiagnosticLogger.LevelSwitch)
                .WriteTo.Console(theme: AnsiConsoleTheme.Code, applyThemeToRedirectedOutput: true)
                .WriteTo.File(LOGS_FOLDER + "ucu_log.txt",
                    fileSizeLimitBytes: 8 * 1024 * 1024, // 8MB
                    rollingInterval: RollingInterval.Day
                )
                .CreateLogger();

            Log.Information("Logger initialized");

            Scripts.Misc.DiagnosticLogger.ApplySettingsLevel();

            // Catch unhandled exceptions from all threads (not just the UI thread)
            AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;

            Log.Information("About to check VC Redist");

            if (!IsVcRedistInstalled("x64") && !IsVcRedistInstalled("x86"))
            {
                var downloadVcRedist = MessageBox.Show(LocalizationService.Get("Microsoft Visual C++ Redistributable is not installed.\nWould you like to download it now?"),
                    LocalizationService.Get("Missing VC Redistributable"),
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);
                if (downloadVcRedist == MessageBoxResult.Yes)
                {
                    Process.Start(
                        new ProcessStartInfo("https://learn.microsoft.com/en-us/cpp/windows/latest-supported-vc-redist")
                            { UseShellExecute = true });
                }

                Shutdown();
                return;
            }

            try
            {
                try
                {
                    product = GetSystemInfo.Product;
                    var displayTask = Task.Run(() =>
                    {
                        try
                        {
                            Uniwill_Control_Utility.Scripts.Misc.Display.SetUpLists();
                        }
                        catch (Exception ex)
                        {
                            Log.Logger.Error(ex, "Failed to setup display refresh rates");
                        }
                    });
                    lock (_displaySetupTaskLock)
                    {
                        DisplaySetupTask = displayTask;
                    }
                }
                catch (Exception ex)
                {
                    Log.Logger.Error(ex, "Failed to setup product and display refresh rates");
                }

           _host = Host
                    .CreateDefaultBuilder()
                    .ConfigureAppConfiguration(c =>
                    {
                        c.SetBasePath(Path.GetDirectoryName(Assembly.GetEntryAssembly()!.Location)!);
                    })
                    .ConfigureLogging(logging =>
                    {
                        logging.ClearProviders();
                        logging.AddSerilog();
                    })
                    .ConfigureServices((context, services) =>
                    {
                        // App Host
                        services.AddHostedService<ApplicationHostService>();

                        // Page resolver service
                        services.AddSingleton<INavigationViewPageProvider, PageService>();

                        // Theme manipulation
                        services.AddSingleton<IThemeService, ThemeService>();

                        // TaskBar manipulation
                        services.AddSingleton<ITaskBarService, TaskBarService>();

                        // Service containing navigation, same as INavigationWindow... but without window
                        services.AddSingleton<INavigationService, NavigationService>();
                        services.AddSingleton<PresetsNavigationRequest>();

                        services.AddSingleton<IHardwareMonitoringService, HardwareMonitoringService>();
                        services.AddSingleton<GpuInventoryService>();
                        services.AddSingleton<IFpsMonitoringService, FpsMonitoringService>();
                        services.AddSingleton<OverlaySettingsStore>();
                        services.AddSingleton<OverlayManagerService>();
                        services.AddHostedService(provider => provider.GetRequiredService<OverlayManagerService>());

                        // Main window with navigation
                        services.AddScoped<INavigationWindow, Views.Windows.MainWindow>();
                        services.AddScoped<ViewModels.MainWindowViewModel>();

                        // Views and ViewModels
                        services.AddScoped<Views.Pages.DashboardPage>();
                        services.AddScoped<ViewModels.DashboardViewModel>();
                        services.AddScoped<Views.Pages.Presets>();
                        services.AddScoped<Views.Pages.Adaptive>();
                        services.AddScoped<Views.Pages.Automations>();
                        services.AddScoped<Views.Pages.FanControl>();
                        services.AddScoped<Views.Pages.Keyboard>();
                        services.AddScoped<Views.Pages.LightBar>();
                        services.AddScoped<Views.Pages.Battery>();
                        services.AddScoped<Views.Pages.SettingsPage>();
                        services.AddScoped<ViewModels.SettingsViewModel>();
                        services.AddScoped<Views.Pages.OverlaySettingsPage>();
                        services.AddScoped<Views.Pages.AboutPage>();

                        // Watercooler service (singleton for auto-connect)
                        services.AddScoped<Views.Pages.Watercooler>();
                        services.AddSingleton<WaterCoolerService>();

                        // Flydigi BS2 Pro cooling pad page and service
                        // Singleton so the host disposes it on shutdown (stops background timers).
                        services.AddSingleton<Views.Pages.FlydigiCooler>();
                        services.AddSingleton<FlydigiCoolerHidService>();

                        // Flydigi BS1 (BLE-only) cooling pad service
                        services.AddSingleton<FlydigiCoolerBleService>();

                        // Uniwill EC access (for XMG / TUXEDO laptops)
                        services.AddSingleton<UniwillECService>(provider =>
                        {
                            string? dllPath = null;
                            try
                            {
                                // Search order: bundled DLL first (shipped with UCU),
                                // then fallback to OEM Control Center installation paths.
                                string appDir = AppDomain.CurrentDomain.BaseDirectory;
                                string[] searchPaths = new[]
                                {
                                    Path.Combine(appDir, "ACPIDriverDll.dll"),
                                    @"C:\Program Files\OEM\Control Center\AiStoneService\MyControlCenter\ACPIDriverDll.dll",
                                    @"C:\Program Files (x86)\OEM\Control Center\AiStoneService\MyControlCenter\ACPIDriverDll.dll",
                                };
                                foreach (string path in searchPaths)
                                {
                                    if (File.Exists(path))
                                    {
                                        dllPath = path;
                                        break;
                                    }
                                }
                            }
                            catch
                            {
                                // Ignore path discovery errors
                            }
                            return new UniwillECService(dllPath);
                        });
                        services.AddSingleton<EcTelemetryService>();

                        // Centralized device applier (singleton)
                        services.AddSingleton<DeviceApplier>();

                        // Configuration
                        services.Configure<AppConfig>(context.Configuration.GetSection(nameof(AppConfig)));
                    }).Build();

                _logger = _host.Services.GetRequiredService<ILogger<App>>();

                _ = Tablet.TabletDevices;
                bool firstBoot = false;
                try
                {
                    if (Settings.Default.SettingsUpgradeRequired)
                    {
                        try
                        {
                            Settings.Default.Upgrade();
                            Settings.Default.SettingsUpgradeRequired = false;
                            Settings.Default.Save();
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Failed to update settings on startup");
                        }
                    }

                    firstBoot = Settings.Default.FirstBoot;
                }
                catch (ConfigurationErrorsException ex)
                {
                    if (ex.InnerException is ConfigurationErrorsException innerEx)
                    {
                        File.Delete(innerEx.Filename!);
                    }
                    Settings.Default.Reload();
                }

                bool createdNew;
                mutex = new Mutex(true, MutexName, out createdNew);

                if (!createdNew)
                {
                    _logger.LogWarning("Another instance is already running. Signalling it to show.");

                    try
                    {
                        using var showEvent = EventWaitHandle.OpenExisting($"{MutexName}.Show");
                        showEvent.Set();
                    }
                    catch (WaitHandleCannotBeOpenedException)
                    {
                        _logger.LogWarning("Existing instance found, but show event not available.");
                    }

                    Shutdown();
                    return;
                } else StartSingleInstanceListener();

                if (File.Exists("C:\\Uniwill.Control.Utility.V2.msi"))
                    File.Delete("C:\\Uniwill.Control.Utility.V2.msi");

                if (File.Exists("C:\\Uniwill.Control.Utility.msi"))
                    File.Delete("C:\\Uniwill.Control.Utility.msi");

                // Run hardware detection and LHM init before building the UI.
                // Both run in parallel on background threads so the total wait
                // is max(WMI, LHM) — the window only shows when all data is ready.
                await DetectHardwareAsync(_host.Services.GetRequiredService<IHardwareMonitoringService>());

                string? entryLocation = System.Reflection.Assembly.GetEntryAssembly()?.Location;
                string path = System.IO.Path.GetDirectoryName(entryLocation)!;

                if (firstBoot || Settings.Default.Path != path)
                {
                    Settings.Default.Path = path;
                    Settings.Default.FirstBoot = false;
                    Settings.Default.Save();

                    try
                    {
                        _ = Task.Run(() => UnblockFilesInDirectory(path));
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to unblock files in {dir} directory", path);
                    }
                }

                await _host.StartAsync(CancellationToken.None);

                // Kick off device auto-connects in parallel so they don't block
                // the dashboard from appearing.  The dashboard's device cards
                // already handle late-arriving connections (they toggle visibility).
                SafeFireAndForget(AutoConnectDevicesAsync());

                // Restore Adaptive Mode override state from previous session.
                SafeFireAndForget(RestoreAdaptiveModeAsync());

                SafeFireAndForget(RunPostStartupTasksAsync());
            }
            catch (Exception ex)
            {
                Log.Logger.Fatal(ex, "Failed to build and start a host");
                MessageBox.Show(ex.Message);
            }
        }

        private static async Task DetectHardwareAsync(IHardwareMonitoringService monitoring)
        {
            try
            {
                // Run WMI detection and LHM init in parallel so the total wait
                // is max(WMI, LHM) rather than their sum.
                var wmiTask = Task.Run(() =>
                {
                    Family.setCpuFamily();
                    LaptopDetector.Initialize();
                    HasMiniLEDPanel = PanelDatabase.IsMiniLED();
                    SkuResolver.Initialize();
                    PrefetchSystemInfo();
                });

                var lhmTask = Task.Run(() =>
                {
                    try
                    {
                        // Use AcquirePersistent to keep the Computer instance alive
                        // through dashboard navigation. This avoids a costly re-probe
                        // when DashboardViewModel acquires its own lease later.
                        monitoring.AcquirePersistent(
                            HardwareMonitoringCategory.Cpu |
                            HardwareMonitoringCategory.Gpu |
                            HardwareMonitoringCategory.Memory |
                            HardwareMonitoringCategory.Battery);
                    }
                    catch
                    {
                        // LHM initialization failed — the dashboard will retry
                        // when it acquires its own lease in OnNavigatedToAsync.
                    }
                });

                await Task.WhenAll(wmiTask, lhmTask);

                // Notify subscribers that hardware detection is done.
                HardwareDetectionCompleted?.Invoke();
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to detect hardware");
            }
        }

        /// <summary>
        /// Runs all WMI queries needed by the Settings System Info section on a background thread.
        /// Must be called from a background thread (not the UI dispatcher).
        /// </summary>
        private static void PrefetchSystemInfo()
        {
            try
            {
                // Device info
                CachedSystemInfo.DeviceName = GetSystemInfo.SystemName;
                CachedSystemInfo.DeviceModel = GetSystemInfo.Product;
                CachedSystemInfo.DeviceProducer = GetSystemInfo.Manufacturer;

                // Baseboard product (for System Identity card)
                try
                {
                    using (var bbSearcher = new ManagementObjectSearcher(@"root\CIMV2", "SELECT Product FROM Win32_BaseBoard"))
                    {
                        foreach (ManagementBaseObject obj in bbSearcher.Get())
                        {
                            CachedSystemInfo.BaseboardProduct = obj["Product"]?.ToString()?.Trim() ?? string.Empty;
                        }
                    }
                }
                catch (System.Runtime.InteropServices.COMException) { }

                // CPU info
                using (var searcher = new ManagementObjectSearcher(@"root\CIMV2", "SELECT * FROM Win32_Processor"))
                {
                    foreach (ManagementBaseObject obj in searcher.Get())
                    {
                        CachedSystemInfo.CpuName = (obj["Name"] as string ?? string.Empty).Trim();
                        CachedSystemInfo.CpuDescription = (obj["Description"] as string ?? string.Empty).Trim();
                        CachedSystemInfo.CpuManufacturer = (obj["Manufacturer"] as string ?? string.Empty).Trim();
                        CachedSystemInfo.CpuCores = Convert.ToInt32(obj["NumberOfCores"]);
                        CachedSystemInfo.CpuLogicalProcessors = Convert.ToInt32(obj["NumberOfLogicalProcessors"]);
                        CachedSystemInfo.CpuL2Size = Convert.ToDouble(obj["L2CacheSize"]) / 1024;
                        CachedSystemInfo.CpuL3Size = Convert.ToDouble(obj["L3CacheSize"]) / 1024;
                        CachedSystemInfo.CpuBaseClock = obj["MaxClockSpeed"] as string ?? string.Empty;
                    }
                }

                CachedSystemInfo.CpuCodeName = GetSystemInfo.Codename() ?? string.Empty;

                uint sum = 0u;
                foreach (uint n in GetSystemInfo.GetCacheSizes(GetSystemInfo.CacheLevel.Level1))
                    sum += n;
                CachedSystemInfo.CpuL1Cache = (decimal)(sum / 1024u);

                sum = 0u;
                foreach (uint n in GetSystemInfo.GetCacheSizes(GetSystemInfo.CacheLevel.Level2))
                    sum += n;
                CachedSystemInfo.CpuL2Cache = (decimal)(sum / 1024u);

                CachedSystemInfo.CpuInstructions = GetSystemInfo.InstructionSets();

                // RAM info
                using (var ramSearcher = new ManagementObjectSearcher(@"root\CIMV2", "SELECT * FROM Win32_PhysicalMemory"))
                {
                    foreach (ManagementBaseObject obj in ramSearcher.Get())
                    {
                        string manufacturer = obj["Manufacturer"] as string ?? string.Empty;
                        string partNumber = obj["PartNumber"] as string ?? string.Empty;

                        if (CachedSystemInfo.RamProducer == "") CachedSystemInfo.RamProducer = manufacturer;
                        else if (!CachedSystemInfo.RamProducer.Contains(manufacturer))
                            CachedSystemInfo.RamProducer = $"{CachedSystemInfo.RamProducer}/{manufacturer}";

                        if (CachedSystemInfo.RamModel == "") CachedSystemInfo.RamModel = partNumber;
                        else if (!CachedSystemInfo.RamModel.Contains(partNumber))
                            CachedSystemInfo.RamModel = $"{CachedSystemInfo.RamModel}/{partNumber}";

                        CachedSystemInfo.RamCapacity += Convert.ToDouble(obj["Capacity"]);
                        CachedSystemInfo.RamSpeed = Convert.ToInt32(obj["ConfiguredClockSpeed"]);
                        CachedSystemInfo.RamType = Convert.ToInt32(obj["SMBIOSMemoryType"]);
                        CachedSystemInfo.RamWidth += Convert.ToInt32(obj["DataWidth"]);
                        CachedSystemInfo.RamSlots++;
                    }
                }

                CachedSystemInfo.RamCapacity /= 1024 * 1024 * 1024;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to prefetch system info");
            }
        }

        private static async Task AutoConnectDevicesAsync()
        {
            // Wait for the Bluetooth stack to stabilize after cold boot.
            // On restart, BLE devices may not be reachable immediately — the radio
            // needs time to power on and start advertising. The watercooler's
            // TryAutoConnectAsync also runs a BLE scan and retries, so this
            // initial delay gives the system time before discovery begins.
            await Task.Delay(3000);

            // Auto-connect watercooler on startup if enabled
            try
            {
                var waterCoolerService = _host?.Services.GetService<WaterCoolerService>();
                if (waterCoolerService != null && WaterCoolerHardwareDetector.IsSupportedHardware())
                    await waterCoolerService.TryAutoConnectAsync();
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to auto-connect watercooler");
            }

            // Auto-connect Flydigi BS2 Pro cooling pad on startup if enabled
            try
            {
                if (Settings.Default.EnableFlydigi)
                {
                    var flydigiCoolerService = _host?.Services.GetService<FlydigiCoolerHidService>();
                    if (flydigiCoolerService != null && FlydigiCoolerHidService.IsDeviceAvailable())
                        await flydigiCoolerService.TryAutoConnectAsync();
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to auto-connect Flydigi cooler");
            }

            // Auto-connect Flydigi BS1 (BLE) cooling pad on startup if enabled
            try
            {
                if (Settings.Default.EnableFlydigi)
                {
                    var bs1Service = _host?.Services.GetService<FlydigiCoolerBleService>();
                    if (bs1Service != null)
                        await bs1Service.TryAutoConnectAsync();
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to auto-connect Flydigi BS1");
            }
        }

        private static async Task RestoreAdaptiveModeAsync()
        {
            // Restore Adaptive Mode override state from previous session.
            // Only restore if Adaptive Mode was actually running when the app closed.
            try
            {
                var deviceApplier = _host?.Services.GetService<DeviceApplier>();
                if (deviceApplier != null && Settings.Default.isAdaptiveModeRunning)
                {
                    if (Settings.Default.EnableFlydigi && Settings.Default.AdaptiveBs2ProEnabled)
                        deviceApplier.EnableFlydigiOverride();

                    if (Settings.Default.AdaptiveWcEnabled)
                        deviceApplier.EnableWatercoolerOverride();

                    if (Settings.Default.AdaptiveKeyboardEnabled)
                        deviceApplier.EnableKeyboardOverride();
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to restore Adaptive Mode override state");
            }

            // Small delay so the overrides apply after devices have had a
            // chance to finish connecting (avoids racing with TryAutoConnectAsync).
            await Task.Delay(500);
        }

        private static async Task RunPostStartupTasksAsync()
        {
            try
            {
                var isInternetAvailable = await Task.Run(IsInternetAvailable);

                if (isInternetAvailable && Settings.Default.UpdateCheck)
                {
                    SafeFireAndForget(CheckForUpdate());
                }

                if (isInternetAvailable && PawnIoDetectionService.ShouldOpenInstaller())
                    new InstallpawnIo().Show();
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed during deferred update and PawnIO checks");
            }

            try
            {
                Game_Manager.installedGames = await Task.Run(() => Game_Manager.syncGame_Library());
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to sync game library");
            }

            // Show the prerequisite checklist on first startup.
            if (Settings.Default.ShowPrerequisiteDialog)
                SafeFireAndForget(ShowPrerequisiteDialogAsync());
        }

        private static async Task ShowPrerequisiteDialogAsync()
        {
            // Brief delay so the main window has time to render first.
            await Task.Delay(500);

            Application.Current.Dispatcher.Invoke(() =>
            {
                try
                {
                    var dialog = new Views.Windows.PrerequisiteDialog();

                    // Scope the dialog to the main window so it stays above UCU
                    // without being globally topmost across all applications.
                    Window? owner = Application.Current.MainWindow;
                    if (owner is not null)
                        dialog.Owner = owner;

                    dialog.ShowDialog();

                    // Only show on first startup.
                    Settings.Default.ShowPrerequisiteDialog = false;
                    Settings.Default.Save();
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Failed to show prerequisite dialog");
                }
            });
        }

        private EventWaitHandle? _showEvent;
        private CancellationTokenSource? _showListenerCts;

        private void StartSingleInstanceListener()
        {
            _showEvent = new EventWaitHandle(
                initialState: false,
                mode: EventResetMode.AutoReset,
                name: $"{MutexName}.Show");

            _showListenerCts = new CancellationTokenSource();

            Task.Run(() =>
            {
                while (!_showListenerCts.IsCancellationRequested)
                {
                    _showEvent.WaitOne();
                    // Must switch to UI thread
                    Application.Current.Dispatcher.Invoke(BringMainWindowToFront);
                }
            });
        }

        private static void BringMainWindowToFront()
        {
            var window = Application.Current.MainWindow;
            if (window == null) return;

            if (!window.IsVisible)
                window.Show();

            if (window.WindowState == WindowState.Minimized)
                window.WindowState = WindowState.Normal;

            window.UpdateLayout();

            var screen = System.Windows.Forms.Screen.FromHandle(
                new WindowInteropHelper(window).Handle);

            var workingArea = screen.WorkingArea;

            double minWidth = window.MinWidth > 0 ? window.MinWidth : 980;
            double minHeight = window.MinHeight > 0 ? window.MinHeight : 420;

            window.Width = Math.Max(window.Width, minWidth);
            window.Height = Math.Max(window.Height, minHeight);

            window.Width = Math.Min(window.Width, workingArea.Width);
            window.Height = Math.Min(window.Height, workingArea.Height);

            window.Left = workingArea.Left +
                (workingArea.Width - window.Width) / 2;

            window.Top = workingArea.Top +
                (workingArea.Height - window.Height) / 2;

            window.Activate();

            window.Topmost = true;
            window.Topmost = false;

            var handle = new WindowInteropHelper(window).Handle;
            if (handle != IntPtr.Zero)
                SetForegroundWindow(handle);
        }

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        public static async Task CheckForUpdate()
        {
            var updateManager = new UpdateManager("jgudec", "Uniwill-Control-Utility", App.version, "C:\\");

            var isUpdateAvailable = await updateManager.IsUpdateAvailable();

            if (isUpdateAvailable)
            {
                if (updateManager.NewVersion.StartsWith("3."))
                    ToastNotification.ShowToastNotification("Phantom Control Centre Now Available!",
                        $"Head to the Phantom Control Centre GitHub releases page to easily download the latest build!");
                else
                    ToastNotification.ShowToastNotification("New Update Available!",
                        $"Head to the settings menu to easily download the new Uniwill Control Utility update!");
            }
        }

        public static bool IsInternetAvailable()
        {
            try
            {
                using (var ping = new Ping())
                {
                    var result = ping.Send("8.8.8.8", 2000);
                    return result.Status == IPStatus.Success;
                }
            }
            catch
            {
                return false;
            }
        }

        private static bool IsVcRedistInstalled(string arch)
        {
            var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(
                $@"SOFTWARE\WOW6432Node\Microsoft\VisualStudio\14.0\VC\Runtimes\{arch}");
            if (key == null) return false;

            return key.GetValue("Installed") is int i && i == 1;
        }

        /// <summary>
        /// Occurs when the application is closing.
        /// Cleanup runs on a background thread to avoid blocking the UI thread
        /// (which would deadlock if hosted services try to dispatch back to the UI).
        /// </summary>
        private void OnExit(object sender, ExitEventArgs e)
        {
            _showListenerCts?.Cancel();
            _showEvent?.Dispose();
            mutex?.Dispose();

            if (Family.TYPE != Family.ProcessorType.Intel)
                SMUCommands.RyzenAccess.Deinitialize();
            else
                IntelManagement.Deinitialize();

            // Disconnect Flydigi cooler services explicitly before the host disposes.
            // Without this, the BLE/HID connections remain held by the device after
            // the process exits, requiring the user to manually disconnect via Windows
            // Bluetooth settings. The host disposal below is fire-and-forget and may
            // not complete before the process terminates.
            //
            // Order matters: stop the page's timers first, then disconnect the services.
            // This prevents timer callbacks from firing Dispatcher.Invoke on a dying
            // dispatcher while the services are still tearing down.
            try
            {
                var page = App._host?.Services.GetService<Views.Pages.FlydigiCooler>();
                page?.Dispose();
            }
            catch { /* Non-critical: dispose during teardown is best-effort */ }

            try
            {
                var coolerService = App._host?.Services.GetService<FlydigiCoolerHidService>();
                coolerService?.DisconnectAsync();
            }
            catch { /* Non-critical: disconnect during teardown is best-effort */ }

            try
            {
                var bs1Service = App._host?.Services.GetService<FlydigiCoolerBleService>();

                // FlydigiCoolerBleService.Dispose performs synchronous cleanup (no async disconnect).
                // We call it here to ensure the BLE GATT session and device are released
                // before the process exits. Disposing an already-disconnected service is safe.
                bs1Service?.Dispose();
            }
            catch { /* Non-critical: dispose during teardown is best-effort */ }

            // Stop the host on a background thread to avoid deadlock with Dispatcher.Invoke
            // calls from hosted services' StopAsync. The host's cancellation token ensures
            // background work stops. We don't block-wait because that would deadlock if
            // a service tries to dispatch to the UI thread.
            _ = Task.Run(async () =>
            {
                try
                {
                    await App._host?.StopAsync(CancellationToken.None)!;
                }
                finally
                {
                    App._host?.Dispose();
                }
            });
        }

        /// <summary>
        /// Occurs when an exception is thrown by an application but not handled.
        /// </summary>
        private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            // For more info see https://docs.microsoft.com/en-us/dotnet/api/system.windows.application.dispatcherunhandledexception?view=windowsdesktop-6.0
            App._logger?.LogCritical(e.Exception, "Unhandled dispatcher exception");

            MessageBox.Show(
                $"An unexpected error occurred:\n\n{e.Exception.Message}\n\nDetails:\n{e.Exception}",
                "Application Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);

            e.Handled = true;
        }

        /// <summary>
        /// Catches unhandled exceptions from all threads (including background threads).
        /// </summary>
        private void OnAppDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            Log.Logger.Fatal(e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString()), "Unhandled exception on non-UI thread");

            if (e.IsTerminating)
            {
                // Try to show a message box, but this may not work if the UI thread is dead
                try
                {
                    Dispatcher.Invoke(() =>
                    {
                        MessageBox.Show(
                            $"A critical error occurred:\n\n{e.ExceptionObject}\n\nThe application will now close.",
                            "Critical Error",
                            MessageBoxButton.OK,
                            MessageBoxImage.Error);
                    });
                }
                catch
                {
                    // UI thread is dead, can't show dialog
                }
            }
        }

        private static void SafeFireAndForget(Task task)
        {
            if (task.IsCompleted)
                return;
            task.ContinueWith(t => t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        }

        static void UnblockFilesInDirectory(string directoryPath)
        {
            // Unblock files in the current directory
            Parallel.ForEach(Directory.GetFiles(directoryPath), filePath => { UnblockFile(filePath); });
        }

        static void UnblockFile(string filePath)
        {
            ProcessStartInfo processInfo = new ProcessStartInfo();
            processInfo.FileName = "powershell.exe";
            processInfo.Arguments = $"Unblock-File -Path \"{filePath}\"";
            processInfo.UseShellExecute = false;
            processInfo.CreateNoWindow = true;
            processInfo.RedirectStandardOutput = true;

            using (Process process = new Process())
            {
                try
                {
                    process.StartInfo = processInfo;
                    process.Start();
                    process.StandardOutput.ReadToEnd();
                    process.WaitForExit();
                }
                finally
                {
                    process.Dispose();
                }
            }
        }
    }
}
