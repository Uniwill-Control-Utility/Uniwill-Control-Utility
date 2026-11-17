using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using UCU.Hardware.Gpu;

namespace UCU.Engine
{
    /// <summary>
    /// Re-applies the live half of a completed GPU unlock at engine start, before the pipe
    /// server answers its first <c>gpu.get_rom_override_state</c>. A reboot keeps the staged
    /// records and the originals file but clears what the EC and the driver hold — the cTGP
    /// offset, the rails, the laptop budget and the driver's own request — so without this
    /// the owner would see "Partly applied" on every boot for a feature they enabled days
    /// ago.
    ///
    /// The service starts before the display driver and NVAPI are necessarily ready, so a
    /// failed attempt is retried on a bounded schedule; the first attempt stays on the
    /// host's start path (one EC + NVAPI pass, and the status card must not race it) while
    /// the retries run in the background so they never hold the pipe server up. The retry
    /// stops at the first success, at the first definitive refusal, and when no unlock is
    /// staged at all — a Restore in the meantime leaves nothing to re-apply, which the store
    /// reports as <see cref="GpuReapplyVerdict.NoIntent"/>.
    /// </summary>
    public sealed class GpuRomOverrideStartup : IHostedService
    {
        /// <summary>
        /// When the attempts happen, seconds after engine start. Bounded: a machine whose
        /// unlock is genuinely broken stops asking, and the state verb reports what failed.
        /// </summary>
        internal static readonly TimeSpan[] Schedule =
        {
            TimeSpan.Zero,
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(60),
            TimeSpan.FromSeconds(120),
        };

        private readonly IGpuRomOverrideService _overrides;
        private readonly ILogger<GpuRomOverrideStartup> _log;
        private readonly Func<TimeSpan, CancellationToken, Task> _delay;
        private readonly CancellationTokenSource _stopping = new();

        public GpuRomOverrideStartup(IGpuRomOverrideService overrides, ILogger<GpuRomOverrideStartup> log)
            : this(overrides, log, (delay, token) => Task.Delay(delay, token))
        {
        }

        internal GpuRomOverrideStartup(
            IGpuRomOverrideService overrides,
            ILogger<GpuRomOverrideStartup> log,
            Func<TimeSpan, CancellationToken, Task> delay)
        {
            _overrides = overrides;
            _log = log;
            _delay = delay;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            if (Attempt() != GpuReapplyVerdict.Transient)
                return Task.CompletedTask;

            _ = Task.Run(() => RetryAsync(_stopping.Token));
            return Task.CompletedTask;
        }

        /// <summary>The retries, in order, from the second scheduled attempt on.</summary>
        internal async Task RetryAsync(CancellationToken cancellationToken)
        {
            var started = Stopwatch.StartNew();

            try
            {
                for (int i = 1; i < Schedule.Length; i++)
                {
                    TimeSpan wait = Schedule[i] - started.Elapsed;
                    if (wait > TimeSpan.Zero)
                        await _delay(wait, cancellationToken).ConfigureAwait(false);

                    GpuReapplyVerdict verdict = Attempt();
                    _log.LogInformation(
                        "GPU ROM override start-up attempt {Attempt}/{Total} at +{Seconds} s: {Verdict}",
                        i + 1, Schedule.Length, (int)Schedule[i].TotalSeconds, verdict);

                    if (verdict != GpuReapplyVerdict.Transient)
                        return;
                }

                _log.LogWarning(
                    "GPU ROM override start-up gave up after {Total} attempts; the state verb reports what did not land",
                    Schedule.Length);
            }
            catch (OperationCanceledException)
            {
                // The engine is stopping: no line, the host is going down.
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "GPU ROM override start-up retry failed");
            }
        }

        /// <summary>One attempt. An exception is the transient verdict: the engine must still come up.</summary>
        private GpuReapplyVerdict Attempt()
        {
            try
            {
                return _overrides.ReapplyAtStartup().Verdict;
            }
            catch (Exception ex)
            {
                // A host that cannot start takes the EC, the fans and every preset down with
                // it, so the GPU side is never allowed to do that.
                _log.LogWarning(ex, "GPU ROM override start-up re-apply failed");
                return GpuReapplyVerdict.Transient;
            }
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            _stopping.Cancel();
            return Task.CompletedTask;
        }
    }
}
