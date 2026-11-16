using UCU.Contracts;
using UCU.Hardware.Gpu;
using NvAPIWrapper.Native.GPU;
using static NvAPIWrapper.Native.GPU.Structures.PerformanceStates20InfoV1;

namespace UCU.Hardware.Tests;

/// <summary>
/// Golden bytes for the NVAPI buffers. The layouts are the app-side working path's
/// (Scripts/GPUs/NVIDIA/NvTuning.cs and GpuPowerSaving.cs), moved engine-side: the
/// version word, the point mask at 0x04, the kHz delta at 0x44 + idx*0x24 + 0x14,
/// and the GPU type as a uint32 at offset 0 of the 64-byte DRS buffer. The curve
/// buffer carries points only: its delta array extends past 80 entries (0xB84 is
/// point 80's entry base, not a memory region), so a memory value there makes the
/// driver refuse the whole call (measured 2026-11-16). A wrong offset here writes
/// another field of the struct, which the driver accepts silently — so the bytes
/// are pinned, not the shape.
/// </summary>
public sealed class NvApiGpuTuningBufferTests
{
    private const int BufferSize = 0x2420;

    [Fact]
    public void ClockBoostTable_VersionWord_AndMask_AndDeltas()
    {
        byte[] buffer = NvApiGpuTuning.BuildClockBoostTableBuffer(
            new Dictionary<int, int> { [5] = 100, [70] = -30 },
            out int appliedPoints);

        Assert.Equal(BufferSize, buffer.Length);
        Assert.Equal(2, appliedPoints);

        // version (v1, size) at 0x00, little-endian
        Assert.Equal(new byte[] { 0x20, 0x24, 0x01, 0x00 }, buffer[..4]);

        // mask bitmap: one bit per point index, byte 0x04 + idx/8
        Assert.Equal(0x20, buffer[0x04 + (5 / 8)]);   // point 5  -> bit 5
        Assert.Equal(0x40, buffer[0x04 + (70 / 8)]);  // point 70 -> bit 6

        // frequency delta in kHz at 0x44 + idx*0x24 + 0x14
        Assert.Equal(new byte[] { 0xA0, 0x86, 0x01, 0x00 }, buffer.AsSpan(DeltaOffset(5), 4).ToArray());
        Assert.Equal(new byte[] { 0xD0, 0x8A, 0xFF, 0xFF }, buffer.AsSpan(DeltaOffset(70), 4).ToArray());
    }

    [Fact]
    public void ClockBoostTable_SkipsIndicesOutsideTheUsableRange_WritesNothingElse()
    {
        // 127 is the app's UsableVfPointCount; a negative or oversized index would
        // land in another field of the struct, so it is skipped, not clamped. The
        // whole buffer is compared, so a stray mask bit or a wrapped delta fails.
        byte[] expected = new byte[BufferSize];
        new byte[] { 0x20, 0x24, 0x01, 0x00 }.CopyTo(expected, 0);
        expected[0x04] = 0x20;                                     // point 5 only
        BitConverter.GetBytes(50_000).CopyTo(expected, DeltaOffset(5));

        byte[] buffer = NvApiGpuTuning.BuildClockBoostTableBuffer(
            new Dictionary<int, int> { [-1] = 100, [127] = 100, [200] = 100, [5] = 50 },
            out int appliedPoints);

        Assert.Equal(1, appliedPoints);
        Assert.Equal(expected, buffer);
    }

    [Fact]
    public void ClockBoostTable_WritesNoMemoryRegion()
    {
        // What the field log showed: a memory value written into this buffer (its old
        // memFilled/memDeltas guesses at 0xB84/0xBE0) made setControl(0x0733E009) answer
        // 0xFFFFFFFF and refuse the whole curve. Those offsets lie inside the point delta
        // array, which extends past 80 entries, so the buffer must carry the points only.
        byte[] buffer = NvApiGpuTuning.BuildClockBoostTableBuffer(
            new Dictionary<int, int> { [5] = 100 },
            out _);

        Assert.Equal(100_000, BitConverter.ToInt32(buffer, DeltaOffset(5)));

        for (int offset = 0x44; offset < 0xC00; offset += 4)
        {
            if (offset == DeltaOffset(5))
                continue;
            Assert.Equal(0, BitConverter.ToInt32(buffer, offset));
        }
    }

    [Fact]
    public void ClockBoostTable_PointEighty_IsInsideTheDeltaArray()
    {
        // 0x44 + 80 * 0x24 = 0xB84: the base of point 80's entry, so a delta there is a
        // legitimate curve value - and a memory value there is a corrupted curve point.
        byte[] buffer = NvApiGpuTuning.BuildClockBoostTableBuffer(
            new Dictionary<int, int> { [80] = 200, [126] = 75 },
            out int appliedPoints);

        Assert.Equal(2, appliedPoints);
        Assert.Equal(0xB84 + 0x14, DeltaOffset(80));
        Assert.Equal(200_000, BitConverter.ToInt32(buffer, DeltaOffset(80)));
        Assert.Equal(75_000, BitConverter.ToInt32(buffer, DeltaOffset(126)));
    }

    [Fact]
    public void MemoryClockState_CarriesTheMemoryDelta_AndNeverACoreOffset()
    {
        // The curve apply's companion write. The Memory entry carries the delta; the core is
        // never given an offset here - the curve owns it (the wrapper may serialize the
        // struct's zero-valued Graphics slot alongside, which is not an offset).
        var state = NvApiGpuTuning.BuildMemoryClockState(500);

        var entries = state.Clocks[PerformanceStateId.P0_3DPerformance];

        Assert.Contains(entries, e =>
            e.DomainId == PublicClockDomain.Memory && e.FrequencyDeltaInkHz.DeltaValue == 500_000);
        Assert.All(
            entries.Where(e => e.DomainId != PublicClockDomain.Memory),
            e => Assert.Equal(0, e.FrequencyDeltaInkHz.DeltaValue));
    }

    [Fact]
    public void MemoryClockState_KeepsANegativeOffsetAsANegativeDelta()
    {
        var state = NvApiGpuTuning.BuildMemoryClockState(-300);

        Assert.Contains(state.Clocks[PerformanceStateId.P0_3DPerformance], e =>
            e.DomainId == PublicClockDomain.Memory && e.FrequencyDeltaInkHz.DeltaValue == -300_000);
    }

    [Fact]
    public void CurveReadback_CountsOnlyTheDeltasThatStillReadAsWritten()
    {
        // The survival witness: a table carrying one of the two applied deltas is 1/2,
        // never "as written". Out-of-range indices are not part of the denominator.
        byte[] table = NvApiGpuTuning.BuildClockBoostTableBuffer(
            new Dictionary<int, int> { [5] = 100, [70] = -30 },
            out _);
        var requested = new Dictionary<int, int> { [5] = 100, [70] = -30, [200] = 90 };

        Assert.Equal(2, NvApiGpuTuning.CountMatchingDeltas(table, requested));

        // the curve write erased point 70: only point 5 still reads as applied
        BitConverter.GetBytes(0).CopyTo(table, DeltaOffset(70));
        Assert.Equal(1, NvApiGpuTuning.CountMatchingDeltas(table, requested));
    }

    [Fact]
    public void ClockBoostReadTable_UsesTheAcceptedEightByteMaskWindow()
    {
        // Measured 2026-11-16: 4- and 8-byte mask spans answer 0x00000000 and are echoed,
        // a 32-byte 0xFF answers 0xFFFFFFFF. The mask field is 16 bytes (0x04..0x13), so 32
        // also stamped unknown1 at 0x14..0x23. Reads walk 64-point windows.
        byte[] first = NvApiGpuTuning.BuildClockBoostReadTable(0);
        Assert.Equal(BufferSize, first.Length);
        for (int i = 0; i < 8; i++)
            Assert.Equal(0xFF, first[0x04 + i]);
        for (int i = 8; i < 32; i++)
            Assert.Equal(0, first[0x04 + i]);   // no 0xFF into 0x0C..0x13 or unknown1

        byte[] second = NvApiGpuTuning.BuildClockBoostReadTable(64);
        for (int i = 0; i < 8; i++)
            Assert.Equal(0, second[0x04 + i]);
        for (int i = 8; i < 15; i++)
            Assert.Equal(0xFF, second[0x04 + i]);
        Assert.Equal(0x7F, second[0x04 + 15]);  // points 120..126; index 127 does not exist
        for (int i = 16; i < 32; i++)
            Assert.Equal(0, second[0x04 + i]);

        // together the two windows select every usable point and nothing else
        int bits = 0;
        for (int i = 0; i < NvApiGpuTuning.ClockBoostMaskBytes; i++)
            bits += System.Numerics.BitOperations.PopCount((uint)(first[0x04 + i] | second[0x04 + i]));
        Assert.Equal(NvApiGpuTuning.UsableVfPointCount, bits);
    }

    [Fact]
    public void ClockBoostWriteTable_ReadModifyWrite_PreservesEverythingButTheMaskAndItsDeltas()
    {
        // The point of the read: the driver's table comes back, the deltas this preset sets
        // are overwritten in it, and nothing else moves - unknown1 (0x14..0x23), the fields
        // we do not understand, and the deltas of points this preset leaves alone.
        byte[] current = NvApiGpuTuning.BuildClockBoostReadTable(0);
        current[0x20] = 0x5A;
        current[0x30] = 0xA5;
        BitConverter.GetBytes(75_000).CopyTo(current, DeltaOffset(70));   // left alone
        BitConverter.GetBytes(-20_000).CopyTo(current, DeltaOffset(5));   // overwritten

        byte[] written = NvApiGpuTuning.PrepareClockBoostWriteTable(
            current,
            new Dictionary<int, int> { [5] = 100 },
            out int applied);

        Assert.Equal(1, applied);
        Assert.Equal(new byte[] { 0x20, 0x24, 0x01, 0x00 }, written[..4]);
        Assert.Equal(0x5A, written[0x20]);
        Assert.Equal(0xA5, written[0x30]);
        Assert.Equal(75_000, BitConverter.ToInt32(written, DeltaOffset(70)));
        Assert.Equal(100_000, BitConverter.ToInt32(written, DeltaOffset(5)));

        // this write's mask is point 5 only: the read windows' 0xFF bytes must not survive
        Assert.Equal(0x20, written[0x04]);
        for (int i = 1; i < NvApiGpuTuning.ClockBoostMaskBytes; i++)
            Assert.Equal(0, written[0x04 + i]);
    }

    [Fact]
    public void ClockBoostWriteTable_RefusedRead_FallsBackToTheFromZeroTable()
    {
        // A refused getControl has no table to preserve: the write is the old from-zero one,
        // and the driver method logs that it is a fallback (asserted in the source test).
        var offsets = new Dictionary<int, int> { [5] = 100, [70] = -30 };

        Assert.Equal(
            NvApiGpuTuning.BuildClockBoostTableBuffer(offsets, out _),
            NvApiGpuTuning.PrepareClockBoostWriteTable(null, offsets, out _));
    }

    [Fact]
    public void DrsBuffer_IsSixtyFourBytes_WithTheGpuTypeAtOffsetZero()
    {
        foreach (DrsGpuPreference preference in Enum.GetValues<DrsGpuPreference>())
        {
            byte[] buffer = NvApiGpuTuning.BuildDrsPreferenceBuffer(preference);

            Assert.Equal(64, buffer.Length);
            Assert.Equal((int)preference, buffer[0]);
            Assert.Equal(0, buffer[3]);
            Assert.Equal(0, buffer[63]);
        }
    }

    [Fact]
    public void SetClocks_OutOfRangeOffsets_AreRefusedWithoutTouchingNvapi()
    {
        // The app's slider range is the gate, checked before any NVAPI call: a
        // refusal must say so (Succeeded=false), never read as a landed write.
        var tuning = new NvApiGpuTuning();

        GpuTuningResultDto high = tuning.SetClocks(4001, 0);
        Assert.False(high.Succeeded);
        Assert.Equal(-1, high.Status);
        Assert.Contains("4000", high.Detail, StringComparison.Ordinal);

        GpuTuningResultDto low = tuning.SetClocks(0, -901);
        Assert.False(low.Succeeded);
        Assert.Equal(-1, low.Status);
        Assert.Contains("-900", low.Detail, StringComparison.Ordinal);
    }

    private static int DeltaOffset(int pointIndex) => 0x44 + (pointIndex * 0x24) + 0x14;
}
