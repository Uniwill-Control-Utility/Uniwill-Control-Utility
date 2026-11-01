using System;

namespace UCU.Core.Takeover
{
    /// <summary>What a takeover mutation did (the journal taxonomy).</summary>
    public enum TakeoverMutationKind
    {
        /// <summary>A Run-key value moved to the backup name (sandbox.rs:160-172).</summary>
        RunKey,

        /// <summary>A service's Start type changed (sc config demand, lifecycle.rs:112-113).</summary>
        ServiceStart,

        /// <summary>A scheduled task was disabled (sandbox.rs:247-255).</summary>
        Task,

        /// <summary>A UWP AppModel startup task State was set (sandbox.rs:269-353).</summary>
        UwpState,

        /// <summary>An Explorer StartupApproved StartupTask flag byte was set
        /// (sandbox.rs:356-415).</summary>
        StartupApproved,

        /// <summary>A verified OEM process was terminated (live effect: the
        /// journal records it for the audit view; a process kill has no
        /// "old value" to write back - restore relaunches the stack via the
        /// service/task starts, the reference's resume order).</summary>
        ProcessKilled,
    }

    /// <summary>
    /// One reversible mutation record (the plan's TakeoverJournal entry:
    /// "every mutation recorded to a reversible journal"). Pure data: the
    /// executor (UCU.Hardware) observes Before and writes the entry; the
    /// clock is injected (Core never reads time). The journal file lives
    /// next to the engine; the RESTORE PLAN is computed here, purely.
    /// </summary>
    public sealed record TakeoverJournalEntry
    {
        public TakeoverMutationKind Kind { get; init; }

        /// <summary>Identity of the mutated object: the run-key value name
        /// (root-qualified), service name, task path, UWP subkey, flag name,
        /// process image path.</summary>
        public string Target { get; init; } = "";

        /// <summary>The pre-mutation state, textual (the registry value's
        /// "name=value", the service Start dword plus the optional
        /// <c>|running</c> flag, the task Enabled, the State/flag byte as
        /// hex). Empty for facts with no prior value (a kill, a task that was
        /// already disabled).</summary>
        public string Before { get; init; } = "";

        /// <summary>The post-mutation state, same textual shape.</summary>
        public string After { get; init; } = "";

        public DateTime Utc { get; init; }
    }

    /// <summary>
    /// The ServiceStart journal entry's text shape, shared by the executor
    /// (writes it on pause) and the restore (reads it) so the two can never
    /// drift. <c>Before</c> is the pre-pause start type plus, when the
    /// service was actually running at pause time, a <c>|running</c> flag.
    ///
    /// The flag exists because the start type alone is not the whole
    /// pre-state: a service already at demand-start is stopped without any
    /// config change, and without the flag that stop is unrecorded - the
    /// field defect (tester laptop: GCUBridge started by hand, paused, and
    /// the restore had nothing to start). Legacy entries carry the bare
    /// dword and restore as a re-arm only.
    /// </summary>
    public static class TakeoverServiceJournal
    {
        public const string RunningFlag = "running";

        public static string FormatBefore(uint startType, bool wasRunning) =>
            wasRunning ? $"{startType}|{RunningFlag}" : startType.ToString();

        /// <summary>Reads a <c>Before</c> value. False for an unreadable
        /// entry (a corrupt journal line restores nothing, forever, and is
        /// consumed by the caller).</summary>
        public static bool TryParseBefore(string? before, out uint startType, out bool wasRunning)
        {
            startType = 0;
            wasRunning = false;

            if (string.IsNullOrWhiteSpace(before))
                return false;

            string text = before!.Trim();
            int separator = text.IndexOf('|');
            string number = separator < 0 ? text : text[..separator];
            if (!uint.TryParse(number, out startType))
                return false;

            if (separator >= 0)
            {
                string flag = text[(separator + 1)..];
                wasRunning = string.Equals(flag, RunningFlag, StringComparison.OrdinalIgnoreCase);
            }

            return true;
        }
    }

    /// <summary>
    /// The takeover decision core (story U-203): pure, no registry, no
    /// services, no processes. The eligibility gate is a data function over
    /// an observation (the executor probes the real system and feeds facts
    /// in); the restore planner turns journal entries into an idempotent,
    /// correctly ordered restore plan; the status decider answers
    /// absent/active/paused/partial from an inventory summary.
    /// </summary>
    public static class TakeoverPolicy
    {
        /// <summary>The eligibility gate. Everything the executor must have
        /// observed about a target; the gate decides touch / never-touch and
        /// names the reason (the reason is audit text, shown in the status).</summary>
        public sealed record TargetObservation
        {
            public string Name { get; init; } = "";

            /// <summary>The image path the target resolves to (service
            /// ImagePath, process MainModule, run-key command, task action
            /// path). Null = the target has no executable identity.</summary>
            public string? Path { get; init; }

            public bool PathExists { get; init; }

            /// <summary>WinVerifyTrust verdict for the path (the executor
            /// observes; Core never calls WinAPI). Null = no signature
            /// observation made (UWP flags have no file identity).</summary>
            public bool? SignatureValid { get; init; }

            /// <summary>The signer certificate's subject (CN), null when
            /// unsigned/unobserved.</summary>
            public string? PublisherSubject { get; init; }
        }

        public enum Eligibility
        {
            /// <summary>All hardening clauses pass: safe to mutate.</summary>
            Eligible,

            /// <summary>Name-squat suspect: catalog name / keyword match, but
            /// the path+publisher gate failed. NEVER touched; audit-listed.</summary>
            SquatSuspect,

            /// <summary>UCU's own install tree. Never touched, never even
            /// audit-listed as a suspect (the box runs "Uniwill Control
            /// Utility"; this is the self-squat guard).</summary>
            UcuSelf,

            /// <summary>No name-catalog / keyword match at all.</summary>
            NotTarget,
        }

        /// <summary>The hardening decision for one observed target. The
        /// signature exemption (null SignatureValid) exists for the UWP
        /// StartupApproved/AppModel flags: registry state bytes, no file
        /// identity, and package identity (not a bare name) makes them
        /// squat-immune - the reference's UWP scope stays in full.</summary>
        public static Eligibility Evaluate(TargetObservation obs, bool keywordMatchedName)
        {
            if (obs is null)
                throw new ArgumentNullException(nameof(obs));

            bool named = keywordMatchedName;
            if (!named)
                return Eligibility.NotTarget;

            // The self-squat guard outranks everything: UCU roots are untouchable.
            if (OemTakeoverTargets.IsUnderUcuRoot(obs.Path ?? obs.Name))
                return Eligibility.UcuSelf;

            // Signature-exempt scope: pure registry state (no file identity observed).
            if (obs.SignatureValid is null && obs.Path is null)
                return Eligibility.Eligible;

            // Package-identified scope: MSIX payloads under the WindowsApps root
            // are covered by the AppX package signature (never per-file
            // Authenticode) and the root is writable only through package
            // identity - squat-immune by construction. The path must still exist:
            // a vanished payload is not a target.
            if (obs.Path is not null && OemTakeoverTargets.IsUnderPackageRoot(obs.Path))
                return obs.PathExists ? Eligibility.Eligible : Eligibility.SquatSuspect;

            // The file-identity scope: path must exist, be signed, chain-valid,
            // and publisher-matched. Any failure = squat suspect.
            if (obs.Path is null || obs.Path.Length == 0 || !obs.PathExists)
                return Eligibility.SquatSuspect;
            if (obs.SignatureValid != true)
                return Eligibility.SquatSuspect;
            if (!OemTakeoverTargets.MatchesPublisher(obs.PublisherSubject ?? string.Empty))
                return Eligibility.SquatSuspect;

            return Eligibility.Eligible;
        }

        /// <summary>
        /// The restore plan: journal entries -> the mutations to replay in
        /// reverse, idempotently. Rules:
        /// 1. Kills are audit-only (no reversible value); they drop out.
        /// 2. The LAST entry per (kind, target) is the live state - the plan
        ///    replays it once, never twice (repeated pauses journal repeatedly;
        ///    the restore stays single-shot per target).
        /// 3. ServiceStart restores come LAST: restoring the registry/autostart
        ///    scope first, then re-arming services, prevents the stopped
        ///    services' watchdogs from re-killing mid-restore (the reference's
        ///    "watchdog cascading" note - it sequences services last too).
        /// </summary>
        public static System.Collections.Generic.IReadOnlyList<TakeoverJournalEntry> PlanRestore(
            System.Collections.Generic.IReadOnlyList<TakeoverJournalEntry> journal)
        {
            if (journal is null)
                throw new ArgumentNullException(nameof(journal));

            // Keep the newest entry per (kind, target), preserving journal order
            // for the dedup (the file is append-only, newest last).
            var latest = new System.Collections.Generic.Dictionary<(TakeoverMutationKind, string), TakeoverJournalEntry>();
            var order = new System.Collections.Generic.List<(TakeoverMutationKind, string)>();
            foreach (TakeoverJournalEntry entry in journal)
            {
                if (entry.Kind == TakeoverMutationKind.ProcessKilled)
                    continue;

                var key = (entry.Kind, EntryKey(entry));
                if (!latest.ContainsKey(key))
                    order.Add(key);
                latest[key] = entry;
            }

            var plan = new System.Collections.Generic.List<TakeoverJournalEntry>();
            foreach (var key in order)
                if (key.Item1 != TakeoverMutationKind.ServiceStart)
                    plan.Add(latest[key]);
            foreach (var key in order)
                if (key.Item1 == TakeoverMutationKind.ServiceStart)
                    plan.Add(latest[key]);
            return plan;
        }

        /// <summary>Run-key targets are root-qualified ("HKCU\...\Run\Name");
        /// the dedup key is case-insensitive (registry identity), the plan
        /// keeps the entry verbatim.</summary>
        private static string EntryKey(TakeoverJournalEntry entry) =>
            entry.Target.ToUpperInvariant();

        /// <summary>The inventory summary the status is decided from (the
        /// executor counts; Core decides).</summary>
        public sealed record Inventory
        {
            /// <summary>How many catalog services exist on the machine.</summary>
            public int ServicesInstalled { get; init; }

            /// <summary>How many of them are currently running.</summary>
            public int ServicesRunning { get; init; }

            /// <summary>Verified OEM processes currently alive.</summary>
            public int OemProcessesRunning { get; init; }

            /// <summary>Pause evidence: journal entries recorded by UCU.</summary>
            public int JournalCount { get; init; }

            /// <summary>Autostart scope still neutralized (run keys in backup
            /// form, tasks disabled with journal evidence, flags disabled).</summary>
            public int AutostartNeutralized { get; init; }
        }

        public enum State
        {
            /// <summary>No catalog service and no CC evidence: the reference's
            /// auto-bypass machines (Thin & Light / APU).</summary>
            NotInstalled,

            /// <summary>Stack present and live (nothing of it paused).</summary>
            Active,

            /// <summary>UCU paused it: no service running, no verified process
            /// alive, and the pause evidence exists.</summary>
            Paused,

            /// <summary>UCU paused part of it (journal exists) but pieces are
            /// still live - a watchdog re-armed something, or a manual start.</summary>
            PartiallyPaused,

            /// <summary>Nothing installed per the services, but journal
            /// evidence exists (the stack was uninstalled while paused).</summary>
            Stale,
        }

        /// <summary>The pause-state decision (status line and the
        /// pause/resume enablement the UI reflects).</summary>
        public static State DecideState(Inventory inv)
        {
            if (inv is null)
                throw new ArgumentNullException(nameof(inv));

            if (inv.ServicesInstalled == 0 && inv.OemProcessesRunning == 0 && inv.JournalCount == 0)
                return State.NotInstalled;

            if (inv.ServicesInstalled == 0 && inv.JournalCount > 0)
                return State.Stale;

            bool live = inv.ServicesRunning > 0 || inv.OemProcessesRunning > 0;

            if (!live && (inv.JournalCount > 0 || inv.AutostartNeutralized > 0))
                return State.Paused;

            if (live && (inv.JournalCount > 0 || inv.AutostartNeutralized > 0))
                return State.PartiallyPaused;

            return State.Active;
        }
    }
}
