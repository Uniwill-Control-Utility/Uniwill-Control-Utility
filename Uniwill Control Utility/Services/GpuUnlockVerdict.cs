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
        /// True when the laptop budget is below the request the unlock made, i.e. the driver is
        /// answering min(request, budget) with the budget. That is the gap the drift watch repairs
        /// and the one a read can catch seconds before the repair lands.
        /// </summary>
        public static bool BudgetShort(int? requestedMilliwatts, int? budgetMilliwatts) =>
            requestedMilliwatts is int asked && budgetMilliwatts is int budget && budget < asked;

        /// <summary>
        /// The state the card's actions follow. A read taken inside a hush window — the
        /// post-apply verification or the repair a short budget asked for — can land on the EC's
        /// republish and answer <c>Uncertain</c>, which counts as unlockable, so the actions would
        /// re-derive from it and flash (field, v0.9.14: Apply 200 W briefly enabled Unlock extra
        /// GPU power). While a window is open the last read taken outside one stands.
        /// </summary>
        public static string? Settled(string? lastSettled, string? read, bool hushing) =>
            hushing ? lastSettled : read;

        /// <summary>
        /// The one line the card owes when the machine enforces less than it was asked for
        /// (the EC refusing the cTGP offset is the measured cause), or null when it delivers.
        /// Nothing while a post-apply verification — or the repair a state read asked for — is
        /// open: the EC republishes its own F7 budget for a few seconds after a write (measured
        /// 2026-11-18: the applied 225 W read back at 150 W), so the gap the card would name
        /// there is the feature recovering, not a failure. The re-read that closes either window
        /// renders with it closed, so a shortfall that survived is still said.
        /// </summary>
        public static string? Shortfall(int? requestedMilliwatts, int? enforcedMilliwatts, bool midRecovery = false)
        {
            if (midRecovery)
                return null;

            return requestedMilliwatts is int asked && enforcedMilliwatts is int got && got < asked
                ? LocalizationService.Format("The card enforces {0}, not the requested {1}.", Watts(got), Watts(asked))
                : null;
        }

        /// <summary>
        /// The envelope line under the status row. The row above carries the two driver-side
        /// figures, named for what they are (the request and the laptop budget); this says the
        /// EC's own ceiling once, with what the machine actually enforces — and, when the EC is
        /// the lower of the two, that the EC is what binds. Null when the driver was not asked
        /// (no invented number).
        /// </summary>
        public static string? Envelope(int? ecCeilingMilliwatts, int? requestedMilliwatts, int? enforcedMilliwatts)
        {
            if (ecCeilingMilliwatts is not int ceiling)
                return null;

            string line = LocalizationService.Format(
                "EC ceiling {0} · enforced {1}",
                Watts(ceiling),
                enforcedMilliwatts is int enforced ? Watts(enforced) : "-- W");

            bool binding = requestedMilliwatts is int asked
                && enforcedMilliwatts is int got
                && got == ceiling
                && ceiling < asked;

            return binding ? line + " · " + LocalizationService.Get("the EC is the limit") : line;
        }

        private static string Watts(int milliwatts) =>
            (milliwatts / 1000.0).ToString("0.##", CultureInfo.InvariantCulture) + " W";
    }
}
