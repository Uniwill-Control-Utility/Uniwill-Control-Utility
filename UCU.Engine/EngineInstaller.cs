using System.Diagnostics;
using Microsoft.Win32;
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
        /// Service DACL. System and Administrators keep full control; Authenticated
        /// Users - the identity the unelevated app runs as - get exactly the four
        /// rights the product's design needs, decoded per ace (each SDDL service
        /// right is two letters):
        ///
        ///   CC  SERVICE_QUERY_CONFIG    (0x0001) - read the registration
        ///   SW  SERVICE_QUERY_STATUS    (0x0004) - see that the engine is stopped
        ///   RP  SERVICE_START           (0x0020) - start it, which is what keeps a
        ///                                       stopped engine from needing UAC
        ///   RC  READ_CONTROL            (0x20000) - read the descriptor (diagnostics)
        ///
        /// <c>RP</c> is the right the whole de-elevation design rests on: without it
        /// the app can never start the engine it owns. What the ace
        /// deliberately does *not* grant is as important: no
        /// <c>LC</c>/SERVICE_QUERY_INTERROGATE (0x0080: on a LocalSystem service this
        /// is the token-duplication elevation hole - InterrogateService lets the
        /// caller open the service process's token), no
        /// <c>LO</c>/SERVICE_CHANGE_CONFIG (0x0002: re-point binPath, arbitrary code
        /// as LocalSystem), no <c>DT</c>/delete, no write-DAC. Stopping and removing
        /// the service stay administrator-only: the update shim and the uninstall
        /// shim run requireAdministrator, and the app has no reason to stop the
        /// hardware service.
        ///
        /// Consequence the app code must honour: .NET's <c>ServiceController</c>
        /// read-open requests SERVICE_QUERY_ALL_ACCESS, which includes interrogate
        /// and enumerate-dependents - rights this descriptor withholds - so every
        /// ServiceController query fails with Access Denied against a healthy
        /// install. The app's SCM reads go through <c>ScmNative</c>, which opens
        /// for exactly the rights granted (SW, RP).
        /// </summary>
        internal const string ServiceSddl = "D:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;CCSWRPRC;;;AU)";

        /// <summary>
        /// What services.msc shows. The SCM service name stays the stable
        /// identity (<c>UCUEngine</c>: sc commands, the pipe contract, the
        /// app's ServiceController lookups), and the exe keeps the product
        /// name; this is the short user-facing label. Re-applied by the
        /// install self-heal so existing registrations rename themselves.
        /// </summary>
        private const string DisplayName = "UCU Engine";

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

            // sc.exe's key= value convention depends on exact quoting: a value with
            // spaces must be wrapped in quotes on the command line. The argument
            // strings below are what a cmd user would type; RunScAsync passes them
            // through verbatim (ArgumentList escaping would merge the pairs into a
            // single argv and sc rejects the line with exit code 1639).
            string binPath = $"binPath= \"{exePath}\"";
            string displayName = $"DisplayName= \"{DisplayName}\"";
            string name = EngineHostFactory.ServiceName;

            int rc = await RunScAsync("query", name).ConfigureAwait(false);
            if (rc == ErrorServiceDoesNotExist)
            {
                rc = await RunScAsync("create", name, binPath, "start= auto", displayName)
                    .ConfigureAwait(false);
                if (rc != 0 && rc != ErrorServiceAlreadyExists)
                    return rc;
            }
            else if (rc != 0)
            {
                return rc;
            }

            // Registration self-heal: the binPath and display name must follow the
            // exe/build through an update (the rename from the product-length name to
            // "UCU Engine" re-applies here for installs that predate it), and the
            // DACL/failure rules travel with the build that installed them, so
            // re-assert all of them.
            rc = await RunScAsync("config", name, binPath, displayName).ConfigureAwait(false);
            if (rc != 0)
                return rc;

            rc = await RunScAsync("sdset", name, ServiceSddl).ConfigureAwait(false);
            if (rc != 0)
                return rc;

            // Best effort: a missing description or failure rule degrades the
            // service, it never makes it wrong. The description is a free-text
            // value with spaces, so it travels quoted.
            await RunScAsync("description", name, $"\"{Description}\"").ConfigureAwait(false);
            await RunScAsync("failure", name, "reset= 86400", $"actions= {FailureActions}").ConfigureAwait(false);

            rc = await RunScAsync("start", name).ConfigureAwait(false);
            if (rc != 0 && rc != ErrorServiceAlreadyRunning)
                return rc;

            // The DACL is the contract the unelevated app runs on, and a fresh
            // registration has a proven way to swallow it: sc create commits the
            // default security descriptor into the registry after sc.exe has already
            // returned, and a sdset that ran inside that window is silently reverted
            // (field incident: the install log recorded sdset rc=0, twenty seconds
            // later sc sdshow showed the create template, and the app - no AU
            // query-status, no start - elevated for a "DACL repair" on every launch).
            // Read the descriptor back, re-assert until it sticks.
            if (!await EnsureSddlCommittedAsync(name).ConfigureAwait(false))
            {
                Log.Warning(
                    "[EngineInstaller] Engine service DACL did not converge to {Sddl} - the app cannot open the service it owns",
                    ServiceSddl);
            }

            // The ARP entries are part of this install's health, not a separate
            // feature: see RepairControlPanelUninstall.
            RepairControlPanelUninstall(exePath);

            Log.Information("[EngineInstaller] Engine service installed and running from {Path}", exePath);
            return 0;
        }

        /// <summary>
        /// Verifies the service's live DACL carries the app contract ace
        /// (<see cref="ServiceSddl"/>'s AU ace), re-asserting sdset until it does.
        /// The create-commit race needs one or two rounds; the budget is generous so
        /// a descriptor that converges slowly (antivirus on the registry write)
        /// still lands.
        /// </summary>
        internal static async Task<bool> EnsureSddlCommittedAsync(string name)
        {
            for (int attempt = 0; attempt < 10; attempt++)
            {
                string shown = await RunScCaptureAsync("sdshow", name).ConfigureAwait(false);
                if (SddlCarriesAppContract(shown))
                {
                    if (attempt > 0)
                        Log.Information("[EngineInstaller] Engine service DACL converged after {Attempts} re-assertions", attempt + 1);
                    return true;
                }

                Log.Warning("[EngineInstaller] Engine service DACL reads {Shown} - re-asserting (attempt {Attempt})",
                    shown.Trim(), attempt + 1);
                await RunScAsync("sdset", name, ServiceSddl).ConfigureAwait(false);
                await Task.Delay(500).ConfigureAwait(false);
            }

            return false;
        }

        /// <summary>
        /// True when an <c>sc sdshow</c> line grants Authenticated Users the rights
        /// the app's design (gate + unelevated start) requires: the exact
        /// <c>CCSWRPRC</c> mask. The create-template ace (AU: <c>KR</c>) answers
        /// false, which is the point: anything short of the contract is the
        /// elevation-every-launch state.
        /// </summary>
        internal static bool SddlCarriesAppContract(string? sdshowOutput) =>
            sdshowOutput is not null && sdshowOutput.Contains("CCSWRPRC;;;AU", StringComparison.Ordinal);

        /// <summary>Display name the MSI writes to Add/Remove Programs, and the shim file name.</summary>
        internal const string MsiDisplayName = "Uniwill Control Utility";
        internal const string ShimFileName = "Uninstall.exe";

        /// <summary>
        /// The layout's uninstall shim (<c>&lt;install root&gt;\Uninstall.exe</c>,
        /// compiled by <c>build\pack.ps1</c>), given the engine exe path it runs from
        /// (<c>&lt;install root&gt;\Service\</c>). Null in a dev checkout, which has no
        /// shim: the registration self-heal then has nothing to point ARP at.
        /// </summary>
        internal static string? UninstallShimPath(string engineExePath)
        {
            string? serviceDir = Path.GetDirectoryName(engineExePath);
            string? root = serviceDir is null ? null : Path.GetDirectoryName(serviceDir);
            if (root is null)
                return null;

            string shim = Path.Combine(root, ShimFileName);
            return File.Exists(shim) ? shim : null;
        }

        /// <summary>
        /// Points the Control Panel's uninstall entry at <c>Uninstall.exe</c>.
        ///
        /// The MSI's own entry runs <c>msiexec /x {GUID}</c>, which is incomplete for
        /// this product: the engine service is registered by the app, not by the MSI,
        /// so the MSI uninstall leaves the registration behind (stopped, pointing at
        /// the files it just deleted), and the per-user Velopack ARP entry survives as
        /// a husk in the program list. The shim is the complete route - it stops the
        /// service before killing the app (a force-killed app is a crash the engine's
        /// supervisor relaunches), runs the MSI uninstall synchronously, deletes the
        /// service and all three ARP entries, then the install root - and it is stamped
        /// requireAdministrator, so Control Panel elevation is already its own.
        ///
        /// Best effort by design: a failed redirect leaves the old partial route in
        /// place, which is what shipped before; the service install is not failed by it.
        /// </summary>
        internal static void RepairControlPanelUninstall(string engineExePath)
        {
            try
            {
                string? shim = UninstallShimPath(engineExePath);
                if (shim is null)
                {
                    Log.Debug("[EngineInstaller] No uninstall shim next to {Path}: dev layout, ARP left alone", engineExePath);
                    return;
                }

                string? productKey = FindMsiProductKey(MsiDisplayName);
                if (productKey is null)
                {
                    Log.Debug("[EngineInstaller] No MSI ARP entry for '{Name}': nothing to redirect", MsiDisplayName);
                    return;
                }

                using RegistryKey? uninstall = RegistryKey
                    .OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                    .OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", writable: true);
                using RegistryKey? entry = uninstall?.OpenSubKey(productKey, writable: true);
                if (entry is null)
                    return;

                // Quoted: the path contains spaces, and Control Panel hands the value
                // to ShellExecute, which splits an unquoted path with spaces.
                string command = "\"" + shim + "\"";
                entry.SetValue("UninstallString", command, RegistryValueKind.String);
                entry.SetValue("QuietUninstallString", command, RegistryValueKind.String);

                Log.Information("[EngineInstaller] Control Panel uninstall for {Product} now runs the shim {Shim}",
                    productKey, shim);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[EngineInstaller] Control Panel uninstall redirect skipped");
            }
        }

        /// <summary>
        /// The MSI product GUID whose Add/Remove Programs entry names <paramref name="displayName"/>.
        /// Same lookup the uninstall shim performs, for the same reason: the GUID is
        /// generated by the packer per version channel, so nothing hardcodes it.
        /// </summary>
        internal static string? FindMsiProductKey(string displayName)
        {
            using RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using RegistryKey? uninstall = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
            if (uninstall is null)
                return null;

            foreach (string name in uninstall.GetSubKeyNames())
            {
                // Product keys are the only children of this shape, and reading a
                // display name out of every key is what the shim does too.
                if (name.Length != 38 || name[0] != '{' || name[^1] != '}')
                    continue;

                try
                {
                    using RegistryKey? entry = uninstall.OpenSubKey(name);
                    if (entry?.GetValue("DisplayName") as string == displayName)
                        return name;
                }
                catch (Exception ex)
                {
                    Log.Debug(ex, "[EngineInstaller] Uninstall key {Key} unreadable during the product key scan", name);
                }
            }

            return null;
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

            // Removal routes launch this verb elevated (the app's
            // UninstallSentinel after a Control Panel / Settings msiexec /x,
            // the Uninstall.exe shim, the Velopack uninstall hook), so it also
            // sweeps everything the MSI's thin removal cannot: the running app
            // (it locks the layout), the scheduled tasks, every ARP husk that
            // names the product, the service's ProgramData log directory, and
            // the install layout itself (Velopack's Update.exe cannot be
            // launched from the MSI's uninstall context - documented dead
            // end - so the verb destroys the layout). The Uninstall.exe shim
            // shares this exact list, so all removal routes converge on the
            // same end state. All best effort: an uninstall never fails here.
            KillRunningApp();
            await RunCleanupCommandAsync(
                "schtasks.exe", "/Delete /TN UCU /F").ConfigureAwait(false);
            await RunCleanupCommandAsync(
                "schtasks.exe", "/Delete /TN \"UCU Autostart\" /F").ConfigureAwait(false);
            await RunCleanupCommandAsync(
                "schtasks.exe", "/Delete /TN \"UCU Guardian\" /F").ConfigureAwait(false);
            DeleteArpHusks();
            DeleteProgramDataLogs();
            DeleteInstallLayout();

            return rc;
        }

        /// <summary>
        /// Force-closes the app (and the retired guardian) so it stops locking
        /// the install layout. This process is the engine exe, a distinct image
        /// name, so no self-exclusion is needed.
        /// </summary>
        private static void KillRunningApp()
        {
            foreach (string appName in new[] { "Uniwill Control Utility", "UCU Guardian" })
            {
                foreach (Process running in Process.GetProcessesByName(appName))
                {
                    try
                    {
                        running.Kill();
                    }
                    catch (Exception ex)
                    {
                        Log.Debug(ex, "[EngineInstaller] {App} process {Pid} not killed", appName, running.Id);
                    }
                    finally
                    {
                        running.Dispose();
                    }
                }
            }
        }

        private static async Task RunCleanupCommandAsync(string fileName, string arguments)
        {
            try
            {
                var psi = new ProcessStartInfo(fileName, arguments)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };

                using Process process = Process.Start(psi)!;
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
                Log.Debug("[EngineInstaller] cleanup {File} {Args} exited {Code}",
                    fileName, arguments, process.ExitCode);
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "[EngineInstaller] cleanup {File} {Args} failed", fileName, arguments);
            }
        }

        /// <summary>
        /// Delete every Add/Remove Programs entry that names the product: the
        /// "MSI:Uniwill-Control-Utility" husk the registration pass leaves
        /// when the thin MSI removal unregisters the product, Velopack's GUID
        /// husk (its HKLM entry, plus the orphaned product-key entry when the
        /// MSI's display pass orphans it), and the legacy plain-name key.
        /// Matching is by DisplayName so no key name is hardcoded.
        /// </summary>
        private static void DeleteArpHusks()
        {
            const string UninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

            try
            {
                using RegistryKey localMachine =
                    RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                using RegistryKey? uninstall = localMachine.OpenSubKey(UninstallPath);
                if (uninstall == null)
                    return;

                // GetSubKeyNames snapshot first: deleting while enumerating.
                foreach (string keyName in uninstall.GetSubKeyNames())
                {
                    try
                    {
                        using (RegistryKey? entry = uninstall.OpenSubKey(keyName))
                        {
                            if (!string.Equals(entry?.GetValue("DisplayName") as string,
                                    "Uniwill Control Utility", StringComparison.OrdinalIgnoreCase))
                            {
                                continue;
                            }
                        }

                        uninstall.DeleteSubKeyTree(keyName, throwOnMissingSubKey: false);
                        Log.Debug("[EngineInstaller] ARP husk removed: {Key}", keyName);
                    }
                    catch (Exception ex)
                    {
                        Log.Debug(ex, "[EngineInstaller] ARP husk {Key} not removed", keyName);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "[EngineInstaller] ARP husks not swept");
            }
        }

        private static void DeleteProgramDataLogs()
        {
            try
            {
                string directory = Path.GetDirectoryName(EnginePaths.ServiceLogDirectory)
                    ?? EnginePaths.ServiceLogDirectory;
                if (Directory.Exists(directory))
                    Directory.Delete(directory, recursive: true);
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "[EngineInstaller] ProgramData log directory not removed");
            }
        }

        /// <summary>
        /// Destroy the install layout: the thin MSI removal deletes no files
        /// (the layout is deployed by the package's bootstrap custom action,
        /// so RemoveFiles sees almost nothing), and Velopack's Update.exe
        /// cannot be launched from the MSI's uninstall context (documented
        /// dead end), so the verb - the only route that runs elevated with
        /// full access - removes the root. This exe runs from
        /// &lt;root&gt;\current\Service\, so the root is two levels up; the
        /// Update.exe marker proves the layout is a real Velopack root before
        /// anything is deleted (source/dev layouts are never touched). The
        /// image files delete fine: Windows opens running exes and loaded
        /// assemblies for delete (the standard self-update pattern).
        /// </summary>
        private static void DeleteInstallLayout()
        {
            try
            {
                string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", ".."));
                if (!Directory.Exists(root) || !File.Exists(Path.Combine(root, "Update.exe")))
                    return;

                for (int attempt = 0; ; attempt++)
                {
                    try
                    {
                        Directory.Delete(root, recursive: true);
                        Log.Information("[EngineInstaller] Install layout removed: {Root}", root);
                        return;
                    }
                    catch (Exception ex) when (attempt < 2)
                    {
                        // The app's just-killed images stay locked for a
                        // moment while AV scans them; a retry clears them.
                        Log.Debug(ex, "[EngineInstaller] layout delete attempt {Attempt}", attempt + 1);
                        Thread.Sleep(1500);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "[EngineInstaller] install layout not removed");
            }
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

        /// <summary>
        /// Runs sc.exe and returns its exit code with standard output. The command
        /// line is verbatim (as typed in cmd): sc.exe's parser needs the exact
        /// key= value quoting, which ArgumentList's automatic escaping destroys.
        /// The values here are our own exe path and fixed strings, never user input.
        /// </summary>
        private static async Task<(int Code, string Output, string Error)> RunScFullAsync(params string[] args)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "sc.exe",
                Arguments = string.Join(" ", args),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            using var proc = Process.Start(psi);
            if (proc is null)
                return (-1, string.Empty, string.Empty);

            string output = await proc.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
            string error = await proc.StandardError.ReadToEndAsync().ConfigureAwait(false);
            await proc.WaitForExitAsync().ConfigureAwait(false);

            return (proc.ExitCode, output, error);
        }

        /// <summary>sc.exe standard output (the elevated pass reads sdshow through
        /// this to verify the DACL it just wrote actually committed).</summary>
        private static async Task<string> RunScCaptureAsync(params string[] args)
        {
            (int code, string output, string _) = await RunScFullAsync(args).ConfigureAwait(false);
            return output;
        }

        private static async Task<int> RunScAsync(params string[] args)
        {
            (int rc, string output, string error) = await RunScFullAsync(args).ConfigureAwait(false);
            if (rc != 0)
            {
                Log.Warning("[EngineInstaller] sc {Args} exited with {Code}: {Output} {Error}",
                    string.Join(' ', args), rc, output.Trim(), error.Trim());
            }

            return rc;
        }
    }
}
