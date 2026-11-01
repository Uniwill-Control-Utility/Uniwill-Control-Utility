using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;
using UCU.Core.Takeover;

namespace UCU.Hardware.Takeover
{
    /// <summary>Machine-scope takeover inventory (counts + audit lists; the
    /// state is decided Core-side from the counts). Mirrors
    /// <see cref="TakeoverPolicy.Inventory"/> plus the UI-facing audit.</summary>
    public sealed record OemMachineStatus
    {
        public int ServicesInstalled { get; init; }

        public int ServicesRunning { get; init; }

        public int OemProcessesRunning { get; init; }

        public int JournalCount { get; init; }

        public int AutostartNeutralized { get; init; }

        public IReadOnlyList<string> SquatSuspects { get; init; } = Array.Empty<string>();

        public IReadOnlyList<string> NeutralizedTargets { get; init; } = Array.Empty<string>();
    }

    /// <summary>A completed pause/restore: the machine state after the
    /// action, how many mutations landed, and one audit line each.</summary>
    public sealed record OemActionResult
    {
        public OemMachineStatus StatusAfter { get; init; } = new();

        public int MutationsApplied { get; init; }

        public IReadOnlyList<string> Actions { get; init; } = Array.Empty<string>();
    }

    /// <summary>The engine-scope takeover surface (story U-203): the
    /// machine-scope half of the OEM pause. HKLM run keys, the companion
    /// services, the machine scheduled tasks and the verified OEM
    /// processes. The user-scope half (HKCU, UWP AppModel, StartupApproved)
    /// is composed app-side where the signed-in user's registry is the
    /// write scope; the journals stay separate per scope and the app
    /// merges them for display.</summary>
    public interface IOemTakeoverService
    {
        OemMachineStatus GetStatus();

        OemActionResult Pause();

        OemActionResult Resume();
    }

    /// <summary>Source-generated metadata for the journal file (the house
    /// discipline: no reflection serialization, so the engine publishes
    /// trimmed). Default naming (PascalCase) is the file's format; the app's
    /// merged journal view reads the same shape with plain options.</summary>
    [JsonSourceGenerationOptions(WriteIndented = false)]
    [JsonSerializable(typeof(List<TakeoverJournalEntry>))]
    [JsonSerializable(typeof(TakeoverJournalEntry))]
    internal sealed partial class TakeoverJournalJson : JsonSerializerContext
    {
    }

    /// <summary>
    /// The machine-scope takeover executor. Every mutation first passes the
    /// Core hardening gate (<see cref="TakeoverPolicy.Evaluate"/>): catalog
    /// name AND existing image path AND chain-valid signature (observed via
    /// WinVerifyTrust) AND allowlist publisher, outside UCU's own roots.
    /// Gate refusals are collected as squat-suspects and never touched;
    /// a machine without the stack answers zero mutations (the reference's
    /// auto-bypass, dev-box probe-proven).
    ///
    /// Pause sequence follows the reference (lifecycle.rs): kills first
    /// (the watchdogs stop interfering), services stop + park at demand,
    /// tasks disable, HKLM run keys rename to the backup form. Resume
    /// replays the Core restore plan (autostart first, service re-arm and
    /// starts last, bridge-first with the spawned service watched) and
    /// consumes the entries it reversed - the journal is "mutations not
    /// yet undone", so its drain drives the status decider.
    /// </summary>
    public sealed class OemTakeoverService : IOemTakeoverService
    {
        private const string RunKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";

        private readonly string _journalPath;

        public OemTakeoverService(string journalFilePath) => _journalPath = journalFilePath;

        // ---- Status --------------------------------------------------------

        public OemMachineStatus GetStatus()
        {
            var suspects = new List<string>();
            var neutralized = new List<string>();
            var probe = new PublisherProbe();
            List<TakeoverJournalEntry> journal = LoadJournal();

            var (svcInstalled, svcRunning, svcNeutralized, svcSuspects) = ProbeServices(probe, journal, audit: null, neutralizedSink: neutralized);
            suspects.AddRange(svcSuspects);

            int processesRunning = CountOemProcesses(probe, suspects);

            var (backupRunKeys, runKeySuspects) = ProbeRunKeys(probe, neutralizedSink: neutralized, audit: null);
            suspects.AddRange(runKeySuspects);

            var (neutralizedTasks, taskSuspects) = ProbeTasks(probe, journal, neutralizedSink: neutralized, audit: null);
            suspects.AddRange(taskSuspects);

            int autostartNeutralized = backupRunKeys + neutralizedTasks;

            return new OemMachineStatus
            {
                ServicesInstalled = svcInstalled,
                ServicesRunning = svcRunning,
                OemProcessesRunning = processesRunning,
                JournalCount = journal.Count,
                AutostartNeutralized = autostartNeutralized,
                SquatSuspects = suspects,
                NeutralizedTargets = neutralized,
            };
        }

        // ---- Pause ----------------------------------------------------------

        public OemActionResult Pause()
        {
            var actions = new List<string>();
            var probe = new PublisherProbe();
            List<TakeoverJournalEntry> journal = LoadJournal();
            int baseline = journal.Count;

            void Journal(TakeoverJournalEntry entry, string auditLine)
            {
                journal.Add(entry);
                actions.Add(auditLine);
            }

            // 1. Kill the verified OEM processes (reference order: kills
            // first, so the watchdogs cannot fight the service stops).
            foreach (Process process in Process.GetProcesses())
            {
                try
                {
                    if (!OemTakeoverTargets.IsProcessName(process.ProcessName))
                        continue;

                    string? path = TakeoverNative.ProcessImagePath(process.Id);
                    var (verdict, reason) = GateFileIdentity(probe, path);

                    if (verdict == TakeoverPolicy.Eligibility.UcuSelf)
                        continue; // the box runs "Uniwill Control Utility"; self-guard

                    if (verdict != TakeoverPolicy.Eligibility.Eligible)
                        continue; // status lists the suspect; a pause does not re-audit it

                    try
                    {
                        process.Kill(entireProcessTree: true);
                        process.WaitForExit(3000);
                        Journal(
                            new TakeoverJournalEntry
                            {
                                Kind = TakeoverMutationKind.ProcessKilled,
                                Target = path!,
                                After = $"pid={process.Id}",
                                Utc = DateTime.UtcNow,
                            },
                            $"killed {process.ProcessName} (pid {process.Id})");
                    }
                    catch (Exception ex)
                    {
                        actions.Add($"kill {process.ProcessName} (pid {process.Id}) failed: {ex.Message}");
                    }
                }
                finally
                {
                    process.Dispose();
                }
            }

            // 2. Stop the catalog services and park them at demand-start.
            PauseServices(probe, journal, actions);

            // 3. Disable the catalog tasks.
            PauseTasks(probe, journal, actions);

            // 4. Neutralize the HKLM run keys (rename to the backup form).
            PauseRunKeys(probe, journal, actions);

            SaveJournal(journal);

            return new OemActionResult
            {
                StatusAfter = GetStatus(),
                MutationsApplied = journal.Count - baseline,
                Actions = actions,
            };
        }

        // ---- Resume ----------------------------------------------------------

        public OemActionResult Resume()
        {
            var actions = new List<string>();
            var probe = new PublisherProbe();
            List<TakeoverJournalEntry> journal = LoadJournal();

            IReadOnlyList<TakeoverJournalEntry> plan = TakeoverPolicy.PlanRestore(journal);
            var consumed = new HashSet<(TakeoverMutationKind, string)>();
            var serviceRestarts = new List<string>(); // catalog order preserved by the plan tail

            foreach (TakeoverJournalEntry entry in plan)
            {
                switch (entry.Kind)
                {
                    case TakeoverMutationKind.RunKey:
                        if (RestoreRunKey(entry, actions))
                            consumed.Add((entry.Kind, entry.Target.ToUpperInvariant()));
                        break;

                    case TakeoverMutationKind.Task:
                        if (RestoreTask(entry, actions))
                            consumed.Add((entry.Kind, entry.Target.ToUpperInvariant()));
                        break;

                    case TakeoverMutationKind.ServiceStart:
                        (bool entryConsumed, bool needsStart) = RestoreServiceConfig(entry, actions);
                        if (entryConsumed)
                            consumed.Add((entry.Kind, entry.Target.ToUpperInvariant()));
                        if (needsStart)
                            serviceRestarts.Add(entry.Target);
                        break;

                    case TakeoverMutationKind.UwpState:
                    case TakeoverMutationKind.StartupApproved:
                        // User-scope kinds land in the app's journal, never
                        // the engine's; a merged plan handing one here is a
                        // caller bug. Skip (the entry stays in the journal).
                        break;

                    case TakeoverMutationKind.ProcessKilled:
                        // PlanRestore drops kills; seeing one here is the same caller bug.
                        break;
                }
            }

            StartCascade(serviceRestarts, actions);
            VerifyStarted(serviceRestarts, actions);

            if (consumed.Count > 0)
            {
                journal.RemoveAll(e => consumed.Contains((e.Kind, e.Target.ToUpperInvariant())));
                SaveJournal(journal);
            }

            return new OemActionResult
            {
                StatusAfter = GetStatus(),
                MutationsApplied = consumed.Count,
                Actions = actions,
            };
        }

        // ---- Run keys (HKLM 64- and 32-bit views) ---------------------------

        private sealed class RunKeyRoot
        {
            public RegistryView View;
            public string Label = string.Empty;   // "HKLM\Run" / "HKLM\Run(WOW)"
            public string TargetPrefix = string.Empty; // journal target qualifier
        }

        private static readonly RunKeyRoot[] HkmlRunRoots =
        {
            new() { View = RegistryView.Registry64, Label = "HKLM\\Run", TargetPrefix = "HKLM\\Run" },
            new() { View = RegistryView.Registry32, Label = "HKLM\\Run(WOW)", TargetPrefix = "HKLM\\Run(WOW)" },
        };

        private static (int backupCount, List<string> suspects) ProbeRunKeys(
            PublisherProbe probe, List<string>? neutralizedSink, List<string>? audit)
        {
            int backups = 0;
            var suspects = new List<string>();

            foreach (RunKeyRoot root in HkmlRunRoots)
            {
                using RegistryKey? run = OpenRunKey(root.View, writable: false);
                if (run is null)
                    continue;

                foreach (string valueName in run.GetValueNames())
                {
                    if (TryClassifyRunValue(probe, root, valueName, out bool isBackup, out string? reason))
                    {
                        if (isBackup)
                        {
                            backups++;
                            neutralizedSink?.Add($@"{root.Label}\{StripBackupPrefix(valueName)}");
                        }
                    }
                    else if (reason is not null)
                    {
                        string line = $@"run key {root.Label}\{valueName}: {reason}";
                        suspects.Add(line);
                        audit?.Add(line);
                    }
                }
            }

            return (backups, suspects);
        }

        private static void PauseRunKeys(PublisherProbe probe, List<TakeoverJournalEntry> journal, List<string> actions)
        {
            foreach (RunKeyRoot root in HkmlRunRoots)
            {
                using RegistryKey? run = OpenRunKey(root.View, writable: true);
                if (run is null)
                    continue;

                foreach (string valueName in run.GetValueNames())
                {
                    if (valueName.StartsWith(OemTakeoverTargets.RunKeyBackupPrefix, StringComparison.OrdinalIgnoreCase))
                        continue; // already neutralized

                    if (!TryClassifyRunValue(probe, root, valueName, out _, out string? reason) || reason is not null)
                        continue; // not a target, or gate-refused (status lists suspects)

                    string command = Convert.ToString(run.GetValue(valueName)) ?? string.Empty;
                    RegistryValueKind kind = run.GetValueKind(valueName);
                    string backupName = OemTakeoverTargets.RunKeyBackupPrefix + valueName;

                    try
                    {
                        run.SetValue(backupName, command, kind);
                        run.DeleteValue(valueName);
                        journal.Add(new TakeoverJournalEntry
                        {
                            Kind = TakeoverMutationKind.RunKey,
                            Target = $@"{root.TargetPrefix}\{valueName}",
                            Before = $"{valueName}={command}",
                            After = $"{backupName}={command}",
                            Utc = DateTime.UtcNow,
                        });
                        actions.Add($@"neutralized run key {root.Label}\{valueName}");
                    }
                    catch (Exception ex)
                    {
                        actions.Add($@"run key {root.Label}\{valueName}: write refused ({ex.Message})");
                    }
                }
            }
        }

        private static bool RestoreRunKey(TakeoverJournalEntry entry, List<string> actions)
        {
            // Target: "HKLM\Run\Name" / "HKLM\Run(WOW)\Name"; Before:
            // "Name=command" (the first '=' splits name from command: the
            // name never contains one, the command always starts after it).
            RunKeyRoot root = entry.Target.StartsWith(@"HKLM\Run(WOW)", StringComparison.OrdinalIgnoreCase)
                ? HkmlRunRoots[1]
                : HkmlRunRoots[0];

            int lastSep = entry.Target.LastIndexOf('\\');
            string valueName = lastSep > 0 ? entry.Target[(lastSep + 1)..] : entry.Target;
            string backupName = OemTakeoverTargets.RunKeyBackupPrefix + valueName;

            int eq = entry.Before.IndexOf('=');
            string command = eq >= 0 ? entry.Before[(eq + 1)..] : string.Empty;

            using RegistryKey? run = OpenRunKey(root.View, writable: true);
            if (run is null)
                return false;

            try
            {
                object? current = run.GetValue(backupName);
                if (current is null)
                    return true; // nothing in backup form: already restored (idempotent)

                run.SetValue(valueName, Convert.ToString(current) ?? string.Empty, run.GetValueKind(backupName));
                run.DeleteValue(backupName);
                actions.Add($@"restored run key {root.Label}\{valueName}");
                return true;
            }
            catch (Exception ex)
            {
                actions.Add($@"run key {root.Label}\{valueName}: restore refused ({ex.Message})");
                return false;
            }
        }

        private static RegistryKey? OpenRunKey(RegistryView view, bool writable)
        {
            using RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            return baseKey.OpenSubKey(RunKeyPath, writable);
        }

        /// <summary>Classifies one HKLM run-key value. Returns true when the
        /// value is a takeover-relevant entry (live target or UCU backup);
        /// false + a reason string when it is a gate refusal (squat-suspect);
        /// false + null reason when it is not a target at all.</summary>
        private static bool TryClassifyRunValue(
            PublisherProbe probe, RunKeyRoot root, string valueName,
            out bool isBackup, out string? reason)
        {
            isBackup = false;
            reason = null;

            bool backup = valueName.StartsWith(OemTakeoverTargets.RunKeyBackupPrefix, StringComparison.OrdinalIgnoreCase);
            string realName = backup
                ? valueName[OemTakeoverTargets.RunKeyBackupPrefix.Length..]
                : valueName;

            using RegistryKey? run = OpenRunKey(root.View, writable: false);
            string command = run is null
                ? string.Empty
                : Convert.ToString(run.GetValue(valueName)) ?? string.Empty;

            if (OemTakeoverTargets.IsUnderUcuRoot(command) || OemTakeoverTargets.IsUnderUcuRoot(realName))
                return false; // self-guard: silent, never audit-listed

            if (!OemTakeoverTargets.MatchesKeyword(realName) && !OemTakeoverTargets.MatchesKeyword(command))
                return false; // not a target

            isBackup = backup;

            string? exe = ExePathFromCommand(command);
            var (verdict, gateReason) = GateFileIdentity(probe, exe);
            if (verdict == TakeoverPolicy.Eligibility.Eligible)
                return true;

            if (verdict == TakeoverPolicy.Eligibility.UcuSelf)
                return false;

            reason = gateReason;
            return false;
        }

        private static string StripBackupPrefix(string valueName) =>
            valueName.StartsWith(OemTakeoverTargets.RunKeyBackupPrefix, StringComparison.OrdinalIgnoreCase)
                ? valueName[OemTakeoverTargets.RunKeyBackupPrefix.Length..]
                : valueName;

        /// <summary>Extracts the executable path from a run-key command
        /// string (quoted, unquoted-with-args, env-var and relative-system
        /// forms - the same normalization the field probe uses).</summary>
        internal static string? ExePathFromCommand(string command)
        {
            if (string.IsNullOrWhiteSpace(command))
                return null;

            string s = command.Trim();

            // Prefer the .exe boundary when present: it survives spaces in
            // unquoted paths and strips trailing arguments in one step.
            int exeIndex = s.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            string token;
            if (exeIndex > 0)
            {
                token = s[..(exeIndex + 4)];
            }
            else if (s.StartsWith("\"", StringComparison.Ordinal))
            {
                int end = s.IndexOf('"', 1);
                token = end > 1 ? s[1..end] : s.Trim('"');
            }
            else
            {
                int space = s.IndexOf(' ');
                token = space > 0 ? s[..space] : s;
            }

            token = Environment.ExpandEnvironmentVariables(token.Trim());
            if (string.IsNullOrEmpty(token))
                return null;

            if (!Path.IsPathRooted(token))
            {
                string systemRoot = Environment.GetFolderPath(Environment.SpecialFolder.System);
                token = Path.Combine(systemRoot, token);
            }

            return token;
        }

        // ---- Services ---------------------------------------------------------

        private sealed class ScmScope : IDisposable
        {
            public IntPtr Manager = TakeoverNative.OpenSCManager(null, null, TakeoverNative.SC_MANAGER_CONNECT);

            public void Dispose()
            {
                if (Manager != IntPtr.Zero)
                    TakeoverNative.CloseServiceHandle(Manager);
            }
        }

        private static (int installed, int running, int neutralized, List<string> suspects) ProbeServices(
            PublisherProbe probe, List<TakeoverJournalEntry> journal, List<string>? audit, List<string>? neutralizedSink)
        {
            int installed = 0, running = 0, neutralized = 0;
            var suspects = new List<string>();
            var demandEvidence = JournalTargets(journal, TakeoverMutationKind.ServiceStart);

            using var scm = new ScmScope();
            if (scm.Manager == IntPtr.Zero)
                return (0, 0, 0, suspects);

            foreach (string name in OemTakeoverTargets.ServiceNames)
            {
                IntPtr service = TakeoverNative.OpenService(scm.Manager, name, TakeoverNative.SERVICE_TAKEOVER_ACCESS);
                if (service == IntPtr.Zero)
                    continue; // not installed

                try
                {
                    installed++;

                    TakeoverNative.QUERY_SERVICE_CONFIG? config = TakeoverNative.QueryServiceConfig(service);
                    string? image = config is null ? null : ExePathFromCommand(Marshal.PtrToStringUni(config.Value.lpBinaryPathName) ?? string.Empty);

                    var (verdict, reason) = GateFileIdentity(probe, image);
                    if (verdict == TakeoverPolicy.Eligibility.UcuSelf)
                        continue;

                    if (verdict != TakeoverPolicy.Eligibility.Eligible)
                    {
                        string line = $"service '{name}': {reason}";
                        suspects.Add(line);
                        audit?.Add(line);
                        continue;
                    }

                    var status = new TakeoverNative.SERVICE_STATUS();
                    if (TakeoverNative.QueryServiceStatus(service, ref status) && status.dwCurrentState == TakeoverNative.SERVICE_RUNNING)
                        running++;

                    bool parked = config!.Value.dwStartType == TakeoverNative.SERVICE_DEMAND_START
                                  && demandEvidence.Contains(name.ToUpperInvariant());
                    if (parked)
                    {
                        neutralized++;
                        neutralizedSink?.Add($"service {name} (demand)");
                    }
                }
                finally
                {
                    TakeoverNative.CloseServiceHandle(service);
                }
            }

            return (installed, running, neutralized, suspects);
        }

        private static void PauseServices(PublisherProbe probe, List<TakeoverJournalEntry> journal, List<string> actions)
        {
            using var scm = new ScmScope();
            if (scm.Manager == IntPtr.Zero)
            {
                actions.Add("the service control manager is unreachable");
                return;
            }

            foreach (string name in OemTakeoverTargets.ServiceNames)
            {
                IntPtr service = TakeoverNative.OpenService(scm.Manager, name, TakeoverNative.SERVICE_TAKEOVER_ACCESS);
                if (service == IntPtr.Zero)
                    continue;

                try
                {
                    TakeoverNative.QUERY_SERVICE_CONFIG? config = TakeoverNative.QueryServiceConfig(service);
                    string? image = config is null ? null : ExePathFromCommand(Marshal.PtrToStringUni(config.Value.lpBinaryPathName) ?? string.Empty);

                    var (verdict, _) = GateFileIdentity(probe, image);
                    if (verdict != TakeoverPolicy.Eligibility.Eligible)
                        continue; // status audit-lists refusals; a pause acts only on gated targets

                    uint start = config!.Value.dwStartType;

                    // Stop first (bounded), then park at demand: the reference's
                    // "sc stop + sc config demand" pair, in one gated pass.
                    bool stopped = false;
                    var status = new TakeoverNative.SERVICE_STATUS();
                    if (TakeoverNative.QueryServiceStatus(service, ref status) &&
                        status.dwCurrentState is TakeoverNative.SERVICE_RUNNING
                            or TakeoverNative.SERVICE_START_PENDING
                            or TakeoverNative.SERVICE_STOP_PENDING)
                    {
                        TakeoverNative.ControlService(service, TakeoverNative.SERVICE_CONTROL_STOP, ref status);
                        WaitForState(service, TakeoverNative.SERVICE_STOPPED, TimeSpan.FromSeconds(5));
                        stopped = true;
                        actions.Add($"stopped service {name}");
                    }

                    bool parked = false;
                    if (start != TakeoverNative.SERVICE_DEMAND_START)
                    {
                        if (TakeoverNative.ChangeServiceConfig(
                                service, TakeoverNative.SERVICE_NO_CHANGE, TakeoverNative.SERVICE_DEMAND_START,
                                TakeoverNative.SERVICE_NO_CHANGE, null, null, IntPtr.Zero, null, null, null, null))
                        {
                            parked = true;
                            actions.Add($"parked service {name} at demand-start (was {start})");
                        }
                        else
                        {
                            actions.Add($"service {name}: start-type change refused");
                        }
                    }

                    // Journal whenever the pause changed something. The stop
                    // alone is a mutation: a service already parked at
                    // demand-start (a hand-started OEM service - the tester
                    // laptop's GCUBridge) gets no config write, and without
                    // this entry the restore had nothing to start.
                    if (stopped || parked)
                    {
                        journal.Add(new TakeoverJournalEntry
                        {
                            Kind = TakeoverMutationKind.ServiceStart,
                            Target = name,
                            Before = TakeoverServiceJournal.FormatBefore(start, stopped),
                            After = TakeoverNative.SERVICE_DEMAND_START.ToString(),
                            Utc = DateTime.UtcNow,
                        });
                    }
                }
                finally
                {
                    TakeoverNative.CloseServiceHandle(service);
                }
            }
        }

        /// <summary>Re-arms one journaled service. <c>Consumed</c> = the entry
        /// is done with (restored, target gone, or unreadable); <c>NeedsStart</c>
        /// = the service was RUNNING at pause time, so the resume cascade must
        /// start it. A stop with no config change (a hand-started service
        /// already at demand-start) carries the running flag and re-arms to
        /// the same value - the re-arm is a no-op, the start is the point.</summary>
        private static (bool Consumed, bool NeedsStart) RestoreServiceConfig(
            TakeoverJournalEntry entry, List<string> actions)
        {
            if (!TakeoverServiceJournal.TryParseBefore(entry.Before, out uint startType, out bool wasRunning) ||
                (startType != TakeoverNative.SERVICE_AUTO_START && startType != TakeoverNative.SERVICE_DEMAND_START))
            {
                actions.Add($"service {entry.Target}: journal entry has an unreadable Start value '{entry.Before}'");
                return (true, false); // consume the broken entry: it restores nothing, forever, otherwise
            }

            using var scm = new ScmScope();
            if (scm.Manager == IntPtr.Zero)
                return (false, false);

            IntPtr service = TakeoverNative.OpenService(scm.Manager, entry.Target, TakeoverNative.SERVICE_TAKEOVER_ACCESS);
            if (service == IntPtr.Zero)
            {
                actions.Add($"service {entry.Target}: gone (uninstalled while paused)");
                return (true, false); // consume: the target no longer exists
            }

            try
            {
                if (TakeoverNative.ChangeServiceConfig(
                        service, TakeoverNative.SERVICE_NO_CHANGE, startType,
                        TakeoverNative.SERVICE_NO_CHANGE, null, null, IntPtr.Zero, null, null, null, null))
                {
                    actions.Add($"re-armed service {entry.Target} (Start={startType})");
                    return (true, wasRunning);
                }

                actions.Add($"service {entry.Target}: re-arm refused");
                return (false, false);
            }
            finally
            {
                TakeoverNative.CloseServiceHandle(service);
            }
        }

        /// <summary>Reference resume cascade: the bridge starts first, the
        /// service it spawns is watched (six seconds), the rest get an
        /// explicit start. "Already running/stopped" and delete-pending are
        /// treated as success: the target is where the cascade wanted it.</summary>
        private static void StartCascade(List<string> serviceNames, List<string> actions)
        {
            if (serviceNames.Count == 0)
                return;

            var ordered = new List<string>(OemTakeoverTargets.ServiceNames);
            serviceNames.Sort((a, b) => ordered.IndexOf(a).CompareTo(ordered.IndexOf(b)));

            using var scm = new ScmScope();
            if (scm.Manager == IntPtr.Zero)
                return;

            foreach (string name in serviceNames)
            {
                // GCUService is the bridge's own spawn: starting it directly
                // while the bridge is coming up races the bridge. It gets the
                // watched start below instead.
                if (string.Equals(name, "GCUService", StringComparison.OrdinalIgnoreCase))
                    continue;

                IntPtr service = TakeoverNative.OpenService(scm.Manager, name, TakeoverNative.SERVICE_TAKEOVER_ACCESS);
                if (service == IntPtr.Zero)
                    continue;

                try
                {
                    var status = new TakeoverNative.SERVICE_STATUS();
                    if (TakeoverNative.QueryServiceStatus(service, ref status) && status.dwCurrentState == TakeoverNative.SERVICE_RUNNING)
                        continue;

                    if (!TakeoverNative.StartServiceW(service, 0, IntPtr.Zero))
                    {
                        int err = Marshal.GetLastWin32Error();
                        if (err is not (int)TakeoverNative.ERROR_SERVICE_ALREADY_RUNNING
                                    and not (int)TakeoverNative.ERROR_SERVICE_MARKED_FOR_DELETE)
                            actions.Add($"service {name}: start failed (Win32 {err})");
                        continue;
                    }

                    actions.Add($"started service {name}");

                    // Watchdog cascade window: the bridge spawns its service;
                    // give it the reference's budget before touching the rest.
                    if (string.Equals(name, "GCUBridge", StringComparison.OrdinalIgnoreCase))
                        System.Threading.Thread.Sleep(TimeSpan.FromSeconds(3));
                }
                finally
                {
                    TakeoverNative.CloseServiceHandle(service);
                }
            }

            // GCUService is the bridge's spawn: watch for it, start it only
            // if the bridge failed to bring it up.
            StartServiceIfNotRunningAfter(scm.Manager, "GCUService", TimeSpan.FromSeconds(6), actions);

            foreach (string name in serviceNames)
            {
                if (string.Equals(name, "GCUBridge", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(name, "GCUService", StringComparison.OrdinalIgnoreCase))
                    continue;

                StartServiceIfNotRunningAfter(scm.Manager, name, TimeSpan.FromMilliseconds(300), actions);
            }
        }

        /// <summary>Post-cascade audit: every service the pause stopped must be
        /// running after the restore. A survivor is reported with the SCM's
        /// own state, so the action list says exactly where the restore
        /// stopped - the field defect's signature was silence (the service
        /// stayed down and nothing said so).</summary>
        private static void VerifyStarted(List<string> serviceNames, List<string> actions)
        {
            if (serviceNames.Count == 0)
                return;

            using var scm = new ScmScope();
            if (scm.Manager == IntPtr.Zero)
                return;

            foreach (string name in serviceNames)
            {
                IntPtr service = TakeoverNative.OpenService(scm.Manager, name, TakeoverNative.SERVICE_TAKEOVER_ACCESS);
                if (service == IntPtr.Zero)
                    continue;

                try
                {
                    var status = new TakeoverNative.SERVICE_STATUS();
                    if (TakeoverNative.QueryServiceStatus(service, ref status) &&
                        status.dwCurrentState != TakeoverNative.SERVICE_RUNNING)
                    {
                        actions.Add($"service {name}: still not running after restore (state {status.dwCurrentState})");
                    }
                }
                finally
                {
                    TakeoverNative.CloseServiceHandle(service);
                }
            }
        }

        private static void StartServiceIfNotRunningAfter(IntPtr scm, string name, TimeSpan grace, List<string> actions)
        {
            IntPtr service = TakeoverNative.OpenService(scm, name, TakeoverNative.SERVICE_TAKEOVER_ACCESS);
            if (service == IntPtr.Zero)
                return;

            try
            {
                var deadline = DateTime.UtcNow + grace;
                var status = new TakeoverNative.SERVICE_STATUS();
                while (DateTime.UtcNow < deadline)
                {
                    if (TakeoverNative.QueryServiceStatus(service, ref status) && status.dwCurrentState == TakeoverNative.SERVICE_RUNNING)
                        return; // the bridge brought it up: no direct start needed
                    System.Threading.Thread.Sleep(300);
                }

                if (!TakeoverNative.StartServiceW(service, 0, IntPtr.Zero))
                {
                    int err = Marshal.GetLastWin32Error();
                    if (err is not (int)TakeoverNative.ERROR_SERVICE_ALREADY_RUNNING
                                and not (int)TakeoverNative.ERROR_SERVICE_MARKED_FOR_DELETE)
                        actions.Add($"service {name}: start failed (Win32 {err})");
                }
            }
            finally
            {
                TakeoverNative.CloseServiceHandle(service);
            }
        }

        private static void WaitForState(IntPtr service, uint wanted, TimeSpan budget)
        {
            var deadline = DateTime.UtcNow + budget;
            var status = new TakeoverNative.SERVICE_STATUS();
            while (DateTime.UtcNow < deadline)
            {
                if (TakeoverNative.QueryServiceStatus(service, ref status) && status.dwCurrentState == wanted)
                    return;
                System.Threading.Thread.Sleep(200);
            }
        }

        // ---- Processes ----------------------------------------------------------

        private static int CountOemProcesses(PublisherProbe probe, List<string> suspects)
        {
            int alive = 0;

            foreach (Process process in Process.GetProcesses())
            {
                try
                {
                    if (!OemTakeoverTargets.IsProcessName(process.ProcessName))
                        continue;

                    string? path = TakeoverNative.ProcessImagePath(process.Id);
                    var (verdict, reason) = GateFileIdentity(probe, path);

                    if (verdict == TakeoverPolicy.Eligibility.UcuSelf)
                        continue;

                    if (verdict == TakeoverPolicy.Eligibility.Eligible)
                        alive++;
                    else
                        suspects.Add($"process '{process.ProcessName}' (pid {process.Id}): {reason}");
                }
                catch (Exception)
                {
                    // Process vanished mid-enumeration: the takeover-relevant
                    // fact is that it is gone.
                }
                finally
                {
                    process.Dispose();
                }
            }

            return alive;
        }

        // ---- Tasks ----------------------------------------------------------------

        private static (int neutralized, List<string> suspects) ProbeTasks(
            PublisherProbe probe, List<TakeoverJournalEntry> journal, List<string> neutralizedSink, List<string>? audit)
        {
            int neutralized = 0;
            var suspects = new List<string>();
            var taskEvidence = JournalTargets(journal, TakeoverMutationKind.Task);

            OemScheduledTasks tasks;
            try
            {
                tasks = new OemScheduledTasks();
            }
            catch (Exception ex)
            {
                suspects.Add($"task scheduler unreachable: {ex.Message}");
                return (0, suspects);
            }

            using (tasks)
            foreach (OemTaskObservation task in tasks.EnumerateRootTasks())
            {
                if (!OemTakeoverTargets.IsTaskName(task.Name))
                    continue;

                var (verdict, reason) = GateTask(probe, task);
                if (verdict != TakeoverPolicy.Eligibility.Eligible)
                {
                    string line = $"task '{task.Name}': {reason}";
                    suspects.Add(line);
                    audit?.Add(line);
                    continue;
                }

                if (!task.Enabled && taskEvidence.Contains(task.Name.ToUpperInvariant()))
                {
                    neutralized++;
                    neutralizedSink.Add($"task {task.Name}");
                }
            }

            return (neutralized, suspects);
        }

        private static void PauseTasks(PublisherProbe probe, List<TakeoverJournalEntry> journal, List<string> actions)
        {
            OemScheduledTasks tasks;
            try
            {
                tasks = new OemScheduledTasks();
            }
            catch (Exception ex)
            {
                actions.Add($"task scheduler unreachable: {ex.Message}");
                return;
            }

            using (tasks)
            foreach (OemTaskObservation task in tasks.EnumerateRootTasks())
            {
                if (!OemTakeoverTargets.IsTaskName(task.Name) || !task.Enabled)
                    continue;

                var (verdict, _) = GateTask(probe, task);
                if (verdict != TakeoverPolicy.Eligibility.Eligible)
                    continue; // status audit-lists refusals

                bool? readback = tasks.SetEnabled(task.Name, false);
                if (readback == false)
                {
                    journal.Add(new TakeoverJournalEntry
                    {
                        Kind = TakeoverMutationKind.Task,
                        Target = task.Name,
                        Before = "1",
                        After = "0",
                        Utc = DateTime.UtcNow,
                    });
                    actions.Add($"disabled task {task.Name}");
                }
                else
                {
                    actions.Add($"task {task.Name}: disable refused{(readback is null ? " (task vanished)" : string.Empty)}");
                }
            }
        }

        private static bool RestoreTask(TakeoverJournalEntry entry, List<string> actions)
        {
            OemScheduledTasks tasks;
            try
            {
                tasks = new OemScheduledTasks();
            }
            catch (Exception ex)
            {
                actions.Add($"task scheduler unreachable: {ex.Message}");
                return false;
            }

            bool enable = entry.Before == "1";
            bool? readback;
            using (tasks)
                readback = tasks.SetEnabled(entry.Target, enable);
            if (readback == enable)
            {
                actions.Add($"task {entry.Target}: Enabled={readback}");
                return true;
            }

            actions.Add($"task {entry.Target}: enable state refused{(readback is null ? " (task vanished)" : $" (readback {readback})")}");
            return readback is null; // a vanished task consumes its entry
        }

        /// <summary>A task's identity is its Exec actions: every path must
        /// pass the file-identity gate, with two identity exemptions - a
        /// package payload (AppX-signed) and a system binary (the OS owns
        /// System32; a script-action task legitimately runs powershell.exe).
        /// No Exec paths at all (Script/Email actions, unreadable definition)
        /// = no identity = refuse.</summary>
        private static (TakeoverPolicy.Eligibility verdict, string? reason) GateTask(
            PublisherProbe probe, OemTaskObservation task)
        {
            if (task.ActionPaths.Count == 0)
                return (TakeoverPolicy.Eligibility.SquatSuspect, "no Exec action path to verify");

            var normalized = new List<string>(task.ActionPaths.Count);
            foreach (string rawPath in task.ActionPaths)
            {
                // Task actions routinely carry bare names or System32-relative
                // paths ("powershell.exe" - field-proven on the tester
                // laptop's ControlCenter task); normalize through the same
                // extractor the run-key scope uses before the gate sees it.
                normalized.Add(ExePathFromCommand(rawPath) ?? rawPath);
            }

            bool allSystemBinaries = true;
            foreach (string path in normalized)
            {
                if (!OemTakeoverTargets.IsUnderWindowsSystemDirectory(path))
                {
                    allSystemBinaries = false;
                    break;
                }
            }

            // System-identity exemption: every action is an OS binary, so the
            // executable identity is Windows' and the catalog name is the
            // task's whole identity (a user cannot plant a System32 image).
            if (allSystemBinaries)
                return (TakeoverPolicy.Eligibility.Eligible, null);

            foreach (string path in normalized)
            {
                var (verdict, reason) = GateFileIdentity(probe, path);
                if (verdict != TakeoverPolicy.Eligibility.Eligible)
                    return (verdict, $"action '{path}': {reason}");
            }

            return (TakeoverPolicy.Eligibility.Eligible, null);
        }

        // ---- The gate + journal -----------------------------------------------------

        /// <summary>The file-identity gate: path exists + WinVerifyTrust
        /// chain-valid + allowlist publisher, outside UCU roots (Core
        /// decides; this fills the observation from the real system).</summary>
        private static (TakeoverPolicy.Eligibility verdict, string? reason) GateFileIdentity(
            PublisherProbe probe, string? path)
        {
            if (path is null)
                return (TakeoverPolicy.Eligibility.SquatSuspect, "no image path to verify");

            if (OemTakeoverTargets.IsUnderUcuRoot(path))
                return (TakeoverPolicy.Eligibility.UcuSelf, null);

            bool exists = File.Exists(path);

            // Package-identified scope (MSIX payloads under WindowsApps): the
            // AppX package signature covers the file, per-file Authenticode
            // does not exist there, and the tree is writable only through
            // package identity - the policy's documented exemption. Probing
            // the file would answer "unsigned" for every package payload
            // (field-proven: SystrayComponent on the tester laptop).
            bool packageRoot = exists && OemTakeoverTargets.IsUnderPackageRoot(path);
            (bool valid, string? subject) = exists && !packageRoot
                ? probe.Probe(path)
                : (false, null);

            var obs = new TakeoverPolicy.TargetObservation
            {
                Name = path,
                Path = path,
                PathExists = exists,
                SignatureValid = packageRoot ? null : valid,
                PublisherSubject = subject,
            };

            TakeoverPolicy.Eligibility verdict = TakeoverPolicy.Evaluate(obs, keywordMatchedName: true);

            string? reason = !exists
                ? $"image '{path}' not found"
                : packageRoot
                    ? null
                    : !valid
                        ? $"image '{path}' is unsigned or its chain is invalid"
                        : !OemTakeoverTargets.MatchesPublisher(subject ?? string.Empty)
                            ? $"publisher '{subject}' is not on the OEM allowlist"
                            : null;

            return (verdict, reason);
        }

        private static HashSet<string> JournalTargets(List<TakeoverJournalEntry> journal, TakeoverMutationKind kind)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (TakeoverJournalEntry entry in journal)
                if (entry.Kind == kind)
                    set.Add(entry.Target);
            return set;
        }

        internal List<TakeoverJournalEntry> LoadJournal()
        {
            try
            {
                if (!File.Exists(_journalPath))
                    return new List<TakeoverJournalEntry>();

                return JsonSerializer.Deserialize(File.ReadAllBytes(_journalPath), TakeoverJournalJson.Default.ListTakeoverJournalEntry)
                       ?? new List<TakeoverJournalEntry>();
            }
            catch (Exception)
            {
                // The journal is the audit trail: a corrupt file is quarantined
                // (kept on disk, timestamped), never silently discarded.
                try
                {
                    string quarantine = _journalPath + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss");
                    File.Move(_journalPath, quarantine);
                }
                catch
                {
                    // A journal we cannot even move starts the session empty;
                    // the status's journal count reflects that honestly.
                }

                return new List<TakeoverJournalEntry>();
            }
        }

        internal void SaveJournal(List<TakeoverJournalEntry> journal)
        {
            string? directory = Path.GetDirectoryName(_journalPath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            File.WriteAllBytes(_journalPath, JsonSerializer.SerializeToUtf8Bytes(journal, TakeoverJournalJson.Default.ListTakeoverJournalEntry));
        }

        /// <summary>WinVerifyTrust + signer-subject per path, cached per pass
        /// (a pause inspects the same images across the process, service and
        /// task scopes).</summary>
        private sealed class PublisherProbe
        {
            private readonly Dictionary<string, (bool Valid, string? Subject)> _cache =
                new(StringComparer.OrdinalIgnoreCase);

            public (bool Valid, string? Subject) Probe(string path)
            {
                if (_cache.TryGetValue(path, out (bool, string?) hit))
                    return hit;

                bool valid = TakeoverNative.VerifySignedFile(path, out string? subject);

                var result = (valid, subject);
                _cache[path] = result;
                return result;
            }
        }
    }
}
