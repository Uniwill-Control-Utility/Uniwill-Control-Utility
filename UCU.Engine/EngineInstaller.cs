using System.Diagnostics;
using Serilog;

namespace UCU.Engine
{
    /// <summary>
    /// SCM lifecycle for the engine: install, self-heal, uninstall.
    ///
    /// Installation runs elevated: the app self-launches
    /// "<c>exe --install-engine</c>" when the service is missing, which shows the
    /// one UAC prompt of the whole product (the XMG CC pattern: consent once at
    /// install, zero prompts at every launch after). sc.exe is the tool of
    /// record: <c>ServiceController</c> cannot create a service, every Windows
    /// this product ships on has sc.exe, and its exit code is the documented
    /// Win32 error, which is what the elevated self-launch reports back.
    /// </summary>
    internal static class EngineInstaller
    {
        /// <summary>
        /// Service DACL. System and Administrators keep full control;
        /// Authenticated Users get query-config (CC), query-status (SW),
        /// SERVICE_START (WP) and SERVICE_INTERROGATE (CR) — exactly what the
        /// unelevated app needs to keep the engine alive, and nothing else gains
        /// a right. Without the start grant the de-elevated app could never
        /// restart a stopped engine without a UAC prompt.
        /// </summary>
        internal const string ServiceSddl = "D:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;CCSWWPCR;;;AU)";

        private const string DisplayName = "Uniwill Control Utility Engine";

        private const string Description =
            "Runs the privileged Uniwill Control Utility hardware operations (Embedded Controller) as a system service, so the application runs unelevated.";

        /// <summary>
        /// A crashed engine is restarted by the SCM (three times a minute at
        /// most, then the failure count resets): the app's RPC calls go
        /// unavailable until the service is back, so self-healing the process is
        /// the whole recovery story.
        /// </summary>
        private const string FailureActions = "restart/60000/restart/60000/restart/60000";

        // sc.exe exit codes this flow treats as benign.
        private const int ErrorServiceAlreadyRunning = 1056;
        private const int ErrorServiceAlreadyExists = 1073;
        private const int ErrorServiceAlreadyStopped = 1062;
        private const int ErrorServiceDoesNotExist = 1060;

        /// <summary>
        /// Registers (or repairs) the service and starts it. Every step after
        /// <c>create</c> is re-applied on each run: a Velopack update moves the
        /// exe into a new version directory, and the app's startup self-heal
        /// re-runs this elevated pass to move the registration with it.
        /// </summary>
        public static async Task<int> InstallAsync()
        {
            ConfigureLogging();

            string exePath = Environment.ProcessPath
                ?? Path.Combine(AppContext.BaseDirectory, "Uniwill Control Utility Engine.exe");
            string binPath = $"binPath= \"{exePath}\"";
            string name = EngineHostFactory.ServiceName;

            int rc = await RunScAsync("query", name).ConfigureAwait(false);
            if (rc == ErrorServiceDoesNotExist)
            {
                rc = await RunScAsync("create", name, binPath, "start= auto", $"DisplayName= {DisplayName}")
                    .ConfigureAwait(false);
                if (rc != 0 && rc != ErrorServiceAlreadyExists)
                    return rc;
            }
            else if (rc != 0)
            {
                return rc;
            }

            // Registration self-heal: the binPath must follow the exe through an
            // update, and the DACL/failure rules travel with the build that
            // installed them, so re-assert both.
            rc = await RunScAsync("config", name, binPath).ConfigureAwait(false);
            if (rc != 0)
                return rc;

            rc = await RunScAsync("sdset", name, ServiceSddl).ConfigureAwait(false);
            if (rc != 0)
                return rc;

            // Best effort: a missing description or failure rule degrades the
            // service, it never makes it wrong.
            await RunScAsync("description", name, Description).ConfigureAwait(false);
            await RunScAsync("failure", name, "reset= 86400", $"actions= {FailureActions}").ConfigureAwait(false);

            rc = await RunScAsync("start", name).ConfigureAwait(false);
            if (rc != 0 && rc != ErrorServiceAlreadyRunning)
                return rc;

            Log.Information("[EngineInstaller] Engine service installed and running from {Path}", exePath);
            return 0;
        }

        /// <summary>Stops and removes the service (dev cleanup, uninstall chain).</summary>
        public static async Task<int> UninstallAsync()
        {
            ConfigureLogging();

            string name = EngineHostFactory.ServiceName;

            int rc = await RunScAsync("stop", name).ConfigureAwait(false);
            if (rc != 0 && rc != ErrorServiceAlreadyStopped && rc != ErrorServiceDoesNotExist)
            {
                Log.Warning("[EngineInstaller] sc stop exited with {Code} (continuing to delete)", rc);
            }

            rc = await RunScAsync("delete", name).ConfigureAwait(false);
            Log.Information("[EngineInstaller] Engine service removed (rc {Code})", rc);
            return rc;
        }

        /// <summary>
        /// The install runs elevated from the user's session, so the log lands
        /// in the console (user-writable) directory the <c>--console</c> mode
        /// uses; the service's ProgramData log does not exist yet.
        /// </summary>
        private static void ConfigureLogging()
        {
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Debug()
                .WriteTo.File(
                    Path.Combine(EnginePaths.ConsoleLogDirectory, "install.log"),
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 14,
                    shared: true,
                    outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
                .CreateLogger();
        }

        private static async Task<int> RunScAsync(params string[] args)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "sc.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (string arg in args)
                psi.ArgumentList.Add(arg);

            using var proc = Process.Start(psi);
            if (proc is null)
                return -1;

            string output = await proc.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
            string error = await proc.StandardError.ReadToEndAsync().ConfigureAwait(false);
            await proc.WaitForExitAsync().ConfigureAwait(false);

            int rc = proc.ExitCode;
            if (rc != 0)
            {
                Log.Warning("[EngineInstaller] sc {Args} exited with {Code}: {Output} {Error}",
                    string.Join(' ', args), rc, output.Trim(), error.Trim());
            }

            return rc;
        }
    }
}
