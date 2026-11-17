using System;
using System.Threading.Tasks;
using UCU.Core.Gpu;
using Uniwill_Control_Utility.Scripts.GPUs.NVIDIA;
using Uniwill_Control_Utility.Scripts.Misc;

namespace Uniwill_Control_Utility.Services
{
    /// <summary>
    /// Bounded clock settle after the startup apply. A clock write issued seconds after boot
    /// is accepted by the driver and does not take, while the identical write on a settled
    /// GPU lands (field, 2026-11-17: memory +1000 accepted 5 s after start, still not in
    /// force twelve minutes later; the same call landed 22 minutes into the session). The
    /// startup apply already reads the offset back, so this acts only on that verdict: a
    /// NOT-in-force answer re-issues the same write on a short, fixed schedule.
    ///
    /// "unverified" never retries - a pstate move or an NVML refusal is not evidence the
    /// write failed, and retrying it would hammer the driver for nothing. Startup only: a
    /// manual apply runs on a settled machine and schedules nothing.
    /// </summary>
    internal sealed class GpuClockSettle
    {
        /// <summary>Elapsed time since the startup apply for each attempt. Bounded on purpose.</summary>
        internal static readonly TimeSpan[] Attempts =
        {
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(15),
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(60),
        };

        private readonly Func<MemoryOffsetVerdict?> _verdict;
        private readonly Func<int> _reapply;
        private readonly Func<TimeSpan, Task> _delay;

        /// <summary>Test seam: scripted verdicts, a counting re-apply, an injectable delay.</summary>
        internal GpuClockSettle(Func<MemoryOffsetVerdict?> verdict, Func<int> reapply, Func<TimeSpan, Task> delay)
        {
            _verdict = verdict;
            _reapply = reapply;
            _delay = delay;
        }

        /// <summary>
        /// The startup hook. Fire-and-forget, and off the dispatcher: boot must not wait up
        /// to 60 s for a retry schedule, and each attempt is a blocking NVAPI write.
        /// </summary>
        internal static void ScheduleAfterStartup() =>
            _ = Task.Run(() => new GpuClockSettle(
                () => NvTuning.LastMemoryOffset,
                NvTuning.ReapplyLastClockWrite,
                delay => Task.Delay(delay)).RunAsync());

        /// <summary>Runs the schedule; returns the number of attempts made (0 = nothing to do).</summary>
        internal async Task<int> RunAsync()
        {
            int attempts = 0;
            try
            {
                MemoryOffsetVerdict startup = _verdict() ?? default;

                // No write ran, the offset is live, or the readback could not decide: none
                // of those is a failed write.
                if (!startup.Verified || startup.Live) return 0;

                TimeSpan elapsed = TimeSpan.Zero;
                MemoryOffsetVerdict verdict = startup;
                foreach (TimeSpan at in Attempts)
                {
                    await _delay(at - elapsed);
                    elapsed = at;

                    _reapply();
                    attempts++;
                    verdict = _verdict() ?? default;

                    // A later apply (manual, automation) owns the clocks now: never write
                    // the startup values over it.
                    if (verdict.RequestedMhz != startup.RequestedMhz)
                    {
                        DiagnosticLogger.LogInfo(
                            $"clock settle: attempt {attempts} at +{at.TotalSeconds:0}s: a newer clock apply ({Mhz(verdict.RequestedMhz)}) owns the GPU - stopping");
                        return attempts;
                    }

                    DiagnosticLogger.LogInfo(
                        $"clock settle: attempt {attempts} at +{at.TotalSeconds:0}s: {State(verdict)} ({verdict.Detail})");

                    if (verdict.Verified && verdict.Live)
                    {
                        DiagnosticLogger.LogInfo(
                            $"clock settle: memory offset {Mhz(startup.RequestedMhz)} landed on attempt {attempts} at +{at.TotalSeconds:0}s - the startup write was accepted but only took effect late");
                        return attempts;
                    }

                    // Unknown, not a failure: stop instead of hammering the driver.
                    if (!verdict.Verified) return attempts;
                }

                DiagnosticLogger.LogWarning(
                    $"clock settle: memory offset {Mhz(startup.RequestedMhz)} still NOT in force after {attempts} attempts (+5/+15/+30/+60 s): {verdict.Detail}");
                return attempts;
            }
            catch (Exception ex)
            {
                DiagnosticLogger.LogWarning($"clock settle: aborted after {attempts} attempts: {ex.Message}");
                return attempts;
            }
        }

        private static string State(MemoryOffsetVerdict verdict) =>
            verdict.Live ? "live" : verdict.Verified ? "NOT in force" : "unverified";

        private static string Mhz(int mhz) => (mhz > 0 ? "+" : string.Empty) + mhz + " MHz";
    }
}
