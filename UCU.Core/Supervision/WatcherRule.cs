using System;
using System.Collections.Generic;

namespace UCU.Core.Supervision
{
    /// <summary>
    /// One user-defined automation rule (open-revo watcher_hub.rs port, story
    /// U-201). Flat on purpose: the record IS the watchers.json shape, so the
    /// persistence carries no second schema, and the evaluation stays a pure
    /// function over this data (<see cref="WatcherPolicy"/>).
    ///
    /// Reference divergences by design (the story's constraints, not a gap):
    /// actions ride UCU's existing verbs and services only - no raw EC writes,
    /// no raw register mask-modify actions (the reference gates those behind a
    /// "blast shield"; UCU ships without the class of writes at all); the
    /// condition inputs come from the app's served metrics snapshot, the
    /// foreground window, OS power/session events, and named engine getters
    /// (no push channel - pipe contract v1 has none).
    /// </summary>
    public sealed record WatcherRule
    {
        /// <summary>Stable identity for the cooldown ledger, the store, and the UI.</summary>
        public string Id { get; init; } = Guid.NewGuid().ToString("N");

        public string Name { get; init; } = "";

        /// <summary>The per-rule enable the reference carries (enabled default).</summary>
        public bool Enabled { get; init; } = true;

        /// <summary>Seconds between triggers; 5 = the reference's default cooldown.
        /// The store clamps to >= 1 (a 0-second cooldown is a trigger storm).</summary>
        public int CooldownSeconds { get; init; } = 5;

        public WatcherConditionKind Kind { get; init; }

        // --- Telemetry condition: every field is an AND-ed gate; null = not gated.
        // Thresholds compare against the served snapshot (0 = unavailable there,
        // and WatcherPolicy refuses to fire a temp/usage gate on an unavailable
        // reading, so a sensor outage never latches a rule on).

        public int? CpuTempGt { get; init; }
        public int? CpuTempLt { get; init; }
        public int? GpuTempGt { get; init; }
        public int? GpuTempLt { get; init; }
        public int? CpuUsageGt { get; init; }
        public int? GpuUsageGt { get; init; }
        public int? FpsGt { get; init; }

        /// <summary>null = power-source agnostic; true = on AC; false = on battery.
        /// Desktops (no battery) answer true.</summary>
        public bool? OnAc { get; init; }

        /// <summary>Substring, case-insensitive, against the foreground process's
        /// exe name ("cyberpunk"). The story's flagship "GPU>85 in game X" is
        /// one Telemetry rule carrying this gate - the reference needs a plugin
        /// event chain for the same shape.</summary>
        public string? ForegroundExe { get; init; }

        // --- ForegroundProcess condition (edge-triggered on the name change).

        public IReadOnlyList<string> ForegroundTargets { get; init; } = Array.Empty<string>();
        public WatcherEdgeKind EdgeKind { get; init; } = WatcherEdgeKind.Enter;

        // --- PowerEvent condition (OS edge events, no polling).

        public WatcherPowerEventKind? PowerEvent { get; init; }

        // --- ValueChange condition: named engine getter, not a raw EC address
        // (the reference's RawEcDiff reads any register; UCU restricts the
        // watched space to the verb surface). First read seeds, no trigger.

        public WatcherValueSource? ValueSource { get; init; }

        // --- Action (one per rule; the reference sequences, UCU keeps the
        // rule->single-action shape the Automations page rows already model).

        public WatcherActionKind Action { get; init; }

        /// <summary>ApplyPreset: profile key (the same keys the AC/DC/resume rows persist).</summary>
        public string? ProfileName { get; init; }

        /// <summary>SetPerformanceMode: the Q-mode number (0..3 as the Q-key cycles).</summary>
        public int? PerformanceMode { get; init; }

        /// <summary>SetFanTurbo: desired turbo state (set semantics: the hub
        /// reads FanStatusDto.TurboActive and toggles only on a mismatch -
        /// the engine verb is a toggle, not a setter).</summary>
        public bool? FanTurboOn { get; init; }

        /// <summary>Notify: toast title/message (localized through the app's
        /// catalog like every user-visible string).</summary>
        public string? NotifyTitle { get; init; }
        public string? NotifyMessage { get; init; }
    }

    public enum WatcherConditionKind
    {
        Telemetry,
        ForegroundProcess,
        PowerEvent,
        ValueChange,
    }

    /// <summary>Enter/Exit/Both on the foreground-process name edges
    /// (reference ProcessTriggerMode).</summary>
    public enum WatcherEdgeKind
    {
        Enter,
        Exit,
        Both,
    }

    public enum WatcherPowerEventKind
    {
        AcConnected,
        AcDisconnected,
        SessionLock,
        SessionUnlock,
    }

    /// <summary>The watched-value space: named getters, polled on the hub's
    /// diff cadence. Each maps to one verb; FanStatusDto covers three.</summary>
    public enum WatcherValueSource
    {
        FanRpm,
        FanTurbo,
        PerformanceMode,
        GpuCtgp,
        GpuDb,
        CpuPl1,
        CpuPl2,
        MuxMode,
    }

    public enum WatcherActionKind
    {
        ApplyPreset,
        SetPerformanceMode,
        SetFanTurbo,
        Notify,
    }

    /// <summary>What the hub observed this tick. 0 = unavailable for
    /// temperatures and fps (the snapshot's convention); usage 0 is a real
    /// reading (idle), and HasSnapshot distinguishes a live tree from a
    /// parked one so usage gates never fire on zeros.</summary>
    public readonly record struct WatcherTelemetry(
        bool HasSnapshot,
        int CpuTempC,
        int GpuTempC,
        int CpuUsagePct,
        int GpuUsagePct,
        int Fps,
        bool IsOnAc,
        string ForegroundExe);
}
