using Microsoft.Extensions.Hosting;
using Serilog;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using Uniwill_Control_Utility.Models;
using Uniwill_Control_Utility.Properties;
using Uniwill_Control_Utility.Services;
using Uniwill_Control_Utility.Views.Pages;

namespace Uniwill_Control_Utility.Services
{
    /// <summary>
    /// BetterRGB streamer engine — a port of the open-source BetterRGB
    /// (BetterRGB-ITE8291-Adaptive, source\better_rgb.c) streamer, not OpenRevo's
    /// announce framing. Instead of sending an effect byte to the ITE controller's
    /// firmware MCU, this engine renders one 512-byte interleaved frame per tick in
    /// software (BetterRgbFrameRenderer) and streams it on the RGB runtime interface:
    /// UsagePage 0xFF03 (or MI_01) via 1 feature report + 8 x 65-byte output writes
    /// per frame (send_frame, better_rgb.c:1708-1745), paced to the target FPS.
    ///
    /// Transport gate: the reference negotiates the RGB runtime mode ONLY on the
    /// 0xFF03/MI_01 interface (resolve_device_path, 968-1004). If KeyboardHidService
    /// did not find that interface, this engine refuses to start (watchdog then
    /// falls back to the Hardware MCU path) — there is no announce fallback.
    ///
    /// Handle ownership: while running, this service owns the ONLY live keyboard-zone
    /// HID consumer. The Keyboard page and DeviceApplier route their applies through
    /// SetParams instead of opening their own handles — concurrent report traffic
    /// would corrupt the feature → WriteFile pairing.
    ///
    /// Policies mirrored from the reference:
    /// - watchdog: 3 failed start attempts → mark unsupported, fall back to Hardware MCU
    /// - tracker: send-failure streak → in-place reconnect, keeping the frame counter
    ///   and the BetterRgbFrameState ("resuming animation without timeline reset")
    /// - AC policy: plugged in → streamer; on battery → hardware effects (0% CPU)
    /// - wake resume: MainWindow's resume hook calls ResumeAfterWake (8 open attempts)
    /// </summary>
    public sealed class BetterRgbStreamService : IHostedService, IDisposable
    {
        private const int StartupDelayMs = 1000;
        private const int OpenRetries = 10;
        private const int OpenRetryDelayMs = 500;
        private const int FirmwareSettleMs = 200;
        private const int WatchdogAttempts = 3;
        private const int SendFailStreakLimit = 3;
        private const int WakeOpenRetries = 8;

        // Settle after wake before re-handshaking: the ITE controller is not ready
        // right after hibernation resume, and every EnterCustomModeC that lands on a
        // half-ready device clears the plane — that is the visible "blink" storm.
        private const int ResumeSettleMs = 6_000;

        // Retry the first frame on the SAME handle before re-handshaking: re-entering
        // UserMode (zone reset + clear + UserMode) is what repaints/blinks the keyboard,
        // so a failed first frame must not trigger another handshake.
        private const int FirstFrameRetries = 3;
        private const int FirstFrameRetryDelayMs = 300;

        // The ITE RGB MCU resets itself on a display (mux) switch and on wake; streaming
        // frames into a resetting MCU repaints it key-by-key — the visible blink storm.
        // Pause the stream and let the MCU settle before resuming. The watcher detects
        // the transition after the switch already happened, so the MCU is settled by
        // the time the settle starts: no fixed wait needed — StartAttempt's open retries
        // and first-frame retries absorb any residual half-ready window.
        private const int DisplaySwitchSettleMs = 0;
        // Same mechanism on the reconnect ladder: settle before the first handshake so
        // attempts stop landing on a half-ready device.
        private const int ReconnectSettleMs = 3_000;
        private const int StaticKeepaliveFps = 10;
        private const int FpsFloor = 15;   // reference TARGET_FPS_MIN
        private const int FpsCeiling = 60; // reference TARGET_FPS_MAX

        // Reconnect ladder (HydroUIService idiom): short waits first, capped tail.
        private static readonly int[] ReconnectDelaysMs =
            { 100, 250, 500, 1000, 2000, 4000, 8000, 16000 };

        // Light bar sync: the strip is one HID zone, so the bottom-row sample is
        // pushed as a single CMD_SET_COLOR at a throttled rate — 10 Hz is plenty
        // for one color and keeps the feature-report traffic small.
        private const int LightBarSyncIntervalMs = 100;

        private readonly object _engineLock = new();
        private KeyboardHidService? _hid;
        private CancellationTokenSource? _loopCts;
        private Task? _loopTask;

        // Immutable parameter snapshot, swapped atomically (EcTelemetryService's
        // Volatile.Read discipline): the render loop reads the reference once per
        // frame, so SetParams never blocks and never tears.
        private volatile BetterRgbParams? _params;

        // Per-stream animation state (the C effect threads' locals). Kept across
        // in-place reconnects; discarded on a full stop so a fresh start begins a
        // fresh timeline.
        private BetterRgbFrameState? _frameState;

        private volatile bool _running;
        private volatile bool _unsupported;
        private volatile bool _starting; // a start-attempt task owns the handshake window exclusively
        private volatile bool _suspendedForSleep; // blanket thrown on suspend; only ResumeAfterWake lifts it
        private volatile bool _suspendedForOverride;
        private volatile bool _idleOffRequested;
        private int _sendFailStreak;
        private long _totalFrames;
        private bool _disposed;
        private bool _shutdownFallbackDone; // shutdown firmware-effect push runs at most once

        // Light bar sync bookkeeping: whether the strip is currently owned by the
        // sync (a sampled push happened since the last restore) and the last push
        // time for the throttle.
        private bool _syncPushActive;
        private long _lastSyncPushMs;

        // Temporal easing of the pushed strip color: the sampler's target is a
        // discrete argmax (brightest key), so a slow Wave jumps key-to-key. The
        // eased color lerps toward the target each push, turning those jumps into
        // gradual transitions. Reset on restore so a fresh sync starts on its own
        // first target (no ghost from a previous session).
        private bool _syncEaseInit;
        private int _syncR, _syncG, _syncB;
        private const double LightBarSyncEase = 0.3;

        public volatile int EffectiveFps;

        public BetterRgbStreamService()
        {
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            // The startup apply is MainWindow.AsyncApplyKeyboardOnStart's job (it owns
            // the delay/retry ordering against device enumeration); nothing to do here.
            return Task.CompletedTask;
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            StopEngine("shutdown");
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _loopCts?.Cancel();
            _loopCts?.Dispose();
            _hid?.Dispose();
            _hid = null;
            _frameState = null;
        }

        public bool IsRunning { get => _running; }
        public bool IsUnsupported { get => _unsupported; }

        /// <summary>
        /// Light bar sync ownership: the strip is owned by the sync while the loop
        /// runs AND the live parameter snapshot carries the sync flag — so a SetParams
        /// toggle (or an engine stop) flips this within the same frame. The Light Bar
        /// page reads it to drive its override overlay.
        /// </summary>
        public bool IsLightBarSyncActive
            => _running && _params?.LightBarSync == true;
        public long TotalRenderedFrames { get => _totalFrames; }

        /// <summary>
        /// Swaps the render parameters (effect, per-effect sliders, palettes,
        /// brightness, FPS). Cheap and thread-safe — the Keyboard page's debounce
        /// applies and Adaptive presets route through here while streaming.
        /// </summary>
        public void SetParams(KeyboardSettings settings)
        {
            _params = BuildSnapshot(settings);
        }

        /// <summary>
        /// Starts the streamer engine. Non-blocking: the open/handshake/first-frame
        /// attempt sequence runs on a worker task under the watchdog.
        /// </summary>
        public void StartEngine(string reason)
        {
            lock (_engineLock)
            {
                // _starting serializes the handshake window: without it, the resume hook,
                // the AC-policy event and the startup apply each spawn their own StartAttempt
                // task, and every concurrent watchdog attempt blinks the keyboard once more
                // (EnterCustomModeC clears the plane on every try). _suspendedForSleep keeps
                // the blanket thrown on suspend: the AC-policy event fires during sleep/wake
                // and must not restart the stream against the resetting MCU.
                if (_disposed || _running || _starting || _suspendedForSleep)
                    return;

                // A user-initiated start clears the watchdog verdict and idle-off so
                // a previously unsupported engine gets another chance (OpenRevo resets
                // the verdict on an explicit engine switch).
                _unsupported = false;
                _idleOffRequested = false;

                var prm = _params ?? BuildSnapshot(KeyboardSettingsService.Load());
                if (!prm.PowerOn)
                    return;

                _starting = true;
                _ = Task.Run(() =>
                {
                    try
                    {
                        StartAttempt(prm, reason, OpenRetries, OpenRetryDelayMs);
                    }
                    finally
                    {
                        lock (_engineLock) { _starting = false; }
                    }
                });
            }
        }

        public void StopEngine(string reason)
        {
            lock (_engineLock)
            {
                if (_running)
                {
                    _running = false;
                    _loopCts?.Cancel();

                    Log.Information("[BetterRGB] Stream engine thread STOPPED (total rendered frames: {Frames}, reason: {Reason})",
                        _totalFrames, reason);
                }

                // Release the stream endpoint if we still hold it. This must run even
                // when the loop already stopped (a force-close cancels the loop first,
                // leaving _running false), so the keyboard is never left stuck in the
                // BetterRGB UserMode snapshot.
                if (_hid != null)
                {
                    try
                    {
                        _hid.ExitUserModeStream();
                    }
                    catch (Exception) { /* handle may already be dead */ }

                    _hid.Dispose();
                    _hid = null;
                }

                // Light bar sync: when the sync owns the strip and the engine stops for
                // a reason that leaves the keyboard on its own state, hand the strip
                // back to the user's saved LightBar settings. Skip the reasons where
                // another owner takes over immediately: sleep/wake/display-switch (the
                // MCU is resetting and MainWindow's resume apply re-applies after wake),
                // adaptive-override (DeviceApplier owns the strip), auto-power-battery
                // (MainWindow's auto-power policy owns the strip).
                if (_syncPushActive && reason != "sleep" && reason != "wake"
                    && reason != "display-switch" && reason != "adaptive-override"
                    && reason != "auto-power-battery")
                {
                    _syncPushActive = false;
                    _ = Task.Run(() => RestoreLightBar(reason));
                }

                // On app shutdown, push a firmware RGB effect once so the keyboard isn't
                // black or stuck on the last BetterRGB frame. Runs regardless of whether
                // the loop was running, but only once (StopEngine("shutdown") is called
                // both synchronously from OnExit and again by the host's StopAsync).
                if (reason == "shutdown" && !_shutdownFallbackDone)
                {
                    _shutdownFallbackDone = true;
                    var settings = KeyboardSettingsService.Load();
                    if (settings.Engine == KeyboardEngine.BetterRGB)
                    {
                        // Always leave the keyboard on a predictable, visible effect on
                        // normal exit: the firmware Rainbow effect at the lowest firmware
                        // brightness (level 1 of 1-7). In-memory only — the saved settings
                        // (and Engine=BetterRGB) are untouched, so next startup still boots
                        // BetterRGB.
                        settings.EffectMode = KeyboardEffect.Rainbow;
                        settings.PerKeyMode = false;
                        settings.Brightness = 1;

                        try
                        {
                            ApplyHardwareFallback(settings, "shutdown");
                        }
                        catch (Exception) { /* best-effort during teardown */ }
                    }
                }

                _frameState = null;
            }
        }

        /// <summary>
        /// Wake resume: reopen with more attempts (the HID controller may not be
        /// discoverable right after wake), then restart the loop.
        /// OpenRevo: "resume_after_wake: BetterRGB Streamer successfully reconnected on attempt N".
        /// </summary>
        /// <summary>
        /// Suspend hook (MainWindow's PowerModes.Suspend branch): stop the loop and throw
        /// the blanket BEFORE the MCU's wake-time reset. Without it the loop fights the
        /// dead device all through sleep and burns the reconnect ladder (handshake blinks)
        /// in the sleep/wake window. Only ResumeAfterWake lifts the blanket.
        /// </summary>
        public void SuspendForSleep()
        {
            _suspendedForSleep = true;
            StopEngine("sleep");
        }

        public void ResumeAfterWake()
        {
            lock (_engineLock)
            {
                if (_disposed || _starting || _unsupported)
                    return;

                // Lift the sleep blanket: the system is waking, starts may proceed again
                // (after the settle below).
                _suspendedForSleep = false;

                var prm = _params;
                if (prm == null || !prm.PowerOn)
                    return;

                // The loop may still be "running" while its sends fail against the
                // resetting MCU — stop it so the endpoint is released before settling.
                if (_running)
                    StopEngine("wake");

                _starting = true;
                _ = Task.Run(() =>
                {
                    try
                    {
                        // The ITE HID controller is not ready right after hibernation
                        // resume: handshaking immediately clears the plane on a half-ready
                        // device and every failed attempt blinks the keyboard. Let the
                        // firmware settle first, then handshake once.
                        Thread.Sleep(ResumeSettleMs);
                        if (_running || _disposed)
                            return;
                        StartAttempt(prm, "resume_after_wake", WakeOpenRetries, OpenRetryDelayMs);
                    }
                    finally
                    {
                        lock (_engineLock) { _starting = false; }
                    }
                });
            }
        }

        /// <summary>
        /// Display (mux) switch hook, called by MainWindow's panel watcher on any panel
        /// attach/detach transition: the ITE RGB MCU resets itself during the switch and
        /// streaming frames into it repaints the keyboard key-by-key — the blink storm.
        /// Release the endpoint, let the MCU settle, then resume with one handshake.
        /// </summary>
        public void RestartAfterDisplaySwitch()
        {
            lock (_engineLock)
            {
                if (_disposed || _starting)
                    return;

                var prm = _params;
                if (prm == null || !prm.PowerOn)
                    return;

                if (_running)
                    StopEngine("display-switch");

                _starting = true;
                _ = Task.Run(() =>
                {
                    try
                    {
                        Thread.Sleep(DisplaySwitchSettleMs);
                        if (_running || _disposed)
                            return;
                        StartAttempt(prm, "display-switch", OpenRetries, OpenRetryDelayMs);
                    }
                    finally
                    {
                        lock (_engineLock) { _starting = false; }
                    }
                });
            }
        }

        /// <summary>
        /// AC power policy. Plugged in → restore the streamer; on battery → stop it
        /// and apply the equivalent firmware effect once (0% CPU).
        /// </summary>
        public void ApplyAcPolicy(bool acPlugged)
        {
            var settings = KeyboardSettingsService.Load();
            if (settings.Engine != KeyboardEngine.BetterRGB)
                return;

            if (acPlugged)
            {
                if (!_running && !_suspendedForOverride)
                {
                    Log.Information("[BetterRGB] AC plugged in: auto-restoring BetterRGB {Fps}FPS streamer engine", settings.BetterRgbFps);
                    SetParams(settings);
                    StartEngine("ac-restore");
                }
            }
            else
            {
                if (_running)
                {
                    Log.Information("[BetterRGB] AC unplugged: auto-fallback keyboard lighting to factory hardware mode (0% CPU)");
                    StopEngine("ac-battery");
                    _ = Task.Run(() => ApplyHardwareFallback(settings, "ac-battery"));
                }
            }
        }

        public void SuspendForOverride()
        {
            _suspendedForOverride = true;
            if (_running)
                StopEngine("adaptive-override");
        }

        public void ResumeFromOverride()
        {
            if (!_suspendedForOverride)
                return;
            _suspendedForOverride = false;

            var settings = KeyboardSettingsService.Load();
            if (settings.Engine == KeyboardEngine.BetterRGB && settings.PowerOn)
            {
                SetParams(settings);
                StartEngine("override-lift");
            }
        }

        /// <summary>
        /// Idle timer / power-off: stop the loop and turn the keyboard off once.
        /// TurnOff alone would leave the loop streaming black frames.
        /// </summary>
        public void RequestIdleOff()
        {
            _idleOffRequested = true;
            StopEngine("idle-off");
            _ = Task.Run(() =>
            {
                var hid = new KeyboardHidService();
                try
                {
                    if (hid.Open())
                        hid.TurnOff();
                }
                catch (Exception ex)
                {
                    Log.Warning("[BetterRGB] Idle-off turn off failed: {Message}", ex.Message);
                }
                finally
                {
                    hid.Dispose();
                }
            });
        }

        private static BetterRgbParams BuildSnapshot(KeyboardSettings settings)
        {
            int fps = Math.Clamp(settings.BetterRgbFps, FpsFloor, FpsCeiling);

            var frame = new BetterRgbFrameParams
            {
                // Presets saved before Touch Current / Screen Ambient were removed
                // can carry the retired mode values 14/15 — fall back to Current.
                Mode = settings.BetterRgbMode <= BetterRgbEffect.Current
                    ? settings.BetterRgbMode : BetterRgbEffect.Current,
                BreathSpeed = Math.Clamp(settings.BetterRgbBreathSpeed, 1, 40),
                BreathSmooth = Math.Clamp(settings.BetterRgbBreathSmooth, 2, 150),
                BreathRandom = settings.BetterRgbBreathRandom,
                BreathColor = ParseColorOr(settings.BetterRgbBreathColor, Color.FromRgb(0x00, 0x78, 0xFF)),
                WaveAngle = Math.Clamp(settings.BetterRgbWaveAngle, 0, 359),
                WaveSpeed = Math.Clamp(settings.BetterRgbWaveSpeed, 1, 10),
                SparkleSpeed = Math.Clamp(settings.BetterRgbSparkleSpeed, 1, 10),
                SparkleDensity = Math.Clamp(settings.BetterRgbSparkleDensity, 3, 20),
                ReactiveDuration = Math.Clamp(settings.BetterRgbReactiveDuration, 1, 45),
                ReactiveRandom = settings.BetterRgbReactiveRandom,
                ReactiveColor = ParseColorOr(settings.BetterRgbReactiveColor, Color.FromRgb(0xFF, 0xFF, 0xFF)),
                WheelSpeed = Math.Clamp(settings.BetterRgbWheelSpeed, 1, 10),
                WheelReverse = settings.BetterRgbWheelReverse,
                LightningSpeed = Math.Clamp(settings.BetterRgbLightningSpeed, 1, 10),
                LightningSmooth = Math.Clamp(settings.BetterRgbLightningSmooth, 1, 20),
                LightningWidth = Math.Clamp(settings.BetterRgbLightningWidth, 1, 4),
                LightningConcurrent = Math.Clamp(settings.BetterRgbLightningConcurrent, 1, 4),
                FlameSpeed = Math.Clamp(settings.BetterRgbFlameSpeed, 1, 10),
                FlameSmooth = Math.Clamp(settings.BetterRgbFlameSmooth, 1, 20),
                RainSpeed = Math.Clamp(settings.BetterRgbRainSpeed, 1, 10),
                RainDensity = Math.Clamp(settings.BetterRgbRainDensity, 1, 5),
                RainColorCount = Math.Clamp(settings.BetterRgbRainColorCount, 1, 5),
                RainColors = ParseColorsPadded(settings.BetterRgbRainColors, 5,
                    Color.FromRgb(0x50, 0x8C, 0xFF)),
                MatrixStyle = Math.Clamp(settings.BetterRgbMatrixStyle, 0, 3),
                MatrixSpeed = Math.Clamp(settings.BetterRgbMatrixSpeed, 1, 10),
                MatrixDensity = Math.Clamp(settings.BetterRgbMatrixDensity, 1, 5),
                MatrixColorCount = Math.Clamp(settings.BetterRgbMatrixColorCount, 1, 3),
                MatrixColors = ParseColorsPadded(settings.BetterRgbMatrixColors, 3,
                    Color.FromRgb(0x00, 0xFF, 0x3C)),
                StaticLayout = Math.Clamp(settings.BetterRgbStaticLayout, 0, 1),
                StaticZones = Math.Clamp(settings.BetterRgbStaticZones, 1, 10),
                StaticZoneColors = ParseColorsPadded(settings.BetterRgbStaticZoneColors, 10,
                    Color.FromRgb(0xFF, 0xFF, 0xFF)),
                RippleSpeed = Math.Clamp(settings.BetterRgbRippleSpeed, 1, 10),
                RippleWidth = Math.Clamp(settings.BetterRgbRippleWidth, 1, 4),
                RippleRandom = settings.BetterRgbRippleRandom,
                RippleColor = ParseColorOr(settings.BetterRgbRippleColor, Color.FromRgb(0x00, 0xB4, 0xFF)),
                RainbowSpeed = Math.Clamp(settings.BetterRgbRainbowSpeed, 1, 20),
                QuicksandSpeed = Math.Clamp(settings.BetterRgbQuicksandSpeed, 1, 10),
                QuicksandScale = Math.Clamp(settings.BetterRgbQuicksandScale, 1, 10),
                QuicksandColors = ParseColorsPadded(settings.BetterRgbQuicksandColors, 3,
                    Color.FromRgb(0xFF, 0x00, 0x00)),
                CurrentSpeed = Math.Clamp(settings.BetterRgbCurrentSpeed, 1, 10),
                CurrentWidth = Math.Clamp(settings.BetterRgbCurrentWidth, 2, 8),
                CurrentColors = ParseColorsPadded(settings.BetterRgbCurrentColors, 3,
                    Color.FromRgb(0xFF, 0x00, 0x00)),
            };

            // Brightness is NOT baked into the palettes: the C engine applies
            // g_brightness to the whole rendered frame (send_frame 1711-1715), so
            // the service multiplies every final byte by BrightnessPercent/100.

            return new BetterRgbParams
            {
                Frame = frame,
                BrightnessPercent = Math.Clamp(settings.BrightnessPercent, 0, 100),
                Fps = fps,
                PowerOn = settings.PowerOn,
                LightBarSync = settings.BetterRgbLightBarSync,
                Raw = settings,
            };
        }

        private static System.Windows.Media.Color ParseColorOr(string data,
            System.Windows.Media.Color fallback)
        {
            var list = ParseColorList(data);
            return list.Count > 0 ? list[0] : fallback;
        }

        private static System.Windows.Media.Color[] ParseColorsPadded(string data, int count,
            System.Windows.Media.Color fallback)
        {
            var list = ParseColorList(data);
            var result = new System.Windows.Media.Color[count];
            for (int i = 0; i < count; i++)
                result[i] = i < list.Count ? list[i] : fallback;
            return result;
        }

        private static List<System.Windows.Media.Color> ParseColorList(string data)
        {
            var result = new List<System.Windows.Media.Color>();
            foreach (var part in data.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var hex = part.Trim();
                if (hex.StartsWith('#'))
                    hex = hex.Substring(1);
                if (hex.Length == 6)
                {
                    try
                    {
                        var r = byte.Parse(hex.Substring(0, 2), System.Globalization.NumberStyles.HexNumber);
                        var g = byte.Parse(hex.Substring(2, 2), System.Globalization.NumberStyles.HexNumber);
                        var b = byte.Parse(hex.Substring(4, 2), System.Globalization.NumberStyles.HexNumber);
                        result.Add(System.Windows.Media.Color.FromRgb(r, g, b));
                    }
                    catch (FormatException) { /* skip invalid entry */ }
                }
            }
            return result;
        }

        /// <summary>
        /// One start attempt: open (retry ladder) → firmware settle → RGB-interface
        /// gate → C-exact handshake → first frame. Three failed attempts mark the
        /// streamer unsupported and fall back to the hardware MCU path once
        /// (OpenRevo's STREAMER_WATCHDOG verdict).
        /// </summary>
        private bool StartAttempt(BetterRgbParams prm, string reason, int openRetries, int retryDelayMs)
        {
            for (int attempt = 1; attempt <= WatchdogAttempts; attempt++)
            {
                if (_running || _disposed)
                    return false; // another start won the window; do not blink the keyboard again

                KeyboardHidService? hid = null;
                try
                {
                    for (int open = 0; open < openRetries; open++)
                    {
                        if (open > 0)
                            Thread.Sleep(retryDelayMs);

                        hid = new KeyboardHidService();
                        if (hid.Open())
                            break;
                        hid.Dispose();
                        hid = null;
                    }

                    if (hid == null)
                        continue;

                    // Reference transport gate: the RGB runtime mode lives only on the
                    // UsagePage 0xFF03 / MI_01 interface. No interface → no announce
                    // fallback (user decision) — the watchdog marks unsupported and the
                    // Hardware MCU path takes over.
                    if (!hid.HasRgbStreamInterface)
                    {
                        Log.Warning("[BetterRGB] RGB stream interface (0xFF03/MI_01) not found on this device — refusing to start ({Attempt})", attempt);
                        hid.Dispose();
                        continue;
                    }

                    Thread.Sleep(FirmwareSettleMs);

                    if (!hid.EnterCustomModeC())
                        continue;

                    // First frame must land before the engine counts as active. Retry on
                    // the SAME handle: re-entering UserMode (zone reset + clear + UserMode)
                    // repaints the keyboard, so a failed first frame must not trigger
                    // another handshake — that is the blink storm after wake/mux switches.
                    lock (_engineLock)
                    {
                        _frameState ??= new BetterRgbFrameState();
                    }
                    var frame = new byte[BetterRgbFrameRenderer.FrameSize];
                    bool firstFrameOk = false;
                    for (int fr = 0; fr < FirstFrameRetries; fr++)
                    {
                        if (fr > 0)
                            Thread.Sleep(FirstFrameRetryDelayMs);
                        RenderFrame(prm, _frameState, 0L, 0.0, frame);
                        if (hid.SendStreamFrameInterleaved(frame))
                        {
                            firstFrameOk = true;
                            break;
                        }
                    }
                    if (!firstFrameOk)
                        continue;

                    lock (_engineLock)
                    {
                        if (_disposed)
                        {
                            hid.Dispose();
                            return false;
                        }
                        _hid?.Dispose();
                        _hid = hid;
                        _running = true;
                        _sendFailStreak = 0;
                    }

                    Log.Information("[BetterRGB] RGB runtime mode handshake (0x33/0x32) succeeded on the 0xFF03/MI_01 interface! Spawning streaming worker thread... ({Reason})", reason);
                    _loopCts = new CancellationTokenSource();
                    _loopTask = Task.Run(() => StreamLoop(prm, _loopCts.Token));
                    return true;
                }
                catch (Exception ex)
                {
                    Log.Warning("[BetterRGB] Start attempt {Attempt}/{Total} failed: {Message}",
                        attempt, WatchdogAttempts, ex.Message);
                    hid?.Dispose();
                }
            }

            _unsupported = true;
            Log.Error("[BetterRGB] Streamer thread failed to stay active after {Attempts} attempts. Marking streamer unsupported & falling back to Hardware MCU.",
                WatchdogAttempts);
            _ = Task.Run(() => ApplyHardwareFallback(prm.Raw!, "watchdog-fallback"));
            return false;
        }

        /// <summary>Render one frame + software brightness (C send_frame 1711-1715).</summary>
        private static void RenderFrame(BetterRgbParams prm, BetterRgbFrameState st,
            long frameIndex, double elapsedMs, byte[] frame512)
        {
            BetterRgbFrameRenderer.Render(prm.Frame.Mode, prm.Frame, st, frameIndex, elapsedMs, frame512);
            BetterRgbFrameRenderer.ApplyBrightness(frame512, prm.BrightnessPercent);

        }

        /// <summary>
        /// The paced render loop (the reference's pace_frame + record_transport_result
        /// pacer). frameIndex and the accumulated elapsedMs are the animation
        /// timelines: reconnects and adaptive-FPS changes never reset or skip them —
        /// degradation is done by lowering effectiveFps, never by dropping frames.
        /// </summary>
        private async Task StreamLoop(BetterRgbParams prm, CancellationToken ct)
        {
            try
            {
                long frameIndex = 1;
                bool firstFrameLogged = false;
                // The reference's adaptive pacer (record_transport_result): the target
                // FPS is a wish; effectiveFps tracks what the HID transport can
                // actually sustain, degrading -5 on slow/failing frames and
                // recovering +1 after 10 healthy seconds. Clamped [15, target].
                var effectiveFps = Math.Clamp(prm.Fps, FpsFloor, FpsCeiling);
                EffectiveFps = effectiveFps;
                int slowStreak = 0, healthyStreak = 0;
                var lastTargetFps = Math.Clamp(prm.Fps, FpsFloor, FpsCeiling);
                // The reference drives wall-clock effects from GetTickCount64() -
                // startedAt per frame, not from an accumulated frame period: animation
                // speed is constant real-time and the frame rate only changes sampling
                // density. An accumulated 1/fps timeline drifts slow/fast exactly
                // when the pacer degrades/recovers — that drift is the residual
                // smoothness gap. Keep the anchor across reconnects (no reset).
                var streamStartMs = CurrentMonotonicMs();
                long nextDeadlineMs = CurrentMonotonicMs() + 1000 / effectiveFps;
                // C: every set_effective_fps sets g_pacerResetRequested -> the next
                // pace_frame re-anchors the schedule (no backlog from the old rate).
                bool pacerReset = false;

                var frame = new byte[BetterRgbFrameRenderer.FrameSize];

                while (true)
                {
                    var p = _params ?? prm;
                    if (_idleOffRequested || !p.PowerOn)
                        return;

                    // C: set_effective_fps(value, "target changed by user") — when
                    // the FPS slider moves, snap the pacer to the new target
                    // immediately (OpenRevo does this on every user FPS change), so
                    // the slider takes effect mid-stream instead of crawling +1/10s.
                    var targetFps = Math.Clamp(p.Fps, FpsFloor, FpsCeiling);
                    if (targetFps != lastTargetFps)
                    {
                        lastTargetFps = targetFps;
                        effectiveFps = targetFps;
                        slowStreak = 0;
                        healthyStreak = 0;
                        pacerReset = true;
                    }

                    bool staticSource = p.Frame.Mode == BetterRgbEffect.Static
                        || p.Frame.Mode == BetterRgbEffect.Rainbow;
                    // Pacing + phase use effectiveFps (the adaptive pacer), so the
                    // animation stays time-correct when the transport degrades.
                    int fps = staticSource ? Math.Min(effectiveFps, StaticKeepaliveFps) : effectiveFps;

                    // Wall-clock timeline, exactly like C's per-frame
                    // (GetTickCount64() - startedAt).
                    double elapsedMs = CurrentMonotonicMs() - streamStartMs;

                    var st = _frameState;
                    if (st == null)
                        return; // engine stopped mid-loop

                    RenderFrame(p, st, frameIndex, elapsedMs, frame);

                    if (!firstFrameLogged)
                    {
                        int nz = 0;
                        for (int i = 0; i < frame.Length; i++)
                            if (frame[i] != 0)
                                nz++;
                        Log.Information("[BetterRGB] pre-send frame diagnostic: {NonZero}/512 non-zero; brightness={Bright}; elapsedMs={Elapsed}; mode={Mode}",
                            nz, p.BrightnessPercent, elapsedMs, p.Frame.Mode);
                    }

                    var sendStartMs = CurrentMonotonicMs();
                    bool ok = false;
                    try
                    {
                        ok = _hid != null && _hid.SendStreamFrameInterleaved(frame);
                    }
                    catch (Exception)
                    {
                        ok = false;
                    }
                    var callTimeMs = CurrentMonotonicMs() - sendStartMs;

                    // One-shot frame-content diagnostic: separates "renderer produced
                    // black" from "device ignored the writes".
                    if (!firstFrameLogged && ok)
                    {
                        firstFrameLogged = true;
                        int nonZero = 0;
                        for (int i = 0; i < frame.Length; i++)
                            if (frame[i] != 0)
                                nonZero++;
                        Log.Information("[BetterRGB] first frame diagnostic: {NonZero}/512 non-zero bytes; Esc[21..23]={E0}/{E1}/{E2}, CtrlL[1..3]={C0}/{C1}/{C2}, mode={Mode}",
                            nonZero, frame[21], frame[22], frame[23], frame[1], frame[2], frame[3], p.Frame.Mode);
                    }

                    // The reference's record_transport_result: HID failure or a send
                    // eating >=90% of the frame budget (3 in a row) degrades
                    // effectiveFps by 5; 10 healthy seconds recover +1 toward
                    // target. This is why a 60 target self-tunes instead of
                    // visually resetting.
                    long budgetMs = 1000 / Math.Max(1, fps);
                    if (!ok)
                    {
                        slowStreak = 0;
                        healthyStreak = 0;
                        // C set_effective_fps clamps to [TARGET_FPS_MIN=15, target].
                        effectiveFps = Math.Max(FpsFloor, effectiveFps - 5);
                        pacerReset = true;
                        var streak = Interlocked.Increment(ref _sendFailStreak);
                        if (streak >= SendFailStreakLimit)
                        {
                            Log.Warning("[BetterRGB] send_frame persistent failure (streak={Streak}) - attempting in-place reconnect", streak);
                            if (!TryInPlaceReconnect())
                                break;
                            // frameIndex + _frameState kept: "resuming animation
                            // without timeline reset"
                        }
                    }
                    else
                    {
                        Interlocked.Exchange(ref _sendFailStreak, 0);
                        _totalFrames++;
                        frameIndex++;
                        if (callTimeMs * 10 >= budgetMs * 9)
                        {
                            healthyStreak = 0;
                            if (++slowStreak >= 3)
                            {
                                slowStreak = 0;
                                var before = effectiveFps;
                                effectiveFps = Math.Max(FpsFloor, effectiveFps - 5);
                                pacerReset = true;
                                if (effectiveFps != before)
                                    Log.Information("[BetterRGB] frame pacing: effective FPS {Before} -> {After} (transport exceeded frame budget)",
                                        before, effectiveFps);
                            }
                        }
                        else
                        {
                            slowStreak = 0;
                            if (effectiveFps < targetFps && ++healthyStreak >= effectiveFps * 10)
                            {
                                healthyStreak = 0;
                                var before = effectiveFps;
                                effectiveFps = Math.Min(targetFps, effectiveFps + 1);
                                pacerReset = true;
                                if (effectiveFps != before)
                                    Log.Information("[BetterRGB] frame pacing: effective FPS {Before} -> {After} (transport stable for 10 seconds)",
                                        before, effectiveFps);
                            }
                        }
                        EffectiveFps = effectiveFps;
                    }

                    // Light bar sync (mirror sampling): the strip is one HID zone with
                    // no slots in the 512-byte frame, so push the bottom-row average of
                    // the rendered frame as CMD_SET_COLOR on zone 2, throttled. Only
                    // after a delivered frame — a dead device fails both pushes anyway.
                    // When sync flips off mid-stream, hand the strip back once.
                    if (ok)
                    {
                        if (p.LightBarSync && _hid != null && _hid.IsLightBarAvailable)
                        {
                            var syncNowMs = CurrentMonotonicMs();
                            if (syncNowMs - _lastSyncPushMs >= LightBarSyncIntervalMs)
                            {
                                _lastSyncPushMs = syncNowMs;
                                try
                                {
                                    PushLightBarSync(frame);
                                    _syncPushActive = true;
                                }
                                catch (Exception ex)
                                {
                                    Log.Warning("[BetterRGB] Light bar sync push failed: {Message}", ex.Message);
                                }
                            }
                        }
                        else if (_syncPushActive)
                        {
                            _syncPushActive = false;
                            RestoreLightBar("sync-off");
                        }
                    }

                    long now = CurrentMonotonicMs();
                    long periodMs = Math.Max(1, 1000 / fps);
                    // C pace_frame, exact: re-anchor if more than 2 intervals behind,
                    // advance one interval, then sleep UNTIL the deadline re-checking
                    // after each sleep (Sleep(sleepMs-1) + re-query in C) so Windows
                    // timer granularity can neither overshoot the tick (long gap) nor
                    // undershoot it (burst) — a single Task.Delay does both, which is
                    // the jitter this loop keeps showing.
                    if (pacerReset || nextDeadlineMs == 0 || now > nextDeadlineMs + periodMs * 2)
                    {
                        nextDeadlineMs = now;
                        pacerReset = false;
                    }
                    nextDeadlineMs += periodMs;
                    while (true)
                    {
                        long remaining = nextDeadlineMs - CurrentMonotonicMs();
                        if (remaining <= 1)
                            break;
                        await Task.Delay((int)(remaining - 1), ct);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // normal shutdown
            }
            finally
            {
                _running = false;
                Log.Information("[BetterRGB] Stream engine thread STOPPED (total rendered frames: {Frames})", _totalFrames);
            }
        }

        /// <summary>
        /// Tracker: dispose the dead handle, reopen, re-handshake, keep frameIndex
        /// and the BetterRgbFrameState. The reference: "In-place USB device
        /// reconnected successfully, resuming animation without timeline reset".
        /// </summary>
        private bool TryInPlaceReconnect()
        {
            var hid = _hid;
            _ = Task.Run(() => hid?.Dispose());

            // Settle before the first handshake: a device that is mid-reset (wake, mux
            // switch) accepts the handshake reports and every attempt blinks the keyboard.
            Thread.Sleep(ReconnectSettleMs);

            for (int attempt = 0; attempt < ReconnectDelaysMs.Length; attempt++)
            {
                Thread.Sleep(ReconnectDelaysMs[attempt]);
                var p = _params;
                if (p == null)
                    return false;

                var fresh = new KeyboardHidService();
                if (fresh.Open() && fresh.HasRgbStreamInterface && fresh.EnterCustomModeC()
                    && ProbeStreamAlive(fresh))
                {
                    lock (_engineLock)
                    {
                        _hid = fresh;
                    }
                    _sendFailStreak = 0;
                    Log.Information("[BetterRGB] In-place USB device reconnected successfully, resuming animation without timeline reset");
                    return true;
                }
                fresh.Dispose();
            }
            return false;
        }

        /// <summary>
        /// Cheap liveness probe after a reconnect handshake: one all-black frame on the
        /// fresh handle. Without it a half-ready device "succeeds" the reconnect, the loop
        /// immediately fails sends again, and every extra cycle blinks the keyboard once
        /// more (handshake clears the plane). The probe write itself is invisible — the
        /// handshake already cleared the plane — so it adds no blink of its own.
        /// </summary>
        private static bool ProbeStreamAlive(KeyboardHidService hid)
        {
            var probe = new byte[BetterRgbFrameRenderer.FrameSize];
            try
            {
                return hid.SendStreamFrameInterleaved(probe);
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Light bar sync push: summarize the rendered frame's physical bottom row
        /// (KEYMAP Row >= 5 — width-weighted mean, see SampleBottomRowColor) and push
        /// it, eased toward that target, as CMD_SET_COLOR on zone 2 via
        /// the streamer's own light-bar handle (usage-2 collection, separate from the
        /// keyboard endpoint — no one-live-consumer conflict). Firmware brightness stays
        /// 100: the sampled color already carries the engine's BrightnessPercent
        /// multiplier (ApplyBrightness ran before this).
        /// </summary>
        private void PushLightBarSync(byte[] frame512)
        {
            var hid = _hid;
            if (hid == null || !hid.IsLightBarAvailable)
                return;
            if (!BetterRgbFrameRenderer.SampleBottomRowColor(frame512, out var r, out var g, out var b))
                return;
            // Ease the pushed color toward the sampled target (see field comment):
            // smooths the discrete brightest-key jumps into gradual transitions.
            if (!_syncEaseInit)
            {
                _syncR = r; _syncG = g; _syncB = b;
                _syncEaseInit = true;
            }
            else
            {
                _syncR = (int)Math.Round(_syncR + (r - _syncR) * LightBarSyncEase);
                _syncG = (int)Math.Round(_syncG + (g - _syncG) * LightBarSyncEase);
                _syncB = (int)Math.Round(_syncB + (b - _syncB) * LightBarSyncEase);
            }
            hid.LightBarSetColor((byte)_syncR, (byte)_syncG, (byte)_syncB, 100);
        }

        /// <summary>
        /// Hand the strip back when the sync stops: push the user's saved LightBar page
        /// settings (or off, honoring the auto-power-on-battery policy and the idle-off
        /// reason where the keyboard itself went dark). Mirrors MainWindow.ApplyLightBarOnResume's
        /// effective-state logic on a temporary handle.
        /// </summary>
        private void RestoreLightBar(string reason)
        {
            // A fresh sync (after this restore) must start on its own first target,
            // not ease from a previous session's color.
            _syncEaseInit = false;
            var hid = new KeyboardHidService();
            try
            {
                if (!hid.Open() || !hid.IsLightBarAvailable)
                    return;

                bool enabled = Settings.Default.LightBarEnabled;
                bool autoPower = Settings.Default.LightBarAutoPowerOffOnBattery;
                bool unplugged = System.Windows.Forms.SystemInformation.PowerStatus
                    .PowerLineStatus == System.Windows.Forms.PowerLineStatus.Offline;

                // idle-off: the keyboard went dark on the idle timer — the strip follows.
                if (reason == "idle-off" || !enabled || (autoPower && unplugged))
                {
                    hid.LightBarTurnOff();
                    Log.Information("[BetterRGB] Light bar sync off: strip restored to OFF ({Reason})", reason);
                    return;
                }

                // Light bar effects mapping (same as the LightBar page's Effects array).
                byte effect = Settings.Default.LightBarEffectIndex switch
                {
                    0 => 0x01, // Static
                    1 => 0x02, // Breathing
                    2 => 0x03, // Wave
                    3 => 0x04, // Loading
                    4 => 0x05, // Marquee
                    5 => 0x06, // Meteor
                    _ => 0x01
                };
                // Light bar speed is inverted: UI 0-11 (slow→fast), HID 0-11 (fast→slow).
                byte hidSpeed = (byte)(11 - Math.Clamp(Settings.Default.LightBarSpeed, 0, 11));
                int brightness = Settings.Default.LightBarBrightness;

                Thread.Sleep(FirmwareSettleMs);
                hid.LightBarTurnOn(Settings.Default.LightBarR, Settings.Default.LightBarG,
                    Settings.Default.LightBarB, brightness);
                hid.LightBarSetEffect(effect, hidSpeed, brightness);
                Log.Information("[BetterRGB] Light bar sync off: strip restored to saved settings, effect=0x{Effect:X2} ({Reason})",
                    effect, reason);
            }
            catch (Exception ex)
            {
                Log.Warning("[BetterRGB] Light bar restore failed: {Message}", ex.Message);
            }
            finally
            {
                hid.Dispose();
            }
        }

        /// <summary>
        /// One-shot firmware-effect apply at the current prm (watchdog fallback and
        /// AC-battery fallback). Mirrors DeviceApplier.ApplyKeyboardHid's sequence on a
        /// temporary handle. OpenRevo: "Hardware MCU effect applied: effect=, speed=, enabled=".
        /// </summary>
        private static void ApplyHardwareFallback(KeyboardSettings settings, string reason)
        {
            var hid = new KeyboardHidService();
            try
            {
                if (!hid.Open())
                    return;

                int brightnessPercent = (Math.Clamp(settings.Brightness, 1, 7) * 100) / 7;
                byte speed = settings.Speed;

                if (settings.PerKeyMode)
                {
                    hid.TurnOn(settings.ColorR, settings.ColorG, settings.ColorB, brightnessPercent);
                    hid.SetEffect(KeyboardEffect.Static);
                    hid.SendAllPerKeyColorsFromDict(settings.GetPerKeyColors());
                }
                else if (settings.EffectMode == KeyboardEffect.Rainbow)
                {
                    hid.SendRainbowEffect(brightnessPercent);
                }
                else
                {
                    hid.TurnOnWithEffect(settings.ColorR, settings.ColorG, settings.ColorB,
                        brightnessPercent, settings.EffectMode, speed, settings.Direction, settings.TouchReactive);

                    var colors = ParseColorList(settings.MultiColors);
                    if (colors.Count > 0)
                    {
                        if (Keyboard.IsMultiColor7Effect(settings.EffectMode))
                        {
                            if (settings.EffectMode == KeyboardEffect.Marquee)
                            {
                                var reversed = new List<Color>();
                                for (int i = colors.Count - 1; i >= 0; i--)
                                    reversed.Add(colors[i]);
                                hid.SetMultiColor(reversed);
                            }
                            else
                            {
                                hid.SetMultiColor(colors);
                            }
                        }
                        else if (Keyboard.IsMultiColor4Effect(settings.EffectMode))
                        {
                            var first4 = new List<Color>();
                            for (int i = 0; i < 4 && i < colors.Count; i++)
                                first4.Add(colors[i]);
                            hid.SetMultiColor(first4);
                        }
                    }
                }

                Log.Information("[BetterRGB] Hardware MCU effect applied: effect={Effect}, speed={Speed}, enabled=true ({Reason})",
                    settings.EffectMode, speed, reason);
            }
            catch (Exception ex)
            {
                Log.Warning("[BetterRGB] Hardware fallback apply failed: {Message}", ex.Message);
            }
            finally
            {
                hid.Dispose();
            }
        }

        private static long CurrentMonotonicMs()
        {
            return DateTime.UtcNow.Ticks / 10_000L;
        }

        /// <summary>
        /// Immutable render-parameter snapshot. SetParams builds a fresh instance and
        /// assigns it in one go; the loop reads the reference once per frame, so
        /// parameter changes never tear mid-frame.
        /// </summary>
        internal sealed class BetterRgbParams
        {
            internal BetterRgbFrameParams Frame;      // all per-effect sliders + palettes
            internal int BrightnessPercent;           // software frame multiplier (0-100)
            internal int Fps;                         // 15-60, default 30 (reference range)
            internal bool PowerOn;
            internal bool LightBarSync;               // strip follows the rendered frame
            internal KeyboardSettings? Raw;           // kept for the hardware-fallback apply
        }
    }
}
