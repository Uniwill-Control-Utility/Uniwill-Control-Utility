using Microsoft.Extensions.Hosting;
using Serilog;
using System;
using System.Threading;
using System.Threading.Tasks;
using Uniwill_Control_Utility.Properties;

namespace Uniwill_Control_Utility.Services
{
    /// <summary>
    /// Detects presses of the hardware Performance / Q-key button on Uniwill
    /// NB02-platform laptops and cycles UCU's power presets.
    ///
    /// Detection: the OEM EC firmware re-latches the TCC offset register (0x0786),
    /// alternating 0x5F / 0xDF, on EVERY mode change — a physical button press AND
    /// a software-induced one. UCU's own preset applies move the mode state, so a
    /// change alone is not a press: the poll tracks
    /// <see cref="UniwillECService.SelfModeChangeCount"/> and re-baselines while a
    /// self-induced change is in flight, which leaves only changes the firmware made
    /// on its own. WMI power-management events stay silent on this hardware, so EC
    /// polling remains the only reliable path.
    ///
    /// On a confirmed press the service advances to the next profile from
    /// <see cref="PresetProfileApplier.GetProfileNames"/> (Office -> Gaming -> Turbo
    /// -> saved Custom slots, wrapping), applies it through the same code path
    /// the UI uses, and persists the selection in Settings so the Presets page
    /// and resume handlers pick it up.
    ///
    /// Q-key LED: the EC firmware drives the LED from its own internal mode
    /// state, and the OEM button already cycles it (white -> red -> yellow),
    /// which stays aligned with the Office -> Gaming -> Turbo preset order.
    /// When <c>EnableQKeyLedSync</c> is on, the service additionally mirrors
    /// the selected preset into the EC mode index register (0x07AB) and pokes
    /// the trigger register (0x0767) so firmware that honors the mode index
    /// (e.g. Mechrevo/Uniwill EC per OpenRevo) changes the LED to match.
    /// </summary>
    public sealed class QKeyButtonService : IHostedService, IDisposable
    {
        /// <summary>
        /// EC reads are fast (~1 ms via IOCTL); 50 ms keeps button response
        /// snappy while staying far below any EC throttle limit.
        /// </summary>
        private const int PollIntervalMs = 50;

        /// <summary>
        /// A register change must persist this many consecutive reads
        /// (~200 ms) before it counts as a press. Rejects EC noise while
        /// still feeling instant to a user.
        /// </summary>
        private const int StableReadsForPress = 4;

        /// A UCU mode assert re-latches 0x786 within ~300 ms of its EC writes
        /// (measured 2026-10-11: 95 ms and 295 ms after the write). Suppress this
        /// many poll ticks (1 s at 50 ms) so the re-latch can never count as a press.
        private const int SelfChangeSuppressTicks = 20;

        /// <summary>
        /// Wait this long after host start before polling. Phase 1 startup
        /// (startup settings apply) touches the EC; polling in parallel with
        /// the handshake would race it.
        /// </summary>
        private const int StartupDelayMs = 4000;

        private readonly UniwillECService _ec;
        private readonly CancellationTokenSource _cts = new();
        private Task? _pollTask;

        private volatile bool _ecUnavailableLogged;
        private byte _lastValue;
        private byte _candidateValue;
        private int _candidateReads;
        private int _selfModeChanges;
        private int _selfChangeTicks;
        private bool _hasBaseline;
        private bool _pendingPress;
        private bool _handlingPress;
        private bool _disposed;

        public QKeyButtonService(UniwillECService ec)
        {
            _ec = ec;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _pollTask = Task.Run(() => PollLoop(_cts.Token));
            return Task.CompletedTask;
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            _cts.Cancel();
            if (_pollTask is not null)
            {
                try
                {
                    await Task.WhenAny(_pollTask, Task.Delay(2000, CancellationToken.None));
                }
                catch (Exception ex)
                {
                    Log.Warning("[QKey] {ExceptionType} while stopping poll loop: {Message}",
                        ex.GetType().Name, ex.Message);
                }
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _cts.Cancel();
            _cts.Dispose();
        }

        private async Task PollLoop(CancellationToken ct)
        {
            await Task.Delay(StartupDelayMs, ct);

            if (!_ec.Initialize())
            {
                if (!_ecUnavailableLogged)
                {
                    _ecUnavailableLogged = true;
                    Log.Information("[QKey] EC driver unavailable - Performance button intercept disabled on this machine");
                }
                return;
            }

            Log.Information("[QKey] Performance button monitor started (polling 0x786 every {Interval}ms)", PollIntervalMs);

            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(PollIntervalMs));
            long ticks = 0;
            long consecutiveReadFailures = 0;
            try
            {
                while (await timer.WaitForNextTickAsync(ct))
                {
                    ticks++;
                    byte value;
                    try
                    {
                        value = _ec.ReadECRegister(UniwillECService.REG_TCC_OFFSET);
                        if (consecutiveReadFailures > 0)
                            Log.Information("[QKey] EC reads recovered after {Failures} failures", consecutiveReadFailures);
                        consecutiveReadFailures = 0;
                    }
                    catch
                    {
                        // Transient driver/driver-handle hiccup - skip this tick.
                        consecutiveReadFailures++;
                        if (consecutiveReadFailures == 1)
                            Log.Warning("[QKey] EC read of 0x786 failed (driver handle may be dead) - will keep retrying");
                        else if (consecutiveReadFailures % 200 == 0)
                            Log.Warning("[QKey] EC reads still failing ({Count} consecutive)", consecutiveReadFailures);
                        continue;
                    }

                    OnTick(value);

                    if (ticks % 1200 == 0) // ~60s heartbeat so silence is diagnosable
                        Log.Information("[QKey] heartbeat: ticks={Ticks} value=0x{Value:X2} readFailures={Failures}",
                            ticks, value, consecutiveReadFailures);

                    if (_pendingPress)
                    {
                        _pendingPress = false;
                        if (!_handlingPress)
                        {
                            _handlingPress = true;
                            _ = HandlePressAsync();
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // normal shutdown
            }
        }

        /// <summary>
        /// Edge detector: a new value that persists for
        /// <see cref="StableReadsForPress"/> consecutive reads is a button press.
        /// The OEM firmware alternates 0x5F / 0xDF per press, but any
        /// sustained change counts, so firmware updates that change the value
        /// pattern are still detected.
        /// </summary>
        private void OnTick(byte value)
        {
            if (!_hasBaseline)
            {
                _hasBaseline = true;
                _lastValue = value;
                Log.Information("[QKey] baseline 0x786 = 0x{Baseline:X2}", value);
                return;
            }

            if (_ec.SelfModeChangeCount() != _selfModeChanges)
            {
                _selfModeChanges = _ec.SelfModeChangeCount();
                _selfChangeTicks = SelfChangeSuppressTicks;
            }

            if (_selfChangeTicks > 0)
            {
                _selfChangeTicks--;
                if (value != _lastValue)
                    Log.Information("[QKey] 0x786 re-latched by UCU's own mode assert (0x{From:X2} -> 0x{To:X2}) - not a button press",
                        _lastValue, value);
                _lastValue = value;
                _candidateValue = value;
                _candidateReads = 0;
                return;
            }

            if (value == _lastValue)
            {
                _candidateReads = 0;
                return;
            }

            if (value != _candidateValue)
            {
                _candidateValue = value;
                _candidateReads = 1;
                return;
            }

            _candidateReads++;
            if (_candidateReads == 2)
            {
                // Change persisted >=100ms: log it immediately so a register that
                // moves but never hits the press threshold is still visible.
                Log.Information("[QKey] 0x786 changed: 0x{From:X2} -> 0x{To:X2}", _lastValue, value);
            }
            if (_candidateReads >= StableReadsForPress)
            {
                byte from = _lastValue;
                _lastValue = value;
                _candidateReads = 0;
                _pendingPress = true;
                Log.Information("[QKey] Performance button press detected (0x786: 0x{From:X2} -> 0x{To:X2})", from, value);
            }
        }

        private async Task HandlePressAsync()
        {
            try
            {
                if (!Settings.Default.EnableQKeyButtonIntercept)
                    return;

                if (Settings.Default.isAdaptiveModeRunning)
                {
                    Log.Information("[QKey] Ignoring button press - Adaptive Mode is running");
                    return;
                }

                string[] names = PresetProfileApplier.GetProfileNames();
                if (names.Length == 0)
                    return;

                string? current = Settings.Default.presetProfile;
                int idx = -1;
                for (int i = 0; i < names.Length; i++)
                {
                    if (string.Equals(names[i], current, StringComparison.OrdinalIgnoreCase))
                    {
                        idx = i;
                        break;
                    }
                }

                // Unknown or never-set profile (including legacy bare "Custom"):
                // start the cycle from the first profile.
                string next = names[(idx + 1) % names.Length];
                if (names.Length > 1 && string.Equals(next, current, StringComparison.OrdinalIgnoreCase))
                    next = names[(idx + 2) % names.Length];

                string displayName = PresetProfileApplier.DisplayName(next);
                await PresetProfileApplier.ApplyProfileAsync(next, displayName);

                Settings.Default.presetProfile = next;
                Settings.Default.Save();

                if (Settings.Default.EnableQKeyLedSync)
                    SyncQKeyLed(next);

                Log.Information("[QKey] Cycled to {Profile} preset", displayName);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[QKey] Failed to apply preset after button press");
            }
            finally
            {
                _handlingPress = false;
            }
        }

        /// <summary>
        /// Mirrors the applied preset into the EC mode index register and pokes
        /// the trigger register so EC firmware that drives the Q-key LED from
        /// the mode index (OpenRevo's sequence on Mechrevo units) updates the
        /// LED. Read-modify-write on the trigger keeps any RGB effect bits
        /// (logo / rainbow) set by the keyboard apply path intact.
        /// Never uses the _Checked write variants: the EC on this platform
        /// reports error code 1 for writes even on success.
        /// </summary>
        private void SyncQKeyLed(string profileName)
        {
            try
            {
                int modeIndex = PresetProfileApplier.OemModeIndexFor(profileName);

                // Read-modify-write: bit 4 is the mode engine's arm bit, so a bare
                // mode write disarms the OEM button engine.
                byte prevMode = _ec.ReadECRegister(UniwillECService.REG_MODE_INDEX);
                _ec.WriteECRegister(UniwillECService.REG_MODE_INDEX,
                    (byte)((prevMode & 0xE0) | modeIndex | UniwillECService.MODE_INDEX_ARM));
                Thread.Sleep(10);

                byte trigger = _ec.ReadECRegister(UniwillECService.REG_TRIGGER);
                _ec.WriteECRegister(UniwillECService.REG_TRIGGER,
                    (byte)(trigger | UniwillECService.TRIGGER_RGB_APPLY_COLOR));
                _ec.MarkSelfModeChange();

                Log.Information("[QKey] LED sync: mode index {Mode} written to 0x7AB", modeIndex);
            }
            catch (Exception ex)
            {
                Log.Warning("[QKey] LED sync failed: {Message}", ex.Message);
            }
        }
    }
}
