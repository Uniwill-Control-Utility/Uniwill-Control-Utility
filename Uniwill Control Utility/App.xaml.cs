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
using Velopack;
using Wpf.Ui;
using Wpf.Ui.Abstractions;
using UCU.Contracts;
using UCU.Client;
using UCU.Contracts.Capabilities;

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

        /// <summary>
        /// Custom entry point required by Velopack: runs the Velopack bootstrap
        /// (install/update hooks, package cleanup) before any WPF object exists,
        /// then starts the application. On portable (non-installed) layouts,
        /// VelopackApp.Run() is a no-op. Auto-apply on startup is disabled —
        /// updates are applied on exit (title-bar card / graceful quit), never
        /// as a surprise restart under the user.
        /// </summary>
        [STAThread]
        public static void Main(string[] args)
        {
            StartupArgs = args ?? Array.Empty<string>();
            GuardianRestart = StartupArgs.Contains("--from-guardian");

            // The uninstall sweep window: the Control Panel /x removes the
            // registration in ~2 s, then the sentinel's verb sweep (service,
            // layout, husks) runs for a few tens of seconds. The engine's
            // supervision relaunches the app it sees die during that window,
            // and a relaunch that opened windows, the tray, or its own
            // sentinel elevation prompt is the "UCU in task manager with no
            // window" + repeated-prompt state users report. Silent exit.
            if (UninstallSentinel.IsBeingUninstalled())
                return;

            // Velopack's MSI launches this exe with --velopackuninstall during any
            // /x transaction of the product. Since the sentinel design the handler
            // is a logged no-op (see the method): a ghost registration's /x fires
            // the same hook, and the sweep the hook used to run destroyed live
            // installs. Removal intent belongs to UninstallSentinel; the verb owns
            // the sweep. The app never launches from this argument:
            // VelopackApp.Run() handles --velopackuninstall and exits the process.
            if (StartupArgs.Contains("--velopackuninstall", StringComparer.OrdinalIgnoreCase))
                CleanupEngineOnVelopackUninstall();

            VelopackApp.Build()
                .SetAppUserModelId(AppUserModelId)
                .SetAutoApplyOnStartup(false)
                .OnRestarted(_ => RestartedByVelopack = true)
                .Run();

            var app = new App();
            app.InitializeComponent();
            app.Run();
        }

        /// <summary>
        /// Velopack's uninstall hook is a logged no-op since the sentinel design,
        /// and this is field-proven necessity. The hook fires during ANY of the
        /// product's /x transactions - including Velopack's own startup
        /// self-repair passes over stale per-user ghost registrations, which
        /// fire on machines that carry them long after nobody uninstalled
        /// anything. The pre-sentinel hook ran a machine sweep (stop+delete the
        /// service, kill the app, delete the tasks and the install root), so a
        /// ghost /x destroyed the LIVE install: the app died ~20 s after every
        /// launch, the service and layout were removed, and the registration
        /// /x loop kept re-firing the hook (observed on the dev box: the whole
        /// install self-destructed at 18:12:35, eighteen seconds after launch).
        /// Uninstall intent is detected by UninstallSentinel - the vanished
        /// registration arms the full sweep through the verb's SYSTEM task,
        /// within ~10 s of a real Control Panel /x - and the thin Velopack MSI
        /// needs no help inside its own wizard (it removes no locked files:
        /// /x completes in ~2.3 s, no reboot prompt). This method contributes
        /// its log line and nothing else.
        /// </summary>
        private static void CleanupEngineOnVelopackUninstall()
        {
            string log = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "ucu_uninstall_log.txt");

            try
            {
                System.IO.File.AppendAllText(log,
                    "velopack-hook: noop (the sentinel detects removal intent; " +
                    "the verb sweep owns uninstall completeness) " +
                    DateTime.Now + System.Environment.NewLine);
            }
            catch
            {
                // Logging is a luxury here; the uninstall proceeds.
            }
        }

        /// <summary>
        /// Command-line arguments captured in Main before the WPF app exists.
        /// Used for --from-update (Velopack restart) and --from-guardian
        /// (supervisor relaunch; the name is the app's historical marker for
        /// "relaunched after a crash", and the engine's supervisor passes it).
        /// </summary>
        internal static string[] StartupArgs { get; private set; } = Array.Empty<string>();

        /// <summary>
        /// True when the engine's supervisor relaunched us after a crash: the
        /// startup overlay is suppressed and the main window opens minimized to
        /// the tray, so the recovery never interrupts what the user is doing.
        /// </summary>
        internal static bool GuardianRestart { get; private set; }

        /// <summary>
        /// Set by the Velopack OnRestarted hook when Update.exe relaunched us
        /// after applying an update (VELOPACK_RESTART environment variable).
        /// </summary>
        internal static bool RestartedByVelopack;

        private Mutex? mutex;
        private const string MutexName = "UniwillControlUtility";

        private const string AutostartTaskName = "UCU Autostart";
        private const string LegacyAutostartTaskName = "UCU";

        /// <summary>
        /// The RTSS verb proxy, resolved from the running host: the callers are
        /// paths that DI does not construct (the prerequisite dialog is created
        /// with new). Null before the host exists (the splash path) - and
        /// RtssLauncher's fallback covers that, as it covers an engine that is
        /// not there.
        /// </summary>
        internal static IRtssCapability? RtssCapability => _host?.Services.GetService<IRtssCapability>();

        internal static readonly string SCALE_MODELS_JSON_PATH = ".\\ScaleModels.json";

        // Logs live with the app's other per-user state, not beside the exe: a
        // per-machine (MSI) install runs from Program Files, which an unelevated
        // user cannot write, and this is the process that logs. %LOCALAPPDATA% is
        // writable in every layout (dev, portable, installed), and it is where the
        // About page's "Open logs" button and the docs point.
        internal static readonly string LOGS_FOLDER = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UCU", "logs") + "\\";

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

                // A guardian relaunch must be silent: no splash overlay, and the
                // main window opens minimized to the tray (see mainWindow_Loaded).
                // A second-instance handoff also skips the overlay: it signals the
                // running instance and exits within a second, so the splash would
                // only flash.
                bool handoffInstance = IsAppAlreadyRunning();
                bool showStartupProgressOverlay = Settings.Default.ShowStartupProgressOverlay
                    && !GuardianRestart && !handoffInstance;
                if (showStartupProgressOverlay)
                {
                    startupWindow = new StartupProgressWindow();
                    startupWindow.Show();
                }

                startupProgressReporter = StartupProgressReporter.Create(showStartupProgressOverlay,
                    stage => ReportStartupProgressAsync(startupWindow!, stage));

                await startupProgressReporter.ReportAsync(StartupStage.InitializingApplication);

                await startupProgressReporter.ReportAsync(StartupStage.PreparingDiagnostics);

                var loggerConfiguration = new LoggerConfiguration()
                    .MinimumLevel.ControlledBy(Scripts.Misc.DiagnosticLogger.LevelSwitch)
                    .WriteTo.Console(theme: AnsiConsoleTheme.Code, applyThemeToRedirectedOutput: true);

                // The file sink is diagnostics, never a reason to refuse startup: a
                // directory the user may not write (a per-machine install root, a
                // full disk) degrades logging to the console instead of ending the
                // launch with an access-denied popup.
                try
                {
                    Directory.CreateDirectory(LOGS_FOLDER);
                    loggerConfiguration.WriteTo.File(LOGS_FOLDER + "ucu_log.txt",
                        fileSizeLimitBytes: 8 * 1024 * 1024, // 8MB
                        rollingInterval: RollingInterval.Day
                    );
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"File logging to '{LOGS_FOLDER}' unavailable: {ex.Message}");
                }

                Log.Logger = loggerConfiguration.CreateLogger();

                Log.Information("Logger initialized");

                Scripts.Misc.DiagnosticLogger.ApplySettingsLevel();

                // Mixed-install guard. Installing the MSI while the app is running
                // leaves the running exe in place (Windows cannot replace a locked
                // image) while the new DLLs land beside it - field, 2026-11-02: an
                // old app exe next to a new UCU.Core.dll, so every preset apply
                // died with MissingMethodException and the EC kept the previous
                // session's fan curve. Fail loudly at startup instead of half
                // working: nothing below can be trusted in that state.
                if (!IsInstallationConsistent())
                {
                    Log.Error("[Startup] Mixed installation: this exe and the UCU.* assemblies beside it come from different builds");
                    MessageBox.Show(
                        LocalizationService.Get("This installation mixes files from different builds because the app was running while it was updated. Close the app and run the installer again."),
                        LocalizationService.Get("Installation needs repair"),
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                    Shutdown();
                    return;
                }

                // Uninstall self-heal (see UninstallSentinel): start it right
                // here, before anything can block - the post-startup task
                // queue it lived in first waits behind the PawnIO installer
                // dialog and the whole window init, and the Control Panel
                // /x it must react to can land at any moment.
                UninstallSentinel.CreateDefault(
                    message => Log.Warning("[UninstallSentinel] {Message}", message)).Start();

                // Ghost-registration self-heal (see SelfHealStaleVelopackRoot):
                // must run before the first UpdateManager call, which is where
                // Velopack's registration-repair /x storm fires from.
                SelfHealStaleVelopackRoot();

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

                        // Velopack update engine (GitHub Releases feed; no-op on portable layouts).
                        services.AddSingleton<VelopackUpdateService>();

                        // The EC CPU temperature is the fallback sensor source for the CPU
                        // metric: the CPU's own MSR sensor needs the PawnIO driver, whose
                        // device ACL admits System + Administrators, so the asInvoker app
                        // of a per-machine install has no CPU sensor at all. The source is
                        // EcTelemetryService (engine RPC, cached, non-blocking).
                        services.AddSingleton<IHardwareMonitoringService>(provider =>
                            new HardwareMonitoringService(
                                provider.GetService<IEcCpuTemperatureSource>(),
                                provider.GetService<IEcEngineMetricsSource>()));
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
                                provider.GetService<FlydigiCoolerHidService>(),
                                provider.GetService<Services.ShortcutService>()));
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

                        // One-shot "dashboard has real data" signal: gates the
                        // splash's final stage (see StartupProgressWindow use).
                        services.AddSingleton<Services.DashboardFirstFrameSignal>();

                        // Flydigi BS2 Pro cooling pad page and service
                        // Singleton so the host disposes it on shutdown (stops background timers).
                        services.AddSingleton<Views.Pages.FlydigiCooler>();
                        services.AddSingleton<FlydigiCoolerHidService>();

                        // Flydigi BS1 (BLE-only) cooling pad service
                        services.AddSingleton<FlydigiCoolerBleService>();

                        // EC access (for XMG / TUXEDO laptops). The UI process reaches the
                        // EC only through the capability interfaces: RPC proxies into the
                        // UCUEngine service — the product's single path to a register. No
                        // app assembly references the hardware layer (the test suite's
                        // layering guard pins that).
                        services.AddSingleton<EngineClient>();
                        services.AddSingleton<IFanCapability, FanCapability>();
                        services.AddSingleton<IPowerCapability, PowerCapability>();
                        services.AddSingleton<IGpuMuxCapability, GpuMuxCapability>();
                        services.AddSingleton<IKeyboardCapability, KeyboardCapability>();
                        services.AddSingleton<IBatteryCapability, BatteryCapability>();
                        services.AddSingleton<IEcTelemetryCapability, EcTelemetryCapability>();
                        services.AddSingleton<IDeviceCapability, DeviceCapability>();
                        services.AddSingleton<IAmdSmuCapability, AmdSmuCapability>();
                        services.AddSingleton<IIntelMsrCapability, IntelMsrCapability>();
                        services.AddSingleton<IEngineCapability, EngineCapability>();
                        services.AddSingleton<IRtssCapability, RtssCapability>();
                        services.AddSingleton<IOemCapability, OemCapability>();
                        services.AddSingleton<EcTelemetryService>();
                        services.AddSingleton<IEcCpuTemperatureSource>(provider =>
                            provider.GetRequiredService<EcTelemetryService>());
                        // The engine's kernel-side sensor snapshot travels on the same
                        // cached-read pattern as the EC temperature: injected into the
                        // snapshot producer and merged into every snapshot, so the tray
                        // flyout, FanControl cards, the RTSS OSD and the dashboard all
                        // get the Ring0/SPD fields from one merge point.
                        services.AddSingleton<IEcEngineMetricsSource>(provider =>
                            provider.GetRequiredService<EcTelemetryService>());

                        // Engine service state: the SCM probe every EC call site gates
                        // on (a connect to a stopped engine's pipe costs the full 5 s
                        // connect timeout, and the startup path issues many), plus the
                        // pending marker for EC writes skipped while it was down. The
                        // RPC ping is the liveness override for the state where the SCM
                        // cannot speak (a security product guarding service descriptors
                        // denies the app's opens while the engine answers RPC fine).
                        services.AddSingleton(new EngineAvailability(EngineRpcReachableAsync));
                        services.AddSingleton<EngineStartupApply>();
                        // The client's transport gate: a verb for a stopped engine is
                        // refused before the connect (which costs the full 5 s
                        // connect timeout), so the startup path, page constructors,
                        // the tray tick and the EC telemetry caches degrade at the
                        // cost of an SCM query.
                        services.AddSingleton<UCU.Contracts.IEngineServiceGate>(provider =>
                            new EngineServiceGate(provider.GetRequiredService<EngineAvailability>()));

                        // The single execution pipeline for preset power-mode intents
                        // (OpenRevo's power-arbiter pattern): startup restore, resume,
                        // tray, Q-key, and the AC/DC automations all send intents here;
                        // the pure PresetArbiterPolicy rules boot-grace, Adaptive
                        // ownership, and duplicate debounce. Registered before the
                        // Q-key service that consumes it.
                        services.AddSingleton<PresetArbiter>();

                        // The watcher/trigger hub (U-201, open-revo watcher_hub.rs
                        // port): user automation rules (watchers.json) evaluated on
                        // a 1 s tick — telemetry thresholds, foreground edges, OS
                        // power/session edges, named-value diffs — acting only
                        // through the whitelisted verbs and the arbiter above.
                        services.AddSingleton<WatcherStore>();
                        services.AddSingleton<WatcherHubService>();
                        services.AddHostedService(provider => provider.GetRequiredService<WatcherHubService>());

                        // The OEM takeover composition (U-203, open-revo takeover/
                        // lifecycle.rs port): the user scope (HKCU Run keys, UWP
                        // AppModel startup tasks, StartupApproved flags, the user
                        // journal) composed app-side; the machine scope (services,
                        // tasks, HKLM, gated kills) called through the oem.* verbs.
                        // The Settings card is the only consumer; the merge is pure.
                        services.AddSingleton<Services.OemTakeoverService>();

                        // Diagnostics bundle (U-208): versions, hardware,
                        // engine and takeover state plus logs and journals.
                        // App-side only; best-effort per file.
                        services.AddSingleton<Services.DiagnosticsExportService>();

                        // User shortcut map (U-204): chord -> vetted action,
                        // dispatched from the overlay's existing global hook.
                        services.AddSingleton<Services.ShortcutStore>();
                        services.AddSingleton<Services.ShortcutService>();

                        // Hardware Performance / Q-key button intercept: polls the EC
                        // TCC offset register for button presses and cycles UCU presets.
                        services.AddSingleton<QKeyButtonService>();
                        services.AddHostedService(provider => provider.GetRequiredService<QKeyButtonService>());

                        // BetterRGB streamer engine: software-rendered keyboard RGB
                        // streamed to the ITE controller at up to 60 FPS (port of
                        // OpenRevo's BetterRGB). Hosted so shutdown stops the loop.
                        services.AddSingleton<BetterRgbStreamService>();
                        services.AddHostedService(provider => provider.GetRequiredService<BetterRgbStreamService>());

                        // The power-tuning guardian (OpenRevo's heal-contract
                        // pattern): asks the EC what power contract it enforces
                        // and re-applies the preset's contract when a third-party
                        // tuner reset it. Per-feature opt-in, off by default; the
                        // hosted loop is gated, throttled, and never queues onto
                        // a saturated engine domain.
                        services.AddSingleton<PowerGuardianService>();
                        services.AddHostedService(provider => provider.GetRequiredService<PowerGuardianService>());

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
                }
                else
                {
                    StartSingleInstanceListener();
                }

                // --- Phase 1: independent startup tasks (all run in parallel) ---
                // Hardware detection + sensor init, device connections, startup settings
                // apply, and game library sync are all independent and can run concurrently.
                // The total wait is the slowest individual task, not the sum.

                var monitoring = _host.Services.GetRequiredService<IHardwareMonitoringService>();

                // Wire the power capability to the static field before startup preset apply.
                // The MainWindowViewModel constructor also sets this field (idempotent —
                // same singleton instance), but ApplyOnStart() runs here in the pre-loader
                // phase _before_ _host.StartAsync() creates the MainWindow.
                // Without this, RyzenAdj_To_UCU.EcService is null during startup preset
                // apply and GPU power limit writes fail silently.
                var powerCapability = _host.Services.GetService<IPowerCapability>();
                if (powerCapability != null)
                {
                    Scripts.RyzenAdj_To_UCU.EcService = powerCapability;
                }

                // Load the persisted power contract (U-107) before the
                // startup apply: the EC keeps the last applied power values
                // across app restarts, so when boot apply cannot reach the
                // engine the guardian's contract is the one the PREVIOUS
                // session established, not an empty one. A boot apply that
                // does run notes over it with its own exact bytes.
                Services.PowerContractStore.Load();

                var fanCapability = _host.Services.GetService<IFanCapability>();
                if (fanCapability != null)
                {
                    Scripts.RyzenAdj_To_UCU.FanService = fanCapability;
                }

                // Wire the device capability (webcam/dGPU PnP rows) to the two
                // statics that actuate them, before any startup apply can reach
                // the GPU power saving chain. Same idempotent-singleton idiom as
                // the power/fan capabilities above; the engine answers these with
                // no consent prompt, which is what retires the automatic-path UAC.
                var deviceCapability = _host.Services.GetService<IDeviceCapability>();
                if (deviceCapability != null)
                {
                    Scripts.GPUs.NVIDIA.GpuPowerSaving.Devices = deviceCapability;
                    Scripts.Misc.HardwareManagement.Devices = deviceCapability;
                    Scripts.Misc.NvramAccess.Devices = deviceCapability;
                    Scripts.Misc.MiniLedAccess.Devices = deviceCapability;
                }

                // The SMU rows (Curve Optimizer, preset power commands) actuate
                // through the engine's cpu capability: PawnIO's device ACL admits
                // System/Administrators, so the app's own driver open is
                // access-denied unelevated. The RyzenAdj interpreter routes
                // engine-first through this static.
                var smuCapability = _host.Services.GetService<IAmdSmuCapability>();
                if (smuCapability != null)
                    Scripts.Misc.SmuAccess.Cpu = smuCapability;

                // The Intel MSR rows (TDP, voltage, ratio, power balance) ride
                // the same PawnIO ACL as the SMU; the interpreter's Intel
                // commands route engine-first through IntelManagement.Msr.
                var intelMsrCapability = _host.Services.GetService<IIntelMsrCapability>();
                if (intelMsrCapability != null)
                    Scripts.Intel_Backend.IntelManagement.Msr = intelMsrCapability;

                var updateService = _host.Services.GetRequiredService<VelopackUpdateService>();

                var phase1TasksList = new[]
                {
                    ("Detecting hardware", true),
                    ("Initializing sensors", true),
                    ("Connecting devices", Settings.Default.EnableHydroUiNavigation || Settings.Default.EnableFlydigi),
                    ("Applying startup settings", true),
                    ("Loading game library", true),
                    ("Checking for updates", Settings.Default.UpdateCheck && updateService.IsInstalled),
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
                // installed games list ready immediately. The scan is bounded by a
                // startup budget (see SyncGameLibraryWithBudgetAsync) so a volume the
                // user is not using cannot hold the splash.
                phase1Tasks.Add(Task.Run(async () =>
                {
                    await SyncGameLibraryWithBudgetAsync();
                    _ = startupWindow?.Dispatcher.InvokeAsync(() => tracker?.TaskCompleted(4));
                }));

                // Check the GitHub Releases feed for a new Velopack package. The
                // check is gated to 10 seconds so a slow network call never stalls
                // the splash — if it runs long, the decision continues in the
                // background. A found update is downloaded silently (fire-and-
                // forget) when automatic updates are on; the title-bar card
                // tracks progress and the package applies on graceful exit.
                phase1Tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        var checkTask = updateService.CheckForUpdatesAsync();
                        var winner = await Task.WhenAny(checkTask, Task.Delay(TimeSpan.FromSeconds(10)));

                        if (winner == checkTask)
                        {
                            _ = await checkTask;
                            if (UpdatePolicy.ShouldSilentDownload(updateService.IsInstalled,
                                    Settings.Default.UpdateCheck, Settings.Default.AutoUpdateApply, updateService.State))
                                SafeFireAndForget(updateService.DownloadAsync(null));
                        }
                        else
                        {
                            _ = checkTask.ContinueWith(t =>
                            {
                                try
                                {
                                    if (t.IsCompletedSuccessfully && t.Result is not null &&
                                        UpdatePolicy.ShouldSilentDownload(updateService.IsInstalled,
                                            Settings.Default.UpdateCheck, Settings.Default.AutoUpdateApply, updateService.State))
                                        SafeFireAndForget(updateService.DownloadAsync(null));
                                }
                                catch (Exception ex)
                                {
                                    _logger?.LogWarning(ex, "Background update check failed");
                                }
                            }, TaskScheduler.Default);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogWarning(ex, "Update check failed");
                    }
                    finally
                    {
                        _ = startupWindow?.Dispatcher.InvokeAsync(() => tracker?.TaskCompleted(5));
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

                if (startupWindow is not null)
                    Views.Windows.MainWindow.GateFirstShowWindow = true;

                await _host.StartAsync(CancellationToken.None);

                // The dashboard window must be shown for its navigation to
                // start the metrics timers, and the first tick fills it 1-2 s
                // (healthy) up to ~6 s (guarded: the sensor probe parks and
                // the first-snapshot budget runs out). MainWindow.ShowWindow
                // (gate above) initializes it hidden; release on the first
                // frame with real OS-path data (bounded), then show it
                // populated - the splash card is a small centered card, so
                // "window hidden until data" is what removes the empty-
                // window field signature.
                if (startupWindow is not null)
                {
                    var firstFrame = _host.Services.GetService<Services.DashboardFirstFrameSignal>();
                    if (firstFrame is not null)
                    {
                        await Task.WhenAny(
                            firstFrame.FirstFrame,
                            Task.Delay(DashboardFirstFrameBudget));
                    }
                }

                Views.Windows.MainWindow.GateFirstShowWindow = false;

                // Play the splash out and close it BEFORE the window lands.
                // Releasing the gate while the 320x290 card is up exposes the
                // 1080x1080 dashboard on the rest of the screen (the field's
                // "the main window blips at 92% of the pre-loader" signature),
                // and the window's first visible presentation should be the
                // populated dashboard, not a frame beside the splash. The
                // window stays parked off-screen through the whole playback.
                await startupProgressReporter.ReportAsync(StartupStage.Complete);
                if (startupWindow is not null)
                {
                    await startupWindow.WaitForProgressPlaybackAsync();
                    startupWindow.Close();
                    startupWindow = null;
                }
                startupProgressReporter.Detach();

                Views.Windows.MainWindow.Instance?.ReleaseStartupGate();

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
            // The EC branches below need the engine, and keeping the engine alive is
            // the app's job (the XMG Control Center shell does the same for GCUBridge).
            // So the engine is ensured first, inside the splash: the SCM query is
            // sub-millisecond in the common case (already running), a missing service
            // is registered here - the one UAC prompt of the product, and the reason
            // the service is created AND started by the first launch - and a stopped
            // one is started unelevated through the DACL's SERVICE_START grant. The EC
            // writes that follow then land on a running engine, which is why the
            // dashboard shows the EC-fed metrics on a first launch rather than after
            // the user opens services.msc.
            await EnsureEngineAndApplyDeferredAsync();

            // Run keyboard RGB, EC fan control, and preset apply in parallel.
            // Keyboard RGB has retries (up to ~6s), EC writes are fast, preset
            // translation varies. Running them concurrently hides all latency.
            await Task.WhenAll(
                Views.Windows.MainWindow.ApplyOnStart(),
                Task.Run(Views.Windows.MainWindow.ApplyEcSettingsOnStart),
                Views.Windows.MainWindow.AsyncApplyKeyboardOnStart());
        }

        /// <summary>One subscription per process to the engine's Running transition.</summary>
        private static bool _engineStartedHooked;

        /// <summary>
        /// Ensures the engine service, then applies the EC settings that startup
        /// skipped because the engine was not running (fan curve, battery charge
        /// limit - see <see cref="EngineStartupApply"/>).
        ///
        /// Called from the startup EC task (so the service is created and started
        /// before any EC verb) and again from the post-splash deferred chain: a start
        /// that needed elevation shows its prompt there, with the window already up.
        /// </summary>
        private static async Task<EngineEnsureOutcome> EnsureEngineAndApplyDeferredAsync()
        {
            var availability = GetService<EngineAvailability>();
            if (availability is null)
                return EngineEnsureOutcome.Failed;

            // A service the app cannot start (declined UAC) may come up later: at the
            // next boot, from services.msc, or from a repair pass in this process. The
            // transition event is what makes the deferred apply survive that.
            if (!_engineStartedHooked)
            {
                _engineStartedHooked = true;
                availability.Started += () => SafeFireAndForget(ApplyDeferredEcSettingsIfPendingAsync());
            }

            EngineEnsureOutcome outcome = await EngineLifecycle.EnsureAsync(availability, EngineRpcReachableAsync);

            await ApplyDeferredEcSettingsIfPendingAsync(outcome);

            return outcome;
        }

        /// <summary>
        /// Liveness through the engine's own pipe, for the state where the SCM
        /// cannot describe the service (a security product guarding service
        /// descriptors reverts every DACL write, so the app's opens are denied):
        /// the RPC pipe is independent of the SCM descriptor, and the engine
        /// answers. Two attempts with a gap: a service in START_PENDING has not
        /// opened its pipe yet, and declaring it dead on the first miss is how a
        /// working engine gets a repair elevation. The probe re-runs on its own
        /// 5 s timer, so the budget here is only the startup path's stall.
        /// </summary>
        private static async Task<bool> EngineRpcReachableAsync()
        {
            for (int attempt = 1; attempt <= 2; attempt++)
            {
                try
                {
                    var client = new UCU.Client.EngineClient(
                        connectTimeout: TimeSpan.FromSeconds(2));
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
                    // Typed overload, deliberately: engine.ping is a result-bearing
                    // verb (it answers true), and the untyped InvokeAsync runs with
                    // T=object, which the source-generated contract refuses
                    // ("'Object' is not registered"). Field-proven (2026-10-28):
                    // the untyped call threw AFTER a healthy 60 ms round trip, so
                    // the probe reported a live engine dead on every guarded-SCM
                    // machine, and every EC feature (fan speeds, battery health,
                    // charge control) stayed gated off while the pipe answered
                    // fine. The typed shape is the one the proxy (PingAsync) uses.
                    await client.InvokeAsync<bool>(UCU.Contracts.EngineVerbs.Engine.Ping, cts.Token);
                    return true;
                }
                catch (Exception ex)
                {
                    _logger?.LogDebug(
                        ex, "Engine RPC liveness probe attempt {Attempt} failed", attempt);

                    if (attempt < 2)
                        await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
                }
            }

            return false;
        }

        /// <summary>
        /// Applies the deferred EC settings when the engine is up and the mark is
        /// pending. The mark survives an engine that is still down: the EC charge
        /// limit resets to the BIOS default at every cold boot, so a skipped write
        /// must stay pending rather than be dropped on the floor.
        /// </summary>
        private static async Task ApplyDeferredEcSettingsIfPendingAsync(
            EngineEnsureOutcome? outcome = null)
        {
            var availability = GetService<EngineAvailability>();
            var deferred = GetService<EngineStartupApply>();
            if (availability is null || deferred is null || !deferred.IsPending)
                return;

            if (!availability.IsRunning)
            {
                _logger?.LogWarning(
                    "Engine service is not running{Outcome}; the deferred EC fan curve and battery charge limit stay pending"
                    + " until the service starts",
                    outcome is null ? "" : $" ({outcome})");
                return;
            }

            if (!deferred.ConsumePending())
                return;

            _logger?.LogInformation(
                "Engine service is running{Outcome}; applying the EC settings startup deferred",
                outcome is null ? "" : $" ({outcome})");

            try
            {
                await Task.Run(Views.Windows.MainWindow.ApplyEcSettingsOnStart);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Deferred EC settings apply failed");
            }
        }

        private static async Task RunPostStartupTasksAsync()
        {
            try
            {
                // Relaunched by Velopack after "Update and restart" (title-bar card).
                if (StartupArgs.Contains("--from-update"))
                {
                    var newVersion = Assembly.GetEntryAssembly()?.GetName().Version;
                    ToastNotification.ShowToastNotification("Update installed",
                        LocalizationService.Format("Updated to version {0}", newVersion), category: ToastCategory.Settings);
                }

                var isInternetAvailable = await Task.Run(IsInternetAvailable);

                if (isInternetAvailable && PawnIoDetectionService.ShouldOpenInstaller())
                    new InstallpawnIo().Show();
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed during deferred update and PawnIO checks");
            }

            // Engine service: install when missing (one elevated self-launch,
            // the single UAC prompt of the product), start when stopped (the
            // service DACL grants the unelevated app SERVICE_START), re-assert
            // the registration when it is not openable (a DACL from a build
            // before that grant). This replaces the App Paths auto-elevation
            // shim the retired requireAdministrator manifest needed: the app is
            // asInvoker now.
            SafeFireAndForget(EnsureEngineAndApplyDeferredAsync());

            // Migrate the legacy "UCU" autostart task (pre-engine name,
            // registered with highest privileges) to the "UCU Autostart" task.
            MigrateLegacyAutostartTask();

            // The engine's supervisor replaced the scheduled-task guardian
            // (the guardian exe is no longer shipped), so an install that had
            // the feature enabled drops its "UCU Guardian" task on this start.
            RetireGuardianTask();

            // Self-heal the scheduled task action paths: the path is baked at
            // registration time, so installs that moved (zip → MSI migration)
            // would otherwise autostart the old exe.
            SelfHealScheduledTask(AutostartTaskName, "", null, TimeSpan.FromSeconds(Settings.Default.StartupDelaySeconds), "Start UCU");

            // Point the per-user Add/Remove Programs entry (Velopack writes
            // HKCU\...\Uninstall\Uniwill-Control-Utility) at the layout's
            // Uninstall.exe shim. Its default action is Velopack's own uninstaller,
            // which leaves the engine service registration and the MSI's ARP entry
            // behind - the "husk" entry users report after an uninstall. The shim is
            // the complete route; this hive is the user's own, so no elevation.
            SelfHealControlPanelUninstall();

            // RTSS auto-start for the overlay (boot-start contract, or the
            // Overlay page's "Enable overlay" + "Show on startup" pair): the
            // overlay's frame source is expected live at startup - RTSS
            // installed but not running means the overlay silently runs
            // without RTSS data, and games launched while RTSS is absent are
            // never hooked (no OSD at all, field-proven with the zombie
            // section forensics 2026-10-31). The APP is the starter, not the
            // engine: RTSS is a user-session overlay (tray icon, game
            // injection) and a SYSTEM session-0 launch would render it
            // invisible. The start goes through the elevation-free launcher:
            // RTSS commonly carries the "Run as administrator" flag
            // (field-reported: double-clicking it prompts UAC), and a plain
            // Process.Start would pop that prompt at every logon.
            SafeFireAndForget(AutoStartRtssForOverlayAsync());

            // Show the prerequisite checklist on first startup.
            if (Settings.Default.ShowPrerequisiteDialog)
                SafeFireAndForget(ShowPrerequisiteDialogAsync());
        }

        /// <summary>
        /// RTSS auto-start: starts it when the startup expects the overlay
        /// live - (a) UCU is the boot-started app (the "UCU Autostart" task
        /// is the witness: the overlay's frame source is expected live at
        /// boot - RTSS installed but not in the user's startup set means the
        /// overlay silently runs without RTSS data), or (b) the Overlay
        /// page's "Enable overlay" and "Show on startup" are both ON (the
        /// user wants the overlay every session - and the OSD needs RTSS
        /// running before games launch, because RTSS hooks at game start).
        /// Silent no-op otherwise (no RTSS, RTSS running, neither contract).
        /// The APP is the starter, not the engine: RTSS is a user-session
        /// overlay (tray icon, game injection) and a SYSTEM session-0 launch
        /// would render it invisible. The start goes through the
        /// elevation-free launcher: RTSS commonly carries the "Run as
        /// administrator" flag (field-reported: double-clicking it prompts
        /// UAC), and a plain Process.Start would pop that prompt at every
        /// logon.
        /// </summary>
        private static async Task AutoStartRtssForOverlayAsync()
        {
            try
            {
                bool bootStarted = ScheduledTaskService.GetActionPath(AutostartTaskName) is not null;

                OverlayOptions overlay = _host?.Services.GetService<OverlaySettingsStore>()?.Current
                    ?? new OverlayOptions();
                bool overlayDemandsRtss = overlay.Enabled && overlay.ShowOnStartup;

                if (!bootStarted && !overlayDemandsRtss)
                    return; // Neither the boot-start contract nor the overlay toggles ask for RTSS.

                if (!RtssDetectionService.IsInstalled() || RtssDetectionService.IsRunning())
                    return;

                bool started = await RtssLauncher.StartAsync();
                if (started)
                    // Warning severity by project rule (the app log's field
                    // level): this startup action must be verifiable in a
                    // field report, not silently successful.
                    _logger?.LogWarning("[Startup] RTSS was installed but not running - started for the overlay");
                else
                    _logger?.LogWarning("[Startup] RTSS auto-start did not bring RTSS up");
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "[Startup] RTSS auto-start failed");
            }
        }

        /// <summary>
        /// Non-creating probe of the single-instance mutex: true when an
        /// instance is already running (this process is a handoff instance
        /// that will signal it and exit).
        /// </summary>
        private static bool IsAppAlreadyRunning()
        {
            try
            {
                using var probe = new Mutex(false, MutexName, out bool createdNew);
                return !createdNew;
            }
            catch (AbandonedMutexException)
            {
                return true;
            }
        }

        /// <summary>
        /// Moves the pre-engine "UCU" autostart task (registered with highest
        /// privileges for the requireAdministrator manifest) to the
        /// "UCU Autostart" task the asInvoker app registers with limited
        /// privileges. The old task only exists on installs that had autostart
        /// enabled, so re-registering the new one preserves the user's choice;
        /// a task created by the new app never appears under the old name.
        /// Idempotent: after one run the legacy task is gone.
        /// </summary>
        private static void MigrateLegacyAutostartTask()
        {
            try
            {
                string? legacyPath = ScheduledTaskService.GetActionPath(LegacyAutostartTaskName);
                if (legacyPath is null)
                    return;

                ScheduledTaskService.Register(
                    AutostartTaskName, "Start UCU", "",
                    TimeSpan.FromSeconds(Settings.Default.StartupDelaySeconds));
                ScheduledTaskService.Unregister(LegacyAutostartTaskName);
                _logger?.LogInformation("Migrated the legacy '{Legacy}' autostart task to '{Task}'",
                    LegacyAutostartTaskName, AutostartTaskName);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to migrate the legacy autostart scheduled task");
            }
        }

        /// <summary>
        /// The engine's supervisor (AppSupervision) replaced the scheduled-task
        /// guardian, and the guardian exe no longer ships: an install that had
        /// the feature enabled unregisters its "UCU Guardian" task and clears
        /// the (now vestigial) setting. Idempotent: the setting is cleared the
        /// first time, so the task removal runs once.
        /// </summary>
        private static void RetireGuardianTask()
        {
            if (!Settings.Default.GuardianEnabled)
                return;

            try
            {
                ScheduledTaskService.Unregister("UCU Guardian");
                _logger?.LogInformation("Removed the retired 'UCU Guardian' task (the engine supervisor owns crash relaunch now)");
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to remove the retired guardian scheduled task");
            }

            Settings.Default.GuardianEnabled = false;
            Settings.Default.Save();
        }

        /// <summary>
        /// Points this user's Add/Remove Programs entry (Velopack writes
        /// HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Uniwill-Control-Utility)
        /// at the layout's <c>Uninstall.exe</c> shim. Its default action is Velopack's
        /// own uninstaller, which leaves the engine service registration and the MSI's
        /// ARP entry behind - the husk entry that stays in the program list after a
        /// "successful" uninstall. The shim is the complete route (service stop +
        /// delete, MSI uninstall, ARP entries, install root), and this hive belongs to
        /// the user, so it needs no elevation. The MSI's machine-level entry is
        /// redirected by the elevated engine install pass.
        /// </summary>
        private static void SelfHealControlPanelUninstall()
        {
            try
            {
                string shim = Path.Combine(AppContext.BaseDirectory, "Uninstall.exe");
                if (!File.Exists(shim))
                    return;

                using RegistryKey? entry = Registry.CurrentUser.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Uniwill-Control-Utility",
                    writable: true);

                if (entry is null)
                    return;

                // Quoted: the path contains spaces, and the shell splits an unquoted value.
                entry.SetValue("UninstallString", "\"" + shim + "\"", RegistryValueKind.String);
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Control Panel uninstall redirect (per-user entry) skipped");
            }
        }

        /// <summary>
        /// Ghost-registration self-heal, field-proven necessity. The per-user
        /// Velopack root (%LOCALAPPDATA%\Uniwill-Control-Utility, created by
        /// the per-machine MSI install's bootstrap in a user context) feeds
        /// Velopack's registration-repair: the app's own UpdateManager sees
        /// the ghost root, decides the live machine registrations are stale,
        /// and runs "msiexec /x" on them - observed at +4 min into a healthy
        /// session, removing the live product's registration. The sentinel
        /// reads the vanished registration plus the genuine removal events as
        /// the Control Panel uninstall it is contracted to detect, and runs
        /// the full sweep: the app dies seconds after the dashboard, the
        /// machine is cleaned under a running app. This is the upgrade path
        /// every old per-user-install user walks into. The per-machine layout
        /// is authoritative: when the app runs from Program Files, the
        /// per-user root is stale by definition and this app never reads it,
        /// so the self-heal deletes it. The root lives in the user's own
        /// AppData, so no elevation is involved. Runs before the first
        /// UpdateManager call.
        /// </summary>
        /// <summary>
        /// True when this exe can actually reach the newest API on the UCU
        /// assemblies shipped beside it. A method that exists in the source but
        /// not in the deployed assembly throws MissingMethodException at the call
        /// site, which is the signature of an install that overwrote the DLLs
        /// while the app was running (the locked exe survives, everything else
        /// moves on) - field, 2026-11-02: BatteryPowerPolicy.ClampForBattery gone
        /// from the new core, every preset apply dead, fans left on the previous
        /// session's curve.
        /// </summary>
        internal static bool IsInstallationConsistent()
        {
            try
            {
                _ = UCU.Core.Power.BatteryPowerPolicy.ClampCpu(1, 1, onBattery: false, cpuCeiling: 1);
                return true;
            }
            catch (MissingMethodException)
            {
                return false;
            }
            catch (TypeLoadException)
            {
                return false;
            }
            catch (System.IO.FileLoadException)
            {
                return false;
            }
        }

        private static void SelfHealStaleVelopackRoot()
        {
            try
            {
                string? root = StaleVelopackRootPath(
                    AppContext.BaseDirectory,
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
                if (root is null || !Directory.Exists(root))
                    return;

                Directory.Delete(root, recursive: true);
                _logger?.LogWarning(
                    "[SelfHeal] removed the stale per-user Velopack root {Root}: the app runs " +
                    "per-machine, and the ghost root drives Velopack's registration-repair /x " +
                    "storm that the uninstall sentinel executes",
                    root);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "[SelfHeal] stale per-user Velopack root not removed");
            }
        }

        /// <summary>
        /// Derivation seam: the per-user Velopack root is stale only when the
        /// app image runs from the per-machine layout (Program Files);
        /// dev/portable layouts keep their roots (Velopack's own storage)
        /// untouched. The machine root name is the packer's app name
        /// (vpk pack -u Uniwill-Control-Utility).
        /// </summary>
        internal static string? StaleVelopackRootPath(string appBaseDirectory, string programFiles, string localAppData)
        {
            if (string.IsNullOrEmpty(programFiles) ||
                !appBaseDirectory.StartsWith(programFiles, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return Path.Combine(localAppData, "Uniwill-Control-Utility");
        }

        /// <summary>
        /// Re-registers a scheduled task when its action path no longer points
        /// at the running executable (install moved, e.g. zip → MSI migration).
        /// Never creates tasks that the user has not enabled.
        /// </summary>
        private static void SelfHealScheduledTask(string taskName, string arguments, string? exePath, TimeSpan delay, string description)
        {
            try
            {
                var registeredPath = ScheduledTaskService.GetActionPath(taskName);
                if (registeredPath is null)
                    return; // task not registered — nothing to heal

                string expectedPath = exePath ?? ScheduledTaskService.CurrentExePath;
                if (!string.Equals(registeredPath, expectedPath, StringComparison.OrdinalIgnoreCase))
                {
                    ScheduledTaskService.Register(taskName, description, arguments, delay, exePath);
                    _logger?.LogInformation("Re-pointed the {Task} scheduled task at {Path}", taskName, expectedPath);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to self-heal the {Task} scheduled task", taskName);
            }
        }

        /// <summary>
        /// How long the splash's final stage waits for the dashboard's first
        /// data frame. Field-tuned: the first snapshot lands 1-2 s after the
        /// window exists on a healthy machine, and up to the 6 s
        /// first-snapshot budget on a guarded one (the sensor probe parks,
        /// the dashboard fills from the OS-only path) - 10 s keeps margin
        /// over the observed fill while capping the worst case, and a wedged
        /// probe releases the splash at the cap.
        /// </summary>
        private static readonly TimeSpan DashboardFirstFrameBudget = TimeSpan.FromSeconds(10);

        /// <summary>
        /// How long the splash waits for the game-library scan before releasing it.
        /// Mirrors the update check's 10 s gate: both probe resources outside the app's
        /// control (a feed, every volume on the machine) on the startup path.
        /// </summary>
        private static readonly TimeSpan GameLibraryStartupBudget = TimeSpan.FromSeconds(10);

        /// <summary>
        /// Scans the game library under a startup budget. The scan walks every local
        /// volume, and one the user is not using can block a directory probe for tens of
        /// seconds - a disconnected mapped network drive measured 21-63 s in the
        /// redirector - so the splash is released at the budget and the list publishes
        /// when the scan finishes. The scan's own drive loop skips network drives, so
        /// reaching this budget means something else is slow; the budget is the guard for
        /// the class, not for one device.
        /// </summary>
        private static async Task SyncGameLibraryWithBudgetAsync()
        {
            Task<List<Game_Manager.GameLauncherItem>?> scan =
                Task.Run(() => Game_Manager.syncGame_Library());

            bool finishedInBudget;
            try
            {
                finishedInBudget = scan.Wait(GameLibraryStartupBudget);
            }
            catch (AggregateException)
            {
                // Faulted inside the budget: PublishGameLibraryScan logs it.
                finishedInBudget = true;
            }

            if (finishedInBudget)
            {
                PublishGameLibraryScan(scan);
                return;
            }

            _logger?.LogWarning(
                "Game library scan outlived the {Budget}s startup budget; the list publishes when the scan finishes",
                GameLibraryStartupBudget.TotalSeconds);
            _ = scan.ContinueWith(t => PublishGameLibraryScan(t), TaskScheduler.Default);
        }

        /// <summary>
        /// Publishes a completed scan. A failed or empty scan keeps the list that is
        /// already published: null means "no games" to the FPS overlay and Adaptive Mode,
        /// so a late failure must not wipe a list the UI is showing.
        /// </summary>
        private static void PublishGameLibraryScan(Task<List<Game_Manager.GameLauncherItem>?> scan)
        {
            try
            {
                if (scan.IsCompletedSuccessfully && scan.Result is not null)
                    Game_Manager.installedGames = scan.Result;
                else if (scan.IsFaulted || scan.IsCanceled)
                    Log.Logger.Error(scan.Exception?.GetBaseException(),
                        "Failed to sync game library during startup");
            }
            catch (Exception ex)
            {
                Log.Logger.Error(ex, "Failed to sync game library during startup");
            }
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
            // Tell the engine this shutdown is intentional so its supervisor does
            // not relaunch the app (a crash never reaches OnExit). The app exits
            // right after this, so the call is waited on - bounded, because a
            // wedged engine must never wedge the exit. A signal the engine never
            // receives costs one relaunch, not correctness.
            try
            {
                IEngineCapability? engine = GetService<IEngineCapability>();
                if (engine is not null)
                {
                    using var notifyCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    engine.NotifyAppExitAsync(notifyCts.Token).GetAwaiter().GetResult();
                }
            }
            catch
            {
                // Best effort during teardown. This is also the normal path when
                // the engine is not running, where there is nothing to disarm.
            }

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

            // Apply a downloaded update on graceful exit. ApplyUpdatesAndExit
            // terminates the process, so this must run after all synchronous
            // hardware teardown above (SMU / Flydigi / BetterRGB) — it skips
            // the async host stop below, which is best-effort by design.
            try
            {
                App._host?.Services.GetService<VelopackUpdateService>()?.TryApplyOnExit();
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
            // Log only - never block here. The message box this handler used to
            // show ran through Dispatcher.Invoke, and while it was up the runtime
            // could not terminate the process: field, 2026-11-04, a background
            // NRE at 08:41 left a frozen app behind a dialog until the user
            // dismissed it at 08:57, so the engine's supervisor saw a live
            // process the whole time and could not revive anything. A logged
            // death the supervisor can react to beats a hang nobody can see.
            Log.Logger.Fatal(e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString()),
                "Unhandled exception on non-UI thread (terminating={Terminating})", e.IsTerminating);
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
