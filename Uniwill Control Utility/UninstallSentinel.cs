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
    /// Self-heal net for the raw <c>msiexec /x</c> uninstall routes (scripts,
    /// GPO, <c>appwiz</c> on machines where a legacy GUID-keyed entry still
    /// routes by product code).
    ///
    /// The canonical Control Panel route no longer needs the net: the packed
    /// MSI (build\pack.ps1 step 5a + build\msi-edit) publishes ONE Add/Remove
    /// Programs entry — ARPSYSTEMCOMPONENT=1 suppresses the Windows Installer
    /// engine's GUID-keyed entry, and the package's own entry (component
    /// <c>ArpRegistration</c>) carries UninstallString = the complete-sweep
    /// shim <c>Uninstall.exe</c>. The shim is the shared full-cleanup entry
    /// point: service stop+delete, app kill, tasks, MSI /x, ARP husks, stale
    /// per-user MSI registrations, ProgramData logs, install layout.
    ///
    /// What remains thin is a direct <c>msiexec /x {code}</c>: vpk's package
    /// is a thin bootstrap (the layout is deployed by the package's own
    /// bootstrap custom action, invisible to MSI file records), so the
    /// removal unregisters the product in ~2 s and leaves the engine service,
    /// the running app, the layout, and the tasks behind. What is guaranteed
    /// present during every such removal is the app itself: the transaction
    /// never kills it. This sentinel watches the product's machine
    /// registration — the ARP GUID entry when one is published (its
    /// UninstallString is the MsiExec call, which distinguishes it from the
    /// shim-keyed entry), the MSI database enumeration via MsiEnumProductsW
    /// when it is not (the suppressed case; filtered to Machine context so
    /// stale per-user ghosts of old broken uninstalls never keep the probe
    /// armed) — and when it vanishes, the app self-executes the engine's
    /// elevated --uninstall-engine verb, converging every removal route on
    /// the shim's end state. The vanish alone is not trusted: AV registry
    /// protection (field-proven: Emsisoft) can hide the live registration
    /// from the app's own view, so firing also requires the MsiInstaller
    /// "Removal completed successfully" event bound to the watched product
    /// code and postdating this process — a machine-wide trace only the
    /// Windows Installer service writes. One UAC prompt at the verb, the
    /// same consent the shim route asks for.
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
            // The destructive firing demands the time floor (a removal that
            // predates this process cannot describe this install's uninstall);
            // IsBeingUninstalled is the mid-sweep machine fact and keeps the
            // plain 3-minute window.
            _removalEvidence = removalEvidence
                ?? (code => MsiRemovalEvidence(code, requireAfterThisProcessStart: true));
            _onUninstallDetected = onUninstallDetected;
            _log = log;
            _pollInterval = pollInterval ?? DefaultPollInterval;
        }

        /// <summary>
        /// Production wiring: the union registration probe (ARP GUID entry
        /// when the MSI publishes one, MSI database enumeration when it does
        /// not - the packed MSI suppresses its ARP entry: see pack.ps1), the
        /// elevated engine verb, the app logger.
        /// </summary>
        public static UninstallSentinel CreateDefault(Action<string> log) =>
            new(EnumerateRegistrations, RunEngineUninstallVerb, log,
                pollInterval: null, installedLayoutProbe: InstalledLayoutPresent,
                enumerateCodes: EnumerateRegistrationCodes);

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
                    // Binding precondition, field-proven necessity (2026-10-28
                    // 20:57/20:59): on a machine whose security product guards
                    // the ARP keys, this process NEVER sees the registration,
                    // so no product code was ever watched, and the evidence
                    // probe degenerates to "any removal transaction in the
                    // last 3 minutes". Two fresh GUI installs then died 16 s
                    // and 39 s after launch: the first executed a test /x from
                    // the 3-minute pool, the second executed the PREVIOUS app
                    // session's self-repair /x. An unbindable firing is
                    // unprovable: the verb stays parked for the whole session.
                    // On such machines the complete uninstall route is the
                    // app's own uninstall entry (the shim, HKCU-visible, runs
                    // the full elevated sweep); code signing restores Control
                    // Panel detection by making the registration visible.
                    if (_lastSeenCode is null)
                    {
                        if (!_noEvidenceLogged)
                        {
                            _noEvidenceLogged = true;
                            _log("uninstall sentinel: this process has never seen the registration (registry-guarded view), so no removal event can be bound to our product code - the destructive verb stays parked for the whole session; the complete uninstall route on this machine is the app's own uninstall entry");
                        }
                        await Task.Delay(_pollInterval).ConfigureAwait(false);
                        continue;
                    }

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
        /// The DisplayName of every live machine registration for the product
        /// visible in the HKLM (64-bit view) ARP tree: the published vpk
        /// entry (fixed key <c>MSI:Uniwill-Control-Utility</c>, our
        /// DisplayName, GUID-shaped <c>UCUProductCode</c> row shipped by
        /// build\msi-edit — the component publishes and removes it with the
        /// product), and GUID-format keys carrying the MsiExec UninstallString
        /// (packs from before the ARP suppression). Velopack's shim husk
        /// (no UCUProductCode row) and the old broken-uninstall husks never
        /// match, so husks do not keep the sentinel armed, and per-user
        /// registrations live in other hives, so old ones cannot poison the
        /// probe.
        /// </summary>
        internal static string[] EnumerateMachineRegistrations() =>
            MachineRegistrations().Select(pair => pair.DisplayName).ToArray();

        /// <summary>
        /// The registration probe for the production sentinel: the ARP tree
        /// first — the published vpk entry is the normal signal on the packed
        /// MSI (the engine's GUID entry is suppressed via ARPSYSTEMCOMPONENT=1
        /// and the canonical Control Panel route runs the bound shim, not a
        /// thin /x) — and the MSI database enumeration via MsiEnumProductsW
        /// as the fallback for ARP-blind views. Field-proven degradation on
        /// the guarded box: the enumeration is phantom to in-process readers
        /// there (421 other products enumerate, ours never, elevated
        /// included), so when the ARP view is ALSO blind the probe reports
        /// "not installed" and the binding gate parks the verb — the safe
        /// direction; the removal-evidence rule plus the process-start floor
        /// stay the firing gate either way. Stale per-user ghost
        /// registrations enumerate as User context and are filtered out: they
        /// would keep the probe "installed" through a real machine-scope
        /// removal.
        /// </summary>
        internal static string[] EnumerateRegistrations()
        {
            string[] arp = EnumerateMachineRegistrations();
            return arp.Length > 0 ? arp : EnumerateMsiProductDisplayNames();
        }

        /// <summary>
        /// Product codes for the probe union: ARP-view codes when visible —
        /// the UCUProductCode row of the published entry, GUID key names for
        /// pre-suppression packs (the key IS the code) — MSI enumeration
        /// otherwise. These are the codes the MsiInstaller evidence names, so
        /// the sentinel watches them while installed and the removal event
        /// must name one; a previous uninstall's event never licenses the
        /// verb for a fresh install.
        /// </summary>
        internal static string[] EnumerateRegistrationCodes()
        {
            string[] arp = EnumerateMachineRegistrationCodes();
            return arp.Length > 0 ? arp : EnumerateMsiProductCodes();
        }

        internal static string[] EnumerateMsiProductDisplayNames() =>
            MsiRegisteredProducts().Select(pair => pair.DisplayName).ToArray();

        internal static string[] EnumerateMsiProductCodes() =>
            MsiRegisteredProducts().Select(pair => pair.Key).ToArray();

        /// <summary>
        /// Machine-context MSI registrations of the product (product code,
        /// display name). Machine-context only: per-user ghosts of old broken
        /// uninstalls are the verb's sweep targets, not a registration the
        /// sentinel should stay armed on. Fails open to empty on any
        /// enumeration error.
        /// </summary>
        private static List<(string Key, string DisplayName)> MsiRegisteredProducts()
        {
            var found = new List<(string, string)>();
            try
            {
                var code = new StringBuilder(39);
                for (int index = 0; index < 4096; index++)
                {
                    if (MsiEnumProductsW(index, code) != 0)
                        break;

                    string candidate = code.ToString();
                    code.Clear();
                    if (candidate.Length == 0)
                        continue;

                    var name = new StringBuilder(256);
                    int nameLength = 256;
                    if (MsiGetProductInfoW(candidate, "ProductName", name, ref nameLength) != 0 ||
                        !string.Equals(name.ToString(), ProductName, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var context = new StringBuilder(32);
                    int contextLength = 32;
                    if (MsiGetProductInfoW(candidate, "AssignmentType", context, ref contextLength) == 0 &&
                        context.ToString().Equals("Machine", StringComparison.OrdinalIgnoreCase))
                    {
                        found.Add((candidate, ProductName));
                    }
                }
            }
            catch
            {
                // Fail open toward "not installed": the sentinel then parks on
                // the binding gate (no watched code) and the verb stays inert -
                // the safe direction for a destructive verb.
            }

            return found;
        }

        [DllImport("msi.dll", CharSet = CharSet.Unicode)]
        private static extern int MsiEnumProductsW(int iProductIndex, StringBuilder lpProductBuf);

        [DllImport("msi.dll", CharSet = CharSet.Unicode)]
        private static extern int MsiGetProductInfoW(
            string szProduct, string szAttribute, StringBuilder lpValueBuf, ref int pcchValueBuf);

        /// <summary>
        /// The product codes of the live machine registrations, same rule as
        /// EnumerateMachineRegistrations: the <c>UCUProductCode</c> row for
        /// the published entry, the key name for GUID-format entries. The
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
                // The packed MSI (build\msi-edit) ships the product's ONE
                // visible Control Panel entry: the vpk component-published
                // row set, bound to the complete-sweep shim and carrying the
                // product code in UCUProductCode. Windows Installer installs
                // and removes those rows WITH the product (the component's
                // KeyPath is the uninstall subkey), so the entry IS the
                // machine registration for the sentinel. The key name is
                // vpk's business (the current template names it
                // `MSI:{product-code}` - the documented convention that
                // satisfies appwiz.cpl's product-code routing when an ARP
                // anti-hijack enforcer re-adds WindowsInstaller=1; installs
                // from the transitional window carry vpk's app-name key or
                // the plain renamed one) - identity is the content: our
                // DisplayName + a GUID-shaped UCUProductCode. GUID-format
                // keys with the MsiExec UninstallString are the Windows
                // Installer engine's canonical entry (hidden via
                // SystemComponent=1): watched too, its key name IS the code.
                bool guidKey = GuidKey.IsMatch(keyName);

                using RegistryKey? entry = uninstall.OpenSubKey(keyName);
                if (entry?.GetValue("DisplayName") is not string displayName)
                    continue;

                if (!string.Equals(displayName, ProductName, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!guidKey)
                {
                    string code = (entry.GetValue("UCUProductCode") as string ?? "").Trim();
                    if (GuidKey.IsMatch(code))
                        found.Add((code, displayName));
                    continue;
                }

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

        /// <summary>One Application-log event as the evidence rule sees it.</summary>
        internal readonly record struct EvidenceEntry(int InstanceId, DateTime TimeGenerated, string Message);

        /// <summary>
        /// Machine-wide proof that a real uninstall of this product just ran:
        /// the MsiInstaller "Removal completed successfully" event (11724,
        /// the Windows Installer success fingerprint - 11707 is the install
        /// twin) naming the product, paired with the code-bound, non-self-issued
        /// 1042 transaction end. The event log is written by the Windows
        /// Installer service itself - an AV registry view trick cannot
        /// fabricate it - and a ghost registration's self-repair /x ends 1603
        /// and never writes this event. Fail-closed: a missing floor match
        /// reads as no evidence, and the destructive verb stays parked.
        ///
        /// <paramref name="evidenceFloor"/> is the time floor, the third
        /// binding after code and pid - field-proven necessity (21:55:22):
        /// the user uninstalled with the shim (11724 + a code-bound 1042 for
        /// {09C0B11B} at 21:53:59), reinstalled the SAME package 67 s later -
        /// pack.ps1 pins the product code per package, so the fresh install
        /// carries the same code - and the relaunched app's sentinel found the
        /// OLD uninstall's fingerprint in its 3-minute pool naming exactly the
        /// watched code. Code and pid bound; the fresh install died 16 s after
        /// launch. Events older than this process started cannot describe
        /// THIS install's uninstall: the app that outlives a real removal
        /// started before it, so the floor is the process start (paired
        /// halves included: 11724 is name-only, so a fresh 1042 must not pair
        /// with a stale 11724 of ours).
        /// </summary>
        internal static bool HasRemovalEvidence(
            IEnumerable<EvidenceEntry> entriesNewestFirst,
            string? productCode,
            int selfPid,
            DateTime evidenceFloor)
        {
            bool removalCompleted = false;
            bool transactionEnded = false;

            foreach (EvidenceEntry entry in entriesNewestFirst)
            {
                if (entry.TimeGenerated < evidenceFloor)
                    break;

                switch (entry.InstanceId)
                {
                    case 11724 when !removalCompleted:
                        if (entry.Message.IndexOf(ProductName, StringComparison.OrdinalIgnoreCase) >= 0)
                            removalCompleted = true;
                        break;

                    case 1042 when !transactionEnded:
                        // "Ending a Windows Installer transaction: {GUID}"
                        // - bind to the code the sentinel watched when the
                        // registration was visible, so an uninstall of a
                        // different product cannot license our verb.
                        bool codeMatch = productCode is null
                            ? entry.Message.IndexOf("}", StringComparison.Ordinal) >= 0
                            : entry.Message.IndexOf(productCode, StringComparison.OrdinalIgnoreCase) >= 0;

                        // A removal this very process initiated is Velopack's
                        // registration repair, not the Control Panel uninstall
                        // the sentinel is contracted to execute - field-proven:
                        // the app's own UpdateManager /x-ed the live code 17 s
                        // after a GUI install (registry-guarded machine) and the
                        // sentinel executed it, sweeping the live app mid-splash.
                        if (codeMatch && !IsSelfIssuedTransaction(entry.Message, selfPid))
                            transactionEnded = true;
                        break;
                }

                if (removalCompleted && transactionEnded)
                    return true;
            }

            return removalCompleted && transactionEnded;
        }

        /// <summary>
        /// The event-log adapter for <see cref="HasRemovalEvidence"/>. The
        /// wall window is 3 minutes; <paramref name="requireAfterThisProcessStart"/>
        /// raises the floor to this process's start - the sentinel's destructive
        /// firing demands it (a pre-launch uninstall is evidence for the install
        /// it removed, not for the one running now), while
        /// <see cref="IsBeingUninstalled"/> keeps the plain window: the machine
        /// being mid-sweep is a fact newer processes must see, relaunch
        /// suppression included.
        /// </summary>
        internal static bool MsiRemovalEvidence(
            string? productCode = null,
            bool requireAfterThisProcessStart = false)
        {
            try
            {
                using var application = new EventLog("Application");

                DateTime floor = DateTime.Now - TimeSpan.FromMinutes(3);
                if (requireAfterThisProcessStart)
                {
                    DateTime processStart = Process.GetCurrentProcess().StartTime;
                    if (processStart > floor)
                        floor = processStart;
                }

                EventLogEntryCollection entries = application.Entries;
                var newestFirst = new List<EvidenceEntry>((int)Math.Min(entries.Count, 4096));
                for (int i = entries.Count - 1; i >= 0; i--)
                {
                    EventLogEntry entry = entries[i];
                    if (entry.TimeGenerated < floor)
                        break;

                    newestFirst.Add(new EvidenceEntry((int)entry.InstanceId, entry.TimeGenerated, entry.Message));
                }

                return HasRemovalEvidence(newestFirst, productCode, Environment.ProcessId, floor);
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
