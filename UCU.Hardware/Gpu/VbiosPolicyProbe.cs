using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace UCU.Hardware.Gpu
{
    /// <summary>
    /// One power-policy entry's limits as the ROM carries them: the minimum at
    /// <see cref="Offset"/>, the default at +4 and the maximum at +8 (u32 LE).
    /// </summary>
    public sealed record VbiosPolicyField(int Offset, uint Min, uint Default, uint Max)
    {
        /// <summary>0 = min, 1 = default, 2 = max.</summary>
        public uint At(int component) => component <= 0 ? Min : component == 1 ? Default : Max;

        public static string NameOf(int component) => component <= 0 ? "min" : component == 1 ? "default" : "max";
    }

    /// <summary>
    /// One of the seven record offsets the unlock patches, compared with what the parsed
    /// table says that record addresses. <see cref="Matches"/> needs both the offset and
    /// the value: a different VBIOS can hold the same number somewhere else in its ROM.
    /// </summary>
    public sealed record VbiosPinnedCheck(
        string Name, uint Offset, uint ImageValue, int TableValueOffset, string Component, uint TableValue)
    {
        public bool Matches => Offset == (uint)TableValueOffset && ImageValue == TableValue;
    }

    /// <summary>
    /// What the VBIOS image says. A failure is data: <see cref="Reason"/> names why no
    /// table was read, and every number is then absent rather than guessed.
    /// </summary>
    public sealed record VbiosPolicyState(
        string? Reason,
        string? Version,
        int TableOffset,
        string? TableHeader,
        int? PciDevice,
        VbiosPolicyField? Power,
        VbiosPolicyField? NvVdd,
        VbiosPolicyField? MsVdd,
        VbiosPolicyField? BoardInput,
        IReadOnlyList<VbiosPinnedCheck> Pinned)
    {
        public bool Succeeded => Reason == null;

        /// <summary>True when every pinned record offset lands on its policy field.</summary>
        public bool PinnedOffsetsMatch => Succeeded && Pinned.Count > 0 && Pinned.All(check => check.Matches);

        public static VbiosPolicyState Failed(string reason) => new(
            reason, null, -1, null, null, null, null, null, null, Array.Empty<VbiosPinnedCheck>());
    }

    /// <summary>Read-only VBIOS probe, behind a seam so the packet flow is testable.</summary>
    public interface IVbiosPolicyProbe
    {
        VbiosPolicyState Read();
    }

    /// <summary>
    /// Dumps the adapter's VBIOS image through NVAPI's private interface and parses the
    /// power-policy table out of it. Read-only: the dump interface is the only call made,
    /// nothing is written and no registry key is touched.
    ///
    /// The answer exists to explain an unpinned VBIOS: a tester's ROM (98.03.5E.00.1E)
    /// carries its own table, so the pinned record offsets can only be trusted once this
    /// says they land on the same fields.
    /// </summary>
    public sealed class NvApiVbiosPolicyProbe : IVbiosPolicyProbe
    {
        /// <summary><c>Api::bios</c>: packet(16, 0x20010), capacity u32 at 4, image pointer at 8.</summary>
        public const uint BiosId = 0xfc13ee11;
        public const uint PacketVersion = 0x20010;
        public const int PacketLength = 16;

        /// <summary>MechControl's capacity; the driver reports the real length back at offset 4.</summary>
        public const int Capacity = 1_024_000;

        public const int MinSize = 512;
        public const int MaxSize = 0x1000000;

        private readonly INvApiPowerCalls _calls;

        public NvApiVbiosPolicyProbe()
            : this(new NvApiPowerCalls())
        {
        }

        public NvApiVbiosPolicyProbe(INvApiPowerCalls calls) => _calls = calls;

        public VbiosPolicyState Read()
        {
            if (!_calls.TryGetGpu(out IntPtr gpu, out string? gpuReason))
                return VbiosPolicyState.Failed(gpuReason ?? "NVAPI enumerated no GPU");

            if (!_calls.TryResolve(BiosId, out NvApiGpuCall bios))
                return VbiosPolicyState.Failed(NvApiPowerLayout.Unavailable(BiosId));

            byte[] image;
            try
            {
                if (!TryDump(gpu, bios, out image, out string? dumpReason))
                    return VbiosPolicyState.Failed(dumpReason ?? "the VBIOS dump failed");
            }
            catch (Exception ex)
            {
                return VbiosPolicyState.Failed($"the VBIOS dump failed ({ex.GetType().Name}: {ex.Message})");
            }

            return VbiosPolicyParser.Parse(image);
        }

        /// <summary>
        /// One dump call. Both buffers sit between canary bands, like every other buffer
        /// the engine hands the driver: a driver writing past it is silent corruption
        /// otherwise.
        /// </summary>
        internal static bool TryDump(IntPtr gpu, NvApiGpuCall bios, out byte[] image, out string? reason)
        {
            int guard = NvApiPowerLayout.GuardBandLength;
            image = Array.Empty<byte>();
            reason = null;

            byte[] rom = new byte[Capacity + 2 * guard];
            rom.AsSpan().Fill(NvApiPowerLayout.GuardByte);
            byte[] packet = new byte[PacketLength + 2 * guard];
            packet.AsSpan().Fill(NvApiPowerLayout.GuardByte);
            packet.AsSpan(guard, PacketLength).Clear();

            NvApiPowerRequest.Write32(packet, guard, PacketVersion);
            NvApiPowerRequest.Write32(packet, guard + 4, Capacity);

            GCHandle romHandle = GCHandle.Alloc(rom, GCHandleType.Pinned);
            GCHandle packetHandle = GCHandle.Alloc(packet, GCHandleType.Pinned);
            try
            {
                BinaryPrimitives.WriteUInt64LittleEndian(
                    packet.AsSpan(guard + 8, 8), (ulong)(romHandle.AddrOfPinnedObject() + guard));

                int status = bios(gpu, packetHandle.AddrOfPinnedObject() + guard);

                if (!NvApiPowerRequest.GuardBandsIntact(rom) || !NvApiPowerRequest.GuardBandsIntact(packet))
                {
                    reason = "the driver wrote past the VBIOS buffer";
                    return false;
                }

                if (status != 0)
                {
                    reason = $"the VBIOS dump call failed (status {status})";
                    return false;
                }

                uint size = NvApiPowerRequest.Read32(packet, guard + 4);
                if (size < MinSize || size > Capacity)
                {
                    reason = $"the driver reported a VBIOS image of {size} bytes";
                    return false;
                }

                image = new byte[size];
                Buffer.BlockCopy(rom, guard, image, 0, (int)size);
                return true;
            }
            finally
            {
                packetHandle.Free();
                romHandle.Free();
            }
        }
    }

    /// <summary>
    /// The image-side half: find the power-policy table the way MechControl's
    /// <c>Bios::parse</c> does (image chain → BIT directory → the 'P' token's pointer)
    /// and read the four policies plus the seven pinned offsets.
    ///
    /// Only the RTX 50 layout (<c>40 38 66 14</c>) is accepted: it is the layout this
    /// fork pins, and a report that quietly read another layout's numbers would be worse
    /// than a refusal. The option-ROM checksum is not verified — the driver hands back
    /// its patched copy, so a mismatch there would refuse a ROM that is exactly what the
    /// unlock wrote.
    /// </summary>
    public static class VbiosPolicyParser
    {
        public static VbiosPolicyState Parse(byte[] image)
        {
            try
            {
                return ParseCore(image);
            }
            catch (Refusal refused)
            {
                return VbiosPolicyState.Failed(refused.Message);
            }
            catch (Exception ex)
            {
                return VbiosPolicyState.Failed($"the VBIOS image could not be parsed ({ex.GetType().Name}: {ex.Message})");
            }
        }

        private sealed class Refusal : Exception
        {
            public Refusal(string message)
                : base(message)
            {
            }
        }

        private static void Require(bool ok, string what)
        {
            if (!ok)
                throw new Refusal("Unsupported NVIDIA BIOS: " + what);
        }

        private static VbiosPolicyState ParseCore(byte[] b)
        {
            Require(b.Length >= NvApiVbiosPolicyProbe.MinSize && b.Length <= NvApiVbiosPolicyProbe.MaxSize,
                $"ROM size {b.Length} bytes");

            static uint U32(byte[] b, int p)
            {
                Require(p >= 0 && (long)p + 4 <= b.Length, "truncated ROM");
                return BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(p, 4));
            }

            static int U16(byte[] b, int p)
            {
                Require(p >= 0 && (long)p + 2 <= b.Length, "truncated ROM");
                return BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(p, 2));
            }

            static bool Sig(byte[] b, int p, string signature)
            {
                Require(p >= 0 && (long)p + signature.Length <= b.Length, "truncated ROM");
                return b.AsSpan(p, signature.Length).SequenceEqual(Encoding.ASCII.GetBytes(signature));
            }

            // The option ROM chain: the legacy image's length maps the BIT tokens' logical
            // pointers (an EFI-region pointer is offset by the EFI image's length).
            long legacy = 0, efi = 0;
            int pos = 0, pcir = -1;
            bool ended = false;
            for (int i = 0; i < 16; i++)
            {
                Require((long)pos + 0x1a <= b.Length, "truncated ROM");
                int signature = U16(b, pos);
                Require(signature == 0xaa55 || signature == 0x4e56 || signature == 0xbb77, "image signature");

                int pci = pos + U16(b, pos + 0x18);
                Require(Sig(b, pci, "PCIR") || Sig(b, pci, "NPDS") || Sig(b, pci, "RGIS"), "PCI directory");
                int pciLength = U16(b, pci + 10);
                Require(pciLength >= 0x18, "PCI directory length");

                int npde = (pci + pciLength + 15) & ~15;
                Require(Sig(b, npde, "NPDE") && (U16(b, npde + 4) == 0x100 || U16(b, npde + 4) == 0x101) &&
                        U16(b, npde + 6) >= 12, "NPDE directory");

                long length = U16(b, npde + 8) * 512L;
                long pciSize = U16(b, pci + 0x10) * 512L;
                Require(length != 0 && pciSize != 0 && pciSize <= length &&
                        npde + U16(b, npde + 6) <= pos + length, "image length");

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

                if ((long)pos + length > b.Length)
                    Require(i >= 2 && type == 0xe0, "truncated ROM");

                ended = (b[npde + 10] & 128) != 0;
                pos += (int)length;
                if (ended)
                    break;

                Require(pos < b.Length, "unterminated image chain");
            }

            Require(ended, "image count");

            int Map(uint logical, int count)
            {
                Require(logical != 0 && logical != (uint)legacy && logical <= int.MaxValue, "logical pointer");
                int p = (int)logical + (logical > legacy ? (int)efi : 0);
                Require(p >= 0 && (long)p + count <= b.Length, "truncated ROM");
                return p;
            }

            int bit = -1;
            for (int p = 0; (long)p + 12 <= legacy; p++)
            {
                if (b[p] == 0xff && b[p + 1] == 0xb8 && Sig(b, p + 2, "BIT\0"))
                {
                    Require(bit < 0, "ambiguous BIT directory");
                    bit = p;
                }
            }

            Require(bit >= 0 && U16(b, bit + 6) == 0x100 && b[bit + 8] == 12 && b[bit + 9] == 6, "BIT header");

            int tokens = b[bit + 10];
            Require((long)bit + 12 + tokens * 6 <= legacy, "BIT entries");

            var entries = new Dictionary<byte, (int Version, int Offset, int Size)>();
            for (int i = 0; i < tokens; i++)
            {
                int t = bit + 12 + i * 6;
                int tokenSize = U16(b, t + 2);
                Require(!entries.ContainsKey(b[t]), "duplicate BIT token");
                if (tokenSize == 0)
                    continue;

                entries.Add(b[t], (b[t + 1], Map((uint)U16(b, t + 4), tokenSize), tokenSize));
            }

            Require(entries.ContainsKey((byte)'B') && entries[(byte)'B'].Version == 2 && entries[(byte)'B'].Size >= 5 &&
                    entries.ContainsKey((byte)'P') && entries[(byte)'P'].Version == 2 && entries[(byte)'P'].Size >= 0xf8,
                "BIT B/P token");

            int bios = entries[(byte)'B'].Offset;
            int powerToken = entries[(byte)'P'].Offset;
            string version = string.Join(".", new[]
            {
                b[bios + 3].ToString("X2", CultureInfo.InvariantCulture),
                b[bios + 2].ToString("X2", CultureInfo.InvariantCulture),
                b[bios + 1].ToString("X2", CultureInfo.InvariantCulture),
                b[bios].ToString("X2", CultureInfo.InvariantCulture),
                b[bios + 4].ToString("X2", CultureInfo.InvariantCulture),
            });

            int table = Map(U32(b, powerToken + 0x2c), 4);
            string tableHeader = Hex(b.AsSpan(table, 4));

            // RTX 50's table: 40 38 66 14. The other layouts are not this fork's business.
            Require(b[table] == 0x40 && b[table + 1] == 0x38 && b[table + 2] == 0x66 && b[table + 3] == 20,
                $"power policy layout {tableHeader}");

            int size = b[table + 1], stride = b[table + 2];
            Require((long)table + size + 15L * stride + 12 <= b.Length, "power policy table bounds");

            int Entry(int index) => table + size + index * stride;

            VbiosPolicyField Field(int index, byte type, uint flags)
            {
                int e = Entry(index);
                Require(b[e] == type && U32(b, e + 1) == flags && b[e + 5] < 32, $"policy identity at index {index}");

                uint min = U32(b, e + 13), value = U32(b, e + 17), max = U32(b, e + 21);
                Require(min <= value && value <= max && max != 0 && max <= int.MaxValue,
                    $"policy range at index {index} ({min}/{value}/{max})");
                return new VbiosPolicyField(e + 13, min, value, max);
            }

            VbiosPolicyField power = Field(2, 1, 2);
            VbiosPolicyField nvVdd = Field(13, 7, 1);
            VbiosPolicyField msVdd = Field(14, 7, 1);
            VbiosPolicyField boardInput = Field(9, 3, 1);

            var pinned = new List<VbiosPinnedCheck>(RomOverrideLayout.Fields.Length);
            foreach (RomOverrideField field in RomOverrideLayout.Fields)
            {
                VbiosPolicyField parsed = field.Rail switch
                {
                    RomOverrideRail.Power => power,
                    RomOverrideRail.BoardInput => boardInput,
                    RomOverrideRail.NvVdd => nvVdd,
                    _ => msVdd,
                };

                int component = PinnedComponent(field.Name);
                pinned.Add(new VbiosPinnedCheck(
                    field.Name,
                    field.Offset,
                    U32(b, (int)field.Offset),
                    parsed.Offset + component * 4,
                    VbiosPolicyField.NameOf(component),
                    parsed.At(component)));
            }

            return new VbiosPolicyState(
                null, version, table, tableHeader, U16(b, pcir + 6),
                power, nvVdd, msVdd, boardInput, pinned);
        }

        /// <summary>
        /// Which limit of the parsed field each record patches (0 min, 1 default, 2 max),
        /// measured on 98.03.5E.00.5D: the power ceiling is the single power record, and
        /// each rail carries its default and its maximum (never its minimum).
        /// </summary>
        private static int PinnedComponent(string name) => name switch
        {
            "romOverride00" => 2,
            "romOverride01" => 1,
            "romOverride03" => 1,
            "romOverride05" => 1,
            _ => 2,
        };

        private static string Hex(ReadOnlySpan<byte> bytes) =>
            string.Join(" ", bytes.ToArray().Select(value => value.ToString("X2", CultureInfo.InvariantCulture)));
    }
}
