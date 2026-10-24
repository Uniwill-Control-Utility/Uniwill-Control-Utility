using System;

namespace Uniwill_Control_Utility.Services
{
    internal enum GuardianAction
    {
        /// <summary>Relaunch the app immediately (it survived a real run, then died).</summary>
        Relaunch,
        /// <summary>Relaunch after a backoff delay (crashed fast — possible crash loop).</summary>
        RelaunchAfter,
        /// <summary>Stop watching (intentional exit, or crash loop exceeded the budget).</summary>
        Stop,
    }

    internal readonly record struct GuardianDecision(GuardianAction Action, TimeSpan Delay);

    /// <summary>
    /// Pure relaunch policy for the guardian — no process/IPC types, unit-testable.
    ///
    /// The guardian watches the main app's single-instance mutex and reacts to
    /// running→stopped transitions:
    /// - Graceful exit (the app wrote the graceful-exit flag in OnExit) → Stop.
    ///   This is what keeps "Close Application" from fighting the watchdog.
    /// - Crash after a real run (runDuration >= threshold) → relaunch immediately
    ///   and reset the failure budget.
    /// - Crash fast (runDuration < threshold) → exponential backoff; after
    ///   maxFastCrashAttempts consecutive fast crashes, Stop (a startup crash
    ///   loop must not hammer the EC/hardware forever).
    /// </summary>
    internal static class GuardianPolicy
    {
        public static GuardianDecision Evaluate(
            bool gracefulExitSignaled,
            TimeSpan runDuration,
            int consecutiveFailures,
            int maxFastCrashAttempts,
            TimeSpan baseDelay,
            TimeSpan maxDelay,
            TimeSpan fastExitThreshold)
        {
            if (gracefulExitSignaled)
                return new GuardianDecision(GuardianAction.Stop, TimeSpan.Zero);

            if (runDuration < fastExitThreshold)
            {
                if (consecutiveFailures >= maxFastCrashAttempts)
                    return new GuardianDecision(GuardianAction.Stop, TimeSpan.Zero);

                // Exponential backoff: baseDelay * 2^failures, capped at maxDelay.
                long ticks = baseDelay.Ticks << Math.Min(consecutiveFailures, 20);
                var backoff = TimeSpan.FromTicks(Math.Min(ticks, maxDelay.Ticks));
                return new GuardianDecision(GuardianAction.RelaunchAfter, backoff);
            }

            return new GuardianDecision(GuardianAction.Relaunch, TimeSpan.Zero);
        }
    }
}
