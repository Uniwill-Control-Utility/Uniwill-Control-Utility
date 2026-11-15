namespace NvBiosProbe;

/// <summary>Thrown exactly where MechControl's <c>require</c> refuses, so the message reads the same.</summary>
internal sealed class BiosException(string message) : Exception(message);

internal readonly record struct Field(int Offset, uint Min, uint Def, uint Max);

internal readonly record struct TokenRow(byte Name, int Version, long Offset, int Size);

internal readonly record struct ChainImage(int Index, byte Type, long Length, bool ChecksumOk);

/// <summary>Everything the parser learned, kept even when it refuses part way through.</summary>
internal sealed class BiosReport
{
    public List<ChainImage> Images { get; } = [];
    public List<TokenRow> Tokens { get; } = [];
    public bool BitFound { get; set; }
    public int BitOffset { get; set; } = -1;
    public byte[]? BitHeader { get; set; }
    public int TokenCount { get; set; } = -1;
    public byte[]? VersionBytes { get; set; }
    public long Legacy { get; set; }
    public long Efi { get; set; }
    public int TableOffset { get; set; } = -1;
    public byte[]? TableHeader { get; set; }
    public bool Ada { get; set; }
    public bool Rtx50 { get; set; }
    public Field? Power { get; set; }
    public Field? Nv { get; set; }
    public Field? Ms { get; set; }
    public Field? Input { get; set; }
    public int? Device { get; set; }
    public string ProfileCheck { get; set; } = "not reached";
    public string? Refusal { get; set; }
}

/// <summary>
/// Port of MechControl's <c>Bios::parse</c> (src/nvidia_bios.cpp) - validation and read paths
/// only. The patch path (<c>Bios::build</c>) is deliberately absent: this probe never writes.
/// </summary>
internal static class Bios
{
    public static BiosReport Parse(byte[] image)
    {
        var report = new BiosReport();
        try
        {
            ParseCore(image, report);
        }
        catch (BiosException refused)
        {
            report.Refusal = refused.Message;
        }

        return report;
    }

    private static void ParseCore(byte[] b, BiosReport r)
    {
        static void Require(bool ok, string what)
        {
            if (!ok)
                throw new BiosException("Unsupported NVIDIA BIOS: " + what);
        }

        static uint U32(byte[] b, int p)
        {
            if (p < 0 || (long)p > b.Length || (long)b.Length - p < 4)
                throw new BiosException("Truncated NVIDIA buffer");
            return BitConverter.ToUInt32(b, p);
        }

        static int U16(byte[] b, int p)
        {
            if (p < 0 || (long)p > b.Length || (long)b.Length - p < 2)
                throw new BiosException("Truncated NVIDIA buffer");
            return BitConverter.ToUInt16(b, p);
        }

        Require(b.Length >= 512 && b.Length <= 0x1000000, "ROM size");

        void Span(int p, long n)
        {
            Require(p <= b.Length && n <= b.Length - p, "truncated ROM");
        }

        bool Sig(int p, string s)
        {
            Span(p, s.Length);
            return b.AsSpan(p, s.Length).SequenceEqual(s.Select(c => (byte)c).ToArray());
        }

        long legacy = 0, efi = 0;
        int pcir = 0, pos = 0;
        bool ended = false;
        for (int i = 0; i < 16; i++)
        {
            Span(pos, 0x1a);
            int signature = U16(b, pos);
            Require(signature == 0xaa55 || signature == 0x4e56 || signature == 0xbb77, "image signature");
            int pci = pos + U16(b, pos + 0x18);
            Require(Sig(pci, "PCIR") || Sig(pci, "NPDS") || Sig(pci, "RGIS"), "PCI directory");
            Span(pci, 0x18);
            int pciLength = U16(b, pci + 10);
            Require(pciLength >= 0x18, "PCI directory length");
            int npde = (pci + pciLength + 15) & ~15;
            Require(Sig(npde, "NPDE") && (U16(b, npde + 4) == 0x100 || U16(b, npde + 4) == 0x101) &&
                    U16(b, npde + 6) >= 12, "NPDE directory");
            Span(npde, U16(b, npde + 6));
            long length = U16(b, npde + 8) * 512L;
            long pciSize = U16(b, pci + 0x10) * 512L;
            Require(length != 0 && pciSize != 0 && pciSize <= length && npde + U16(b, npde + 6) <= pos + length,
                "image length");
            byte type = b[pci + 0x14];
            if (i == 0)
            {
                Require(type == 0 && length == pciSize && U16(b, pci + 4) == 0x10de, "NVIDIA legacy image");
                legacy = length;
                pcir = pci;
            }
            else if (i == 1 && type == 3)
            {
                Require(length == pciSize, "EFI image length");
                efi = length;
            }
            else
            {
                Require(type == 0xe0, "image ordering");
            }

            bool checksumOk = true;
            if (type == 0 || type == 3)
            {
                Span(pos, length);
                int checksum = 0;
                for (int j = pos; j < pos + length; j++)
                    checksum += b[j];
                checksumOk = (checksum & 255) == 0;
                r.Images.Add(new ChainImage(i, type, length, checksumOk));
                Require(checksumOk, "option ROM checksum");
            }
            else
            {
                r.Images.Add(new ChainImage(i, type, length, checksumOk));
            }

            if (pos + length > b.Length)
                Require(i >= 2 && type == 0xe0, "truncated image");
            ended = (b[npde + 10] & 128) != 0;
            pos += (int)length;
            if (ended)
                break;
            Require(pos < b.Length, "unterminated image chain");
        }

        Require(ended, "image count");
        r.Legacy = legacy;
        r.Efi = efi;

        int Map(uint logical, int count)
        {
            Require(logical != 0 && logical != (uint)legacy && logical <= int.MaxValue, "logical pointer");
            int p = (int)logical + (logical > legacy ? (int)efi : 0);
            Span(p, count);
            return p;
        }

        int bit = -1;
        for (int p = 0; p + 12 <= legacy; p++)
        {
            if (b[p] == 255 && b[p + 1] == 0xb8 && Sig(p + 2, "BIT\0"))
            {
                Require(bit < 0, "ambiguous BIT directory");
                bit = p;
            }
        }

        r.BitFound = bit >= 0;
        r.BitOffset = bit;
        Require(bit >= 0 && U16(b, bit + 6) == 0x100 && b[bit + 8] == 12 && b[bit + 9] == 6, "BIT header");
        r.BitHeader = b.AsSpan(bit, 12).ToArray();

        int bitSum = 0;
        for (int i = bit; i < bit + 12; i++)
            bitSum += b[i];
        Require((bitSum & 255) == 0, "BIT checksum");

        int tokens = b[bit + 10];
        r.TokenCount = tokens;
        Require(bit + 12 + tokens * 6 <= legacy, "BIT entries");

        Dictionary<byte, (int Version, int Offset, int Size)> entries = [];
        for (int i = 0; i < tokens; i++)
        {
            int t = bit + 12 + i * 6;
            int tokenSize = U16(b, t + 2);
            Require(!entries.ContainsKey(b[t]), "duplicate BIT token");
            if (tokenSize == 0)
            {
                r.Tokens.Add(new TokenRow(b[t], b[t + 1], -1, 0));
                continue;
            }

            int offset = Map((uint)U16(b, t + 4), tokenSize);
            entries.Add(b[t], (b[t + 1], offset, tokenSize));
            r.Tokens.Add(new TokenRow(b[t], b[t + 1], offset, tokenSize));
        }

        Require(entries.ContainsKey((byte)'B') && entries[(byte)'B'].Version == 2 && entries[(byte)'B'].Size >= 5 &&
                entries.ContainsKey((byte)'P') && entries[(byte)'P'].Version == 2 && entries[(byte)'P'].Size >= 0xf8,
            "BIT B/P token");

        int bios = entries[(byte)'B'].Offset, power = entries[(byte)'P'].Offset;
        r.VersionBytes = [b[bios + 3], b[bios + 2], b[bios + 1], b[bios], b[bios + 4]];
        int table = Map(U32(b, power + 0x2c), 4);
        r.TableOffset = table;
        r.TableHeader = b.AsSpan(table, 4).ToArray();

        // RTX 50 uses 40 38 66 14; RTX 40 uses 30 32 53 14, or 30 34 5B 14 in later branches.
        bool Layout(byte version, byte header, byte entry) =>
            b[table] == version && b[table + 1] == header && b[table + 2] == entry && b[table + 3] == 20;

        r.Ada = Layout(0x30, 0x32, 0x53) || Layout(0x30, 0x34, 0x5b);
        r.Rtx50 = Layout(0x40, 0x38, 0x66);
        if (!r.Ada && !r.Rtx50)
            throw new BiosException($"Unsupported NVIDIA BIOS: power policy layout " +
                                    $"{b[table]:X2} {b[table + 1]:X2} {b[table + 2]:X2} {b[table + 3]:X2}");

        int size = b[table + 1], stride = b[table + 2];
        Span(table, size + 20 * stride);
        int Entry(int i) => table + size + i * stride;

        Field Limits(int p)
        {
            uint min = U32(b, p), def = U32(b, p + 4), max = U32(b, p + 8);
            Require(min <= def && def <= max && max != 0 && max <= int.MaxValue, "policy range");
            return new Field(p, min, def, max);
        }

        if (r.Ada)
        {
            // Identity byte at +0, channel at +1, limits at +2. Index 14 is not MSVDD on Ada.
            Field AdaField(int i, bool identity)
            {
                int e = Entry(i);
                Require(identity && b[e + 1] < 32, "policy identity");
                return Limits(e + 2);
            }

            int p = Entry(2), n = Entry(13);
            r.Power = AdaField(2, b[p] == 1 || b[p] == 0x21);
            r.Nv = AdaField(13, (byte)((b[n + 0x42] << 4) | (b[n] & 15)) == 0x10 && U32(b, n + 0xe) == 0xd00);
            if (b[Entry(9)] == 0x15)
                r.Input = AdaField(9, true);
        }
        else
        {
            Field FixedField(int i, byte type, uint flags)
            {
                int e = Entry(i);
                Require(b[e] == type && U32(b, e + 1) == flags && b[e + 5] < 32, "policy identity");
                return Limits(e + 13);
            }

            r.Power = FixedField(2, 1, 2);
            foreach (int i in (int[])[7, 8])
            {
                int e = Entry(i);
                Require(b[e] == 0 && U32(b, e + 1) == 0 && U32(b, e + 13) == 0 && U32(b, e + 17) == 0 &&
                        U32(b, e + 21) == 0, "additional board-power policies");
            }

            r.Nv = FixedField(13, 7, 1);
            r.Ms = FixedField(14, 7, 1);
            if (b[Entry(9)] == 3 && U32(b, Entry(9) + 1) == 1)
                r.Input = FixedField(9, 3, 1);
            Require(b[Entry(11)] == 6 && b[Entry(12)] == 6 && U32(b, Entry(11) + 1) == 1 &&
                    U32(b, Entry(12) + 1) == 1 && b[Entry(11) + 5] == b[Entry(13) + 5] &&
                    b[Entry(12) + 5] == b[Entry(14) + 5] && b[Entry(13) + 5] != b[Entry(14) + 5],
                "rail monitor identity");
        }

        r.Device = U16(b, pcir + 6);

        uint profilePointer = U32(b, power + 0xf4);
        if (profilePointer != 0)
        {
            int profile = Map(profilePointer, 0x58e);
            byte[] header = [0x10, 0x0a, 5, 4, 7, 8, 1, 0x10, 9, 8];
            Require(b.AsSpan(profile, header.Length).SequenceEqual(header), "BIOS profile header");
            for (int i = 0; i < 4; i++)
                Require((b[profile + 10 + i * 0x3d] & 15) == 0, "active base BIOS profiles");
            for (int i = 0; i < 16; i++)
                Require((b[profile + 0xfe + i * 0x49] & 15) == 0, "active extended BIOS profiles");
            r.ProfileCheck = $"checked @ 0x{profile:X} - header matches, no active base/extended profiles";
        }
        else
        {
            r.ProfileCheck = "skipped (u32(power+0xf4) == 0)";
        }
    }
}
