using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
            TimeSpan? pollInterval = null)
        {
            _enumerateRegistrations = enumerateRegistrations;
            _onUninstallDetected = onUninstallDetected;
            _log = log;
            _pollInterval = pollInterval ?? DefaultPollInterval;
        }

        /// <summary>
        /// Production wiring: watch the real machine ARP registration, run
        /// the elevated engine verb, log through the app logger.
        /// </summary>
        public static UninstallSentinel CreateDefault(Action<string> log) =>
            new(EnumerateMachineRegistrations, RunEngineUninstallVerb, log);

        /// <summary>Watch in the background for the lifetime of the process.</summary>
        public void Start() => _ = Task.Run(RunAsync);

        internal async Task RunAsync()
        {
            while (true)
            {
                bool found;
                try
                {
                    found = Registered(_enumerateRegistrations());
                }
                catch (Exception ex)
                {
                    // A probe failure says nothing about the registration;
                    // treat it as present so a broken probe never triggers a
                    // self-uninstall. The engine verb is destructive.
                    _log("uninstall sentinel: registration probe failed, treating as present: " + ex.Message);
                    found = true;
                }

                bool fire = Step(found);
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
        /// State machine: arm only after the registration has been seen
        /// (dev/portable runs have none from the start, so an un-armed
        /// sentinel stays inert forever), then fire once it has been missing
        /// for <see cref="MissesToFire"/> consecutive polls. A present
        /// registration resets the miss count.
        /// </summary>
        internal bool Step(bool found)
        {
            if (found)
            {
                _armed = true;
                _misses = 0;
                _fire = false;
                return false;
            }

            if (!_armed)
                return false;

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
        /// Launches the engine exe with --uninstall-engine elevated (the verb
        /// needs BA to stop and delete the service, exactly as at install),
        /// then exits: the verb force-closes this app image, the graceful
        /// OnExit handshake would only stall the sweep.
        /// </summary>
        private static void RunEngineUninstallVerb()
        {
            string engine = Path.Combine(
                AppContext.BaseDirectory, "Service", "Uniwill Control Utility Engine.exe");
            if (!File.Exists(engine))
                throw new FileNotFoundException("engine exe not found", engine);

            var psi = new ProcessStartInfo(engine, "--uninstall-engine")
            {
                UseShellExecute = true,
                Verb = "runas",
                // Not the layout: the verb deletes the install root, and
                // Windows refuses to delete a live process's CWD.
                WorkingDirectory = Environment.SystemDirectory,
            };
            Process.Start(psi);
            Environment.Exit(0);
        }
    }
}
