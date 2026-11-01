using System;
using System.Collections.Generic;
using Uniwill_Control_Utility.Properties;
using UCU.Core.Supervision;
namespace Uniwill_Control_Utility.Services
{
    /// <summary>
    /// The one localized display vocabulary for the watcher hub's enums
    /// (story U-201). The dialog builds its ComboBox option lists from the
    /// ordered name lists (index = enum value) and the page builds rule
    /// summaries from the same names, so a rule always displays identically
    /// in the card and in the editor. Every string is composed through
    /// LocalizationService.Get at call time; the persisted rule carries only
    /// enums, so culture never leaks into watchers.json.
    /// </summary>
    internal static class WatcherVocabulary
    {
        // Computed per call, never cached: LocalizationService.Get must run
        // under the CURRENT culture, and the culture changes at runtime
        // (the page rebuilds rows on CultureChanged).
        public static IReadOnlyList<string> ConditionNames => new[]
        {
            LocalizationService.Get("Telemetry thresholds"),
            LocalizationService.Get("App in foreground"),
            LocalizationService.Get("Power / session event"),
            LocalizationService.Get("EC value changed"),
        };

        public static IReadOnlyList<string> PowerStateNames => new[]
        {
            LocalizationService.Get("Any power state"),
            LocalizationService.Get("On AC power"),
            LocalizationService.Get("On battery"),
        };

        public static IReadOnlyList<string> EdgeNames => new[]
        {
            LocalizationService.Get("When the app is entered"),
            LocalizationService.Get("When the app is exited"),
            LocalizationService.Get("On enter or exit"),
        };

        public static IReadOnlyList<string> PowerEventNames => new[]
        {
            LocalizationService.Get("AC connected"),
            LocalizationService.Get("AC disconnected"),
            LocalizationService.Get("Session locked"),
            LocalizationService.Get("Session unlocked"),
        };

        public static IReadOnlyList<string> ValueSourceNames => new[]
        {
            LocalizationService.Get("Fan RPM"),
            LocalizationService.Get("Fan turbo"),
            LocalizationService.Get("Performance mode"),
            LocalizationService.Get("GPU cTGP"),
            LocalizationService.Get("GPU Dynamic Boost"),
            LocalizationService.Get("CPU PL1"),
            LocalizationService.Get("CPU PL2"),
            LocalizationService.Get("MUX mode"),
        };

        public static IReadOnlyList<string> TurboNames => new[]
        {
            LocalizationService.Get("On"),
            LocalizationService.Get("Off"),
        };

        public static IReadOnlyList<string> ActionNames => new[]
        {
            LocalizationService.Get("Apply preset"),
            LocalizationService.Get("Set performance mode"),
            LocalizationService.Get("Set fan turbo"),
            LocalizationService.Get("Notify"),
        };

        public static string ConditionName(WatcherConditionKind kind) =>
            ConditionNames[(int)kind];

        public static string ValueSourceName(WatcherValueSource source) =>
            ValueSourceNames[(int)source];

        public static string PowerEventName(WatcherPowerEventKind kind) =>
            PowerEventNames[(int)kind];

        public static string EdgeName(WatcherEdgeKind edge) =>
            EdgeNames[(int)edge];

        public static string ActionName(WatcherActionKind action) =>
            ActionNames[(int)action];

        /// <summary>Short edge token for the summary line (the combo carries
        /// the full "When the app is entered" phrasing).</summary>
        public static string EdgeShortName(WatcherEdgeKind edge) =>
            LocalizationService.Get(edge switch
            {
                WatcherEdgeKind.Enter => "entered",
                WatcherEdgeKind.Exit => "exited",
                _ => "entered or exited",
            });

        /// <summary>The card row's one-line rule summary: the condition
        /// fragments, an arrow, the action. Built from the localized
        /// fragments; rule content (names, targets, messages) shows as
        /// written by the user.</summary>
        public static string Describe(WatcherRule rule)
        {
            List<string> gates = new();

            switch (rule.Kind)
            {
                case WatcherConditionKind.Telemetry:
                    if (rule.CpuTempGt is int cpuGt) gates.Add(LocalizationService.Format("{0} above {1} °C", LocalizationService.Get("CPU"), cpuGt));
                    if (rule.CpuTempLt is int cpuLt) gates.Add(LocalizationService.Format("{0} below {1} °C", LocalizationService.Get("CPU"), cpuLt));
                    if (rule.GpuTempGt is int gpuGt) gates.Add(LocalizationService.Format("{0} above {1} °C", LocalizationService.Get("GPU"), gpuGt));
                    if (rule.GpuTempLt is int gpuLt) gates.Add(LocalizationService.Format("{0} below {1} °C", LocalizationService.Get("GPU"), gpuLt));
                    if (rule.CpuUsageGt is int cpuUse) gates.Add(LocalizationService.Format("{0} above {1} %", LocalizationService.Get("CPU usage"), cpuUse));
                    if (rule.GpuUsageGt is int gpuUse) gates.Add(LocalizationService.Format("{0} above {1} %", LocalizationService.Get("GPU usage"), gpuUse));
                    if (rule.FpsGt is int fps) gates.Add(LocalizationService.Format("FPS above {0}", fps));
                    if (rule.OnAc is true) gates.Add(LocalizationService.Get("On AC power"));
                    else if (rule.OnAc is false) gates.Add(LocalizationService.Get("On battery"));
                    if (!string.IsNullOrEmpty(rule.ForegroundExe))
                        gates.Add(LocalizationService.Format("foreground app: {0}", rule.ForegroundExe));
                    break;

                case WatcherConditionKind.ForegroundProcess:
                    gates.Add(LocalizationService.Format(
                        "app {0}: {1}", EdgeShortName(rule.EdgeKind), string.Join(", ", rule.ForegroundTargets)));
                    break;

                case WatcherConditionKind.PowerEvent:
                    gates.Add(rule.PowerEvent is WatcherPowerEventKind pe ? PowerEventName(pe) : string.Empty);
                    break;

                case WatcherConditionKind.ValueChange:
                    if (rule.ValueSource is WatcherValueSource source)
                        gates.Add(LocalizationService.Format("{0} changed", ValueSourceName(source)));
                    break;
            }

            string action = rule.Action switch
            {
                WatcherActionKind.ApplyPreset => LocalizationService.Format("{0}: {1}", LocalizationService.Get("Apply preset"), rule.ProfileName),
                WatcherActionKind.SetPerformanceMode => LocalizationService.Format("{0}: {1}", LocalizationService.Get("Set performance mode"), rule.PerformanceMode),
                WatcherActionKind.SetFanTurbo => LocalizationService.Format("{0}: {1}", LocalizationService.Get("Fan turbo"), rule.FanTurboOn is true ? LocalizationService.Get("On") : LocalizationService.Get("Off")),
                WatcherActionKind.Notify => LocalizationService.Format("Notify: {0}", rule.NotifyMessage),
                _ => string.Empty,
            };

            string condition = string.Join(LocalizationService.Get(", "), gates);
            return string.IsNullOrEmpty(condition)
                ? action
                : condition + "  →  " + action;
        }
    }
}
