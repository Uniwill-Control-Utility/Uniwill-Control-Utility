using System;
using UCU.Core.Ec.Generated;

namespace UCU.Core.Ec
{
    /// <summary>
    /// Rules for supervising the EC from the service: when a gap in the tick means the
    /// machine suspended, and when the EC has dropped the activation bit that makes its
    /// power and fan writes take effect.
    ///
    /// Pure so both are pinned by tests: the cost of a wrong answer is either a service
    /// that re-initialises the EC on every tick (a write storm) or one that never
    /// notices the EC reset (silent no-ops until the next preset).
    /// </summary>
    public static class EcSupervisionPolicy
    {
        /// <summary>
        /// A tick gap this many times the interval means the clock stopped — the machine
        /// suspended. Measured 2026-11-11: three intervals (15 s) is too small, because a
        /// loaded host can delay a tick that long and a false resume re-runs the handshake;
        /// six (30 s) still catches every real suspend, which are minutes.
        /// </summary>
        public const int ResumeGapMultiplier = 6;

        /// <summary>0x0741 bit 0: the EC's activation bit (APPE), set by the handshake.</summary>
        public const byte ActivationBit = Generated.EcVault.AP_OEM_ACTIVATION_BIT;

        /// <summary>True when a tick arrived far later than expected, i.e. we slept.</summary>
        public static bool LooksLikeResume(TimeSpan elapsed, TimeSpan interval)
        {
            if (interval <= TimeSpan.Zero)
                return false;

            return elapsed > interval * ResumeGapMultiplier;
        }

        /// <summary>
        /// True when 0x0741 shows the EC is no longer activated, so the handshake must be
        /// re-run before any power or fan write can take effect. Measured behaviour: the
        /// EC clears this bit on its own reset paths, which is why the reference
        /// implementation re-initialises from a 30 s tick.
        /// </summary>
        public static bool NeedsReinitialization(int register741) =>
            (register741 & ActivationBit) == 0;

        /// <summary>
        /// After this many consecutive failed supervisions, back off: a machine without an
        /// EC (or with a driver that never answers) must not retry forever at the fast
        /// cadence.
        /// </summary>
        public const int FailuresBeforeBackoff = 3;
    }
}
