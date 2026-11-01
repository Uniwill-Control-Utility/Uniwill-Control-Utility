using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Win32;
using Serilog;
using UCU.Contracts;
using UCU.Contracts.Capabilities;
using UCU.Core.Takeover;

namespace Uniwill_Control_Utility.Services
{
    /// <summary>One journal row in the merged (engine + user) audit view.</summary>
    public sealed record OemJournalRow
    {
        public string Kind { get; init; } = string.Empty;

        public string Target { get; init; } = string.Empty;

        public DateTime Utc { get; init; }

        /// <summary>Local-time label for the list (the record carries the
        /// clock; the label is view-side formatting).</summary>
        public string TimeText { get; init; } = string.Empty;
    }

    /// <summary>The merged OEM takeover picture the Settings card renders.</summary>
    public sealed record OemOverview
    {
        /// <summary>A Core TakeoverPolicy.State name, decided from the MERGED
        /// counts (the engine answers machine scope alone; the user-scope
        /// journal and neutralized entries join here).</summary>
        public string State { get; init; } = "NotInstalled";

        public bool EngineReachable { get; init; }

        public int ServicesInstalled { get; init; }

        public int ServicesRunning { get; init; }

        public int OemProcessesRunning { get; init; }

        public int JournalCount { get; init; }

        public int AutostartNeutralized { get; init; }

        public IReadOnlyList<string> SquatSuspects { get; init; } = Array.Empty<string>();

        public IReadOnlyList<string> NeutralizedTargets { get; init; } = Array.Empty<string>();

        public IReadOnlyList<OemJournalRow> Journal { get; init; } = Array.Empty<OemJournalRow>();
    }

    /// <summary>The merged pause/restore report (toast + status refresh).</summary>
    public sealed record OemMergeReport
    {
        public bool EngineReachable { get; init; }

        public string State { get; init; } = "NotInstalled";

        public int MutationsApplied { get; init; }

        public int SquatSuspects { get; init; }

        public IReadOnlyList<string> Actions { get; init; } = Array.Empty<string>();
    }

    /// <summary>
    /// The OEM takeover composition (story U-203). The app side owns the
    /// USER scope: the HKCU Run key, the UWP AppModel startup-task State,
    /// the Explorer StartupApproved flags, and the user journal. The
    /// machine scope (services, tasks, HKLM, kills) is the engine's - the
    /// composition calls the verbs and merges the two scopes for the UI.
    ///
    /// Same hardening as the engine for every HKCU run-key value that
    /// carries a file identity (path + WinVerifyTrust chain + publisher
    /// allowlist, UCU's own roots untouchable - the dev-box probe showed
    /// UCU's own autostart entry is the only "uniwill" run-key match on a
    /// clean box, so the self-guard is not theoretical). The UWP AppModel
    /// and StartupApproved scopes are registry state identified by package
    /// family name - no file identity exists, and package identity is
    /// squat-immune (TakeoverPolicy's documented signature exemption).
    /// </summary>
    public sealed class OemTakeoverService
    {
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

        private const string SystemAppDataPath =
            @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\SystemAppData";

        private const string StartupApprovedPath =
            @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupTask";

        /// <summary>The engine's journal file (service mode): read-only here,
        /// merged into the audit view.</summary>
        public static string EngineJournalPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "UCU", "engine", "oem-takeover-journal.json");

        private readonly IOemCapability _engine;
        private readonly string _journalPath;
        private readonly object _gate = new();
        private bool _busy;

        public OemTakeoverService(IOemCapability engine)
        {
            _engine = engine;
            _journalPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "UCU", "oem-takeover-journal.json");
        }

        // ---- Overview -------------------------------------------------------

        public async Task<OemOverview> GetOverviewAsync()
        {
            OemStatusDto? machine = null;
            try
            {
                machine = await _engine.GetStatusAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "[OemTakeover] engine status unreachable; the card shows the user scope alone");
            }

            List<TakeoverJournalEntry> userJournal = LoadUserJournal();
            var userSuspects = new List<string>();
            var neutralized = new List<string>();

            int userNeutralized = ProbeUserScope(userSuspects, neutralized, userJournal);

            var inventory = new TakeoverPolicy.Inventory
            {
                ServicesInstalled = machine?.ServicesInstalled ?? 0,
                ServicesRunning = machine?.ServicesRunning ?? 0,
                OemProcessesRunning = machine?.OemProcessesRunning ?? 0,
                JournalCount = (machine?.JournalCount ?? 0) + userJournal.Count,
                AutostartNeutralized = (machine?.AutostartNeutralized ?? 0) + userNeutralized,
            };

            var suspects = new List<string>();
            if (machine is not null)
                suspects.AddRange(machine.SquatSuspects);
            suspects.AddRange(userSuspects);

            if (machine is not null)
                neutralized.InsertRange(0, machine.NeutralizedTargets);

            return new OemOverview
            {
                State = TakeoverPolicy.DecideState(inventory).ToString(),
                EngineReachable = machine is not null,
                ServicesInstalled = inventory.ServicesInstalled,
                ServicesRunning = inventory.ServicesRunning,
                OemProcessesRunning = inventory.OemProcessesRunning,
                JournalCount = inventory.JournalCount,
                AutostartNeutralized = inventory.AutostartNeutralized,
                SquatSuspects = suspects,
                NeutralizedTargets = neutralized,
                Journal = MergeJournal(userJournal),
            };
        }

        /// <summary>
        /// Whether the title bar should warn that the OEM stack is live. The
        /// warning exists because the takeover is otherwise discoverable only
        /// inside Settings, and a running OEM Control Center silently fights
        /// UCU's fan and power writes (the story's whole premise): a user who
        /// never opens that card would never learn why fan settings "don't
        /// stick". Active and PartiallyPaused are the two states where the
        /// conflict is happening right now; Paused/Stale are the states this
        /// feature created on purpose.
        /// </summary>
        public static bool ShouldWarn(OemOverview overview) =>
            overview is not null
            && overview.EngineReachable
            && overview.ServicesInstalled > 0
            && (overview.State == "Active" || overview.State == "PartiallyPaused");

        /// <summary>
        /// The action lines worth showing without opening the full card:
        /// anything that names a refusal or a failure ("refused", "failed",
        /// "still not running", "unreachable"). The journal list shows
        /// mutations, not outcomes, and a silent failure was the field
        /// defect's signature - a restore that left GCUBridge down said
        /// nothing at all.
        /// </summary>
        public static IReadOnlyList<string> Failures(OemMergeReport report, int max = 2)
        {
            var failures = new List<string>();
            if (report?.Actions is null || max <= 0)
                return failures;

            foreach (string line in report.Actions)
            {
                if (!line.Contains("refused", StringComparison.OrdinalIgnoreCase)
                    && !line.Contains("failed", StringComparison.OrdinalIgnoreCase)
                    && !line.Contains("still not running", StringComparison.OrdinalIgnoreCase)
                    && !line.Contains("unreachable", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                failures.Add(line);
                if (failures.Count >= max)
                    break;
            }

            return failures;
        }

        // ---- Pause / restore ---------------------------------------------------

        public Task<OemMergeReport> PauseAsync() => RunExclusive(async () =>
        {
            OemActionDto? machine = null;
            try
            {
                machine = await _engine.PauseAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[OemTakeover] the engine pause did not land; the user scope ran alone");
            }

            var userActions = new List<string>();
            PauseRunKeys(userActions);
            PauseUwpStartupTasks(userActions);
            PauseStartupApprovedFlags(userActions);

            return Compose(machine, userActions, engineMutations: machine?.MutationsApplied ?? 0);
        });

        public Task<OemMergeReport> ResumeAsync() => RunExclusive(async () =>
        {
            // Reference resume order across scopes: autostart state first
            // (run keys, UWP, flags), then the engine re-arms and STARTS the
            // services (its plan sequences the watchdog cascade itself).
            var userActions = new List<string>();
            RestoreUserScope(userActions);

            OemActionDto? machine = null;
            try
            {
                machine = await _engine.ResumeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[OemTakeover] the engine resume did not land; user scope restored alone");
            }

            return Compose(machine, userActions, engineMutations: machine?.MutationsApplied ?? 0);
        });

        private async Task<OemMergeReport> RunExclusive(Func<Task<OemMergeReport>> action)
        {
            lock (_gate)
            {
                if (_busy)
                    return new OemMergeReport { State = "Busy" };
                _busy = true;
            }

            try
            {
                OemMergeReport report = await action().ConfigureAwait(false);

                OemOverview overview = await GetOverviewAsync().ConfigureAwait(false);
                return report with { State = overview.State, EngineReachable = overview.EngineReachable };
            }
            finally
            {
                lock (_gate)
                    _busy = false;
            }
        }

        private OemMergeReport Compose(OemActionDto? machine, List<string> userActions, int engineMutations)
        {
            var actions = new List<string>();
            if (machine is not null)
                actions.AddRange(machine.Actions);
            actions.AddRange(userActions);

            return new OemMergeReport
            {
                EngineReachable = machine is not null,
                MutationsApplied = engineMutations + CountUserMutations(userActions),
                SquatSuspects = machine?.SquatSuspects ?? 0,
                Actions = actions,
            };
        }

        // The audit lines are one per applied mutation (refusals phrase as
        // "refused"/"failed"); the count keeps the toast honest.
        private static int CountUserMutations(List<string> userActions)
        {
            int applied = 0;
            foreach (string line in userActions)
                if (!line.Contains("refused", StringComparison.OrdinalIgnoreCase)
                    && !line.Contains("failed", StringComparison.OrdinalIgnoreCase)
                    && !line.Contains("unreachable", StringComparison.OrdinalIgnoreCase))
                    applied++;
            return applied;
        }

        // ---- HKCU run keys --------------------------------------------------------

        private void PauseRunKeys(List<string> actions)
        {
            using RegistryKey? run = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (run is null)
                return;

            foreach (string valueName in run.GetValueNames())
            {
                if (valueName.StartsWith(OemTakeoverTargets.RunKeyBackupPrefix, StringComparison.OrdinalIgnoreCase))
                    continue;

                string command = Convert.ToString(run.GetValue(valueName)) ?? string.Empty;

                if (OemTakeoverTargets.IsUnderUcuRoot(command) || OemTakeoverTargets.IsUnderUcuRoot(valueName))
                    continue; // self-guard: UCU's own autostart entry is never a target

                if (!OemTakeoverTargets.MatchesKeyword(valueName) && !OemTakeoverTargets.MatchesKeyword(command))
                    continue;

                string? exe = ExePathFromCommand(command);
                (TakeoverPolicy.Eligibility verdict, string? reason) = GateFileIdentity(exe);
                if (verdict != TakeoverPolicy.Eligibility.Eligible)
                {
                    if (verdict == TakeoverPolicy.Eligibility.SquatSuspect)
                        actions.Add($"run key HKCU\\Run\\{valueName}: refused ({reason})");
                    continue;
                }

                string backupName = OemTakeoverTargets.RunKeyBackupPrefix + valueName;
                try
                {
                    run.SetValue(backupName, command, run.GetValueKind(valueName));
                    run.DeleteValue(valueName);
                    AppendJournal(new TakeoverJournalEntry
                    {
                        Kind = TakeoverMutationKind.RunKey,
                        Target = $@"HKCU\Run\{valueName}",
                        Before = $"{valueName}={command}",
                        After = $"{backupName}={command}",
                        Utc = DateTime.UtcNow,
                    });
                    actions.Add($@"neutralized run key HKCU\Run\{valueName}");
                }
                catch (Exception ex)
                {
                    actions.Add($@"run key HKCU\Run\{valueName}: write refused ({ex.Message})");
                }
            }
        }

        private bool RestoreRunKey(TakeoverJournalEntry entry, List<string> actions)
        {
            int lastSep = entry.Target.LastIndexOf('\\');
            string valueName = lastSep > 0 ? entry.Target[(lastSep + 1)..] : entry.Target;
            string backupName = OemTakeoverTargets.RunKeyBackupPrefix + valueName;

            using RegistryKey? run = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (run is null)
                return false;

            try
            {
                object? current = run.GetValue(backupName);
                if (current is null)
                    return true; // already restored (idempotent)

                run.SetValue(valueName, Convert.ToString(current) ?? string.Empty, run.GetValueKind(backupName));
                run.DeleteValue(backupName);
                actions.Add($@"restored run key HKCU\Run\{valueName}");
                return true;
            }
            catch (Exception ex)
            {
                actions.Add($@"run key HKCU\Run\{valueName}: restore refused ({ex.Message})");
                return false;
            }
        }

        /// <summary>Mirrors the engine's extraction (the format is the
        /// shared file shape; a drift between the two readers is a bug that
        /// strands run keys, so one documented copy per scope).</summary>
        internal static string? ExePathFromCommand(string command)
        {
            if (string.IsNullOrWhiteSpace(command))
                return null;

            string s = command.Trim();

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
                token = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), token);

            return token;
        }

        // ---- UWP AppModel startup tasks (State: 1 = disabled, 2 = enabled) -------

        private void PauseUwpStartupTasks(List<string> actions)
        {
            using RegistryKey? baseKey = Registry.CurrentUser.OpenSubKey(SystemAppDataPath, writable: false);
            if (baseKey is null)
                return;

            foreach (string familyName in baseKey.GetSubKeyNames())
            {
                if (!OemTakeoverTargets.MatchesKeyword(familyName))
                    continue;

                using RegistryKey? family = baseKey.OpenSubKey(familyName, writable: false);
                if (family is null)
                    continue;

                foreach (string taskName in family.GetSubKeyNames())
                {
                    string target = $@"{familyName}\{taskName}";

                    using RegistryKey? task = family.OpenSubKey(taskName, writable: true);
                    object? state = task?.GetValue("State");
                    if (task is null || state is null)
                        continue;

                    int stateValue = Convert.ToInt32(state);
                    if (stateValue == 1)
                        continue; // already disabled

                    try
                    {
                        task.SetValue("State", 1, RegistryValueKind.DWord);
                        AppendJournal(new TakeoverJournalEntry
                        {
                            Kind = TakeoverMutationKind.UwpState,
                            Target = target,
                            Before = stateValue.ToString(),
                            After = "1",
                            Utc = DateTime.UtcNow,
                        });
                        actions.Add($"disabled UWP startup task {target}");
                    }
                    catch (Exception ex)
                    {
                        actions.Add($"UWP startup task {target}: write refused ({ex.Message})");
                    }
                }
            }
        }

        private bool RestoreUwpStartupTask(TakeoverJournalEntry entry, List<string> actions)
        {
            int lastSep = entry.Target.LastIndexOf('\\');
            if (lastSep <= 0 || !int.TryParse(entry.Before, out int wanted))
            {
                actions.Add($"UWP startup task {entry.Target}: journal entry unreadable");
                return true; // consume the broken entry
            }

            string familyName = entry.Target[..lastSep];
            string taskName = entry.Target[(lastSep + 1)..];

            using RegistryKey? baseKey = Registry.CurrentUser.OpenSubKey(SystemAppDataPath, writable: false);
            using RegistryKey? family = baseKey?.OpenSubKey(familyName, writable: false);
            using RegistryKey? task = family?.OpenSubKey(taskName, writable: true);
            if (task is null)
            {
                actions.Add($"UWP startup task {entry.Target}: package gone (uninstalled while paused)");
                return true; // consume: the target no longer exists
            }

            try
            {
                task.SetValue("State", wanted, RegistryValueKind.DWord);
                actions.Add($"restored UWP startup task {entry.Target} (State={wanted})");
                return true;
            }
            catch (Exception ex)
            {
                actions.Add($"UWP startup task {entry.Target}: restore refused ({ex.Message})");
                return false;
            }
        }

        // ---- Explorer StartupApproved StartupTask flags (byte[2]: 02 enabled, 03 disabled) ----

        private void PauseStartupApprovedFlags(List<string> actions)
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(StartupApprovedPath, writable: true);
            if (key is null)
                return;

            foreach (string valueName in key.GetValueNames())
            {
                if (!OemTakeoverTargets.MatchesKeyword(valueName))
                    continue;

                byte[]? blob = key.GetValue(valueName) as byte[];
                if (blob is null || blob.Length < 3)
                    continue;

                if (blob[2] == 0x03)
                    continue; // already disabled

                try
                {
                    byte[] paused = (byte[])blob.Clone();
                    paused[2] = 0x03;
                    key.SetValue(valueName, paused, RegistryValueKind.Binary);
                    AppendJournal(new TakeoverJournalEntry
                    {
                        Kind = TakeoverMutationKind.StartupApproved,
                        Target = valueName,
                        Before = blob[2].ToString("X2"),
                        After = "03",
                        Utc = DateTime.UtcNow,
                    });
                    actions.Add($"disabled startup task flag {valueName}");
                }
                catch (Exception ex)
                {
                    actions.Add($"startup task flag {valueName}: write refused ({ex.Message})");
                }
            }
        }

        private bool RestoreStartupApprovedFlag(TakeoverJournalEntry entry, List<string> actions)
        {
            if (!byte.TryParse(entry.Before, System.Globalization.NumberStyles.HexNumber, null, out byte wanted))
            {
                actions.Add($"startup task flag {entry.Target}: journal entry unreadable");
                return true; // consume
            }

            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(StartupApprovedPath, writable: true);
            byte[]? blob = key?.GetValue(entry.Target) as byte[];
            if (key is null || blob is null || blob.Length < 3)
            {
                actions.Add($"startup task flag {entry.Target}: gone (uninstalled while paused)");
                return true; // consume
            }

            try
            {
                byte[] restored = (byte[])blob.Clone();
                restored[2] = wanted;
                key.SetValue(entry.Target, restored, RegistryValueKind.Binary);
                actions.Add($"restored startup task flag {entry.Target} ({wanted:X2})");
                return true;
            }
            catch (Exception ex)
            {
                actions.Add($"startup task flag {entry.Target}: restore refused ({ex.Message})");
                return false;
            }
        }

        // ---- User restore (Core plan over the user journal) --------------------------

        private void RestoreUserScope(List<string> actions)
        {
            List<TakeoverJournalEntry> journal = LoadUserJournal();
            IReadOnlyList<TakeoverJournalEntry> plan = TakeoverPolicy.PlanRestore(journal);
            var consumed = new HashSet<(TakeoverMutationKind, string)>();

            foreach (TakeoverJournalEntry entry in plan)
            {
                bool reversed = entry.Kind switch
                {
                    TakeoverMutationKind.RunKey => RestoreRunKey(entry, actions),
                    TakeoverMutationKind.UwpState => RestoreUwpStartupTask(entry, actions),
                    TakeoverMutationKind.StartupApproved => RestoreStartupApprovedFlag(entry, actions),
                    // Machine-scope kinds live in the engine journal; the
                    // merged view shows them, the user scope never restores
                    // them. Kills drop out in PlanRestore.
                    _ => true,
                };

                if (reversed)
                    consumed.Add((entry.Kind, entry.Target.ToUpperInvariant()));
            }

            if (consumed.Count > 0)
            {
                journal.RemoveAll(e => consumed.Contains((e.Kind, e.Target.ToUpperInvariant())));
                SaveUserJournal(journal);
            }
        }

        // ---- User-scope probe (inventory + suspects, no mutation) ----------------------

        private int ProbeUserScope(List<string> suspects, List<string> neutralized, List<TakeoverJournalEntry> journal)
        {
            int count = 0;

            using (RegistryKey? run = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false))
            {
                if (run is not null)
                {
                    foreach (string valueName in run.GetValueNames())
                    {
                        bool backup = valueName.StartsWith(OemTakeoverTargets.RunKeyBackupPrefix, StringComparison.OrdinalIgnoreCase);
                        string realName = backup
                            ? valueName[OemTakeoverTargets.RunKeyBackupPrefix.Length..]
                            : valueName;

                        if (OemTakeoverTargets.IsUnderUcuRoot(realName))
                            continue;

                        string command = Convert.ToString(run.GetValue(valueName)) ?? string.Empty;
                        if (OemTakeoverTargets.IsUnderUcuRoot(command))
                            continue;

                        if (!OemTakeoverTargets.MatchesKeyword(realName) && !OemTakeoverTargets.MatchesKeyword(command))
                            continue;

                        if (backup)
                        {
                            count++;
                            neutralized.Add($@"HKCU\Run\{realName}");
                            continue;
                        }

                        (TakeoverPolicy.Eligibility verdict, string? reason) = GateFileIdentity(ExePathFromCommand(command));
                        if (verdict == TakeoverPolicy.Eligibility.SquatSuspect)
                            suspects.Add($@"run key HKCU\Run\{valueName}: {reason}");
                    }
                }
            }

            var uwpEvidence = JournalTargets(journal, TakeoverMutationKind.UwpState);
            using (RegistryKey? baseKey = Registry.CurrentUser.OpenSubKey(SystemAppDataPath, writable: false))
            {
                if (baseKey is not null)
                {
                    foreach (string familyName in baseKey.GetSubKeyNames())
                    {
                        if (!OemTakeoverTargets.MatchesKeyword(familyName))
                            continue;

                        using RegistryKey? family = baseKey.OpenSubKey(familyName, writable: false);
                        if (family is null)
                            continue;

                        foreach (string taskName in family.GetSubKeyNames())
                        {
                            int? state = family.OpenSubKey(taskName)?.GetValue("State") is object s
                                ? Convert.ToInt32(s)
                                : null;

                            string target = $@"{familyName}\{taskName}";
                            if (state == 1 && uwpEvidence.Contains(target.ToUpperInvariant()))
                            {
                                count++;
                                neutralized.Add($"UWP task {target}");
                            }
                        }
                    }
                }
            }

            var flagEvidence = JournalTargets(journal, TakeoverMutationKind.StartupApproved);
            using (RegistryKey? key = Registry.CurrentUser.OpenSubKey(StartupApprovedPath, writable: false))
            {
                if (key is not null)
                {
                    foreach (string valueName in key.GetValueNames())
                    {
                        if (!OemTakeoverTargets.MatchesKeyword(valueName))
                            continue;

                        byte[]? blob = key.GetValue(valueName) as byte[];
                        if (blob is not null && blob.Length >= 3 && blob[2] == 0x03
                            && flagEvidence.Contains(valueName.ToUpperInvariant()))
                        {
                            count++;
                            neutralized.Add($"startup flag {valueName}");
                        }
                    }
                }
            }

            return count;
        }

        private static HashSet<string> JournalTargets(List<TakeoverJournalEntry> journal, TakeoverMutationKind kind)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (TakeoverJournalEntry entry in journal)
                if (entry.Kind == kind)
                    set.Add(entry.Target);
            return set;
        }

        // ---- The file-identity gate (user scope; same clauses as the engine) -----------

        private static (TakeoverPolicy.Eligibility verdict, string? reason) GateFileIdentity(string? path)
        {
            if (path is null)
                return (TakeoverPolicy.Eligibility.SquatSuspect, "no image path to verify");

            if (OemTakeoverTargets.IsUnderUcuRoot(path))
                return (TakeoverPolicy.Eligibility.UcuSelf, null);

            if (!File.Exists(path))
                return (TakeoverPolicy.Eligibility.SquatSuspect, $"image '{path}' not found");

            // Package-identified scope: MSIX payloads are covered by the AppX
            // package signature, never per-file Authenticode (the same
            // exemption the policy documents; the tree is writable only
            // through package identity).
            if (OemTakeoverTargets.IsUnderPackageRoot(path))
                return (TakeoverPolicy.Eligibility.Eligible, null);

            bool valid = VerifySignedFile(path, out string? subject);

            if (!valid)
                return (TakeoverPolicy.Eligibility.SquatSuspect, $"image '{path}' is unsigned or its chain is invalid");

            if (!OemTakeoverTargets.MatchesPublisher(subject ?? string.Empty))
                return (TakeoverPolicy.Eligibility.SquatSuspect, $"publisher '{subject}' is not on the OEM allowlist");

            return (TakeoverPolicy.Eligibility.Eligible, null);
        }

        // ---- Journal store (user scope; same file shape as the engine's) ---------------

        internal List<TakeoverJournalEntry> LoadUserJournal()
        {
            try
            {
                if (!File.Exists(_journalPath))
                    return new List<TakeoverJournalEntry>();

                return JsonSerializer.Deserialize<List<TakeoverJournalEntry>>(File.ReadAllText(_journalPath))
                       ?? new List<TakeoverJournalEntry>();
            }
            catch (Exception)
            {
                try
                {
                    File.Move(_journalPath, _journalPath + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss"));
                }
                catch
                {
                    // Unmovable: the session starts with an empty journal.
                }

                return new List<TakeoverJournalEntry>();
            }
        }

        private void AppendJournal(TakeoverJournalEntry entry)
        {
            List<TakeoverJournalEntry> journal = LoadUserJournal();
            journal.Add(entry);
            SaveUserJournal(journal);
        }

        private void SaveUserJournal(List<TakeoverJournalEntry> journal)
        {
            string? directory = Path.GetDirectoryName(_journalPath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            File.WriteAllText(_journalPath, JsonSerializer.Serialize(journal));
        }

        private IReadOnlyList<OemJournalRow> MergeJournal(List<TakeoverJournalEntry> userJournal)
        {
            var rows = new List<OemJournalRow>();

            foreach (TakeoverJournalEntry entry in userJournal)
                rows.Add(new OemJournalRow { Kind = entry.Kind.ToString(), Target = entry.Target, Utc = entry.Utc, TimeText = entry.Utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm") });

            try
            {
                if (File.Exists(EngineJournalPath))
                {
                    List<TakeoverJournalEntry>? machine =
                        JsonSerializer.Deserialize<List<TakeoverJournalEntry>>(File.ReadAllText(EngineJournalPath));

                    if (machine is not null)
                        foreach (TakeoverJournalEntry entry in machine)
                            rows.Add(new OemJournalRow { Kind = entry.Kind.ToString(), Target = entry.Target, Utc = entry.Utc, TimeText = entry.Utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm") });
                }
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "[OemTakeover] the engine journal is not readable for the merged view");
            }

            rows.Sort((a, b) => b.Utc.CompareTo(a.Utc));
            return rows;
        }

        // ---- Authenticode (user-scope P/Invoke; same clauses as the engine) -----------
        // WinVerifyTrust answers "trusted?" but not "by whom?"; the signer
        // subject is the publisher evidence the allowlist compares, read from
        // the VERIFY state's provider chain (the documented WTHelper walk).
        // A certificate-file loader answers nothing for a PE with an embedded
        // signature - field-proven on the tester laptop, pack #23 round:
        // every signed OEM binary read with an empty subject.

        private const uint WtdStateActionVerify = 1;
        private const uint WtdStateActionClose = 2;
        private const uint X509AsnEncoding = 0x00000001;
        private const uint Pkcs7AsnEncoding = 0x00010000;
        private const uint CertSimpleNameStr = 1;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WintrustFileInfo
        {
            public uint cbStruct;
            [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
            public IntPtr hFile;
            public IntPtr pgKnownSubject;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WintrustData
        {
            public uint cbStruct;
            public IntPtr pPolicyCallbackData;
            public IntPtr pSIPClientData;
            public uint dwUIChoice;
            public uint fdwRevocationChecks;
            public uint dwUnionChoice;
            public IntPtr pFileData;
            public uint dwStateAction;
            public IntPtr hWVTStateData;
            public IntPtr pwszURLReference;
            public uint dwProvFlags;
            public uint dwUIContext;
            public IntPtr pSignatureSettings;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct CryptIntegerBlob
        {
            public uint cbData;
            public IntPtr pbData;
        }

        /// <summary>wincrypt's FILETIME: two DWORDs, 4-byte aligned (a CLR
        /// long would shift every following field's offset).</summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct NativeFileTime
        {
            public uint dwLowDateTime;
            public uint dwHighDateTime;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct CryptAlgorithmIdentifier
        {
            public IntPtr pszObjId;
            public CryptIntegerBlob Parameters;
        }

        /// <summary>The CERT_INFO prefix the subject walk needs.</summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct CertInfoPrefix
        {
            public uint dwVersion;
            public CryptIntegerBlob SerialNumber;
            public CryptAlgorithmIdentifier SignatureAlgorithm;
            public CryptIntegerBlob Issuer;
            public NativeFileTime NotBefore;
            public NativeFileTime NotAfter;
            public CryptIntegerBlob Subject;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct CertContextPrefix
        {
            public uint dwCertEncodingType;
            public IntPtr pbCertEncoded;
            public uint cbCertEncoded;
            public IntPtr pCertInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct CryptProviderCertPrefix
        {
            public uint cbStruct;
            public IntPtr pCert;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct CryptProviderSgnrPrefix
        {
            public uint cbStruct;
            public NativeFileTime sftVerifyAsOf;
            public uint csCertChain;
            public IntPtr pasCertChain;
        }

        [DllImport("wintrust.dll", SetLastError = true)]
        private static extern uint WinVerifyTrust(IntPtr hWnd, in Guid pgActionID, ref WintrustData pWVTData);

        [DllImport("wintrust.dll", SetLastError = true)]
        private static extern IntPtr WTHelperProvDataFromStateData(IntPtr hStateData);

        [DllImport("wintrust.dll", SetLastError = true)]
        private static extern IntPtr WTHelperGetProvSignerFromChain(
            IntPtr pProvData, uint dwIndex, [MarshalAs(UnmanagedType.Bool)] bool fCounterSigner, uint dwIndexCounterSigner);

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern int CertNameToStrW(
            uint dwCertEncodingType, ref CryptIntegerBlob pName, uint dwStrType, StringBuilder? psz, int csz);

        private static readonly Guid WinTrustActionGenericVerifyV2 =
            new(0x00AAC56B, 0xCD44, 0x11D0, 0x8C, 0xC2, 0x00, 0xC0, 0x4F, 0xC2, 0x95, 0xEE);

        private static bool VerifySignedFile(string filePath, out string? subject)
        {
            subject = null;

            var fileInfo = new WintrustFileInfo
            {
                cbStruct = (uint)Marshal.SizeOf<WintrustFileInfo>(),
                pcwszFilePath = filePath,
            };

            IntPtr fileInfoPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WintrustFileInfo>());
            var data = new WintrustData
            {
                cbStruct = (uint)Marshal.SizeOf<WintrustData>(),
                dwUIChoice = 2,          // WTD_UI_NONE
                fdwRevocationChecks = 0, // WTD_REVOKE_NONE (offline chain)
                dwUnionChoice = 1,       // WTD_CHOICE_FILE
                dwStateAction = WtdStateActionVerify,
            };

            uint status;
            try
            {
                Marshal.StructureToPtr(fileInfo, fileInfoPtr, false);
                data.pFileData = fileInfoPtr;

                status = WinVerifyTrust(IntPtr.Zero, in WinTrustActionGenericVerifyV2, ref data);

                if (status == 0 && data.hWVTStateData != IntPtr.Zero)
                    subject = ReadSignerSubject(data.hWVTStateData);
            }
            finally
            {
                if (data.hWVTStateData != IntPtr.Zero)
                {
                    WintrustData close = data;
                    close.dwStateAction = WtdStateActionClose;
                    WinVerifyTrust(IntPtr.Zero, in WinTrustActionGenericVerifyV2, ref close);
                }

                Marshal.DestroyStructure<WintrustFileInfo>(fileInfoPtr);
                Marshal.FreeHGlobal(fileInfoPtr);
            }

            return status == 0;
        }

        private static string? ReadSignerSubject(IntPtr hStateData)
        {
            try
            {
                IntPtr providerData = WTHelperProvDataFromStateData(hStateData);
                if (providerData == IntPtr.Zero)
                    return null;

                IntPtr signer = WTHelperGetProvSignerFromChain(providerData, 0, false, 0);
                if (signer == IntPtr.Zero)
                    return null;

                CryptProviderSgnrPrefix sgnr = Marshal.PtrToStructure<CryptProviderSgnrPrefix>(signer);
                if (sgnr.csCertChain == 0 || sgnr.pasCertChain == IntPtr.Zero)
                    return null;

                IntPtr certificate = Marshal.PtrToStructure<CryptProviderCertPrefix>(sgnr.pasCertChain).pCert;
                if (certificate == IntPtr.Zero)
                    return null;

                CertContextPrefix context = Marshal.PtrToStructure<CertContextPrefix>(certificate);
                if (context.pCertInfo == IntPtr.Zero)
                    return null;

                CertInfoPrefix info = Marshal.PtrToStructure<CertInfoPrefix>(context.pCertInfo);
                if (info.Subject.pbData == IntPtr.Zero || info.Subject.cbData == 0)
                    return null;

                int length = CertNameToStrW(
                    X509AsnEncoding | Pkcs7AsnEncoding, ref info.Subject, CertSimpleNameStr, null, 0);
                if (length <= 1)
                    return null;

                var text = new StringBuilder(length);
                length = CertNameToStrW(
                    X509AsnEncoding | Pkcs7AsnEncoding, ref info.Subject, CertSimpleNameStr, text, text.Capacity);
                return length > 1 ? text.ToString() : null;
            }
            catch
            {
                return null;
            }
        }
    }
}
