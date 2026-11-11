using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using UCU.Contracts;
using UCU.Core.Ec;
using UCU.Hardware.Ec;

namespace UCU.Engine
{
    /// <summary>
    /// Keeps the EC activated and notices suspends, from inside the service.
    ///
    /// Two failures this exists for, both silent:
    ///
    /// 1. The EC clears its activation bit (0x0741 bit 0) on its own reset paths. Every
    ///    power and fan write is then accepted and ignored — "the preset applied but
    ///    nothing happened" — until something happens to re-run the handshake. The
    ///    reference implementation watches this bit on a 30 s tick and re-initialises.
    ///
    /// 2. A resume leaves the driver handle's device state stale. The app re-applies
    ///    presets 1.5 s after waking, but until now the engine's EC state was never
    ///    invalidated: recovery relied on one blind reconnect retry inside a failed
    ///    IOCTL. Resume is detected here without a ServiceBase power hook — the host
    ///    uses UseWindowsService, which does not surface POWER_EVENT_RESUME — by noticing
    ///    that the tick clock jumped by more than three intervals.
    ///
    /// It never calls FailFast: a wedged *call* is the watchdog's business, and treating a
    /// transient read failure as fatal would restart the service on a harmless hiccup.
    /// After three consecutive failed reads it backs off for a minute.
    /// </summary>
    public sealed class EcSupervisionService : BackgroundService
    {
        /// <summary>Tick cadence. Only a stopwatch compare on most ticks.</summary>
        internal static readonly TimeSpan Tick = TimeSpan.FromSeconds(5);

        /// <summary>How often 0x0741 is read to prove the EC is still activated.</summary>
        internal static readonly TimeSpan LivenessInterval = TimeSpan.FromSeconds(30);

        /// <summary>Quiet period after repeated failures (no EC / dead driver).</summary>
        internal static readonly TimeSpan FailureBackoff = TimeSpan.FromSeconds(60);

        private readonly EcEngineGate _gate;
        private readonly ILogger<EcSupervisionService> _log;

        public EcSupervisionService(EcEngineGate gate, ILogger<EcSupervisionService> log)
        {
            _gate = gate;
            _log = log;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var clock = Stopwatch.StartNew();
            TimeSpan lastTick = clock.Elapsed;
            TimeSpan lastLiveness = clock.Elapsed;
            TimeSpan backoffUntil = TimeSpan.Zero;
            int consecutiveFailures = 0;
            bool sawActivation = false;

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(Tick, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                TimeSpan now = clock.Elapsed;

                if (EcSupervisionPolicy.LooksLikeResume(now - lastTick, Tick))
                {
                    _log.LogInformation(
                        "[Supervision] resume detected (tick gap {Gap:F0}s): re-initialising the EC",
                        (now - lastTick).TotalSeconds);
                    Reinitialize("resume");
                    lastLiveness = now; // the resume path already proved the EC answers
                }
                lastTick = now;

                if (now < backoffUntil || now - lastLiveness < LivenessInterval)
                    continue;

                lastLiveness = now;

                try
                {
                    int register = _gate.Acquire().ReadECRegister(EcEngine.REG_AP_OEM);
                    consecutiveFailures = 0;

                    if (!EcSupervisionPolicy.NeedsReinitialization(register))
                    {
                        // Bit 0 is set only once manual control is in use (UCU's handshake
                        // deliberately leaves 0x0741 alone). Seeing it set is what makes a
                        // later clear meaningful.
                        sawActivation = true;
                    }
                    else if (sawActivation)
                    {
                        _log.LogWarning(
                            "[Supervision] 0x0741 = 0x{Value:X2} lost the activation bit it had: re-initialising",
                            register);
                        sawActivation = false;
                        Reinitialize("activation lost");
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    consecutiveFailures++;
                    _log.LogWarning(ex,
                        "[Supervision] EC liveness read failed ({Count}/{Limit})",
                        consecutiveFailures, EcSupervisionPolicy.FailuresBeforeBackoff);

                    if (consecutiveFailures >= EcSupervisionPolicy.FailuresBeforeBackoff)
                    {
                        consecutiveFailures = 0;
                        backoffUntil = now + FailureBackoff;
                        _log.LogWarning("[Supervision] backing off for {Seconds:F0}s", FailureBackoff.TotalSeconds);
                    }
                }
            }
        }

        private void Reinitialize(string reason)
        {
            try
            {
                _gate.Invalidate();
                _gate.Acquire();
                _log.LogInformation("[Supervision] EC re-initialised ({Reason})", reason);
            }
            catch (Exception ex)
            {
                // The next tick retries; a failed re-init is never fatal.
                _log.LogWarning(ex, "[Supervision] re-initialisation after {Reason} failed", reason);
            }
        }
    }
}
