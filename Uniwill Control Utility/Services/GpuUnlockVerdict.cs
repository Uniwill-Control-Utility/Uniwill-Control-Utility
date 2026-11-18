using System;
using System.Globalization;

namespace Uniwill_Control_Utility.Services
{
    /// <summary>
    /// The Extra GPU power status row's judgement, kept out of the WPF page so a test can
    /// drive it: the badge is a comparison with the one target the unlock was measured at,
    /// and a request the machine does not deliver is a sentence, never a success.
    /// </summary>
    public static class GpuUnlockVerdict
    {
        /// <summary>The target the unlock was measured at (225 W), the only "Tested" one.</summary>
        public const int TestedTargetMilliwatts = 225_000;

        /// <summary>
        /// Tested only at the measured target: a higher request (250 W, the ROM ceiling) is
        /// legal to make but outside the measured configuration, and a shortfall is its own
        /// state, so neither may carry the badge.
        /// </summary>
        public static bool IsTested(int? requestedMilliwatts) =>
            requestedMilliwatts == TestedTargetMilliwatts;

        /// <summary>
        /// What the card can enforce: the smallest of the request, the laptop budget and the
        /// driver's current limit — the driver answers min(request, budget), and the EC's
        /// cTGP caps that again. Null when none of the three was read.
        /// </summary>
        public static int? Enforced(int? requestedMilliwatts, int? budgetMilliwatts, int? currentLimitMilliwatts)
        {
            int? lowest = null;

            foreach (int? value in new[] { requestedMilliwatts, budgetMilliwatts, currentLimitMilliwatts })
            {
                if (value is int known && (lowest is not int low || known < low))
                    lowest = known;
            }

            return lowest;
        }

        /// <summary>
        /// The one line the card owes when the machine enforces less than it was asked for
        /// (the EC refusing the cTGP offset is the measured cause), or null when it delivers.
        /// </summary>
        public static string? Shortfall(int? requestedMilliwatts, int? enforcedMilliwatts) =>
            requestedMilliwatts is int asked && enforcedMilliwatts is int got && got < asked
                ? LocalizationService.Format("The card enforces {0}, not the requested {1}.", Watts(got), Watts(asked))
                : null;

        private static string Watts(int milliwatts) =>
            (milliwatts / 1000.0).ToString("0.##", CultureInfo.InvariantCulture) + " W";
    }
}
