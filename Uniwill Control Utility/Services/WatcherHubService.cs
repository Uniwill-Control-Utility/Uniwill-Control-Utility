using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Win32;
using Serilog;
using Uniwill_Control_Utility.Properties;
using Uniwill_Control_Utility.Scripts;
using Uniwill_Control_Utility.Scripts.Misc;
using Uniwill_Control_Utility.Services.Performance;
using UCU.Contracts;
using UCU.Contracts.Capabilities;
using UCU.Core.Supervision;

namespace Uniwill_Control_Utility.Services
{
    /// <summary>
    /// The watcher/trigger hub (story U-201, open-revo watcher_hub.rs port):
    /// one hosted service that owns every user automation - telemetry
    /// thresholds, foreground-process edges, OS power/session edges, and
    /// named-EC-value changes - and executes their whitelisted actions
    /// through the app's existing verbs and the preset arbiter.
    ///
    /// Shape decisions vs the reference:
    /// - All evaluation is the pure <see cref="WatcherPolicy"/>; this class is
    ///   the runtime: 1 s tick (the story's hard floor), a 5 s floor on the
    ///   EC-verb diff poll (pipe v1 has no push channel - the accepted
    ///   contract-v2 candidate), per-rule cooldown, engine-Busy backoff.
    /// - Session work, engine-side by rule: foreground detection, WPF toasts,
    ///   SystemEvents. It consumes verbs (fan/power/mux getters, the arbiter)
    ///   and never the EC directly, so the unelevated app stays the app.
    /// - No raw-EC actions exist here: the whitelist is ApplyPreset /
    ///   SetPerformanceMode / SetFanTurbo / Notify.
    ///
    /// Test seams mirror the PresetArbiter pattern: the production wiring
    /// composes observation (snapshot + fps + foreground + AC), the verb
    /// readbacks, and the executor from the services; the test constructor
    /// injects all three plus a clock, so the loop runs headless.
    /// </summary>
    public sealed partial class WatcherHubService : IHostedService, IDisposable
    {
        private readonly WatcherStore _store;
        private readonly Func<WatcherTelemetry> _observe;
        private readonly Func<WatcherValueSource, Task<string?>> _readValue;
        private readonly Func<WatcherRule, Task> _execute;
        private readonly Func<DateTime> _clock;
        private readonly Func<string> _foreground;
        private readonly IFpsMonitoringService? _fps;

        private readonly Dictionary<string, DateTime> _lastFiredUtc = new();
        private readonly Dictionary<string, string> _lastReadback = new();

        private readonly object _gate = new();
        private CancellationTokenSource? _cancellation;
        private IDisposable? _fpsLease;
        private string _lastForegroundExe = "";
        private DateTime _nextDiffPollUtc;
        private List<WatcherRule> _rules = new();

        // AC line memory: PowerModeChanged StatusChange fires with the new
        // state; the edge is the transition from the remembered one.
        private bool? _lastAcOnline;

        public WatcherHubService(
            WatcherStore store,
            IHardwareMonitoringService hardware,
            IFpsMonitoringService fps,
            IPowerCapability power,
            IFanCapability fan,
            IGpuMuxCapability mux,
            PresetArbiter arbiter)
            : this(
                store,
                observe: () => Observe(hardware, fps),
                readValue: source => ReadVerbValueAsync(source, fan, power, mux),
                execute: rule => ExecuteAsync(rule, arbiter, fan, power),
                clock: () => DateTime.UtcNow)
        {
            _fps = fps;
        }

        /// <summary>Test seam: observed telemetry, verb readbacks, foreground
        /// resolution, and the action executor injected; every loop decision
        /// is exercised headless.</summary>
        public WatcherHubService(
            WatcherStore store,
            Func<WatcherTelemetry> observe,
            Func<WatcherValueSource, Task<string?>> readValue,
            Func<WatcherRule, Task> execute,
            Func<DateTime>? clock = null,
            IFpsMonitoringService? fps = null,
            Func<string>? foreground = null)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _observe = observe ?? throw new ArgumentNullException(nameof(observe));
            _readValue = readValue ?? throw new ArgumentNullException(nameof(readValue));
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _clock = clock ?? (() => DateTime.UtcNow);
            _foreground = foreground ?? ResolveForegroundExe;
            _fps = fps;

            _rules = _store.Rules.ToList();
            _store.Changed += ReloadRules;
        }

        #region Hosted lifecycle

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _store.Load();
            ReloadRules();

            SystemEvents.PowerModeChanged += OnPowerModeChanged;
            SystemEvents.SessionSwitch += OnSessionSwitch;

            _cancellation = new CancellationTokenSource();
            _ = Task.Run(() => LoopAsync(_cancellation.Token));
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            SystemEvents.SessionSwitch -= OnSessionSwitch;

            _cancellation?.Cancel();
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            StopAsync(CancellationToken.None).GetAwaiter().GetResult();
            _fpsLease?.Dispose();
            _fpsLease = null;
            _cancellation?.Dispose();
        }

        private void ReloadRules()
        {
            lock (_gate) _rules = _store.Rules.ToList();
            RefreshFpsLease();
        }

        /// <summary>The RTSS fps sampler only runs while leased; the hub
        /// leases it exactly when an enabled rule reads fps (the story's
        /// "no polling without a consumer" discipline, OverlayManager-style).</summary>
        private void RefreshFpsLease()
        {
            bool needsFps;
            lock (_gate)
            {
                needsFps = _rules.Any(r =>
                    r.Enabled && r.Kind == WatcherConditionKind.Telemetry && r.FpsGt is not null);
            }

            if (needsFps && _fpsLease is null && _fps is not null)
                _fpsLease = _fps.Acquire();
            else if (!needsFps)
            {
                _fpsLease?.Dispose();
                _fpsLease = null;
            }
        }

        #endregion

        #region Loop

        private async Task LoopAsync(CancellationToken ct)
        {
            using var timer = new PeriodicTimer(WatcherPolicy.TickFloor);
            _nextDiffPollUtc = _clock();

            try
            {
                while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
                {
                    try
                    {
                        await TickAsync(_clock(), ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        // One rule set must never die with the loop (the LHM
                        // producer's parking lesson: the loop outlives its
                        // sources' bad days).
                        Log.Error(ex, "[Watcher] tick failed");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Normal stop.
            }
        }

        /// <summary>One hub cycle, clock-pure (the test seam drives it).
        /// Telemetry + foreground run every tick; the verb diff poll keeps
        /// its own 5 s floor.</summary>
        internal async Task TickAsync(DateTime nowUtc, CancellationToken ct)
        {
            List<WatcherRule> rules;
            lock (_gate) rules = _rules;
            if (rules.Count == 0)
                return;

            List<WatcherRule> enabled = rules.Where(r => r.Enabled).ToList();
            if (enabled.Count == 0)
                return;

            // Foreground edges: name transitions only (the reference scans
            // every 800 ms and fires on the change; same shape at the 1 s
            // floor).
            string foregroundExe = _foreground();
            string previous = _lastForegroundExe;
            _lastForegroundExe = foregroundExe;

            WatcherTelemetry t = _observe() with { ForegroundExe = foregroundExe };

            foreach (WatcherRule rule in enabled)
            {
                switch (rule.Kind)
                {
                    case WatcherConditionKind.Telemetry:
                        if (WatcherPolicy.TelemetryMatches(rule, t) && CooldownOk(rule, nowUtc))
                            await FireAsync(rule, "telemetry", ct).ConfigureAwait(false);
                        break;

                    case WatcherConditionKind.ForegroundProcess:
                        if (WatcherPolicy.ForegroundFires(rule, previous, foregroundExe) && CooldownOk(rule, nowUtc))
                            await FireAsync(rule, "foreground", ct).ConfigureAwait(false);
                        break;

                    case WatcherConditionKind.ValueChange:
                        if (nowUtc >= _nextDiffPollUtc && rule.ValueSource is WatcherValueSource source)
                        {
                            string? readback = await SafeReadAsync(source).ConfigureAwait(false);
                            if (readback is not null)
                            {
                                _lastReadback.TryGetValue(rule.Id, out string? last);
                                if (WatcherPolicy.ValueChanged(last, readback))
                                {
                                    _lastReadback[rule.Id] = readback;
                                    if (CooldownOk(rule, nowUtc))
                                        await FireAsync(rule, $"value:{source}", ct).ConfigureAwait(false);
                                }
                                else
                                {
                                    _lastReadback[rule.Id] = readback;
                                }
                            }
                        }
                        break;
                }
            }

            if (nowUtc >= _nextDiffPollUtc)
                _nextDiffPollUtc = nowUtc + WatcherPolicy.DiffPollFloor;
        }

        /// <summary>The diff-poll read through the injected delegate. The
        /// production delegate swallows Busy/verb failures itself (backoff);
        /// a test-seam delegate may throw, and the loop must survive it.</summary>
        private async Task<string?> SafeReadAsync(WatcherValueSource source)
        {
            try
            {
                return await _readValue(source).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Debug("[Watcher] diff poll failed on {Source}: {Message}", source, ex.Message);
                return null;
            }
        }

        /// <summary>Power/session edge entry (the SystemEvents handlers and
        /// the test seam call this).</summary>
        internal async Task NotifyPowerEventAsync(WatcherPowerEventKind powerEvent, DateTime nowUtc)
        {
            List<WatcherRule> rules;
            lock (_gate) rules = _rules;

            foreach (WatcherRule rule in rules.Where(r =>
                         r.Enabled
                         && r.Kind == WatcherConditionKind.PowerEvent
                         && r.PowerEvent == powerEvent))
            {
                if (CooldownOk(rule, nowUtc))
                    await FireAsync(rule, $"power:{powerEvent}", CancellationToken.None).ConfigureAwait(false);
            }
        }

        private bool CooldownOk(WatcherRule rule, DateTime nowUtc)
        {
            _lastFiredUtc.TryGetValue(rule.Id, out DateTime lastFired);
            if (!WatcherPolicy.CooldownElapsed(lastFired == default ? (DateTime?)null : lastFired,
                    WatcherPolicy.CooldownOf(rule), nowUtc))
                return false;

            _lastFiredUtc[rule.Id] = nowUtc;
            return true;
        }

        private async Task FireAsync(WatcherRule rule, string witness, CancellationToken ct)
        {
            // The user-visible-effect discipline: WRN with the triggering
            // witness, like the OSD focus lines - field debugging starts here.
            Log.Warning("[Watcher] rule '{Name}' fired ({Witness}) -> {Action}",
                rule.Name, witness, rule.Action);
            try
            {
                await _execute(rule).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[Watcher] rule '{Name}' action {Action} failed", rule.Name, rule.Action);
            }
        }

        #endregion

        #region OS events

        private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode != PowerModes.StatusChange)
                return;

            bool online = SystemInformationPowerLineOnline();
            bool? previous = _lastAcOnline;
            _lastAcOnline = online;
            if (previous is null || previous == online)
                return;

            WatcherPowerEventKind edge = online
                ? WatcherPowerEventKind.AcConnected
                : WatcherPowerEventKind.AcDisconnected;

            _ = NotifyPowerEventAsync(edge, _clock());

            // The battery envelope clamp is applied when a preset is applied, so a
            // Custom preset applied on AC keeps its AC numbers after unplugging -
            // the hardware is capped by the firmware either way, but the app would
            // keep claiming the AC values (field, 2026-11-02). Re-request the saved
            // preset on the edge unless the user's own AC/DC automation owns it.
            _ = ReapplyPresetForPowerEdgeAsync(online);
        }

        private static async Task ReapplyPresetForPowerEdgeAsync(bool online)
        {
            try
            {
                // An AC/DC automation re-applies its own choice on this edge; two
                // applies racing the same transition is how a preset ping-pongs.
                //
                // The gate is the EDGE's automation, not the master switch: with
                // automation on but this edge set to "None" (the common case) the
                // automation applies nothing, so returning here would leave the
                // preset on its AC values after unplugging - the exact gap this
                // hook exists to close (found in review, 2026-11-02).
                string edgePreset = online
                    ? Properties.Settings.Default.acPreset
                    : Properties.Settings.Default.dcPreset;
                bool automationOwnsEdge = Properties.Settings.Default.EnableAutoNavigation
                    && !string.IsNullOrEmpty(edgePreset)
                    && !string.Equals(edgePreset, "None", StringComparison.OrdinalIgnoreCase);
                if (automationOwnsEdge)
                    return;

                // Stock presets are the firmware's to apply (no app-side clamp), so
                // only a Custom preset needs the re-apply.
                string profile = Properties.Settings.Default.presetProfile;
                if (PresetProfileApplier.IsBuiltInProfile(profile))
                    return;

                PresetArbiter? arbiter = App.GetService<PresetArbiter>();
                if (arbiter is null)
                    return;

                Serilog.Log.Information(
                    "[Power] {Edge}: re-applying '{Profile}' so the battery envelope takes effect",
                    online ? "AC connected" : "on battery",
                    profile);

                await arbiter.RequestAsync(
                    online ? PresetIntentSource.AutomationAc : PresetIntentSource.AutomationBattery,
                    profile).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "[Power] re-apply on the power-source edge failed");
            }
        }

        private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
        {
            WatcherPowerEventKind? edge = e.Reason switch
            {
                SessionSwitchReason.SessionLock => WatcherPowerEventKind.SessionLock,
                SessionSwitchReason.SessionUnlock => WatcherPowerEventKind.SessionUnlock,
                _ => null,
            };
            if (edge is null)
                return;

            _ = NotifyPowerEventAsync(edge.Value, _clock());
        }

        private static bool SystemInformationPowerLineOnline()
        {
            // WinForms' cached poll (SystemInformation), not a WMI probe:
            // the edge handler must never park on a device query (the LHM
            // storage-park class of bug).
            return System.Windows.Forms.SystemInformation.PowerStatus.PowerLineStatus
                == System.Windows.Forms.PowerLineStatus.Online;
        }

        #endregion

        #region Production observation and execution

        private static WatcherTelemetry Observe(IHardwareMonitoringService hardware, IFpsMonitoringService fps)
        {
            HardwareMetricsSnapshot snap = hardware.ReadSnapshot();
            bool live = snap.CpuTemperature > 0 || snap.GpuTemperature > 0
                || snap.CpuUsage > 0 || snap.GpuUsage > 0;

            return new WatcherTelemetry(
                HasSnapshot: live,
                CpuTempC: snap.CpuTemperature,
                GpuTempC: snap.GpuTemperature,
                CpuUsagePct: snap.CpuUsage,
                GpuUsagePct: (int)Math.Round(snap.GpuUsage),
                Fps: fps.Latest.Fps,
                IsOnAc: SystemInformationPowerLineOnline(),
                ForegroundExe: "");
        }

        private static async Task<string?> ReadVerbValueAsync(
            WatcherValueSource source, IFanCapability fan, IPowerCapability power, IGpuMuxCapability mux)
        {
            try
            {
                switch (source)
                {
                    case WatcherValueSource.FanRpm:
                        return (await fan.GetStatusAsync().ConfigureAwait(false)).MainFanRpm.ToString();
                    case WatcherValueSource.FanTurbo:
                        return (await fan.GetStatusAsync().ConfigureAwait(false)).TurboActive.ToString();
                    case WatcherValueSource.PerformanceMode:
                        return (await fan.GetStatusAsync().ConfigureAwait(false)).PerformanceMode.ToString();
                    case WatcherValueSource.GpuCtgp:
                        return (await power.GetContractTruthAsync().ConfigureAwait(false)).GpuCtgp.ToString();
                    case WatcherValueSource.GpuDb:
                        return (await power.GetContractTruthAsync().ConfigureAwait(false)).GpuDb.ToString();
                    case WatcherValueSource.CpuPl1:
                        return (await power.GetContractTruthAsync().ConfigureAwait(false)).Pl1.ToString();
                    case WatcherValueSource.CpuPl2:
                        return (await power.GetContractTruthAsync().ConfigureAwait(false)).Pl2.ToString();
                    case WatcherValueSource.MuxMode:
                        return (await mux.GetStateAsync().ConfigureAwait(false)).Mode.ToString();
                    default:
                        return null;
                }
            }
            catch (EngineException ex) when (ex.Code == EngineErrorCode.Busy)
            {
                // The story's backoff requirement: skip this cycle, the next
                // scheduled diff poll (>= 5 s away) retries. No retry storm.
                Log.Debug("[Watcher] diff poll busy on {Source}, next poll in {Floor}s",
                    source, WatcherPolicy.DiffPollFloor.TotalSeconds);
                return null;
            }
            catch (Exception ex)
            {
                Log.Debug("[Watcher] diff poll failed on {Source}: {Message}", source, ex.Message);
                return null;
            }
        }

        /// <summary>The whitelisted action executor (production path; internal
        /// for the app-suite pins). <paramref name="showToast"/> is the
        /// hermetic-test seam for the WPF toast overlay.</summary>
        internal static async Task ExecuteAsync(
            WatcherRule rule, PresetArbiter arbiter, IFanCapability fan, IPowerCapability power,
            Action<string, string, string?>? showToast = null)
        {
            showToast ??= (title, body, accent) =>
                ToastNotification.ShowToastNotification(title, body, accent: accent);

            switch (rule.Action)
            {
                case WatcherActionKind.ApplyPreset:
                {
                    var result = await arbiter.RequestAsync(PresetIntentSource.AutomationRule, rule.ProfileName!)
                        .ConfigureAwait(false);
                    if (result.Applied)
                    {
                        // Same toast shape as the AC/DC automations (the
                        // "{0} Applied" key already exists in every catalog).
                        showToast(
                            LocalizationService.Format("{0} Applied", rule.ProfileName),
                            string.Empty,
                            rule.ProfileName);
                    }
                    break;
                }
                case WatcherActionKind.SetPerformanceMode:
                    await power.SetPerformanceModeAsync(rule.PerformanceMode!.Value).ConfigureAwait(false);
                    break;
                case WatcherActionKind.SetFanTurbo:
                {
                    // The engine verb is a toggle; set semantics: read, and
                    // toggle only on a mismatch (a blind toggle on a matching
                    // state turns it OFF - the classic automation self-flip).
                    bool want = rule.FanTurboOn!.Value;
                    FanStatusDto status = await fan.GetStatusAsync().ConfigureAwait(false);
                    if (status.TurboActive != want)
                        await fan.ToggleTurboAsync().ConfigureAwait(false);
                    break;
                }
                case WatcherActionKind.Notify:
                    // Rule text is user content, not a catalog key: shown as written.
                    showToast(rule.NotifyTitle ?? string.Empty, rule.NotifyMessage ?? string.Empty, null);
                    break;
            }
        }

        #endregion

        #region Foreground process

        private static string ResolveForegroundExe()
        {
            IntPtr window = GetForegroundWindow();
            if (window == IntPtr.Zero)
                return "";

            _ = GetWindowThreadProcessId(window, out uint processId);
            if (processId == 0)
                return "";

            try
            {
                using Process process = Process.GetProcessById(unchecked((int)processId));
                return process.ProcessName;
            }
            catch (Exception)
            {
                // Exiting process / access-denied (elevated foreground app on
                // an asInvoker app): no name this tick, no crash.
                return "";
            }
        }

        [System.Runtime.InteropServices.LibraryImport("user32.dll")]
        private static partial IntPtr GetForegroundWindow();

        [System.Runtime.InteropServices.LibraryImport("user32.dll")]
        private static partial uint GetWindowThreadProcessId(IntPtr window, out uint processId);

        #endregion
    }
}
