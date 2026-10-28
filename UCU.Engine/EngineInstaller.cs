using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
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
            LogInvocationChain();

            string name = EngineHostFactory.ServiceName;

            int rc = await RunScAsync("stop", name).ConfigureAwait(false);
            if (rc != 0 && rc != ErrorServiceAlreadyStopped && rc != ErrorServiceDoesNotExist)
            {
                Log.Warning("[EngineInstaller] sc stop exited with {Code} (continuing to delete)", rc);
            }

            rc = await RunScAsync("delete", name).ConfigureAwait(false);
            if (rc != 0 && rc != ErrorServiceDoesNotExist)
            {
                // Policy-guarded machines park the delete pending: their
                // keep-running policy relaunches the service between stop
                // and delete, and sc.exe blocks behind the SCM. Killing the
                // live service image completes the pending delete, and a
                // service already marked for delete cannot be relaunched -
                // the policy loses. (This process runs the same image:
                // exclude our own pid.)
                KillEngineServiceProcess();
                await Task.Delay(2000).ConfigureAwait(false);
                rc = await RunScAsync("delete", name).ConfigureAwait(false);
            }

            Log.Information("[EngineInstaller] Engine service removed (rc {Code})", rc);

            // Removal routes launch this verb as SYSTEM (the app's
            // UninstallSentinel and the Uninstall.exe shim create a one-shot
            // /ru SYSTEM scheduled task for it; the Velopack uninstall hook
            // already runs as SYSTEM and spawns it directly). SYSTEM is the
            // requirement, not a luxury: guarded machines label the installed
            // layout System-IL (an elevated admin cannot delete the files)
            // and the engine service's re-secured DACL grants full control
            // only to SY/BA. The verb sweeps everything the MSI's thin
            // removal cannot: the running app (it locks the layout), the
            // scheduled tasks, every ARP husk that names the product
            // (machine, HKCU, and every loaded user hive), the stale
            // per-user MSI registrations old broken uninstalls leave, the
            // service's ProgramData log directory, and the install layout
            // itself (Velopack's Update.exe cannot be launched from the
            // MSI's uninstall context - documented dead end). The
            // Uninstall.exe shim shares this list, so all removal routes
            // converge on the same end state. All best effort: an uninstall
            // never fails here.
            KillRunningApp();
            await RunCleanupCommandAsync(
                "schtasks.exe", "/Delete /TN UCU /F").ConfigureAwait(false);
            await RunCleanupCommandAsync(
                "schtasks.exe", "/Delete /TN \"UCU Autostart\" /F").ConfigureAwait(false);
            await RunCleanupCommandAsync(
                "schtasks.exe", "/Delete /TN \"UCU Guardian\" /F").ConfigureAwait(false);
            await RunCleanupCommandAsync(
                "schtasks.exe", "/Delete /TN \"UCU Uninstall Verb\" /F").ConfigureAwait(false);
            DeleteArpHusks(RegistryHive.LocalMachine);
            DeleteArpHusks(RegistryHive.CurrentUser);
            DeleteUserHiveArpHusks();
            await SweepStaleUserRegistrations().ConfigureAwait(false);
            // The registration sweep runs "msiexec /x" passes, and a Velopack MSI
            // uninstall re-creates its shim ARP key ("MSI:..." with our DisplayName,
            // UninstallString pointing at the product it just removed) - a husk
            // Control Panel cannot remove (the cached package is gone). Sweep ARP
            // again after the /x passes, as the final word on the registration.
            DeleteArpHusks(RegistryHive.LocalMachine);
            DeleteArpHusks(RegistryHive.CurrentUser);
            DeleteUserHiveArpHusks();
            DeleteProgramDataLogs();
            DeleteUserVelopackRoots();
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

        /// <summary>
        /// Force-close the running engine service process so a parked
        /// (pending) service delete completes. This verb runs the same
        /// image, so exclude our own pid.
        /// </summary>
        private static void KillEngineServiceProcess()
        {
            foreach (Process running in
                Process.GetProcessesByName("Uniwill Control Utility Engine"))
            {
                try
                {
                    if (running.Id != Environment.ProcessId)
                        running.Kill();
                }
                catch (Exception ex)
                {
                    Log.Debug(ex, "[EngineInstaller] engine service process {Pid} not killed", running.Id);
                }
                finally
                {
                    running.Dispose();
                }
            }
        }

        private static async Task RunCleanupCommandAsync(
            string fileName, string arguments, TimeSpan? timeout = null)
        {
            try
            {
                var psi = new ProcessStartInfo(fileName, arguments)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };

                using Process process = Process.Start(psi)!;
                using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(15));
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
        /// MSI's display pass orphans it), the per-user shim key, and the
        /// legacy plain-name key. Matching is by DisplayName so no key name
        /// is hardcoded.
        /// </summary>
        private static void DeleteArpHusks(RegistryHive hive)
        {
            const string UninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

            try
            {
                using RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
                // Writable open: a default OpenSubKey is a read handle, and
                // DeleteSubKeyTree on it throws "Cannot write to the registry
                // key" - which, logged only at debug level, made this sweep
                // silently delete nothing (field-proven in the verb log).
                using RegistryKey? uninstall = baseKey.OpenSubKey(UninstallPath, writable: true);
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
                        Log.Debug("[EngineInstaller] ARP husk removed: {Hive}\\{Key}", hive, keyName);
                    }
                    catch (Exception ex)
                    {
                        Log.Debug(ex, "[EngineInstaller] ARP husk {Hive}\\{Key} not removed", hive, keyName);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "[EngineInstaller] ARP husks ({Hive}) not swept", hive);
            }
        }

        /// <summary>
        /// Remove the stale per-user MSI registrations old broken uninstalls
        /// leave behind (the thin package unregisters the machine context;
        /// the user-context assignment survives, and every surviving
        /// registration is a Control Panel husk entry naming the product).
        /// The key surgery has two passes: the MSI's own removal where its
        /// cached package still resolves (msiexec /x), and for the husks
        /// whose package is gone, the registration keys themselves -
        /// HKLM\SOFTWARE\Classes\Installer\Products (both views), the
        /// 64-bit-resolved HKCR\Installer\Products key (Wow6432Node alias),
        /// and HKCU\Installer\Products: a full user removal is a complete
        /// removal. Machine-scope entries are skipped: the /x transaction
        /// that triggered this verb owns those.
        /// </summary>
        private static async Task SweepStaleUserRegistrations()
        {
            var codes = new List<string>();
            var product = new StringBuilder(39);
            for (int index = 0; index < 4096; index++)
            {
                if (MsiEnumProductsW(index, product) != 0)
                    break;

                var name = new StringBuilder(256);
                uint nameLength = 256;
                if (MsiGetProductInfoW(product.ToString(), "ProductName", name, ref nameLength) == 0 &&
                    string.Equals(name.ToString(), "Uniwill Control Utility", StringComparison.OrdinalIgnoreCase))
                {
                    codes.Add(product.ToString());
                }

                product.Clear();
            }

            foreach (string code in codes)
            {
                // The registration of the install this verb process is running
                // from is not stale: the sentinel design fires the verb only
                // after the registration is gone, and the Velopack hook path
                // fires it while the install is live (a ghost's /x transaction
                // mid-flight, the hook spawns the verb). Removing the live
                // registration from the hook path is self-destruction.
                var location = new StringBuilder(260);
                uint locationLength = 260;
                if (MsiGetProductInfoW(code, "InstalledLocation", location, ref locationLength) == 0 &&
                    string.Equals(location.ToString().TrimEnd('\\'), InstallLayoutRoot,
                        StringComparison.OrdinalIgnoreCase))
                {
                    Log.Debug("[EngineInstaller] registration {Code} is the live install - left to its own uninstall", code);
                    continue;
                }

                Log.Debug("[EngineInstaller] sweeping stale registration {Code}", code);
                await RunCleanupCommandAsync(
                    "msiexec.exe", "/x " + code + " /qn", TimeSpan.FromSeconds(60)).ConfigureAwait(false);
                RemoveRegistrationKeys(code);
            }
        }

        /// <summary>
        /// The install layout root this engine image lives in
        /// (the verb runs from &lt;root&gt;\current\Service).
        /// </summary>
        private static string InstallLayoutRoot =>
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", ".."));

        private static void RemoveRegistrationKeys(string productCode)
        {
            string compact = productCode.Trim('{', '}').Replace("-", "");

            // MSI stores registration keys under the COMPRESSED product code
            // (first three hex groups reversed, last eight bytes nibble-swapped)
            // - field-proven in the UserData records. Deleting the raw compact
            // form only (the first implementation) never matched a key, so the
            // ghosts survived every sweep and Velopack's startup self-repair
            // kept /x-ing them (their UninstallHookDeferred is what fires this
            // verb - the ghosts are the loop).
            string compressed = CompressProductCode(productCode);

            var hives = new[]
            {
                (RegistryHive.LocalMachine, RegistryView.Registry64),
                (RegistryHive.LocalMachine, RegistryView.Registry32),
                (RegistryHive.CurrentUser, RegistryView.Registry64),
                (RegistryHive.CurrentUser, RegistryView.Registry32),
            };

            foreach (var (hive, view) in hives)
            {
                DeleteInstallerProductKeys(hive, view, "SOFTWARE\\Classes\\Installer\\Products", compressed);
                DeleteInstallerProductKeys(hive, view, "SOFTWARE\\Classes\\Installer\\Products", compact);
                DeleteInstallerProductKeys(hive, view, "SOFTWARE\\Microsoft\\Installer\\Products", compressed);
                DeleteInstallerProductKeys(hive, view, "SOFTWARE\\Microsoft\\Installer\\Products", compact);
            }

            // HKCR\Installer\Products is the 64-bit resolved view of the
            // machine registration (HKLM\SOFTWARE\Classes\Installer\... under
            // Wow6432Node); delete it from the 64-bit base view.
            DeleteInstallerProductKeys(
                RegistryHive.LocalMachine, RegistryView.Registry64, @"Classes\Installer\Products", compressed);
            DeleteInstallerProductKeys(
                RegistryHive.LocalMachine, RegistryView.Registry64, @"Classes\Installer\Products", compact);

            // The per-user-context product records live under
            // HKLM\...\Installer\UserData\<SID>\Products\<compressed> - these
            // are what MsiEnumProductsW enumerates. The Classes-key surgery
            // never touched them (field-proven: the S-1-5-18 records survived
            // a full verb sweep and kept firing Velopack's repair /x passes).
            try
            {
                using RegistryKey baseKey = RegistryKey.OpenBaseKey(
                    RegistryHive.LocalMachine, RegistryView.Registry64);
                using RegistryKey? userData = baseKey.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\UserData", writable: true);
                if (userData != null)
                {
                    foreach (string sid in userData.GetSubKeyNames())
                    {
                        try
                        {
                            using RegistryKey? products = userData.OpenSubKey(sid + "\\Products", writable: true);
                            products?.DeleteSubKeyTree(compressed, throwOnMissingSubKey: false);
                            products?.DeleteSubKeyTree(compact, throwOnMissingSubKey: false);
                        }
                        catch (Exception ex)
                        {
                            Log.Debug(ex, "[EngineInstaller] UserData record {Sid}\\{Code} not removed", sid, compressed);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "[EngineInstaller] UserData products root not opened");
            }
        }

        /// <summary>
        /// MSI's registration key form of a product GUID: the 8-, 4- and
        /// 4-character hex groups reversed, the final eight bytes with their
        /// nibbles swapped (21ABECE1-A692-4EE2-83B7-... → 1ECEBA12296A2EE4387B...).
        /// </summary>
        internal static string CompressProductCode(string guid)
        {
            string[] parts = guid.Trim('{', '}').Split('-');
            if (parts.Length != 5)
                return guid.Trim('{', '}').Replace("-", "");

            char[] head = (ReverseGroup(parts[0]) + ReverseGroup(parts[1]) + ReverseGroup(parts[2])).ToCharArray();
            var tail = new StringBuilder(16);
            string rest = parts[3] + parts[4];
            for (int i = 0; i + 1 < rest.Length; i += 2)
            {
                tail.Append(rest[i + 1]);
                tail.Append(rest[i]);
            }
            return new string(head) + tail;
        }

        private static string ReverseGroup(string hex)
        {
            char[] chars = hex.ToCharArray();
            Array.Reverse(chars);
            return new string(chars);
        }

        private static void DeleteInstallerProductKeys(
            RegistryHive hive, RegistryView view, string productsPath, string compactCode)
        {
            foreach (string suffix in new[] { "", "x", "X" })
            {
                string key = productsPath + "\\" + compactCode + suffix;
                try
                {
                    using RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, view);
                    baseKey.DeleteSubKeyTree(key, throwOnMissingSubKey: false);
                }
                catch (Exception ex)
                {
                    Log.Debug(ex, "[EngineInstaller] registration key {Hive}\\{View}\\{Key} not removed",
                        hive, view, key);
                }
            }
        }

        /// <summary>
        /// Verb forensics (field-proven need): on a dev box carrying stale
        /// per-user ghost registrations, the verb fired ~15 s after an ordinary
        /// elevated app launch and swept the live install - and no route in the
        /// source explained it (hook no-op, sentinel unarmed, shim task absent).
        /// The verb therefore logs its launcher: the parent pid resolved via
        /// ntdll (no WMI dependency), its image name and start time. The parent
        /// is captured at verb entry, while it is alive.
        /// </summary>
        private static void LogInvocationChain()
        {
            try
            {
                int parentPid = 0;
                try
                {
                    var status = NtQueryInformationProcess(
                        System.Diagnostics.Process.GetCurrentProcess().Handle,
                        0, out PROCESS_BASIC_INFORMATION pbi,
                        System.Runtime.InteropServices.Marshal.SizeOf<PROCESS_BASIC_INFORMATION>(),
                        out _);
                    if (status == 0)
                        parentPid = pbi.InheritedFromUniqueProcessId.ToInt32();
                }
                catch
                {
                    // Parent line is the optional half.
                }

                string parent = parentPid == 0 ? "unknown" : DescribeProcess(parentPid);
                string self = DescribeProcess(System.Environment.ProcessId);
                Log.Information(
                    "[EngineInstaller] verb invoked: self {Self}, parent {Parent}, args {Args}",
                    self, parent, string.Join(" ", Environment.GetCommandLineArgs()));
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "[EngineInstaller] invocation chain probe failed");
            }
        }

        private static string DescribeProcess(int pid)
        {
            try
            {
                using var process = System.Diagnostics.Process.GetProcessById(pid);
                string name = string.Empty;
                try { name = process.ProcessName; } catch { }
                string started = string.Empty;
                try { started = ", started " + process.StartTime.ToString("HH:mm:ss"); } catch { }
                return pid + " '" + name + "'" + started;
            }
            catch
            {
                return pid + " (gone)";
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_BASIC_INFORMATION
        {
            public IntPtr Reserved1;
            public IntPtr PebBaseAddress;
            public IntPtr Reserved2_0;
            public IntPtr Reserved2_1;
            public IntPtr UniqueProcessId;
            public IntPtr InheritedFromUniqueProcessId;
        }

        [DllImport("ntdll.dll")]
        private static extern int NtQueryInformationProcess(
            IntPtr processHandle, int processInformationClass,
            out PROCESS_BASIC_INFORMATION processInformation,
            int processInformationLength, out int returnLength);

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
        /// Remove the per-user Velopack root (AppData\Local\Uniwill-Control-Utility)
        /// from every profile on the machine. The ghost root is the fuel of
        /// Velopack's registration-repair /x storm (field-proven: a GUI install
        /// recreated it, the app's UpdateManager /x-ed the live registrations
        /// four minutes later, and the uninstall sentinel - contractually
        /// trusting vanished registrations plus genuine removal events - ran
        /// the sweep under a running app). An uninstall that leaves the roots
        /// behind re-poisons the next install for every user of this machine,
        /// so the sweep enumerates ProfileList and deletes each profile's root;
        /// SYSTEM reaches every hive. Best effort: a locked profile directory
        /// is a cosmetic leftover, never a sweep failure.
        /// </summary>
        private static void DeleteUserVelopackRoots()
        {
            try
            {
                using RegistryKey baseKey = RegistryKey.OpenBaseKey(
                    RegistryHive.LocalMachine, RegistryView.Registry64);
                using RegistryKey? profileList = baseKey.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList");
                if (profileList == null)
                    return;

                foreach (string sid in profileList.GetSubKeyNames())
                {
                    try
                    {
                        using RegistryKey? sidKey = profileList.OpenSubKey(sid);
                        string? profilePath = sidKey?.GetValue("ProfileImagePath") as string;
                        if (string.IsNullOrEmpty(profilePath) ||
                            profilePath!.Contains('%', StringComparison.Ordinal))
                        {
                            // Expansion-dependent defaults (e.g. %SystemRoot%)
                            // are not real user profiles on this machine.
                            continue;
                        }

                        string root = Path.Combine(profilePath,
                            "AppData", "Local", "Uniwill-Control-Utility");
                        if (!Directory.Exists(root))
                            continue;

                        Directory.Delete(root, recursive: true);
                        Log.Debug("[EngineInstaller] per-user Velopack root removed: {Root}", root);
                    }
                    catch (Exception ex)
                    {
                        Log.Debug(ex, "[EngineInstaller] per-user Velopack root not removed for {Sid}", sid);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "[EngineInstaller] ProfileList not opened for the Velopack root sweep");
            }
        }

        /// <summary>
        /// Destroy the install layout: the thin MSI removal deletes no files
        /// (the layout is deployed by the package's bootstrap custom action,
        /// invisible to MSI file records), and Velopack's Update.exe cannot
        /// be launched from the MSI's uninstall context (documented dead
        /// end), so the verb - the only route with SYSTEM identity, which
        /// guarded machines require: their policy labels the installed
        /// layout System-IL and an elevated admin gets Access Denied -
        /// removes the root. This exe runs from &lt;root&gt;\current\Service\,
        /// so the root is two levels up; the Update.exe marker proves the
        /// layout is a real Velopack root before anything is deleted
        /// (source/dev layouts are never touched). Everything outside the
        /// verb's own Service directory deletes in-proc (running exes and
        /// loaded assemblies open for delete, so the verb image itself
        /// deletes-pending); a detached tail re-issues the root delete after
        /// this process exits and releases the last image.
        /// </summary>
        private static void DeleteInstallLayout()
        {
            try
            {
                string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", ".."));
                if (!Directory.Exists(root) || !File.Exists(Path.Combine(root, "Update.exe")))
                    return;

                string selfDirectory = Path.GetFullPath(AppContext.BaseDirectory);
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    try
                    {
                        foreach (string directory in Directory.EnumerateDirectories(root))
                        {
                            if (string.Equals(
                                    Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)),
                                    Path.TrimEndingDirectorySeparator(selfDirectory),
                                    StringComparison.OrdinalIgnoreCase))
                            {
                                // The verb's own image directory: deletes with
                                // the process, via the tail.
                                continue;
                            }

                            try
                            {
                                Directory.Delete(directory, recursive: true);
                            }
                            catch (Exception ex)
                            {
                                Log.Debug(ex, "[EngineInstaller] layout directory {Dir} not removed", directory);
                            }
                        }

                        foreach (string file in Directory.EnumerateFiles(root))
                            File.Delete(file);

                        break;
                    }
                    catch (Exception ex)
                    {
                        // The app's just-killed images stay locked for a
                        // moment while AV scans them; retries clear them.
                        Log.Debug(ex, "[EngineInstaller] layout delete attempt {Attempt}", attempt + 1);
                        Thread.Sleep(1500);
                    }
                }

                SpawnLayoutTail(root);
                Log.Information("[EngineInstaller] Install layout destroyed (tail completes): {Root}", root);
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "[EngineInstaller] install layout not removed");
            }
        }

        /// <summary>
        /// Hidden cmd that retries the root delete after this verb exits
        /// (its own image is the last lock). Self-cleans: the tail deletes
        /// its own script file.
        /// </summary>
        private static void SpawnLayoutTail(string root)
        {
            try
            {
                string script = Path.Combine(Path.GetTempPath(), "ucu-verb-tail.cmd");
                File.WriteAllText(script,
                    "@echo off\r\n" +
                    "ping -n 6 127.0.0.1 >nul\r\n" +
                    "rd /s /q \"" + root + "\"\r\n" +
                    "ping -n 4 127.0.0.1 >nul\r\n" +
                    "rd /s /q \"" + root + "\"\r\n" +
                    "del \"%~f0\"\r\n");

                Process.Start(new ProcessStartInfo("cmd.exe", "/c \"" + script + "\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = Environment.SystemDirectory,
                });
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "[EngineInstaller] layout tail not spawned");
            }
        }

        /// <summary>
        /// Sweep the ARP husks from every loaded user hive: the verb runs as
        /// SYSTEM for the sentinel / scheduled-task route, where the per-user
        /// husk keys (the velopack shim entry among them) live in the users'
        /// hives, not in SYSTEM's HKCU.
        /// </summary>
        private static void DeleteUserHiveArpHusks()
        {
            const string UninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

            try
            {
                using RegistryKey users = RegistryKey.OpenBaseKey(RegistryHive.Users, RegistryView.Registry64);
                foreach (string sid in users.GetSubKeyNames())
                {
                    // _Classes merges the user's HKCU classes; the uninstall
                    // keys never live there.
                    if (sid.StartsWith("_", StringComparison.Ordinal))
                        continue;

                    try
                    {
                        using RegistryKey? hive = users.OpenSubKey(sid);
                        using RegistryKey? uninstall = hive?.OpenSubKey(UninstallPath, writable: true);
                        if (uninstall == null)
                            continue;

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
                            }
                            catch (Exception ex)
                            {
                                Log.Debug(ex, "[EngineInstaller] user hive husk {Sid}\\{Key} not removed", sid, keyName);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Debug(ex, "[EngineInstaller] user hive {Sid} not swept", sid);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "[EngineInstaller] user hives not swept");
            }
        }

        [DllImport("msi.dll", CharSet = CharSet.Unicode, EntryPoint = "MsiEnumProductsW")]
        private static extern int MsiEnumProductsW(int index, StringBuilder productCode);

        [DllImport("msi.dll", CharSet = CharSet.Unicode, EntryPoint = "MsiGetProductInfoW")]
        private static extern int MsiGetProductInfoW(
            string productCode, string attribute, StringBuilder value, ref uint valueLength);

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

            // The SCM server call behind sc delete can block indefinitely on
            // machines whose policy keeps services running (the delete parks
            // pending while a policy restart races us); sc.exe then never
            // closes its stdout. A dead verb is worse than a slow one, so the
            // read is capped and the process killed at the cap (the caller
            // sees rc -1 and takes the force path).
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));

            string output;
            string error;
            bool timedOut = false;
            try
            {
                output = await proc.StandardOutput.ReadToEndAsync(cts.Token).ConfigureAwait(false);
                error = await proc.StandardError.ReadToEndAsync(cts.Token).ConfigureAwait(false);
                await proc.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                timedOut = true;
                output = string.Empty;
                error = "sc.exe timed out";
            }

            if (timedOut)
            {
                try
                {
                    proc.Kill(entireProcessTree: true);
                }
                catch
                {
                    // Already gone.
                }
                return (-1, output, error);
            }

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
