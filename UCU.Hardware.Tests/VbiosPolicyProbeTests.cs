using System.Buffers.Binary;
using System.Runtime.InteropServices;
using UCU.Hardware.Gpu;

namespace UCU.Hardware.Tests;

/// <summary>
/// The read-only VBIOS power-policy report, against synthetic ROM images. The layout is
/// pinned to the field machine's 98.03.5E.00.5D (table at 0x57B1D, 0x38-byte header,
/// 0x66-byte stride), because the whole point is whether a *different* VBIOS still
/// carries the seven record offsets at the same fields. No hardware, no registry.
/// </summary>
public sealed class VbiosPolicyProbeTests
{
    private const int LegacyLength = 0x60000; // enough to hold the pinned offsets, a 512 multiple
    private const int BitOffset = 0x1000;
    private const int VersionOffset = 0x1200;
    private const int PowerTokenOffset = 0x1300;
    private const int PciOffset = 0x40;
    private const int TableOffset = 0x57B1D;

    private static readonly uint[] PinnedOffsets =
    {
        0x57C36, 0x57EFC, 0x57F00, 0x58094, 0x58098, 0x580FA, 0x580FE,
    };

    [Fact]
    public void Parse_ReadsThePinnedLayoutAndTheTableItFinds()
    {
        VbiosPolicyState state = VbiosPolicyParser.Parse(Image());

        Assert.True(state.Succeeded, state.Reason);
        Assert.Equal("98.03.5E.00.5D", state.Version);
        Assert.Equal(TableOffset, state.TableOffset);
        Assert.Equal("40 38 66 14", state.TableHeader);
        Assert.Equal(0x2C18, state.PciDevice);

        AssertPolicy(state.Power, 0x57C2E, 5000, 95000, 175000);
        AssertPolicy(state.NvVdd, 0x58090, 1, 210000, 210000);
        AssertPolicy(state.MsVdd, 0x580F6, 1, 60000, 60000);
        AssertPolicy(state.BoardInput, 0x57EF8, 1, 11053, 11053);

        Assert.Equal(PinnedOffsets, state.Pinned.Select(check => check.Offset));
        Assert.All(state.Pinned, check => Assert.True(check.Matches, check.Name));
        Assert.True(state.PinnedOffsetsMatch);

        // The component each record addresses is what makes a MATCH mean more than "same number".
        Assert.Equal("max", state.Pinned[0].Component);
        Assert.Equal("default", state.Pinned[1].Component);
        Assert.Equal("max", state.Pinned[2].Component);
    }

    [Fact]
    public void Parse_RefusesAnotherPolicyLayoutInsteadOfReadingItsNumbers()
    {
        byte[] image = Image();
        image[TableOffset] = 0x30;
        image[TableOffset + 1] = 0x32;
        image[TableOffset + 2] = 0x53;
        image[TableOffset + 3] = 0x14;

        VbiosPolicyState state = VbiosPolicyParser.Parse(image);

        Assert.False(state.Succeeded);
        Assert.Contains("power policy layout 30 32 53 14", state.Reason);
        Assert.Null(state.Power);
        Assert.Empty(state.Pinned);
    }

    [Fact]
    public void Parse_RefusesATruncatedImage()
    {
        byte[] truncated = Image().AsSpan(0, BitOffset).ToArray();

        VbiosPolicyState state = VbiosPolicyParser.Parse(truncated);

        Assert.False(state.Succeeded);
        Assert.Contains("truncated", state.Reason);
    }

    [Fact]
    public void Parse_RefusesAnImageTooSmallToBeARom()
    {
        VbiosPolicyState state = VbiosPolicyParser.Parse(new byte[64]);

        Assert.False(state.Succeeded);
        Assert.Contains("ROM size 64 bytes", state.Reason);
    }

    [Fact]
    public void Parse_SaysDifferWhenTheTableSitsSomewhereElse()
    {
        // A tester's VBIOS: same layout, its own table address. The pinned offsets then
        // hold whatever that image has there, which is why they cannot be trusted.
        byte[] image = Image(TableOffset - 0x1000);

        VbiosPolicyState state = VbiosPolicyParser.Parse(image);

        Assert.True(state.Succeeded, state.Reason);
        Assert.Equal(TableOffset - 0x1000, state.TableOffset);
        Assert.False(state.PinnedOffsetsMatch);
        Assert.All(state.Pinned, check => Assert.False(check.Matches, check.Name));

        VbiosPinnedCheck power = state.Pinned[0];
        Assert.Equal(0x57C36u, power.Offset);
        Assert.Equal(0u, power.ImageValue);
        Assert.Equal("max", power.Component);
        Assert.Equal(175000u, power.TableValue);
        Assert.Equal(TableOffset - 0x1000 + 0x111 + 8, power.TableValueOffset);
    }

    [Theory]
    [InlineData(5000u, 200000u, 100000u)] // default above max
    [InlineData(5000u, 95000u, 0u)]       // zero maximum
    [InlineData(95000u, 5000u, 175000u)]  // minimum above default
    public void Parse_RefusesAnImplausibleRange(uint min, uint value, uint max)
    {
        VbiosPolicyState state = VbiosPolicyParser.Parse(Image(powerMin: min, powerValue: value, powerMax: max));

        Assert.False(state.Succeeded);
        Assert.Contains("policy range at index 2", state.Reason);
    }

    [Fact]
    public void Read_DumpsTheImageAndParsesIt()
    {
        var probe = new NvApiVbiosPolicyProbe(new FakeCalls(Image()));

        VbiosPolicyState state = probe.Read();

        Assert.True(state.Succeeded, state.Reason);
        Assert.True(state.PinnedOffsetsMatch);
    }

    [Fact]
    public void Read_ReportsAnUnexpectedImageSize()
    {
        var probe = new NvApiVbiosPolicyProbe(new FakeCalls(new byte[100]));

        VbiosPolicyState state = probe.Read();

        Assert.False(state.Succeeded);
        Assert.Contains("the driver reported a VBIOS image of 100 bytes", state.Reason);
    }

    [Fact]
    public void Read_ReportsAFailureInsteadOfThrowing()
    {
        FakeCalls calls = new(Image()) { Status = -1 };
        VbiosPolicyState state = new NvApiVbiosPolicyProbe(calls).Read();
        Assert.Contains("status -1", state.Reason);

        calls = new FakeCalls(Image()) { Overflow = true };
        state = new NvApiVbiosPolicyProbe(calls).Read();
        Assert.Contains("wrote past the VBIOS buffer", state.Reason);

        calls = new FakeCalls(Image()) { Resolve = false };
        state = new NvApiVbiosPolicyProbe(calls).Read();
        Assert.Contains("unavailable", state.Reason);
    }

    private static void AssertPolicy(VbiosPolicyField? field, int offset, uint min, uint value, uint max)
    {
        Assert.NotNull(field);
        Assert.Equal(offset, field!.Offset);
        Assert.Equal(min, field.Min);
        Assert.Equal(value, field.Default);
        Assert.Equal(max, field.Max);
    }

    /// <summary>A synthetic RTX 50 ROM: chain, BIT directory, and the policy table.</summary>
    private static byte[] Image(int table = TableOffset, uint powerMin = 5000, uint powerValue = 95000, uint powerMax = 175000)
    {
        byte[] image = new byte[LegacyLength];

        // Option ROM header, PCI directory and NPDE, as far as the chain walk reads them.
        Write16(image, 0, 0xAA55);
        Write16(image, 0x18, PciOffset);
        Write(image, PciOffset, "PCIR");
        Write16(image, PciOffset + 4, 0x10DE);
        Write16(image, PciOffset + 6, 0x2C18);
        Write16(image, PciOffset + 0x10, LegacyLength / 512);
        Write16(image, PciOffset + 10, 0x18);

        int npde = (PciOffset + 0x18 + 15) & ~15;
        Write(image, npde, "NPDE");
        Write16(image, npde + 4, 0x100);
        Write16(image, npde + 6, 12);
        Write16(image, npde + 8, LegacyLength / 512);
        image[npde + 10] = 0x80;

        // BIT directory: a B token (the VBIOS version) and a P token (the table pointer).
        image[BitOffset] = 0xFF;
        image[BitOffset + 1] = 0xB8;
        Write(image, BitOffset + 2, "BIT\0");
        Write16(image, BitOffset + 6, 0x100);
        image[BitOffset + 8] = 12;
        image[BitOffset + 9] = 6;
        image[BitOffset + 10] = 2;

        int token = BitOffset + 12;
        image[token] = (byte)'B';
        image[token + 1] = 2;
        Write16(image, token + 2, 5);
        Write16(image, token + 4, VersionOffset);

        token += 6;
        image[token] = (byte)'P';
        image[token + 1] = 2;
        Write16(image, token + 2, 0xF8);
        Write16(image, token + 4, PowerTokenOffset);

        // "98.03.5E.00.5D" in the B token's reversed order.
        image[VersionOffset] = 0x00;
        image[VersionOffset + 1] = 0x5E;
        image[VersionOffset + 2] = 0x03;
        image[VersionOffset + 3] = 0x98;
        image[VersionOffset + 4] = 0x5D;

        Write32(image, PowerTokenOffset + 0x2c, (uint)table);

        image[table] = 0x40;
        image[table + 1] = 0x38;
        image[table + 2] = 0x66;
        image[table + 3] = 0x14;

        Policy(image, table + 0x38 + 2 * 0x66, type: 1, flags: 2, powerMin, powerValue, powerMax);
        Policy(image, table + 0x38 + 13 * 0x66, type: 7, flags: 1, 1, 210000, 210000);
        Policy(image, table + 0x38 + 14 * 0x66, type: 7, flags: 1, 1, 60000, 60000);
        Policy(image, table + 0x38 + 9 * 0x66, type: 3, flags: 1, 1, 11053, 11053);
        return image;
    }

    private static void Policy(byte[] image, int entry, byte type, uint flags, uint min, uint value, uint max)
    {
        image[entry] = type;
        Write32(image, entry + 1, flags);
        Write32(image, entry + 13, min);
        Write32(image, entry + 17, value);
        Write32(image, entry + 21, max);
    }

    private static void Write(byte[] image, int offset, string text) =>
        System.Text.Encoding.ASCII.GetBytes(text).CopyTo(image, offset);

    private static void Write16(byte[] image, int offset, int value) =>
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(offset, 2), (ushort)value);

    private static void Write32(byte[] image, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(offset, 4), value);

    /// <summary>The NVAPI entry points without a driver: resolves the VBIOS call and writes a scripted image.</summary>
    private sealed class FakeCalls : INvApiPowerCalls
    {
        private readonly byte[] _rom;

        public FakeCalls(byte[] rom) => _rom = rom;

        public bool Resolve { get; init; } = true;

        public bool Overflow { get; init; }

        public int Status { get; init; }

        public bool TryResolve(uint id, out NvApiGpuCall call)
        {
            call = null!;
            if (!Resolve || id != NvApiVbiosPolicyProbe.BiosId)
                return false;

            call = (gpu, packet) =>
            {
                if (Overflow)
                {
                    // One byte into the leading canary band.
                    Marshal.WriteByte(packet, -NvApiPowerLayout.GuardBandLength, 0x00);
                    return Status;
                }

                byte[] head = new byte[NvApiVbiosPolicyProbe.PacketLength];
                Marshal.Copy(packet, head, 0, head.Length);
                ulong pointer = BinaryPrimitives.ReadUInt64LittleEndian(head.AsSpan(8, 8));

                if (_rom.Length > 0)
                    Marshal.Copy(_rom, 0, new IntPtr((long)pointer), _rom.Length);

                Marshal.Copy(
                    BitConverter.GetBytes((uint)_rom.Length), 0, packet + 4, 4);
                return Status;
            };
            return true;
        }

        public bool TryGetGpu(out IntPtr gpu, out string? reason)
        {
            gpu = new IntPtr(1);
            reason = null;
            return true;
        }
    }
}
