using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace Uniwill_Control_Utility
{
    /// <summary>
    /// Self-heal for the Control Panel / Settings uninstall route.
    ///
    /// The Control Panel and Settings run the product's MSI by product code,
    /// and vpk's generated MSI is a thin bootstrap: its RemoveFiles sees
    /// almost nothing (the layout is deployed by the package's own bootstrap
    /// custom action, not MSI file records), so the removal unregisters the
    /// product in ~2 s and leaves the engine service, the running app, the
    /// layout, the tasks, and the ARP husk behind. Injecting CustomActions
    /// that launch the installed exes at uninstall is a proven dead end in
    /// this package: every exe-CustomAction encoding fails to CreateProcess
    /// a .NET apphost from the msiserver service session (1721 empty source
    /// location / process-creation failures), and Velopack's own uninstall
    /// hook is a per-user-temp design that no-ops in the SYSTEM execute
    /// context of a per-machine install.
    ///
    /// What is guaranteed present during every such uninstall is the app
    /// itself: the removal never kills it, so it outlives the transaction.
    /// This sentinel watches the one fact the thin uninstall does change -
    /// the machine's Add/Remove Programs registration for the product (the
    /// HKLM GUID entry whose UninstallString is the MsiExec call the Control
    /// Panel is running; watching MSI product enumeration instead is
    /// poisoned by stale per-user registrations old broken uninstalls
    /// leave) - and when it vanishes, the app self-executes the engine's
    /// elevated --uninstall-engine verb: the single full-cleanup entry point
    /// shared with the Uninstall.exe shim (service stop+delete, app kill,
    /// tasks, ARP husks, stale per-user MSI registrations, ProgramData logs,
    /// install layout). One UAC prompt at uninstall, the same consent the
    /// shim route asks for, and every removal route converges on the same
    /// end state.
    /// </summary>
    internal sealed class UninstallSentinel
    {
        /// <summary>The ARP DisplayName vpk's generated package registers.</summary>
        internal const string ProductName = "Uniwill Control Utility";

        /// <summary>
        /// Consecutive polls without the registration before acting. The /x
        /// transaction is over in ~2 s; two misses debounce a transient
        /// registry failure, at a cost of ~10 s of detection latency.
        /// </summary>
        internal const int MissesToFire = 2;

        private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(5);
        private static readonly Regex GuidKey = new(@"^\{[0-9A-Fa-f-]+\}$", RegexOptions.Compiled);

        private readonly Func<string[]> _enumerateRegistrations;
        private readonly Func<bool> _installedLayoutProbe;
        private readonly Action _onUninstallDetected;
        private readonly Action<string> _log;
        private readonly TimeSpan _pollInterval;

        private int _misses;
        private bool _armed;
        private bool _fire;

        /// <summary>Test seam: inject the probe and the reaction.</summary>
        internal UninstallSentinel(
            Func<string[]> enumerateRegistrations,
            Action onUninstallDetected,
            Action<string> log,
            TimeSpan? pollInterval = null,
            Func<bool>? installedLayoutProbe = null)
        {
            _enumerateRegistrations = enumerateRegistrations;
            _installedLayoutProbe = installedLayoutProbe ?? NeverInstalled;
            _onUninstallDetected = onUninstallDetected;
            _log = log;
            _pollInterval = pollInterval ?? DefaultPollInterval;
        }

        /// <summary>
        /// Production wiring: watch the real machine ARP registration, run
        /// the elevated engine verb, log through the app logger.
        /// </summary>
        public static UninstallSentinel CreateDefault(Action<string> log) =>
            new(EnumerateMachineRegistrations, RunEngineUninstallVerb, log,
                pollInterval: null, installedLayoutProbe: InstalledLayoutPresent);

        private static bool NeverInstalled() => false;

        /// <summary>
        /// Watch in the background for the lifetime of the process.
        /// </summary>
        public void Start() => _ = Task.Run(RunAsync);

        internal async Task RunAsync()
        {
            while (true)
            {
                bool registrationFound;
                bool installed = false;
                try
                {
                    registrationFound = Registered(_enumerateRegistrations());

                    // Arming signal for the race the thin uninstall wins:
                    // the app needs ~2 minutes of startup on a cold machine,
                    // a /x transaction 2.3 s - the registration can be gone
                    // before the first poll, and a never-armed sentinel
                    // would stay inert forever. Running from the installed
                    // layout with the engine service registered is the same
                    // "installed" fact; dev/portable layouts arm nothing.
                    installed = _installedLayoutProbe();
                }
                catch (Exception ex)
                {
                    // A probe failure says nothing about the registration;
                    // treat it as present so a broken probe never triggers a
                    // self-uninstall. The engine verb is destructive.
                    _log("uninstall sentinel: registration probe failed, treating as present: " + ex.Message);
                    registrationFound = true;
                }

                bool fire = Step(registrationFound, installed);
                if (fire)
                {
                    _log("uninstall sentinel: machine registration gone, running the elevated engine uninstall verb");
                    try
                    {
                        _onUninstallDetected();
                    }
                    catch (Exception ex)
                    {
                        // Consent denied or spawn failed: the app keeps
                        // running with no registration. The Control Panel
                        // entry is already gone; the per-user shim entry that
                        // SelfHealControlPanelUninstall maintains keeps the
                        // complete shim route reachable, so nothing is lost.
                        _log("uninstall sentinel: verb launch failed (" + ex.Message + "), standing down");
                    }
                    return;
                }

                await Task.Delay(_pollInterval).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// State machine: the fire condition is the registration - it arms
        /// the sentinel when seen (a dev/portable run has none from the
        /// start, so an un-armed sentinel stays inert forever; the installed
        /// layout with the engine service is the same installed fact for the
        /// race where a fast /x removes the registration before the slow
        /// startup arms), and it fires once it has been missing for
        /// <see cref="MissesToFire"/> consecutive polls. A present
        /// registration resets the miss count.
        /// </summary>
        internal bool Step(bool registrationFound, bool installedSignal = false)
        {
            if (registrationFound)
            {
                _armed = true;
                _misses = 0;
                _fire = false;
                return false;
            }

            if (!_armed)
            {
                if (!installedSignal)
                    return false;

                // Arming poll: the registration is already missing, count it.
                _armed = true;
            }

            _misses++;
            _fire = _misses >= MissesToFire;
            return _fire;
        }

        internal static bool Registered(string[] registrationNames) =>
            registrationNames.Any(name => ProductName.Equals(name, StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// The DisplayName of every HKLM (64-bit view) ARP entry that is a
        /// live MSI machine registration for the product: GUID-format key,
        /// our DisplayName, and the MsiExec UninstallString the Control Panel
        /// invokes. Velopack's shim husk (UninstallString=Uninstall.exe) and
        /// the old broken-uninstall husks never match the MsiExec rule, so
        /// husks do not keep the sentinel armed, and per-user registrations
        /// live in other hives, so old ones cannot poison the probe.
        /// </summary>
        internal static string[] EnumerateMachineRegistrations()
        {
            using RegistryKey localMachine =
                RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using RegistryKey? uninstall = localMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
            if (uninstall == null)
                return Array.Empty<string>();

            var names = new List<string>();
            foreach (string keyName in uninstall.GetSubKeyNames())
            {
                if (!GuidKey.IsMatch(keyName))
                    continue;

                using RegistryKey? entry = uninstall.OpenSubKey(keyName);
                if (entry?.GetValue("DisplayName") is not string displayName)
                    continue;

                if (!string.Equals(displayName, ProductName, StringComparison.OrdinalIgnoreCase))
                    continue;

                string uninstallString = entry.GetValue("UninstallString") as string ?? "";
                if (uninstallString.Contains("MsiExec", StringComparison.OrdinalIgnoreCase))
                    names.Add(displayName);
            }

            return names.ToArray();
        }

        /// <summary>
        /// The app is running from the installed machine layout with the
        /// engine service registered: the "installed" fact for arming, even
        /// when a fast /x already removed the ARP registration before the
        /// (slow) startup got here. Portable layouts (no Program Files root,
        /// no service) and dev runs never arm.
        /// </summary>
        internal static bool InstalledLayoutPresent()
        {
            string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", ".."));
            if (!File.Exists(Path.Combine(root, "Update.exe")))
                return false;

            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            if (!root.StartsWith(programFiles, StringComparison.OrdinalIgnoreCase))
                return false;

            using RegistryKey localMachine =
                RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using RegistryKey? service = localMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Services\UCUEngine");
            return service != null;
        }

        /// <summary>
        /// Run the engine verb as SYSTEM: create a one-shot /ru SYSTEM
        /// scheduled task for it, run it, drop the task definition. SYSTEM
        /// is required, not a luxury: guarded machines label the installed
        /// layout System-IL, so even an elevated admin cannot delete the
        /// files (field-proven: Access Denied on every layout file as
        /// elevated BA). The launcher cmd runs elevated (the one UAC consent
        /// of the uninstall, the same consent the shim route asks); the task
        /// needs no interactive session, so the verb survives this app's
        /// exit, and the verb sweeps everything - including the task.
        ///
        /// The quoting is by-the-book because both layers parse quotes their
        /// own way: schtasks /tr values cannot carry \" escapes (cmd does
        /// not honor them - the value split at the first space in the field
        /// test), so the task command is cmd.exe plus the 8.3 short path of
        /// a script file (short paths have no spaces, so nothing needs
        /// quoting at the task layer), and that script carries the real
        /// quoted command line, which cmd parses correctly per line.
        /// </summary>
        private static void RunEngineUninstallVerb()
        {
            string engine = Path.Combine(
                AppContext.BaseDirectory, "Service", "Uniwill Control Utility Engine.exe");
            if (!File.Exists(engine))
                throw new FileNotFoundException("engine exe not found", engine);

            string verbScript = Path.Combine(Path.GetTempPath(), "ucu-verb-task.cmd");
            File.WriteAllText(verbScript,
                "@echo off\r\n" +
                "\"" + engine + "\" --uninstall-engine\r\n" +
                "del \"%~f0\"\r\n");

            string launcher = Path.Combine(Path.GetTempPath(), "ucu-sentinel-verb.cmd");
            File.WriteAllText(launcher,
                "@echo off\r\n" +
                "schtasks /create /tn \"UCU Uninstall Verb\" /tr \"cmd.exe /c " +
                    ShortPath(verbScript) + "\" /sc once /st 23:59 /ru SYSTEM /f\r\n" +
                "schtasks /run /tn \"UCU Uninstall Verb\"\r\n" +
                "schtasks /delete /tn \"UCU Uninstall Verb\" /f\r\n" +
                "del \"%~f0\"\r\n");

            // The launcher outlives this process (no job object binds it);
            // a hidden elevated cmd keeps the desktop clean apart from the
            // single consent prompt.
            var psi = new ProcessStartInfo("cmd.exe", "/c \"" + launcher + "\"")
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
                // Not the layout: the verb deletes the install root, and
                // Windows refuses to delete a live process's CWD.
                WorkingDirectory = Environment.SystemDirectory,
            };
            Process.Start(psi);
            Environment.Exit(0);
        }

        /// <summary>
        /// The 8.3 short path of <paramref name="path"/> (space-free, so the
        /// scheduled task's Target can reference it without quoting), or the
        /// original when 8.3 generation is disabled on the volume.
        /// </summary>
        private static string ShortPath(string path)
        {
            var buffer = new StringBuilder(260);
            int length = GetShortPathNameW(path, buffer, buffer.Capacity);
            return length > 0 && length < buffer.Capacity ? buffer.ToString() : path;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int GetShortPathNameW(string path, StringBuilder shortPath, int capacity);
    }
}
