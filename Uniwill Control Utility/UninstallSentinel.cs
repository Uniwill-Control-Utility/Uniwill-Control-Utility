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
    /// install layout). The vanish alone is not trusted: AV registry
    /// protection (field-proven: Emsisoft) can hide the live registration
    /// from the app's own view, so firing also requires the MsiInstaller
    /// "Removal completed successfully" event - a machine-wide trace only
    /// the Windows Installer service writes. One UAC prompt at uninstall,
    /// the same consent the shim route asks for, and every removal route
    /// converges on the same end state.
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
        private readonly Func<string[]> _enumerateCodes;
        private readonly Func<bool> _installedLayoutProbe;
        private readonly Func<string?, bool> _removalEvidence;
        private readonly Action _onUninstallDetected;
        private readonly Action<string> _log;
        private readonly TimeSpan _pollInterval;

        private int _misses;
        private bool _armed;
        private bool _fire;
        private bool _noEvidenceLogged;
        private string? _lastSeenCode;

        /// <summary>Test seam: inject the probe and the reaction.</summary>
        internal UninstallSentinel(
            Func<string[]> enumerateRegistrations,
            Action onUninstallDetected,
            Action<string> log,
            TimeSpan? pollInterval = null,
            Func<bool>? installedLayoutProbe = null,
            Func<string?, bool>? removalEvidence = null,
            Func<string[]>? enumerateCodes = null)
        {
            _enumerateRegistrations = enumerateRegistrations;
            _enumerateCodes = enumerateCodes ?? EnumerateMachineRegistrationCodes;
            _installedLayoutProbe = installedLayoutProbe ?? NeverInstalled;
            _removalEvidence = removalEvidence ?? MsiRemovalEvidence;
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
                    if (registrationFound)
                    {
                        // Remember the product code while it is visible: the
                        // removal evidence must name this exact transaction.
                        string[] codes = _enumerateCodes();
                        if (codes.Length > 0)
                            _lastSeenCode = codes[0];
                    }

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
                    // A vanished ARP registration in this process's registry
                    // view is not sufficient for the destructive verb: an AV
                    // with ARP-key protection (field-proven: Emsisoft) makes
                    // the fresh registration invisible to the app while
                    // msiserver and SYSTEM processes see it normally - the
                    // phantom miss repeats every poll and the sentinel would
                    // self-delete a live install on every launch. Real
                    // removals leave a machine-wide trace: the MsiInstaller
                    // "Removal completed successfully" event. Evidence
                    // required, phantom ignored.
                    if (!_removalEvidence(_lastSeenCode))
                    {
                        if (!_noEvidenceLogged)
                        {
                            _noEvidenceLogged = true;
                            _log("uninstall sentinel: registration gone in this process's view but no MSI removal " +
                                 "transaction in the event log - not treating as an uninstall " +
                                 "(AV registry protection?), staying armed");
                        }
                        await Task.Delay(_pollInterval).ConfigureAwait(false);
                        continue;
                    }

                    _log("uninstall sentinel: machine registration gone and the MSI removal transaction " +
                         "confirmed in the event log, running the elevated engine uninstall verb");
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
        internal static string[] EnumerateMachineRegistrations() =>
            MachineRegistrations().Select(pair => pair.DisplayName).ToArray();

        /// <summary>
        /// The product codes (ARP GUID key names) of the live machine
        /// registrations, same rule as EnumerateMachineRegistrations: the
        /// sentinel remembers the code while the registration is visible, and
        /// the removal evidence must name that same code - a previous
        /// uninstall's event must never license the verb for a fresh install.
        /// </summary>
        internal static string[] EnumerateMachineRegistrationCodes() =>
            MachineRegistrations().Select(pair => pair.Key).ToArray();

        private static List<(string Key, string DisplayName)> MachineRegistrations()
        {
            var found = new List<(string, string)>();
            using RegistryKey localMachine =
                RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using RegistryKey? uninstall = localMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
            if (uninstall == null)
                return found;

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
                    found.Add((keyName, displayName));
            }

            return found;
        }

        /// <summary>
        /// The app is running from the installed machine layout with the
        /// engine service registered: the "installed" fact for arming, even
        /// when a fast /x already removed the ARP registration before the
        /// (slow) startup got here. The app sits at &lt;root&gt;\current\, so
        /// the layout root is the first parent. Portable layouts (no Program
        /// Files root, no service) and dev runs never arm.
        /// </summary>
        internal static bool InstalledLayoutPresent()
        {
            string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ".."));
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
        /// True when the machine is mid-uninstall of this product: the
        /// machine ARP registration is gone while the installed layout and
        /// the engine service are still present, and a real MSI removal
        /// transaction backs that up (the event-log evidence rule of
        /// <see cref="MsiRemovalEvidence"/>: an AV registry-protection
        /// phantom must not silence the app on every launch). The uninstall
        /// sweep runs a few tens of seconds; the engine's supervision
        /// relaunches the app it sees die, and those relaunches must not
        /// open windows, tray icons, or sentinel prompts inside that window
        /// - they exit instantly. Dev/portable layouts (no Program Files
        /// root, no service) and normal machines never match.
        /// </summary>
        public static bool IsBeingUninstalled()
        {
            return !Registered(EnumerateMachineRegistrations())
                && InstalledLayoutPresent()
                && MsiRemovalEvidence();
        }

        /// <summary>
        /// Machine-wide proof that a real uninstall of this product just ran:
        /// the MsiInstaller "Removal completed successfully" event (11724,
        /// the Windows Installer success fingerprint - 11707 is the install
        /// twin) naming the product within the last 5 minutes. The event log
        /// is written by the Windows Installer service itself - an AV registry
        /// view trick cannot fabricate it - and a ghost registration's
        /// self-repair /x ends 1603 and never writes this event. Fail-closed:
        /// an unreadable event log reads as no evidence, and the destructive
        /// verb stays parked.
        /// </summary>
        internal static bool MsiRemovalEvidence(string? productCode = null)
        {
            try
            {
                using var application = new EventLog("Application");
                EventLogEntryCollection entries = application.Entries;
                DateTime cutoff = DateTime.Now - TimeSpan.FromMinutes(3);

                bool removalCompleted = false;
                bool transactionEnded = false;

                for (int i = entries.Count - 1; i >= 0; i--)
                {
                    EventLogEntry entry = entries[i];
                    if (entry.TimeGenerated < cutoff)
                        break;

                    switch (entry.InstanceId)
                    {
                        case 11724 when removalCompleted == false:
                            if (entry.Message.IndexOf(ProductName, StringComparison.OrdinalIgnoreCase) >= 0)
                                removalCompleted = true;
                            break;
                        case 1042 when transactionEnded == false:
                            // "Ending a Windows Installer transaction: {GUID}"
                            // - bind to the code the sentinel watched when the
                            // registration was visible, so an older uninstall's
                            // fingerprint cannot license a fresh install's verb.
                            bool codeMatch = productCode is null
                                ? entry.Message.IndexOf("}", StringComparison.Ordinal) >= 0
                                : entry.Message.IndexOf(productCode, StringComparison.OrdinalIgnoreCase) >= 0;

                            // A removal this very process initiated is Velopack's
                            // registration repair, not the Control Panel uninstall
                            // the sentinel is contracted to execute - field-proven:
                            // the app's own UpdateManager /x-ed the live code 17 s
                            // after a GUI install (registry-guarded machine) and the
                            // sentinel executed it, sweeping the live app mid-splash.
                            if (codeMatch && !IsSelfIssuedTransaction(entry.Message, Environment.ProcessId))
                                transactionEnded = true;
                            break;
                    }

                    if (removalCompleted && transactionEnded)
                        return true;
                }

                return removalCompleted && transactionEnded;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// "Ending a Windows Installer transaction: {GUID}. Client Process Id: N."
        /// - the client pid is the process that initiated the MSI session.
        /// Velopack's registration repair /x-es the live product from inside the
        /// app process, so that transaction's client pid is this process itself:
        /// a self-issued removal is a repair, never the Control Panel uninstall
        /// the sentinel exists for. Control Panel, Settings, the uninstall shim
        /// and a manual elevated msiexec all initiate as their own process
        /// (msiexec.exe), never as this pid. A message without the client pid
        /// is not self-issued (fail open toward the uninstall route).
        /// </summary>
        internal static bool IsSelfIssuedTransaction(string message1042, int selfPid)
        {
            const string marker = "Client Process Id: ";
            int at = message1042.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (at < 0)
                return false;

            int start = at + marker.Length;
            int length = 0;
            while (start + length < message1042.Length && char.IsDigit(message1042[start + length]))
                length++;

            return length > 0 &&
                int.TryParse(message1042.Substring(start, length), out int clientPid) &&
                clientPid == selfPid;
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
