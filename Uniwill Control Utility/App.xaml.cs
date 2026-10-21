using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using RyzenSmu;
using Serilog;
using Serilog.Sinks.SystemConsole.Themes;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Linq;
using System.Text;
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

        internal static readonly string version = "0.9.5.1";
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
            StartupProgressWindow? startupWindow = null;
            StartupProgressReporter? startupProgressReporter = null;

            try
            {
                // Set a stable AppUserModelID so the taskbar button stays pinned
                // to this app even after GPU driver resets (PnP hotswap).
                // Without this, Windows loses the taskbar association and shows
                // a generic icon after dGPU disable/enable.
                SetAppUserModelID(AppUserModelId);

                // Register code page provider so Encoding.GetEncoding(1252) works
                // (required for RTSS OSD degree symbol rendering).
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

                Environment.CurrentDirectory = AppDomain.CurrentDomain.BaseDirectory;

                PrepareUserSettings();
                LocalizationService.Initialize(Settings.Default.Language);

                // Apply the correct WPFUI theme before showing the startup progress window
                // so it renders with the right brushes instead of flashing Dark first.
                ApplySystemTheme();

                bool showStartupProgressOverlay = Settings.Default.ShowStartupProgressOverlay;
                if (showStartupProgressOverlay)
                {
                    startupWindow = new StartupProgressWindow();
                    startupWindow.Show();
                }

                startupProgressReporter = StartupProgressReporter.Create(showStartupProgressOverlay,
                    stage => ReportStartupProgressAsync(startupWindow!, stage));

                await startupProgressReporter.ReportAsync(StartupStage.InitializingApplication);

                await startupProgressReporter.ReportAsync(StartupStage.PreparingDiagnostics);

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

                await startupProgressReporter.ReportAsync(StartupStage.CheckingPrerequisites);
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

                await startupProgressReporter.ReportAsync(StartupStage.ReadingSystemProfile);

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

                await startupProgressReporter.ReportAsync(StartupStage.ConfiguringServices);

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
                        services.AddSingleton(startupProgressReporter);

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
                        services.AddSingleton<OverlayManagerService>(provider =>
                            new OverlayManagerService(
                                provider.GetRequiredService<OverlaySettingsStore>(),
                                provider.GetRequiredService<IHardwareMonitoringService>(),
                                provider.GetRequiredService<IFpsMonitoringService>(),
                                provider.GetService<EcTelemetryService>(),
                                provider.GetService<HydroUIService>(),
                                provider.GetService<FlydigiCoolerHidService>()));
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
                        services.AddScoped<Views.Pages.Display>();
                        services.AddScoped<Views.Pages.SettingsPage>();
                        services.AddScoped<ViewModels.SettingsViewModel>();
                        services.AddScoped<Views.Pages.OverlaySettingsPage>(provider =>
                            new Views.Pages.OverlaySettingsPage(
                                provider.GetRequiredService<OverlayManagerService>(),
                                provider.GetService<HydroUIService>(),
                                provider.GetService<FlydigiCoolerHidService>()));
                        services.AddScoped<Views.Pages.AboutPage>();

                        // HydroUI service (singleton for auto-connect)
                        services.AddScoped<Views.Pages.HydroUI>();
                        services.AddSingleton<HydroUIService>();

                        // Flydigi BS2 Pro cooling pad page and service
                        // Singleton so the host disposes it on shutdown (stops background timers).
                        services.AddSingleton<Views.Pages.FlydigiCooler>();
                        services.AddSingleton<FlydigiCoolerHidService>();

                        // Flydigi BS1 (BLE-only) cooling pad service
                        services.AddSingleton<FlydigiCoolerBleService>();

                        // Uniwill EC access (for XMG / TUXEDO laptops)
                        services.AddSingleton<UniwillECService>(provider =>
                        {
                            return new UniwillECService();
                        });
                        services.AddSingleton<EcTelemetryService>();

                        // Hardware Performance / Q-key button intercept: polls the EC
                        // TCC offset register for button presses and cycles UCU presets.
                        services.AddSingleton<QKeyButtonService>();
                        services.AddHostedService(provider => provider.GetRequiredService<QKeyButtonService>());

                        // BetterRGB streamer engine: software-rendered keyboard RGB
                        // streamed to the ITE controller at up to 60 FPS (port of
                        // OpenRevo's BetterRGB). Hosted so shutdown stops the loop.
                        services.AddSingleton<BetterRgbStreamService>();
                        services.AddHostedService(provider => provider.GetRequiredService<BetterRgbStreamService>());

                        // Centralized device applier (singleton)
                        services.AddSingleton<DeviceApplier>();
                        services.AddSingleton<TrayQuickControlService>();
                        services.AddSingleton(provider => new TrayWidgetController(
                            () => new Views.Windows.TrayWidgetWindow(
                                provider.GetRequiredService<TrayQuickControlService>())));

                        // Configuration
                        services.Configure<AppConfig>(context.Configuration.GetSection(nameof(AppConfig)));
                    }).Build();

                _logger = _host.Services.GetRequiredService<ILogger<App>>();

                await startupProgressReporter.ReportAsync(StartupStage.LoadingSettings);
                _ = Tablet.TabletDevices;
                bool firstBoot = false;
                try
                {
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

                // --- Phase 1: independent startup tasks (all run in parallel) ---
                // Hardware detection + sensor init, device connections, startup settings
                // apply, and game library sync are all independent and can run concurrently.
                // The total wait is the slowest individual task, not the sum.

                var monitoring = _host.Services.GetRequiredService<IHardwareMonitoringService>();

                // Wire the EC service to the static field before startup preset apply.
                // The MainWindowViewModel constructor also sets this field (idempotent —
                // same singleton instance), but ApplyOnStart() runs here in the pre-loader
                // phase _before_ _host.StartAsync() creates the MainWindow.
                // Without this, RyzenAdj_To_UCU.EcService is null during startup preset
                // apply and GPU power limit writes fail silently.
                var ecService = _host.Services.GetService<UniwillECService>();
                if (ecService != null)
                {
                    Scripts.RyzenAdj_To_UCU.EcService = ecService;
                }

                var phase1TasksList = new[]
                {
                    ("Detecting hardware", true),
                    ("Initializing sensors", true),
                    ("Connecting devices", Settings.Default.EnableHydroUiNavigation || Settings.Default.EnableFlydigi),
                    ("Applying startup settings", true),
                    ("Loading game library", true),
                };

                // Wait for any queued pre-Phase-1 animations to finish so the tracker
                // doesn't fight with the playback loop over StatusTitle.
                // The window is null when the startup progress overlay is disabled.
                if (startupWindow is not null)
                    await startupWindow.WaitForProgressPlaybackAsync();

                // The tracker keeps the detail row hidden ("Initializing" only)
                // and surfaces "Connecting devices…" in it if that task is still
                // running 3 seconds after the phase starts.
                // Null when the startup progress overlay is disabled — nothing to drive.
                ParallelPhaseTracker? tracker = startupWindow is null
                    ? null
                    : new ParallelPhaseTracker(
                    "Initializing",
                    phase1TasksList,
                    50,
                    92,
                    (title, detail) => startupWindow.SetStatusLabel(title, detail),
                    startupWindow.SetProgressValue,
                    action => startupWindow.Dispatcher.InvokeAsync(action),
                    slowTaskLabelKey: "Connecting devices");

                var phase1Tasks = new List<Task>();

                // Hardware detection + sensor initialization (WMI + LHM).
                phase1Tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        await DetectHardwareAsync(monitoring);
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogError(ex, "Failed to detect hardware");
                    }
                    finally
                    {
                        _ = startupWindow?.Dispatcher.InvokeAsync(() =>
                        {
                            tracker?.TaskCompleted(0); // Detecting hardware
                            tracker?.TaskCompleted(1); // Initializing sensors (runs inside DetectHardwareAsync)
                        });
                    }
                }));

                // Auto-connect devices (Hydro UI, Flydigi cooler) so the dashboard
                // shows connected state on open.
                phase1Tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        await AutoConnectDevicesAsync();
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogError(ex, "Failed during device auto-connect");
                    }
                    finally
                    {
                        _ = startupWindow?.Dispatcher.InvokeAsync(() => tracker?.TaskCompleted(2));
                    }
                }));

                // Apply startup settings (presets, keyboard RGB, EC fan curve,
                // battery charge limit) so the main window appears instantly
                // without waiting for hardware writes.
                phase1Tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        await ApplyStartupSettingsAsync();
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogError(ex, "Failed to apply startup settings");
                    }
                    finally
                    {
                        _ = startupWindow?.Dispatcher.InvokeAsync(() => tracker?.TaskCompleted(3));
                    }
                }));

                // Load game library so the FPS overlay and Adaptive Mode have the
                // installed games list ready immediately.
                phase1Tasks.Add(Task.Run(() =>
                {
                    try
                    {
                        Game_Manager.installedGames = Game_Manager.syncGame_Library();
                    }
                    catch (Exception ex)
                    {
                        Log.Logger.Error(ex, "Failed to sync game library during startup");
                    }
                    finally
                    {
                        _ = startupWindow?.Dispatcher.InvokeAsync(() => tracker?.TaskCompleted(4));
                    }
                }));

                // Save install path on first boot (runs inline, near-instant).
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

                // Wait for all independent Phase 1 tasks to complete.
                await Task.WhenAll(phase1Tasks);
                startupWindow?.SetProgressValue(92);

                // --- Phase 2: restore Adaptive Mode (depends on devices being connected) ---
                if (Settings.Default.isAdaptiveModeRunning)
                {
                    await startupProgressReporter.ReportAsync(StartupStage.RestoringAdaptiveMode);
                }
                await RestoreAdaptiveModeAsync();

                await _host.StartAsync(CancellationToken.None);

                await startupProgressReporter.ReportAsync(StartupStage.Complete);
                if (startupWindow is not null)
                {
                    await startupWindow.WaitForProgressPlaybackAsync();
                    startupWindow.Close();
                    startupWindow = null;
                }
                startupProgressReporter.Detach();

                SafeFireAndForget(RunPostStartupTasksAsync());
            }
            catch (Exception ex)
            {
                Log.Logger.Fatal(ex, "Failed to build and start a host");
                startupProgressReporter?.Detach();
                startupWindow?.Hide();
                MessageBox.Show(ex.Message);
                Shutdown(-1);
            }
        }

        /// <summary>
        /// Reads the Windows system light/dark theme from the registry and applies it
        /// via WPFUI so that early windows (like the startup progress overlay) render
        /// with the correct brushes instead of flashing Dark first.
        /// </summary>
        private static void ApplySystemTheme()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                bool isLight = key?.GetValue("AppsUseLightTheme") is int lightVal && lightVal == 1;
                Wpf.Ui.Appearance.ApplicationThemeManager.Apply(
                    isLight
                        ? Wpf.Ui.Appearance.ApplicationTheme.Light
                        : Wpf.Ui.Appearance.ApplicationTheme.Dark);
            }
            catch
            {
                // Fall back to the default theme from App.xaml (Dark)
            }
        }

        private static void PrepareUserSettings()
        {
            try
            {
                if (!Settings.Default.SettingsUpgradeRequired)
                    return;

                Settings.Default.Upgrade();
                Settings.Default.SettingsUpgradeRequired = false;
                Settings.Default.Save();
            }
            catch (ConfigurationErrorsException ex)
            {
                if (ex.InnerException is ConfigurationErrorsException innerEx &&
                    !string.IsNullOrWhiteSpace(innerEx.Filename) &&
                    File.Exists(innerEx.Filename))
                {
                    File.Delete(innerEx.Filename);
                }

                Settings.Default.Reload();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to update settings on startup: {ex}");
            }
        }

        private static async Task ReportStartupProgressAsync(
            StartupProgressWindow startupWindow,
            StartupStage stage)
        {
            startupWindow.QueueProgress(StartupProgress.For(stage));
            await startupWindow.Dispatcher.InvokeAsync(
                static () => { },
                DispatcherPriority.Render);
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

                // Check GPU availability after LHM warms up. If the dGPU is offline
                // (MUX=iGPU-only or PnP-disabled by iGPU Power Saving Mode), log the
                // active GPU type so downstream code can adapt.
                try
                {
                    var snapshot = monitoring.ReadSnapshot();
                    if (snapshot.ActiveGpuType != Services.ActiveGpuType.Nvidia)
                    {
                        Log.Information("[Startup] Active GPU is {GpuType} (NVIDIA dGPU unavailable — MUX switch or iGPU Power Saving Mode)", snapshot.ActiveGpuType);
                    }
                }
                catch { /* non-critical — LHM may not be ready yet */ }

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
            // Auto-connect devices in parallel during the pre-loading phase.
            // Each TryAutoConnectAsync has internal guards (AutoConnect flag,
            // LastDeviceAddress, _userDisconnected) and is wrapped in try/catch
            // so one failure does not block the others.
            var tasks = new List<Task>();

            // Auto-connect watercooler on startup if enabled.
            if (Settings.Default.EnableHydroUiNavigation)
            {
                var hydroUIService = _host?.Services.GetService<HydroUIService>();
                if (hydroUIService != null && WaterCoolerHardwareDetector.IsSupportedHardware())
                {
                    tasks.Add(Task.Run(async () =>
                    {
                        try
                        {
                            await hydroUIService.TryAutoConnectAsync();
                        }
                        catch (Exception ex)
                        {
                            _logger?.LogError(ex, "Failed to auto-connect watercooler");
                        }
                    }));
                }
            }

            // Auto-connect Flydigi BS2 Pro/BS3 Pro cooling pad on startup if enabled.
            if (Settings.Default.EnableFlydigi)
            {
                var flydigiCoolerService = _host?.Services.GetService<FlydigiCoolerHidService>();
                if (flydigiCoolerService != null && FlydigiCoolerHidService.IsDeviceAvailable())
                {
                    tasks.Add(Task.Run(async () =>
                    {
                        try
                        {
                            await flydigiCoolerService.TryAutoConnectAsync();
                        }
                        catch (Exception ex)
                        {
                            _logger?.LogError(ex, "Failed to auto-connect Flydigi cooler");
                        }
                    }));
                }
            }

            // Auto-connect Flydigi BS1 (BLE) cooling pad on startup if enabled.
            if (Settings.Default.EnableFlydigi)
            {
                var bs1Service = _host?.Services.GetService<FlydigiCoolerBleService>();
                if (bs1Service != null)
                {
                    tasks.Add(Task.Run(async () =>
                    {
                        try
                        {
                            await bs1Service.TryAutoConnectAsync();
                        }
                        catch (Exception ex)
                        {
                            _logger?.LogError(ex, "Failed to auto-connect Flydigi BS1");
                        }
                    }));
                }
            }

            if (tasks.Count > 0)
                await Task.WhenAll(tasks);
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

                    if (Settings.Default.AdaptiveEcFanEnabled)
                        deviceApplier.EnableEcFanOverride();

                    if (Settings.Default.AdaptiveLbEnabled)
                        deviceApplier.EnableLightBarOverride();

                    if (Settings.Default.AdaptiveCpuEnabled || Settings.Default.AdaptiveGpuEnabled)
                        deviceApplier.EnableCpuGpuOverride();
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to restore Adaptive Mode override state");
            }
        }

        /// <summary>
        /// Applies startup settings (presets, keyboard RGB, EC fan curve, battery charge limit)
        /// during the pre-loading phase. All three run in parallel so the total wait is the
        /// slowest individual operation — not the sum of all three.
        /// </summary>
        private static async Task ApplyStartupSettingsAsync()
        {
            // Run keyboard RGB, EC fan control, and preset apply in parallel.
            // Keyboard RGB has retries (up to ~6s), EC writes are fast, preset
            // translation varies. Running them concurrently hides all latency.
            await Task.WhenAll(
                Views.Windows.MainWindow.ApplyOnStart(),
                Task.Run(Views.Windows.MainWindow.ApplyEcSettingsOnStart),
                Views.Windows.MainWindow.AsyncApplyKeyboardOnStart());
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

            double minWidth = window.MinWidth > 0 ? window.MinWidth : 980;
            double minHeight = window.MinHeight > 0 ? window.MinHeight : 420;

            window.Width = Math.Max(window.Width, minWidth);
            window.Height = Math.Max(window.Height, minHeight);

            // Clamp + center in DIP space. The old code compared the physical-pixel
            // WorkingArea directly against DIP window geometry, which mispositioned
            // the window at display scales above 100%.
            FitWindowToWorkArea(window);

            window.Activate();

            window.Topmost = true;
            window.Topmost = false;

            var handle = new WindowInteropHelper(window).Handle;
            if (handle != IntPtr.Zero)
                SetForegroundWindow(handle);
        }

        /// <summary>
        /// Clamps a window to its monitor's work area and re-centers it.
        /// WPF window geometry (Left/Top/Width/Height) is in DIPs but
        /// WorkingArea is in physical pixels, so the work area is converted
        /// to DIPs using the window's DPI ratio before clamping. Without the
        /// conversion, a window taller than the work area (e.g. 1080 DIP at
        /// 150% scale on the 2560x1600 internal panel) spills its title bar
        /// above the top screen edge.
        /// </summary>
        public static void FitWindowToWorkArea(Window window)
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == 0)
                return;

            var workingArea = System.Windows.Forms.Screen
                .FromHandle(hwnd).WorkingArea;

            // Physical pixels per DIP for the monitor hosting the window,
            // read from the DIP→device transform of the window's source.
            double scaleX = 1.0;
            double scaleY = 1.0;
            var source = PresentationSource.FromVisual(window);
            if (source?.CompositionTarget != null)
            {
                var toDevice = source.CompositionTarget.TransformToDevice;
                if (toDevice.M11 > 0 && toDevice.M22 > 0)
                {
                    scaleX = toDevice.M11;
                    scaleY = toDevice.M22;
                }
            }

            double workLeft = workingArea.Left / scaleX;
            double workTop = workingArea.Top / scaleY;
            double workWidth = workingArea.Width / scaleX;
            double workHeight = workingArea.Height / scaleY;

            if (window.Width > workWidth)
                window.Width = workWidth;
            if (window.Height > workHeight)
                window.Height = workHeight;

            window.Left = workLeft + (workWidth - window.Width) / 2.0;
            window.Top = workTop + (workHeight - window.Height) / 2.0;
        }

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("shell32.dll")]
        private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);

        public static async Task CheckForUpdate()
        {
            var updateManager = new UpdateManager("jgudec", "Uniwill-Control-Utility", App.version, "C:\\");

            var isUpdateAvailable = await updateManager.IsUpdateAvailable();

            if (isUpdateAvailable)
            {
                if (updateManager.NewVersion.StartsWith("3."))
                    ToastNotification.ShowToastNotification("Phantom Control Centre Now Available!",
                        $"Head to the Phantom Control Centre GitHub releases page to easily download the latest build!", category: ToastCategory.Settings);
                else
                    ToastNotification.ShowToastNotification("New Update Available!",
                        $"Head to the settings menu to easily download the new Uniwill Control Utility update!", category: ToastCategory.Settings);
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

            // Stop the BetterRGB stream synchronously (not fire-and-forget) so its
            // shutdown firmware fallback — which pushes a regular RGB effect so the
            // keyboard isn't left black or stuck on the last BetterRGB frame — runs
            // to completion before the process exits. StopEngine touches only HID and
            // logging (no Dispatcher.Invoke), so this cannot deadlock the UI thread.
            try
            {
                App._host?.Services.GetService<BetterRgbStreamService>()?.StopEngine("shutdown");
            }
            catch { /* best-effort during teardown */ }

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

        /// <summary>
        /// Stable identity for the taskbar button (see SetAppUserModelID).
        /// </summary>
        private const string AppUserModelId = "UniwillControlUtility.UCU";

        /// <summary>
        /// Sets a stable AppUserModelID so the taskbar button stays pinned to this app
        /// even after GPU driver resets (PnP hotswap). Must be called before any windows
        /// are shown.
        /// </summary>
        private static void SetAppUserModelID(string appId)
        {
            try
            {
                SetCurrentProcessExplicitAppUserModelID(appId);
            }
            catch { /* non-critical — falls back to default taskbar behavior */ }
        }

        /// <summary>
        /// Re-asserts the AppUserModelID after a GPU driver reset.
        /// Call this from the UI thread after PnP hotswap to restore the taskbar icon.
        /// When the dGPU is PnP-disabled, WPF's D3D device is lost and DWM can no
        /// longer render the taskbar thumbnail. This method forces DWM to re-query
        /// the window by re-sending WM_SETICON and invalidating the composition.
        /// </summary>
        public static void ReassertAppUserModelID()
        {
            SetAppUserModelID(AppUserModelId);

            var window = System.Windows.Application.Current.MainWindow;
            if (window is not null)
            {
                try
                {
                    var hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
                    if (hwnd != IntPtr.Zero)
                    {
                        // Re-send WM_SETICON so DWM picks up the icon handles again
                        // after the GPU driver reinitialization invalidated them.
                        // Get the current class icons, then re-apply them to force
                        // DWM to re-render the taskbar button.
                        IntPtr bigIcon = GetClassLong(hwnd, GCL_HICON);
                        IntPtr smallIcon = GetClassLong(hwnd, GCL_HICONSM);
                        if (bigIcon != IntPtr.Zero)
                        {
                            SendMessage(hwnd, WM_SETICON, ICON_BIG, bigIcon);
                        }
                        if (smallIcon != IntPtr.Zero)
                        {
                            SendMessage(hwnd, WM_SETICON, ICON_SMALL, smallIcon);
                        }

                        // Force DWM to flush pending composition and re-render
                        DwmFlush();

                        // Force a full redraw without moving or resizing
                        // SWP_NOMOVE | SWP_NOSIZE | NOACTIVATE | NOSENDCHANGING | SWP_FRAMECHANGED
                        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, 0x0037);

                        // Force the window to repaint immediately
                        RedrawWindow(hwnd, IntPtr.Zero, IntPtr.Zero, RDW_INVALIDATE | RDW_UPDATENOW | RDW_ALLCHILDREN);
                    }
                }
                catch { /* non-critical */ }
            }
        }

        private const int WM_SETICON = 0x0080;
        private const int ICON_BIG = 0;
        private const int ICON_SMALL = 1;

        private const int GCL_HICON = -14;
        private const int GCL_HICONSM = -34;

        private const uint RDW_INVALIDATE = 0x0001;
        private const uint RDW_UPDATENOW = 0x0100;
        private const uint RDW_ALLCHILDREN = 0x0080;

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr GetClassLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, int wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool RedrawWindow(IntPtr hwnd, IntPtr lprc, IntPtr hrgn, uint flags);

        [DllImport("dwmapi.dll")]
        private static extern int DwmFlush();

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
    }
}
