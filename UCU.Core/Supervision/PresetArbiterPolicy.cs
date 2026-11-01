using System;

namespace UCU.Core.Supervision
{
    /// <summary>
    /// Who is asking for a preset to be applied. Every power-mode transition intent
    /// must declare its source (the OpenRevo power-arbiter pattern): the arbiter's
    /// suppression rules key off the source, so a writer that cannot name itself
    /// has no seat at the table.
    /// </summary>
    public enum PresetIntentSource
    {
        /// <summary>Presets page / tray menu — a human chose this profile. Highest priority.</summary>
        UserInterface,
        /// <summary>The physical Performance / Q-key button, observed as an EC WMI press echo.</summary>
        HardwarePhysicalButton,
        /// <summary>The "apply preset on AC" automation (power-status edge, adapter plugged in).</summary>
        AutomationAc,
        /// <summary>The "apply preset on battery" automation (power-status edge, adapter removed).</summary>
        AutomationBattery,
        /// <summary>A watcher-hub rule (U-201) whose action is ApplyPreset. Same automatic
        /// class as the AC/battery edges: boot-grace-suppressed, yields to Adaptive,
        /// and a successful apply updates the persisted selection.</summary>
        AutomationRule,
        /// <summary>Startup application of the saved preset.</summary>
        BootRestore,
        /// <summary>Re-application of the active preset after sleep/hibernate resume.</summary>
        ResumeFromSleep,
        /// <summary>The Adaptive engine's game-driven target profile.</summary>
        AdaptiveMode,
    }

    public enum PresetArbiterAction
    {
        /// <summary>Apply the profile through the single execution pipeline.</summary>
        Apply,
        /// <summary>Automatic-source intent inside the startup grace window — the OS/EC is still
        /// emitting uncalibrated power-status noise; applying it can stomp the boot restore.</summary>
        SuppressBootGrace,
        /// <summary>Adaptive Mode owns the machine: automatic edges and the physical button yield
        /// to it (the rule QKeyButtonService enforced locally before the arbiter centralized it).</summary>
        SuppressAdaptiveRunning,
        /// <summary>The same profile was applied moments ago — the apply is a no-op and a
        /// repeated SMU/EC sequence costs seconds of queued device I/O per repeat.</summary>
        SuppressDuplicate,
    }

    public readonly record struct PresetArbiterDecision(PresetArbiterAction Action, string Reason);

    /// <summary>
    /// Pure arbitration policy for preset-power-mode intents — no process/IPC/WPF types,
    /// unit-testable. Lives in UCU.Core next to <see cref="GuardianPolicy"/> and follows
    /// the same rule: the decision core is pure, the runtime that consumes it is not.
    ///
    /// The rules, ported from OpenRevo's power arbiter (its arbiter.rs) and adapted to
    /// UCU where preset application is an app-side composition (RyzenAdj + EC verbs +
    /// V-F curve + fans) rather than a single EC mode write:
    ///
    /// 1. Boot grace: automation AC/DC edges are suppressed for the first seconds after
    ///    the app starts. Windows raises PowerModeChanged/StatusChange during startup
    ///    (the adapter state "changes" as the WMI probe settles), and those uncalibrated
    ///    edges used to race the boot restore. UI, the physical button, boot restore,
    ///    resume and Adaptive are never suppressed by grace.
    /// 2. Adaptive ownership: while Adaptive Mode runs, the AC/DC edges and the physical
    ///    button yield to it (UCU's existing local rule — QKeyButtonService ignores
    ///    presses during Adaptive, HandlePowerModeChange skips preset edges during
    ///    Adaptive — lifted into the shared policy so no future writer can forget it).
    /// 3. Duplicate debounce: the same profile within the debounce window applies once.
    /// 4. Persistence: intents that reflect a new user-level selection (UI, button,
    ///    automation) update the persisted selection; restores (boot/resume) and the
    ///    Adaptive engine do not overwrite the user's saved choice.
    ///
    /// Priority is expressed by what survives suppression: a user click can never be
    /// dropped by any rule here, only by serialization (apply order), which is the
    /// arbiter runtime's job.
    /// </summary>
    public static class PresetArbiterPolicy
    {
        /// <summary>Automation edges are noise for this long after the arbiter starts.</summary>
        public static readonly TimeSpan BootGraceWindow = TimeSpan.FromSeconds(10);

        /// <summary>Identical consecutive targets inside this window apply once.</summary>
        public static readonly TimeSpan DebounceWindow = TimeSpan.FromMilliseconds(600);

        /// <summary>Sources that fire without a human: the boot-grace suppression class.</summary>
        public static bool IsAutomaticSource(PresetIntentSource source)
        {
            return source == PresetIntentSource.AutomationAc
                || source == PresetIntentSource.AutomationBattery
                || source == PresetIntentSource.AutomationRule;
        }

        /// <summary>True when the intent's target equals the last applied profile
        /// (profile keys are matched case-insensitively, as everywhere in UCU).</summary>
        public static bool IsSameProfile(string? profile, string? lastAppliedProfile)
        {
            return !string.IsNullOrEmpty(profile)
                && string.Equals(profile, lastAppliedProfile, StringComparison.OrdinalIgnoreCase);
        }

        public static bool ShouldSuppressByBootGrace(PresetIntentSource source, TimeSpan uptime)
        {
            return uptime < BootGraceWindow && IsAutomaticSource(source);
        }

        public static bool ShouldSuppressByAdaptive(PresetIntentSource source, bool adaptiveRunning)
        {
            if (!adaptiveRunning)
                return false;

            // The edges and the button yield to Adaptive; the user acting through the UI
            // is the documented way to take control back, so UserInterface passes, and
            // restores do not fight ownership they do not challenge.
            return source == PresetIntentSource.AutomationAc
                || source == PresetIntentSource.AutomationBattery
                || source == PresetIntentSource.AutomationRule
                || source == PresetIntentSource.HardwarePhysicalButton;
        }

        public static bool ShouldSuppressDuplicate(
            string profile,
            string? lastAppliedProfile,
            TimeSpan sinceLastApplied,
            TimeSpan debounce)
        {
            return IsSameProfile(profile, lastAppliedProfile) && sinceLastApplied < debounce;
        }

        /// <summary>Which intents' target becomes the persisted selection
        /// (Settings.presetProfile) after a successful apply.</summary>
        public static bool ShouldPersistSelection(PresetIntentSource source)
        {
            return source == PresetIntentSource.UserInterface
                || source == PresetIntentSource.HardwarePhysicalButton
                || source == PresetIntentSource.AutomationAc
                || source == PresetIntentSource.AutomationBattery
                || source == PresetIntentSource.AutomationRule;
        }

        /// <summary>
        /// Full evaluation at dispatch time (the arbiter evaluates when it takes the
        /// apply gate, not at request enqueue: a queued intent re-checks the live
        /// uptime, Adaptive flag and last-applied state, so late arrivals of startup
        /// noise are suppressed against the current clock).
        /// </summary>
        public static PresetArbiterDecision Evaluate(
            PresetIntentSource source,
            string profile,
            TimeSpan uptime,
            bool adaptiveRunning,
            string? lastAppliedProfile,
            TimeSpan? sinceLastApplied)
        {
            if (ShouldSuppressByBootGrace(source, uptime))
                return new PresetArbiterDecision(PresetArbiterAction.SuppressBootGrace,
                    $"automation edge suppressed inside the {BootGraceWindow.TotalSeconds:0}s startup grace window (uptime {uptime.TotalSeconds:0.0}s)");

            if (ShouldSuppressByAdaptive(source, adaptiveRunning))
                return new PresetArbiterDecision(PresetArbiterAction.SuppressAdaptiveRunning,
                    "Adaptive Mode is running - this source yields to it");

            if (sinceLastApplied is TimeSpan since && ShouldSuppressDuplicate(profile, lastAppliedProfile, since, DebounceWindow))
                return new PresetArbiterDecision(PresetArbiterAction.SuppressDuplicate,
                    $"profile '{profile}' was applied {since.TotalMilliseconds:0}ms ago (debounce {DebounceWindow.TotalMilliseconds:0}ms)");

            return new PresetArbiterDecision(PresetArbiterAction.Apply, "accepted");
        }
    }
}
