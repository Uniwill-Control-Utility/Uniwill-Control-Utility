using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

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
    /// location / 1338 at process creation), and Velopack's own uninstall
    /// hook is a per-user-temp design that no-ops in the SYSTEM execute
    /// context of a per-machine install.
    ///
    /// What is guaranteed present during every such uninstall is the app
    /// itself: the removal never kills it, so it outlives the transaction.
    /// This sentinel watches the MSI product registration (the one fact the
    /// thin uninstall does change); when the product vanishes, the app
    /// self-executes the engine's elevated --uninstall-engine verb - the
    /// single full-cleanup entry point shared with the Uninstall.exe shim
    /// (service stop+delete, app kill, tasks, ARP husks, ProgramData logs,
    /// install layout) - and exits. One UAC prompt at uninstall, the same
    /// consent the shim route asks for, and every removal route converges
    /// on the same end state.
    /// </summary>
    internal sealed class UninstallSentinel
    {
        /// <summary>The MSI ProductName written by vpk's generated package.</summary>
        internal const string ProductName = "Uniwill Control Utility";

        /// <summary>
        /// Consecutive polls without the product before acting. The /x
        /// transaction is over in ~2 s; two misses debounce a transient
        /// enumeration failure, at a cost of ~10 s of detection latency.
        /// </summary>
        internal const int MissesToFire = 2;

        private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(5);

        private readonly Func<string[]> _enumerateProductNames;
        private readonly Action _onUninstallDetected;
        private readonly Action<string> _log;
        private readonly TimeSpan _pollInterval;

        private int _misses;
        private bool _armed;
        private bool _fire;

        /// <summary>Test seam: inject the enumeration and the reaction.</summary>
        internal UninstallSentinel(
            Func<string[]> enumerateProductNames,
            Action onUninstallDetected,
            Action<string> log,
            TimeSpan? pollInterval = null)
        {
            _enumerateProductNames = enumerateProductNames;
            _onUninstallDetected = onUninstallDetected;
            _log = log;
            _pollInterval = pollInterval ?? DefaultPollInterval;
        }

        /// <summary>
        /// Production wiring: watch the real MSI registry, run the elevated
        /// engine verb, log through the app logger.
        /// </summary>
        public static UninstallSentinel CreateDefault(Action<string> log) =>
            new(EnumerateMsiProductNames, RunEngineUninstallVerb, log);

        /// <summary>Watch in the background for the lifetime of the process.</summary>
        public void Start() => _ = Task.Run(RunAsync);

        internal async Task RunAsync()
        {
            while (true)
            {
                bool found;
                try
                {
                    found = ProductRegistered(_enumerateProductNames());
                }
                catch (Exception ex)
                {
                    // An enumeration failure says nothing about the product;
                    // treat it as present so a broken probe never triggers a
                    // self-uninstall. The engine verb is destructive.
                    _log("uninstall sentinel: product enumeration failed, treating as present: " + ex.Message);
                    found = true;
                }

                bool fire = Step(found);
                if (fire)
                {
                    _log("uninstall sentinel: MSI registration gone, running the elevated engine uninstall verb");
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
        /// State machine: arm only after the product has been seen (dev runs
        /// from a source layout with no registration stay inert), then fire
        /// once it has been missing for <see cref="MissesToFire"/> consecutive
        /// polls. A found product resets the miss count. Returns the new fire
        /// decision.
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

        internal static bool ProductRegistered(string[] productNames) =>
            productNames.Any(name => ProductName.Equals(name, StringComparison.OrdinalIgnoreCase));

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

        /// <summary>
        /// Every MSI ProductName registered for this user and machine
        /// (MsiEnumProducts covers per-user + per-machine products).
        /// </summary>
        internal static string[] EnumerateMsiProductNames()
        {
            var names = new List<string>();
            var productCode = new StringBuilder(39);

            for (int index = 0; index < 4096; index++)
            {
                if (MsiEnumProductsW(index, productCode) != 0)
                    break;

                var name = new StringBuilder(256);
                uint nameLength = 256;
                if (MsiGetProductInfoW(productCode.ToString(), "ProductName", name, ref nameLength) == 0)
                    names.Add(name.ToString());

                productCode.Clear();
            }

            return names.ToArray();
        }

        [DllImport("msi.dll", CharSet = CharSet.Unicode, EntryPoint = "MsiEnumProductsW")]
        private static extern int MsiEnumProductsW(int index, StringBuilder productCode);

        [DllImport("msi.dll", CharSet = CharSet.Unicode, EntryPoint = "MsiGetProductInfoW")]
        private static extern int MsiGetProductInfoW(
            string productCode, string attribute, StringBuilder value, ref uint valueLength);
    }
}
