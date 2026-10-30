using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using Serilog.Events;
using UCU.Hardware.Ec;

namespace UCU.Engine
{
    /// <summary>
    /// Host composition, kept out of <c>Program</c> so the in-process tests build the
    /// same service graph the installed service runs: the pipe server, the dispatcher,
    /// the domain queues and the gate are wired identically, and the test passes a
    /// fake transport, a private pipe name and <c>RestrictPipeAccess=false</c>.
    /// </summary>
    public static class EngineHostFactory
    {
        public const string ServiceName = "UCUEngine";

        public static IHost CreateHost(string[] args, bool isService, EngineRuntimeOptions? overrides = null)
        {
            EngineRuntimeOptions options = overrides ?? ParseOverrides(args);

            var builder = new HostBuilder()
                .UseSerilog((_, config) => config
                    .MinimumLevel.Debug()
                    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
                    .WriteTo.File(
                        Path.Combine(EnginePaths.ResolveLogDirectory(options, isService), "engine.log"),
                        rollingInterval: RollingInterval.Day,
                        retainedFileCountLimit: 14,
                        shared: true,
                        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext} {Message:lj}{NewLine}{Exception}"));

            if (isService)
                builder.UseWindowsService(o => { o.ServiceName = ServiceName; });

            builder.ConfigureServices(services =>
            {
                services.AddSingleton(options);
                services.AddSingleton<EcEngineGate>();
            services.AddSingleton<AppExitSignal>();
                services.AddSingleton<EngineDomainQueues>();
                services.AddSingleton<EngineDispatcher>();

                // The cached kernel-side sensor readings (see MetricsProbeService):
                // LocalSystem opens the sensor drivers the asInvoker app cannot, the
                // verb serves the cache. Console mode gets it too, so the dev F5
                // engine answers the same verb as the service. Tests run a scripted
                // probe through the override, as with the EC transport: the real
                // probe opens sensor drivers, which has no place in a test process.
                services.AddSingleton<EngineMetricsSnapshotSource>();
                services.AddSingleton<UCU.Hardware.Metrics.ISensorProbe>(
                    options.SensorProbeOverride ?? new UCU.Hardware.Metrics.LhmSensorProbe());
                services.AddHostedService<MetricsProbeService>();

                // The PnP device rows (webcam/dGPU): LocalSystem performs the CM API
                // mutation with no consent prompt; tests script the device tree.
                services.AddSingleton<UCU.Hardware.Devices.IPnpDeviceService>(
                    options.PnpDeviceOverride ?? new UCU.Hardware.Devices.PnpDeviceService());

                // The NVRAM rows (AP-menu, mux mirror): LocalSystem enables
                // SeSystemEnvironmentPrivilege, which an unelevated app never can;
                // tests script a variable store.
                services.AddSingleton<UCU.Hardware.Firmware.IFwNvramService>(
                    options.FwNvramOverride ?? new UCU.Hardware.Firmware.FwNvramService());

                // The SMU rows (Curve Optimizer, preset power commands): PawnIO's
                // device ACL admits System/Administrators, so the mailbox protocol
                // runs on LocalSystem's token; tests script a command table.
                services.AddSingleton<UCU.Hardware.Cpu.IAmdSmuService>(
                    options.AmdSmuOverride ?? new UCU.Hardware.Cpu.AmdSmuService());

                // The Intel MSR rows (TDP, voltage offset, ratio, power balance):
                // same PawnIO ACL as the SMU; tests script a register file.
                services.AddSingleton<UCU.Hardware.Cpu.Intel.IIntelMsrService>(
                    options.IntelMsrOverride ?? new UCU.Hardware.Cpu.Intel.IntelMsrService());

                // The MiniLED local-dimming row: executing the OEM root\WMI
                // method is a privileged act the unelevated app cannot do;
                // tests script the handshake.
                services.AddSingleton<UCU.Hardware.Devices.IMiniLedService>(
                    options.MiniLedOverride ?? new UCU.Hardware.Devices.MiniLedWmiService());

                services.AddHostedService<EnginePipeServer>();
                services.AddHostedService<EngineWatchdog>();
                services.AddHostedService<AppSupervision>();

                // Service mode only: the app-contract DACL asserted from inside the
                // service (a security product that guards service descriptors can
                // revert the installer's sdset; the service is the writer that
                // always has rights). See ServiceSelfSecurityService.
                if (isService)
                    services.AddHostedService<ServiceSelfSecurityService>();
            });

            return builder.Build();
        }

        /// <summary>
        /// Command-line overrides. Deliberately a hand-rolled flag list, not
        /// <c>UseCommandLineArguments</c> binding: these flags appear in the service's
        /// binPath (quoted) and in a developer's console line, and a binder that
        /// accepts unknown keys hides typos in the installed path.
        /// </summary>
        public static EngineRuntimeOptions ParseOverrides(string[] args)
        {
            var options = new EngineRuntimeOptions();

            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--console":
                        break; // lifetime selection, not an option field
                    case "--pipe-name" when i + 1 < args.Length:
                        options = options with { PipeName = args[++i] };
                        break;
                    case "--log-dir" when i + 1 < args.Length:
                        options = options with { LogDirectory = args[++i] };
                        break;
                    case "--no-pipe-security":
                        // Test hook only; the installer never sets it.
                        options = options with { RestrictPipeAccess = false };
                        break;
                    case "--no-supervision":
                        // Dev/test hook: the console host must not spawn UI processes.
                        options = options with { SupervisionEnabled = false };
                        break;
                }
            }

            return options;
        }
    }
}
