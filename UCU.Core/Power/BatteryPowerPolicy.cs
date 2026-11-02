using System;

namespace UCU.Core.Power
{
    /// <summary>
    /// The battery power envelope (field-measured 2026-11-02 on the tester
    /// laptop): on DC the firmware caps CPU package power at the Office level
    /// (~40 W) and, worse, a higher request makes it *drop* below that
    /// (160 W requested -> 30 W actual). open-revo documents the same trap
    /// ("High-performance Custom Mode may trigger instant throttling").
    ///
    /// So the app clamps its own CPU limit request to the Office envelope while
    /// on battery: asking for less than the firmware allows is honest, asking
    /// for more actively hurts. GPU/DB/fan settings are untouched - only the
    /// CPU PL pair is firmware-bound.
    /// </summary>
    public static class BatteryPowerPolicy
    {
        public const int DefaultOfficePl1 = 45;

        public const int DefaultOfficePl2 = 45;

        public sealed record Verdict(int Pl1, int Pl2, bool Clamped, int RequestedPl1, int RequestedPl2);

        /// <summary>Clamps the CPU PL pair to the Office envelope when running
        /// on battery. <paramref name="officePl1"/> / <paramref name="officePl2"/>
        /// come from the resolved SKU profile's Office preset (the platform's
        /// own DC envelope); the defaults apply when the SKU has none.</summary>
        public static Verdict ClampForBattery(
            int requestedPl1,
            int requestedPl2,
            bool onBattery,
            int officePl1 = DefaultOfficePl1,
            int officePl2 = DefaultOfficePl2)
        {
            if (!onBattery)
                return new Verdict(requestedPl1, requestedPl2, false, requestedPl1, requestedPl2);

            int ceilingPl1 = officePl1 > 0 ? officePl1 : DefaultOfficePl1;
            int ceilingPl2 = officePl2 > 0 ? officePl2 : DefaultOfficePl2;

            int pl1 = Math.Min(requestedPl1, ceilingPl1);
            int pl2 = Math.Min(requestedPl2, ceilingPl2);

            return new Verdict(pl1, pl2, pl1 != requestedPl1 || pl2 != requestedPl2, requestedPl1, requestedPl2);
        }
    }
}
