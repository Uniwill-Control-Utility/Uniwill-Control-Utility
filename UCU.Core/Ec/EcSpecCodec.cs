using UCU.Core.Ec.Generated;

namespace UCU.Core.Ec
{
    /// <summary>
    /// The EC's per-model power table (the driver's SMAPCTABLE buffer), as the fields this
    /// product needs. Reference: MechControl <c>src/ec.cpp EcTransport::spec</c> (the
    /// <see cref="EcVault.IOCTL_ECSPEC"/> request <c>0xBB | index &lt;&lt; 8</c>) and
    /// <c>re/notes/ECSpec.md</c> for the field meanings.
    ///
    /// Each IOCTL call answers a 32-bit window at byte offset <paramref name="index"/>; only
    /// the low byte is the table entry (the upper three are the following entries leaking
    /// through), so every field below is a byte of the table:
    ///
    /// <code>
    /// index  field                        encoding
    /// 0      PL1 max                      watts, direct
    /// 1      PL2 max                      watts, direct
    /// 2      PL4 max                      watts = raw x 2 (the documented doubling)
    /// 3      TCC offset max               raw; max CPU temperature = 105 C - raw
    /// 4      configurable-TGP offset max  watts; TGP max = raw + DefaultTGP
    /// 5      Dynamic Boost max            watts, direct
    /// 6      GPU target temperature       degrees C, direct
    /// 7      DefaultTGP                   watts, direct; the base the cTGP offset
    ///                                     register (0x0744) is measured from
    /// 8      Dynamic Boost CPU TDP        watts; 0xFF = not applicable
    /// </code>
    ///
    /// The mapping was confirmed on one machine (IDY_7Y, 6/6 known parameters matching), so
    /// this record carries no field the reference merely guesses at: the CPU PL minimums,
    /// the 80 C temperature floor and the implicit TGP/DB minimums are left out on purpose.
    /// </summary>
    public sealed record EcSpec
    {
        /// <summary>Index 0: PL1 (sustained CPU power) ceiling, watts.</summary>
        public int Pl1MaxWatts { get; init; }

        /// <summary>Index 1: PL2 (boost CPU power) ceiling, watts.</summary>
        public int Pl2MaxWatts { get; init; }

        /// <summary>Index 2: PL4 (peak CPU power) ceiling in watts, already doubled.</summary>
        public int Pl4MaxWatts { get; init; }

        /// <summary>
        /// Index 3: the EC's TCC offset ceiling. The temperature it stands for is
        /// <see cref="EcSpecCodec.CpuTemperatureLimitC"/> = TjMax (105 C) minus this offset.
        /// </summary>
        public int TccOffsetMax { get; init; }

        /// <summary>
        /// Index 4: the configurable-TGP offset ceiling in watts. This is not a TGP: the
        /// highest TGP the EC accepts is this plus <see cref="DefaultTgpWatts"/>.
        /// </summary>
        public int TgpOffsetMaxWatts { get; init; }

        /// <summary>Index 5: Dynamic Boost ceiling, watts the GPU may borrow from the CPU.</summary>
        public int DynamicBoostMaxWatts { get; init; }

        /// <summary>Index 6: the GPU thermal target (throttle) temperature, degrees C.</summary>
        public int GpuTargetTemperatureC { get; init; }

        /// <summary>
        /// Index 7: DefaultTGP in watts - the board's base GPU power. The cTGP offset the
        /// engine writes to 0x0744 is (requested TGP - this), which is why the offset cannot
        /// be computed from a constant: the base differs per model (95 W on IDY_7Y).
        /// </summary>
        public int DefaultTgpWatts { get; init; }

        /// <summary>Index 8: the CPU TDP Dynamic Boost may draw against, watts; null when the EC writes 0xFF (not applicable).</summary>
        public int? DynamicBoostCpuTdpWatts { get; init; }
    }

    /// <summary>
    /// Decodes the ECSpec table and answers whether its DefaultTGP can be trusted. Pure:
    /// the transport hands over the dwords, everything the engine decides is here so it is
    /// testable without an EC.
    /// </summary>
    public static class EcSpecCodec
    {
        /// <summary>Table entries the reference documents (indices 0-8).</summary>
        public const int ByteCount = 9;

        /// <summary>
        /// Dwords to read: the nine table entries plus the three the EC echoes back, which
        /// is what proves the table exists (see <see cref="EchoIndex"/>).
        /// </summary>
        public const int DwordCount = 12;

        /// <summary>
        /// The first index past the table. Asking for it makes the driver echo the request
        /// word, which is the only way to tell a real table from a refusal.
        /// </summary>
        public const int EchoIndex = 9;

        /// <summary>The byte the EC echoes at <see cref="EchoIndex"/> and beyond (the request's command byte).</summary>
        public const int OutOfRangeEcho = (int)EcVault.ECSPEC_READ_COMMAND;

        /// <summary>TjMax the TCC offset is measured against, degrees C (the reference's constant).</summary>
        public const int TjMaxC = 105;

        /// <summary>Lowest DefaultTGP this product treats as a real board base, watts (ECSpec.md sanity range).</summary>
        public const int MinDefaultTgpWatts = 50;

        /// <summary>Highest DefaultTGP this product treats as a real board base, watts (ECSpec.md sanity range).</summary>
        public const int MaxDefaultTgpWatts = 200;

        /// <summary>
        /// The base TGP the cTGP offset is computed against when the EC publishes no usable
        /// DefaultTGP. It is a guess (55 W, the legacy assumption) - callers must log that
        /// they used it instead of the machine's real base.
        /// </summary>
        public const int FallbackDefaultTgpWatts = 55;

        private const int NotApplicable = 0xFF;

        /// <summary>The table byte an answering index carries (the window's upper bytes are not data).</summary>
        public static byte ByteAt(IReadOnlyList<uint> dwords, int index) => (byte)(dwords[index] & 0xFF);

        /// <summary>
        /// True when the EC answered a table: index 9 echoed the request byte. Asking past the
        /// nine entries is the discriminator - the echo means "the table ends here", so an EC
        /// that publishes no table never produces it (it refuses or answers something else).
        /// </summary>
        public static bool IsValid(IReadOnlyList<uint> dwords) =>
            dwords is not null &&
            dwords.Count >= DwordCount &&
            (dwords[EchoIndex] & 0xFF) == OutOfRangeEcho;

        /// <summary>Decodes a valid table; null when the EC refused the read or answered garbage.</summary>
        public static EcSpec? Decode(IReadOnlyList<uint> dwords)
        {
            if (!IsValid(dwords))
                return null;

            byte dbCpuTdp = ByteAt(dwords, 8);

            return new EcSpec
            {
                Pl1MaxWatts = ByteAt(dwords, 0),
                Pl2MaxWatts = ByteAt(dwords, 1),
                Pl4MaxWatts = Pl4Watts(ByteAt(dwords, 2)),
                TccOffsetMax = ByteAt(dwords, 3),
                TgpOffsetMaxWatts = ByteAt(dwords, 4),
                DynamicBoostMaxWatts = ByteAt(dwords, 5),
                GpuTargetTemperatureC = ByteAt(dwords, 6),
                DefaultTgpWatts = ByteAt(dwords, 7),
                DynamicBoostCpuTdpWatts = dbCpuTdp == NotApplicable ? null : dbCpuTdp,
            };
        }

        /// <summary>PL4 is stored at half its wattage: the EC's entry is the watts divided by two.</summary>
        public static int Pl4Watts(byte raw) => raw * 2;

        /// <summary>The CPU temperature ceiling a TCC offset stands for (TjMax minus the offset).</summary>
        public static int CpuTemperatureLimitC(int tccOffset) => TjMaxC - tccOffset;

        /// <summary>
        /// The cTGP offset the EC's TGP register (0x0744) takes for a requested total TGP: the
        /// request minus the board's DefaultTGP, clamped to the register's byte. The EC stores
        /// an offset from its own base, so the same 175 W request is 80 on a 95 W-base board
        /// and 95 on an 80 W-base one.
        /// </summary>
        public static int CtgpOffsetForTgp(int targetWatts, int defaultTgpWatts) =>
            Math.Clamp(targetWatts - defaultTgpWatts, 0, 255);

        /// <summary>
        /// Whether a DefaultTGP is a real board base. 0xFF (the "not applicable" marker) and
        /// values outside the documented range fall out of this: an EC that publishes them is
        /// not describing a TGP, and a cTGP offset computed from one would be fiction.
        /// </summary>
        public static bool IsPlausibleDefaultTgp(int watts) =>
            watts >= MinDefaultTgpWatts && watts <= MaxDefaultTgpWatts;
    }
}
