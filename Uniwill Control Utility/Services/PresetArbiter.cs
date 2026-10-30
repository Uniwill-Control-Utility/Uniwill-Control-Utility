using System;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using Uniwill_Control_Utility.Properties;
using UCU.Core.Supervision;

namespace Uniwill_Control_Utility.Services
{
    /// <summary>
    /// The single execution pipeline for preset power-mode intents (OpenRevo's
    /// power-arbiter pattern, ported to UCU's shape).
    ///
    /// Before this service existed, five writers applied presets independently —
    /// the startup restore, the resume handler, the tray menu, the Q-key button
    /// service and the AC/battery automations — and the automations raced the
    /// startup restore inside the first seconds of boot (Windows raises
    /// PowerModeChanged/StatusChange while the WMI battery probe settles). The
    /// arbiter serializes every intent on one gate and rules on it with the pure
    /// <see cref="PresetArbiterPolicy"/>: boot-grace suppression of automatic
    /// edges, Adaptive-Mode ownership, duplicate debounce. A user click is never
    /// dropped; it only queues.
    ///
    /// The engine stays preset-illiterate by design (its verbs are EC semantics;
    /// preset application composes app-side: the RyzenAdj interpreter, V-F curves,
    /// fan settings, EC stock-mode assertion), so the arbiter is app-side too —
    /// it is the app's one point where "which profile, from whom, right now"
    /// is decided.
    ///
    /// Direct-apply paths that cannot route through the pipeline (the Presets
    /// page's slot-aware apply, Adaptive Mode's engine) keep the arbiter's
    /// bookkeeping honest through <see cref="RecordApply"/>.
    /// </summary>
    public sealed class PresetArbiter
    {
        private readonly Func<string, string?, Task> _apply;
        private readonly Func<bool> _isAdaptiveRunning;
        private readonly Action<string> _persistSelection;
        private readonly Func<DateTime> _clock;

        private readonly DateTime _startedUtc;
        private readonly SemaphoreSlim _gate = new(1, 1);

        // Touched under _gate; RecordApply is a bookkeeping write, not an apply,
        // so it only needs a memory barrier (volatile-style via lock-free store
        // of the pair below).
        private string? _lastAppliedProfile;
        private DateTime _lastAppliedUtc;

        /// <summary>Production wiring: apply through the full preset pipeline,
        /// persist the selection to user settings, read the Adaptive flag from
        /// settings as the existing writers do.</summary>
        public PresetArbiter()
            : this(
                (profile, display) => PresetProfileApplier.ApplyProfileAsync(
                    profile, display ?? PresetProfileApplier.DisplayName(profile)),
                () => Settings.Default.isAdaptiveModeRunning,
                profile =>
                {
                    Settings.Default.presetProfile = profile;
                    Settings.Default.Save();
                })
        {
        }

        /// <summary>Test seam: injected applier, Adaptive flag, persistence sink
        /// and clock so the pipeline is unit-testable without settings or hardware.</summary>
        public PresetArbiter(
            Func<string, string?, Task> apply,
            Func<bool> isAdaptiveRunning,
            Action<string> persistSelection,
            Func<DateTime>? clock = null)
        {
            _apply = apply ?? throw new ArgumentNullException(nameof(apply));
            _isAdaptiveRunning = isAdaptiveRunning ?? throw new ArgumentNullException(nameof(isAdaptiveRunning));
            _persistSelection = persistSelection ?? throw new ArgumentNullException(nameof(persistSelection));
            _clock = clock ?? (() => DateTime.UtcNow);
            _startedUtc = _clock();
        }

        /// <summary>
        /// Routes an intent through the arbiter. Returns once the apply has run
        /// (or the intent was suppressed) — callers that show toasts on success
        /// check <see cref="PresetArbiterResult.Applied"/> first.
        /// </summary>
        public async Task<PresetArbiterResult> RequestAsync(PresetIntentSource source, string profile)
        {
            if (string.IsNullOrWhiteSpace(profile))
                return new PresetArbiterResult(false, PresetArbiterAction.SuppressDuplicate, "empty profile");

            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                DateTime now = _clock();
                var decision = PresetArbiterPolicy.Evaluate(
                    source,
                    profile,
                    uptime: now - _startedUtc,
                    adaptiveRunning: _isAdaptiveRunning(),
                    lastAppliedProfile: _lastAppliedProfile,
                    sinceLastApplied: _lastAppliedProfile is null
                        ? (TimeSpan?)null
                        : now - _lastAppliedUtc);

                if (decision.Action != PresetArbiterAction.Apply)
                {
                    Log.Information("[Arbiter] Preset intent {Source}:'{Profile}' suppressed — {Reason}",
                        source, profile, decision.Reason);
                    return new PresetArbiterResult(false, decision.Action, decision.Reason);
                }

                try
                {
                    await _apply(profile, null).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // Keep the last-applied state at the PREVIOUS truth: a failed
                    // apply did not move the machine, and the debounce must not
                    // suppress a retry of the same profile.
                    Log.Error(ex, "[Arbiter] Preset intent {Source}:'{Profile}' failed to apply", source, profile);
                    return new PresetArbiterResult(false, PresetArbiterAction.Apply, $"apply failed: {ex.Message}");
                }

                _lastAppliedProfile = profile;
                _lastAppliedUtc = _clock();

                if (PresetArbiterPolicy.ShouldPersistSelection(source))
                {
                    try
                    {
                        _persistSelection(profile);
                    }
                    catch (Exception ex)
                    {
                        // The hardware moved; the selection save is best-effort
                        // (settings corruption self-heal covers a bad file).
                        Log.Warning(ex, "[Arbiter] Failed to persist selection '{Profile}'", profile);
                    }
                }

                Log.Information("[Arbiter] Preset intent {Source}:'{Profile}' applied", source, profile);
                return new PresetArbiterResult(true, PresetArbiterAction.Apply, "applied");
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>
        /// Bookkeeping for applies that ran outside the pipeline (the Presets
        /// page's slot-aware apply, Adaptive's engine, the tray's custom-slot
        /// legacy path, the startup custom restore). It keeps the debounce state
        /// honest so a pipeline intent arriving moments after a direct apply of
        /// the same profile is recognized as a duplicate. Persists nothing — the
        /// direct paths persist on their own.
        /// </summary>
        public void RecordApply(PresetIntentSource source, string profile)
        {
            if (string.IsNullOrWhiteSpace(profile))
                return;

            _lastAppliedProfile = profile;
            _lastAppliedUtc = _clock();
            Log.Debug("[Arbiter] Recorded direct apply {Source}:'{Profile}'", source, profile);
        }

        /// <summary>The last profile the arbiter (or a recorded direct apply)
        /// moved the machine to; null when nothing has been applied yet.</summary>
        public string? LastAppliedProfile => _lastAppliedProfile;
    }

    /// <summary>Outcome of one arbiter request: did the machine move, and under
    /// which policy rule?</summary>
    public readonly record struct PresetArbiterResult(
        bool Applied,
        PresetArbiterAction Action,
        string Reason);
}
