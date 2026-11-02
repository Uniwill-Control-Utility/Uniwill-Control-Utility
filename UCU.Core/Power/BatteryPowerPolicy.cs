using System;

namespace UCU.Core.Power
{
    /// <summary>
    /// The battery power envelope. On DC the platform firmware owns the power
    /// limits and ignores - or penalises - anything above its own ceiling, so
    /// the app clamps its *requests* to the measured ceiling instead of sending
    /// numbers the hardware will silently refuse.
    ///
    /// Field-measured 2026-11-02 with a CPU stress test and a GPU benchmark
    /// running together, both laptops on battery:
    ///
    ///   Intel Core Ultra 9 275HX laptop : CPU 30-40 W, GPU 35 W
    ///   AMD (XMG Neo 16, Ryzen + RTX)    : CPU 30-40 W, GPU 40 W
    ///
    /// Earlier single-subsystem measurement on the same Intel laptop: a 160 W
    /// CPU request made package power *drop* to 30 W, i.e. asking for more than
    /// the envelope actively hurts (open-revo documents the same trap for its
    /// own chassis). Undervolt and fan curves are not firmware-clamped, so this
    /// policy only ever touches the power numbers.
    ///
    /// The ceiling is the upper end of the measured range: low enough to be
    /// honest about what the firmware will grant, high enough not to give away
    /// headroom the platform does allow on a lighter load.
    /// </summary>
    public static class BatteryPowerPolicy
    {
        /// <summary>The DC ceilings the firmware actually enforces.</summary>
        public sealed record Envelope(int CpuWatts, int GpuWatts);

        public static readonly Envelope IntelEnvelope = new(CpuWatts: 40, GpuWatts: 35);

        public static readonly Envelope AmdEnvelope = new(CpuWatts: 40, GpuWatts: 40);

        /// <summary>Fallback when the platform is not one of the measured ones:
        /// the tighter of the two, so an unknown chassis is never over-requested.</summary>
        public static readonly Envelope ConservativeEnvelope = new(CpuWatts: 40, GpuWatts: 35);

        /// <summary>Picks the measured envelope for the CPU vendor.</summary>
        public static Envelope EnvelopeFor(bool? intelCpu) => intelCpu switch
        {
            true => IntelEnvelope,
            false => AmdEnvelope,
            null => ConservativeEnvelope,
        };

        public sealed record Verdict(int Pl1, int Pl2, bool Clamped, int RequestedPl1, int RequestedPl2);

        public sealed record GpuVerdict(int TgpWatts, int DbWatts, bool Clamped, int RequestedTgp, int RequestedDb);

        /// <summary>Clamps the CPU PL pair to the envelope's CPU ceiling on
        /// battery. <paramref name="cpuCeiling"/> comes from the resolved
        /// envelope; a non-positive value falls back to the conservative one.</summary>
        public static Verdict ClampCpu(
            int requestedPl1,
            int requestedPl2,
            bool onBattery,
            int cpuCeiling)
        {
            if (!onBattery)
                return new Verdict(requestedPl1, requestedPl2, false, requestedPl1, requestedPl2);

            int ceiling = cpuCeiling > 0 ? cpuCeiling : ConservativeEnvelope.CpuWatts;
            int pl1 = Math.Min(requestedPl1, ceiling);
            int pl2 = Math.Min(requestedPl2, ceiling);

            return new Verdict(pl1, pl2, pl1 != requestedPl1 || pl2 != requestedPl2, requestedPl1, requestedPl2);
        }

        /// <summary>Clamps the GPU TGP to the envelope's GPU ceiling on battery
        /// and drops Dynamic Boost: the firmware refuses both, and a TGP request
        /// above the ceiling is what made the GPU sit at ~30 W while the UI
        /// claimed 115 W.</summary>
        public static GpuVerdict ClampGpu(
            int requestedTgpWatts,
            int requestedDbWatts,
            bool onBattery,
            int gpuCeiling)
        {
            if (!onBattery)
                return new GpuVerdict(requestedTgpWatts, requestedDbWatts, false, requestedTgpWatts, requestedDbWatts);

            int ceiling = gpuCeiling > 0 ? gpuCeiling : ConservativeEnvelope.GpuWatts;
            int tgp = Math.Min(requestedTgpWatts, ceiling);
            int db = 0;

            return new GpuVerdict(
                tgp, db,
                tgp != requestedTgpWatts || db != requestedDbWatts,
                requestedTgpWatts,
                requestedDbWatts);
        }
    }
}
