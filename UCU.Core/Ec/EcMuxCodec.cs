using UCU.Core.Ec.Generated;

namespace UCU.Core.Ec
{
    /// <summary>
    /// Translates the semantic <see cref="GpuMuxMode"/> used by the UI and the RPC
    /// contract to the raw EC/NVRAM byte the firmware expects.
    ///
    /// The encodings are not portable across platforms (observed on Intel NB02 vs
    /// AMD NB05): AMD encodes Hybrid/dGPU-only/iGPU-only as 0/1/2, while the Intel
    /// BIOS declares MS Hybrid as 4 (Dynamic) and swaps the two direct-connect
    /// modes, so raw 1 is iGPU-only and raw 2 is dGPU-only there. Because a MUX
    /// write persists in the EC and only takes effect after a restart, a wrong
    /// translation is a user-visible fault that survives the session — so the
    /// mapping lives here, takes the platform as an explicit argument, and is
    /// covered by tables in the test suite.
    /// </summary>
    public static class EcMuxCodec
    {
        /// <summary>AMD/Intel raw byte for MS Hybrid on AMD.</summary>
        public const byte RawHybridAmd = EcVault.MUX_MODE_HYBRID;

        /// <summary>AMD raw byte for dGPU-only.</summary>
        public const byte RawDgpuOnlyAmd = EcVault.MUX_MODE_DGPU_ONLY;

        /// <summary>AMD raw byte for iGPU-only.</summary>
        public const byte RawIgpuOnlyAmd = EcVault.MUX_MODE_IGPU_ONLY;

        /// <summary>Intel raw byte for MS Hybrid ("Dynamic"); raw 0 never appears on Intel.</summary>
        public const byte RawHybridIntel = EcVault.MUX_MODE_HYBRID_INTEL;

        /// <summary>True when a raw byte is one the firmware on this platform can report.</summary>
        public static bool IsKnownRaw(byte raw, EcPlatform platform)
        {
            if (platform == EcPlatform.Intel)
                return raw == RawHybridIntel || raw == RawDgpuOnlyAmd || raw == RawIgpuOnlyAmd;

            return raw == RawHybridAmd || raw == RawDgpuOnlyAmd || raw == RawIgpuOnlyAmd;
        }

        /// <summary>Semantic mode to the raw EC/NVRAM byte for <paramref name="platform"/>.</summary>
        public static byte ModeToRaw(GpuMuxMode mode, EcPlatform platform)
        {
            byte raw = (byte)mode;

            if (platform == EcPlatform.Intel)
            {
                // The Intel BIOS encodes MS Hybrid as 4 (Dynamic), not 0.
                if (raw == RawHybridAmd)
                    return RawHybridIntel;

                // And swaps the direct-connect pair: 1 <-> 2.
                if (raw == RawDgpuOnlyAmd || raw == RawIgpuOnlyAmd)
                    return (byte)(RawDgpuOnlyAmd + RawIgpuOnlyAmd - raw);
            }

            return raw;
        }

        /// <summary>
        /// Raw EC/NVRAM byte to the semantic mode for <paramref name="platform"/>.
        /// Unknown bytes fall through to their numeric value, matching how the EC
        /// has always been read; use <see cref="IsKnownRaw"/> to report a firmware
        /// value this build does not model.
        /// </summary>
        public static GpuMuxMode RawToMode(byte raw, EcPlatform platform)
        {
            if (platform == EcPlatform.Intel)
            {
                if (raw == RawHybridIntel)
                    return GpuMuxMode.Hybrid;

                if (raw == RawDgpuOnlyAmd || raw == RawIgpuOnlyAmd)
                    return (GpuMuxMode)(RawDgpuOnlyAmd + RawIgpuOnlyAmd - raw);
            }

            return (GpuMuxMode)raw;
        }
    }
}
