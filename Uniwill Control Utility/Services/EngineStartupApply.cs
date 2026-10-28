using System.Threading;

namespace Uniwill_Control_Utility.Services
{
    /// <summary>
    /// Records that EC settings were skipped because the engine service was not
    /// running, so they can be applied once when it comes up.
    ///
    /// The skip is a deliberate trade: writing to the embedded controller while the
    /// engine is down costs a 5 s pipe connect per verb on the startup path and
    /// cannot succeed, while the writes themselves are cheap and idempotent. Marking
    /// them pending keeps the user-visible effect (the saved fan curve, the battery
    /// charge limit) instead of dropping it: the EC resets the charge limit to the
    /// BIOS default on every cold boot, so "skipped" must not become "lost".
    /// </summary>
    public sealed class EngineStartupApply
    {
        private int _pending;

        public void MarkPending() => Interlocked.Exchange(ref _pending, 1);

        /// <summary>Takes the pending mark: true when the caller must apply the settings.</summary>
        public bool ConsumePending() => Interlocked.Exchange(ref _pending, 0) == 1;

        public bool IsPending => Volatile.Read(ref _pending) == 1;
    }
}
