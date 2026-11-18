using System;
using System.Globalization;

namespace Uniwill_Control_Utility.Services
{
    /// <summary>
    /// The Extra GPU power card's judgement, kept out of the WPF page so a test can drive it: the
    /// range a target may occupy, what the machine actually enforces, and the one sentence a
    /// shortfall owes. The status row shows numbers without a verdict colour, so the sentence is
    /// where a failure surfaces.
    /// </summary>
    public static class GpuUnlockVerdict
    {
        /// <summary>
        /// The range the unlock accepts, mirrored from UCU.Hardware (which this project does not
        /// reference): <c>RomOverrideLayout.StockPowerMilliwatts</c> up to
        /// <c>GpuPowerTiers.ExtraGpuPower.PowerMilliwatts</c>. Below the stock ceiling the engine
        /// refuses the target outright, above the tier's ceiling it clamps — so a preset there
        /// carries no target at all.
        /// </summary>
        public const int MinTargetMilliwatts = 175_000;
        public const int MaxTargetMilliwatts = 250_000;

        /// <summary>
        /// The target the unlock takes, or null when the number cannot be one (outside the range
        /// the engine accepts). 200 W is a supported configuration, so the measured 225 W is not
        /// the floor.
        /// </summary>
        public static int? UnlockTarget(int? milliwatts) =>
            milliwatts is int value && value >= MinTargetMilliwatts && value <= MaxTargetMilliwatts
                ? value
                : null;

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
