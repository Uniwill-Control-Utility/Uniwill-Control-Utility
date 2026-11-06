using System;

namespace UCU.Core.Supervision
{
    /// <summary>
    /// The watcher hub's decision core (story U-201): pure, unit-testable,
    /// no timers, no WMI, no WPF. The runtime (<c>WatcherHubService</c>,
    /// app-side) feeds observations in and executes the verbs; every "did
    /// this rule fire" answer comes from here.
    ///
    /// Rules, ported from open-revo's watcher_hub.rs and adapted to UCU:
    /// 1. Telemetry: every non-null threshold is an AND gate. A threshold on
    ///    an UNAVAILABLE reading (0 for temps/fps, or a parked snapshot for
    ///    usage) never matches: a sensor outage must not latch a rule on
    ///    (the reference compares raw values and fires on stale zeros).
    /// 2. Cooldown: the reference's 5 s default, per rule, measured on the
    ///    hub's UTC clock.
    /// 3. Foreground: edge-triggered on the exe name (Enter = first match,
    ///    Exit = last match lost, Both = either); no edge, no trigger.
    /// 4. ValueChange: the watched source's readback changed since the rule's
    ///    last read; the FIRST read seeds without firing (the service start
    ///    is not an event).
    /// 5. Storm floor: rules exist at most MaxRules, cooldowns are at least
    ///    1 s, and the diff poll never runs faster than DiffPollFloor.
    /// </summary>
    public static class WatcherPolicy
    {
        /// <summary>Storm control: a 0-second cooldown re-fires every tick.</summary>
        public static readonly TimeSpan MinCooldown = TimeSpan.FromSeconds(1);

        /// <summary>The hard poll floor the story pins: the hub ticks at 1 s.</summary>
        public static readonly TimeSpan TickFloor = TimeSpan.FromSeconds(1);

        /// <summary>The EC-side verb poll floor: named getters take engine
        /// queue time; 5 s is the story's "periodic targeted polls" shape.</summary>
        public static readonly TimeSpan DiffPollFloor = TimeSpan.FromSeconds(5);

        /// <summary>Cap on user rules (open story of the plan - settled 32:
        /// generous for a laptop's real automations, bounded for the poll plan).</summary>
        public const int MaxRules = 32;

        public static TimeSpan CooldownOf(WatcherRule rule)
        {
            int seconds = Math.Max(1, rule.CooldownSeconds);
            return TimeSpan.FromSeconds(seconds);
        }

        /// <summary>True when the rule's last trigger is absent or past the
        /// cooldown (the reference's can_trigger check, UTC-pure).</summary>
        public static bool CooldownElapsed(DateTime? lastTriggeredUtc, TimeSpan cooldown, DateTime nowUtc) =>
            lastTriggeredUtc is null || nowUtc - lastTriggeredUtc.Value >= cooldown;

        public static bool TelemetryMatches(WatcherRule rule, in WatcherTelemetry t)
        {
            if (rule.Kind != WatcherConditionKind.Telemetry)
                return false;

            // Usage gates need a live snapshot: a parked tree answers 0 for
            // everything, and "CPU usage < 5" on zeros is a phantom match.
            if ((rule.CpuUsageGt is not null || rule.GpuUsageGt is not null) && !t.HasSnapshot)
                return false;

            if (rule.CpuTempGt is int cpuGtV)
            {
                if (t.CpuTempC <= 0 || t.CpuTempC <= cpuGtV)
                    return false;
            }
            if (rule.CpuTempLt is int cpuLtV)
            {
                if (t.CpuTempC <= 0 || t.CpuTempC >= cpuLtV)
                    return false;
            }
            if (rule.GpuTempGt is int gpuGtV)
            {
                if (t.GpuTempC <= 0 || t.GpuTempC <= gpuGtV)
                    return false;
            }
            if (rule.GpuTempLt is int gpuLtV)
            {
                if (t.GpuTempC <= 0 || t.GpuTempC >= gpuLtV)
                    return false;
            }
            if (rule.CpuUsageGt is int cpuUseV && t.CpuUsagePct <= cpuUseV)
                return false;
            if (rule.GpuUsageGt is int gpuUseV && t.GpuUsagePct <= gpuUseV)
                return false;

            // Fps 0 = no game rendering: a "fps > x" gate is simply false.
            if (rule.FpsGt is int fpsV && t.Fps <= fpsV)
                return false;

            if (rule.OnAc is bool ac && t.IsOnAc != ac)
                return false;

            if (!string.IsNullOrEmpty(rule.ForegroundExe) &&
                !ContainsExe(t.ForegroundExe, rule.ForegroundExe!))
                return false;

            return true;
        }

        public static bool ForegroundFires(WatcherRule rule, string? previousExe, string? currentExe)
        {
            if (rule.Kind != WatcherConditionKind.ForegroundProcess)
                return false;

            string prev = Normalize(previousExe);
            string curr = Normalize(currentExe);
            if (prev == curr)
                return false;

            bool entered = MatchesTarget(rule, curr);
            bool left = MatchesTarget(rule, prev);

            return rule.EdgeKind switch
            {
                WatcherEdgeKind.Enter => entered,
                WatcherEdgeKind.Exit => left,
                WatcherEdgeKind.Both => entered || left,
                _ => false,
            };
        }

        /// <summary>True when the watched value changed; a null "last" seeds
        /// without firing (service start / first read).</summary>
        public static bool ValueChanged(string? lastReadback, string currentReadback) =>
            lastReadback is not null && !string.Equals(lastReadback, currentReadback, StringComparison.Ordinal);

        /// <summary>The store's validation: returns the reason a rule is not
        /// acceptable, or null when it is.</summary>
        public static string? Validate(WatcherRule rule)
        {
            if (string.IsNullOrWhiteSpace(rule.Name))
                return "name is empty";

            switch (rule.Kind)
            {
                case WatcherConditionKind.Telemetry:
                    bool anyGate = rule.CpuTempGt is not null || rule.CpuTempLt is not null
                        || rule.GpuTempGt is not null || rule.GpuTempLt is not null
                        || rule.CpuUsageGt is not null || rule.GpuUsageGt is not null
                        || rule.FpsGt is not null || rule.OnAc is not null
                        || !string.IsNullOrEmpty(rule.ForegroundExe);
                    if (!anyGate)
                        return "telemetry rule needs at least one gate";
                    break;
                case WatcherConditionKind.ForegroundProcess:
                    if (rule.ForegroundTargets.Count == 0)
                        return "foreground rule needs at least one target";
                    foreach (string t in rule.ForegroundTargets)
                    {
                        if (string.IsNullOrWhiteSpace(t))
                            return "foreground target is blank";
                    }
                    break;
                case WatcherConditionKind.PowerEvent:
                    if (rule.PowerEvent is null)
                        return "power rule needs an event";
                    break;
                case WatcherConditionKind.ValueChange:
                    if (rule.ValueSource is null)
                        return "value-change rule needs a source";
                    break;
            }

            switch (rule.Action)
            {
                case WatcherActionKind.ApplyPreset:
                    if (string.IsNullOrWhiteSpace(rule.ProfileName))
                        return "apply-preset action needs a profile";
                    break;
                case WatcherActionKind.SetPerformanceMode:
                    if (rule.PerformanceMode is null)
                        return "set-performance-mode action needs a mode";
                    break;
                case WatcherActionKind.SetFanTurbo:
                    if (rule.FanTurboOn is null)
                        return "set-fan-turbo action needs a state";
                    break;
                case WatcherActionKind.Notify:
                    if (string.IsNullOrWhiteSpace(rule.NotifyMessage))
                        return "notify action needs a message";
                    break;
                default:
                    return "unknown action";
            }

            return null;
        }

        private static bool MatchesTarget(WatcherRule rule, string normalizedExe)
        {
            if (normalizedExe.Length == 0)
                return false;

            foreach (string target in rule.ForegroundTargets)
            {
                if (MatchesTarget(target, normalizedExe))
                    return true;
            }
            return false;
        }

        private static bool MatchesTarget(string target, string normalizedExe)
        {
            string t = Normalize(target);
            if (t.Length == 0)
                return false;

            // Targets are exe names, with or without ".exe" - match as names.
            return t.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? normalizedExe == t
                : normalizedExe == t || normalizedExe == t + ".exe";
        }

        private static bool ContainsExe(string foregroundExe, string needle) =>
            foregroundExe.Contains(needle.Trim(), StringComparison.OrdinalIgnoreCase);

        private static string Normalize(string? exe)
        {
            string s = (exe ?? string.Empty).Trim();
            return s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? s[..^4]
                : s;
        }
    }
}
