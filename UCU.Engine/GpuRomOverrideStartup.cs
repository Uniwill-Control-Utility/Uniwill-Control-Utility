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
    ///
    /// An Applied attempt is followed by a bounded settle phase (see <see cref="SettleSchedule"/>):
    /// the EC publishes its own F7 budget asynchronously after a boot, so the raise can be
    /// rewritten seconds after it read back exactly.
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

        /// <summary>
        /// The settle passes, seconds after the re-apply settled as Applied. The EC publishes
        /// its own F7 budget asynchronously after a boot, so the raise can read back exactly
        /// and be rewritten seconds later (measured 2026-11-17); the passes re-check and
        /// re-apply only what drifted, and two consecutive passes that end holding the tier
        /// stop them early.
        /// </summary>
        internal static readonly TimeSpan[] SettleSchedule =
        {
            TimeSpan.FromSeconds(20),
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
            GpuReapplyVerdict verdict = Attempt();

            if (verdict == GpuReapplyVerdict.Applied)
                Background = Task.Run(() => SettleAsync(_stopping.Token));
            else if (verdict == GpuReapplyVerdict.Transient)
                Background = Task.Run(() => RetryAsync(_stopping.Token));

            return Task.CompletedTask;
        }

        /// <summary>The background phase (retry, then settle): what <see cref="StopAsync"/> stops.</summary>
        internal Task? Background { get; private set; }

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

                    if (verdict == GpuReapplyVerdict.Applied)
                    {
                        await SettleAsync(cancellationToken).ConfigureAwait(false);
                        return;
                    }

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

        /// <summary>
        /// The settle passes after an Applied re-apply: each one reads the live machine and
        /// re-applies only the parts that drifted. Bounded twice over — the schedule is three
        /// passes, and two consecutive passes that end holding the tier stop it early. A
        /// restore (the store answers <see cref="GpuSettleVerdict.Stopped"/>) ends it at once:
        /// the settle must never fight the owner's own Unlock/Restore.
        /// </summary>
        internal async Task SettleAsync(CancellationToken cancellationToken)
        {
            var started = Stopwatch.StartNew();
            GpuSettleResult? last = null;
            int clean = 0;

            try
            {
                for (int i = 0; i < SettleSchedule.Length; i++)
                {
                    TimeSpan wait = SettleSchedule[i] - started.Elapsed;
                    if (wait > TimeSpan.Zero)
                        await _delay(wait, cancellationToken).ConfigureAwait(false);

                    GpuSettleResult result = Settle();
                    if (result.Verdict == GpuSettleVerdict.Stopped)
                    {
                        _log.LogInformation(
                            "GPU unlock settle: the unlock is no longer staged (a restore ran); the settle phase ends");
                        return;
                    }

                    _log.LogInformation(
                        "gpu.settle: attempt {Attempt} at +{Seconds} s: {What}",
                        i + 1, (int)SettleSchedule[i].TotalSeconds,
                        result.Verdict switch
                        {
                            GpuSettleVerdict.Settled => "clean",
                            GpuSettleVerdict.Reapplied => $"{result.Drift} - re-applied",
                            _ => $"{result.Drift ?? "the pass failed"} - {result.Reason}",
                        });

                    // A pass that ends with the tier holding is clean, whether it re-applied or not.
                    clean = result.Verdict == GpuSettleVerdict.Drifted ? 0 : clean + 1;
                    if (clean == 2)
                        return;

                    last = result;
                }

                if (last?.Verdict == GpuSettleVerdict.Drifted)
                {
                    _log.LogWarning(
                        "GPU unlock settle: still short after {Passes} passes (+{Seconds} s): {Drift} - {Reason}; the state verb reports it",
                        SettleSchedule.Length, (int)SettleSchedule[^1].TotalSeconds, last.Drift, last.Reason);
                }
            }
            catch (OperationCanceledException)
            {
                // The engine is stopping: no line, the host is going down.
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "GPU unlock settle failed");
            }
        }

        /// <summary>One settle pass. An exception is a short pass: never Unlocked, never a re-apply loop.</summary>
        private GpuSettleResult Settle()
        {
            try
            {
                return _overrides.SettleAtStartup();
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "GPU ROM override settle pass failed");
                return new(GpuSettleVerdict.Drifted, null, ex.Message);
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
