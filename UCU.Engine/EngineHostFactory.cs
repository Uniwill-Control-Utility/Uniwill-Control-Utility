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
                services.AddHostedService<EnginePipeServer>();
                services.AddHostedService<EngineWatchdog>();
                services.AddHostedService<AppSupervision>();
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
