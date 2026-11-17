using System.Buffers.Binary;
using System.Globalization;

namespace UCU.Hardware.Gpu
{
    /// <summary>The rail a ROM power-policy field carries (values are mA, or mW for the ceiling).</summary>
    public enum RomOverrideRail
    {
        Power,
        BoardInput,
        NvVdd,
        MsVdd,
    }

    /// <summary>
    /// One <c>romOverrideNN</c> record we own: its registry value name, the ROM file
    /// offset it patches and the stock value the ROM carries there (mW / mA).
    /// </summary>
    public sealed record RomOverrideField(string Name, uint Offset, uint StockValue, RomOverrideRail Rail);

    /// <summary>
    /// A power tier's numbers, in the units the ROM records use. The records are
    /// computed from this, never hardcoded at the write site.
    ///
    /// <see cref="PowerMilliwatts"/> is the policy <em>ceiling</em> (the number the
    /// driver honours a request against) and <see cref="TgpTargetMilliwatts"/> is the
    /// TGP actually asked for. They differ on purpose: a request equal to the ceiling
    /// is answered with the old 175 W limit, so the ceiling must sit above the target
    /// (measured against MechControl-RE, 2026-11-15: ceiling 220 W + request 220 W →
    /// enforced 175 W; ceiling 250 W + request 220 W → enforced 220 W).
    ///
    /// The <c>*MilliAmps</c> values are the record <em>maxima</em>; the
    /// <c>*CurrentMilliAmps</c> ones are the runtime rail currents, which the driver
    /// takes through a separate NVAPI call (the records only widen the policy, they do
    /// not move a rail). Measured 2026-11-15 with everything else identical: stock rails
    /// 210000/60000/11053 mA → 175-179 W board power, 250000/70000/14026 mA → 205-214 W.
    /// </summary>
    public sealed record GpuPowerTier(
        string Id,
        string Name,
        int PowerMilliwatts,
        int TgpTargetMilliwatts,
        int NvVddMilliAmps,
        int MsVddMilliAmps,
        int BoardInputMilliAmps,
        int NvVddCurrentMilliAmps,
        int MsVddCurrentMilliAmps,
        int BoardInputCurrentMilliAmps)
    {
        /// <summary>The TGP target in watts, for the EC's cTGP offset and log text.</summary>
        public int TgpTargetWatts => TgpTargetMilliwatts / 1000;
    }

    /// <summary>
    /// The tiers to ship. "Extra GPU power" is the measured unlock: 250 W ceiling,
    /// 225 W TGP target (the ceiling minus the 25 W Dynamic Boost maximum — the EC's
    /// own limit for this board), rails at MechControl-RE's measured values, i.e.
    /// ≈1.4× the ~200-210 W draw.
    ///
    /// The board input carries two numbers on purpose: the record maximum is the
    /// reference's 17.021 A (the policy ceiling the driver may offer, and therefore the
    /// bound the sliders read), while the runtime rail current we apply stays 14.026 A —
    /// 14.026 A × ~20 V + 130 W CPU stays inside the 420 W adapter where 17.021 A does
    /// not. So a driver reset leaves the rails at 17.021 A until the re-apply lands,
    /// which is why the start-up re-apply retries.
    /// </summary>
    public static class GpuPowerTiers
    {
        public static readonly GpuPowerTier ExtraGpuPower = new(
            Id: "extra_gpu_power",
            Name: "Extra GPU power",
            PowerMilliwatts: 250_000,
            TgpTargetMilliwatts: 225_000,
            // 250 A for every image: the card draws ~67 A at this tier, the reference's 294 A was
            // only ever stock x1.4 for its own ROM, and 250 A is what the tester's image already
            // ships as its stock ceiling - one number that fits both rather than a per-maker table.
            NvVddMilliAmps: 250_000,
            MsVddMilliAmps: 84_000,
            BoardInputMilliAmps: 17_021,
            NvVddCurrentMilliAmps: 250_000,
            MsVddCurrentMilliAmps: 70_000,
            BoardInputCurrentMilliAmps: 14_026);
    }

    /// <summary>
    /// The measured override table. The seven records, their addresses and their stock
    /// values were measured on VBIOS 98.03.5E.00.5D (GB20x, PCI 0x2C18) and read back from
    /// the driver key. A different version is only pinned when its own parsed image puts
    /// every record offset on the same policy field; its records are then the same tier
    /// values, written above that image's stock caps on purpose (the unlock's whole point).
    /// </summary>
    public static class RomOverrideLayout
    {
        public const string PinnedVbiosVersion = "98.03.5E.00.5D";

        /// <summary>
        /// The runtime rail currents the VBIOS-pinned stock ROM carries (mA). The rails
        /// are a separate NVAPI write from these records, so a restore has to put these
        /// back explicitly — the records already carry the same numbers as their stock
        /// values, but only the runtime setter actually lowers a rail.
        /// </summary>
        public const int StockNvVddMilliAmps = 210_000;
        public const int StockMsVddMilliAmps = 60_000;
        public const int StockBoardInputMilliAmps = 11_053;

        public static readonly RomOverrideField[] Fields =
        {
            new("romOverride00", 0x57C36, 175000, RomOverrideRail.Power),
            new("romOverride01", 0x57EFC, (uint)StockBoardInputMilliAmps, RomOverrideRail.BoardInput),
            new("romOverride02", 0x57F00, (uint)StockBoardInputMilliAmps, RomOverrideRail.BoardInput),
            new("romOverride03", 0x58094, (uint)StockNvVddMilliAmps, RomOverrideRail.NvVdd),
            new("romOverride04", 0x58098, (uint)StockNvVddMilliAmps, RomOverrideRail.NvVdd),
            new("romOverride05", 0x580FA, (uint)StockMsVddMilliAmps, RomOverrideRail.MsVdd),
            new("romOverride06", 0x580FE, (uint)StockMsVddMilliAmps, RomOverrideRail.MsVdd),
        };

        /// <summary>The stock ceiling the pinned ROM enforces (romOverride00's value).</summary>
        public const int StockPowerMilliwatts = 175000;

        /// <summary>
        /// The known-good version, asserted without a probe: it is the only image this
        /// build's offsets were read from.
        /// </summary>
        public static bool IsPinned(string? vbiosVersion) =>
            string.Equals(vbiosVersion, PinnedVbiosVersion, StringComparison.Ordinal);

        /// <summary>
        /// True when this build may patch this adapter's image: the known-good version, or
        /// any other version whose own parse puts all seven record offsets on the field
        /// component the records are meant to patch. The version string is evidence for the
        /// report, never the gate; a DIFFER verdict, no answer and an unparseable image all
        /// stay refusals.
        /// </summary>
        public static bool IsPinned(string? vbiosVersion, VbiosPolicyState? image) =>
            IsPinned(vbiosVersion) || image is { PinnedOffsetsMatch: true };

        /// <summary>
        /// The driver key's <c>HardwareInformation.BiosString</c> reads like
        /// "Version98.3.5e.0.5d"; the records carry the same five bytes as
        /// "98.03.5E.00.5D". Null when the text is not five hex bytes.
        /// </summary>
        public static string? NormalizeVbiosVersion(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return null;

            string text = raw!.Trim();
            if (text.StartsWith("Version", StringComparison.OrdinalIgnoreCase))
                text = text.Substring("Version".Length);

            string[] parts = text.Split('.');
            if (parts.Length != 5)
                return null;

            string[] hex = new string[5];
            for (int i = 0; i < parts.Length; i++)
            {
                if (!byte.TryParse(parts[i], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte value))
                    return null;

                hex[i] = value.ToString("X2", CultureInfo.InvariantCulture);
            }

            return string.Join(".", hex);
        }

        /// <summary>Every record for a tier, in table order.</summary>
        public static IReadOnlyList<(string Name, byte[] Data)> BuildRecords(GpuPowerTier tier, string vbiosVersion)
        {
            var records = new List<(string, byte[])>(Fields.Length);
            foreach (RomOverrideField field in Fields)
                records.Add((field.Name, RomOverrideRecord.Encode(vbiosVersion, field.Offset, ValueFor(field.Rail, tier))));

            return records;
        }

        public static uint ValueFor(RomOverrideRail rail, GpuPowerTier tier) => rail switch
        {
            RomOverrideRail.Power => (uint)tier.PowerMilliwatts,
            RomOverrideRail.BoardInput => (uint)tier.BoardInputMilliAmps,
            RomOverrideRail.NvVdd => (uint)tier.NvVddMilliAmps,
            RomOverrideRail.MsVdd => (uint)tier.MsVddMilliAmps,
            _ => throw new ArgumentOutOfRangeException(nameof(rail)),
        };
    }

    /// <summary>
    /// The 14-byte driver record: [0..4] the five VBIOS version bytes, [5..8] the ROM
    /// offset (u32 LE), [9] the patch length (always 4), [10..13] the value (u32 LE).
    /// Layout read back from the driver key on the field machine, not inferred.
    /// </summary>
    public static class RomOverrideRecord
    {
        public const int Length = 14;
        public const byte ValueLength = 4;

        public static byte[] Encode(string vbiosVersion, uint offset, uint value)
        {
            byte[] data = new byte[Length];

            string[] parts = vbiosVersion.Split('.');
            if (parts.Length != 5)
                throw new ArgumentException($"'{vbiosVersion}' is not a five-byte VBIOS version", nameof(vbiosVersion));

            for (int i = 0; i < parts.Length; i++)
                data[i] = byte.Parse(parts[i], NumberStyles.HexNumber, CultureInfo.InvariantCulture);

            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(5, 4), offset);
            data[9] = ValueLength;
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(10, 4), value);
            return data;
        }

        /// <summary>False for a record that is not ours to read: wrong size or wrong patch length.</summary>
        public static bool TryDecode(byte[]? data, out string vbiosVersion, out uint offset, out uint value)
        {
            vbiosVersion = string.Empty;
            offset = 0;
            value = 0;

            if (data == null || data.Length != Length || data[9] != ValueLength)
                return false;

            var hex = new string[5];
            for (int i = 0; i < hex.Length; i++)
                hex[i] = data[i].ToString("X2", CultureInfo.InvariantCulture);

            vbiosVersion = string.Join(".", hex);
            offset = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(5, 4));
            value = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(10, 4));
            return true;
        }
    }
}
