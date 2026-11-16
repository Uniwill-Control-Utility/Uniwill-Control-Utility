using Uniwill_Control_Utility.Scripts.GPUs.NVIDIA;

namespace Uniwill_Control_Utility.Tests.Services;

/// <summary>
/// The curve buffer carries points and nothing else. The field defect (2026-11-16) was
/// a memory value written at 0xB84: that offset is GPU point 80's entry base - the delta
/// array extends past 80 entries - so the driver refused the whole setControl(0x0733E009)
/// call and the curve never landed. The memory offset is a separate, memory-only pstate
/// write applied after the curve.
/// </summary>
public sealed class GpuCurveMemoryOffsetTests
{
    private const int BufferSize = 0x2420;
    private const int PointEightyEntryBase = 0xB84; // 0x44 + 80 entries * 0x24

    [Fact]
    public void LocalFallbackBuffer_CarriesThePoints_AndNoMemoryRegion()
    {
        // The engine-less path: an elevated dev run, or a machine whose engine is not
        // answering. Its own buffer must carry the point deltas and stop there.
        byte[] buffer = NvTuning.BuildClockBoostTableBuffer(
            new Dictionary<int, int> { [72] = 150 });

        Assert.Equal(BufferSize, buffer.Length);
        Assert.Equal(150_000, BitConverter.ToInt32(buffer, 0x44 + (72 * 0x24) + 0x14));

        // the old memFilled/memDeltas guesses are inside the point delta array, so they
        // must stay zero: 0xB84 and 0xBE0 are point-curve bytes, not a memory region
        Assert.Equal(0, BitConverter.ToInt32(buffer, PointEightyEntryBase));
        Assert.Equal(0, BitConverter.ToInt32(buffer, 0xBE0));
    }

    [Fact]
    public void LocalFallbackBuffer_PointEighty_IsAPointDelta()
    {
        // A point past the old "80 entries" comment must still be writable: the driver
        // accepts it, and refusing it would leave a curve point unset.
        byte[] buffer = NvTuning.BuildClockBoostTableBuffer(
            new Dictionary<int, int> { [80] = 200, [126] = 75 });

        Assert.Equal(200_000, BitConverter.ToInt32(buffer, PointEightyEntryBase + 0x14));
        Assert.Equal(75_000, BitConverter.ToInt32(buffer, 0x44 + (126 * 0x24) + 0x14));
    }

    [Fact]
    public void LocalFallbackReadModifyWrite_KeepsBytesOutsideTheDeltas()
    {
        // The local path reads the driver's table first (mask windowed, never the refused
        // 32-byte 0xFF) and applies its deltas into that copy: unknown fields and the deltas
        // of points this preset leaves alone must come back out unchanged.
        byte[] current = NvTuning.BuildClockBoostTableBuffer(new Dictionary<int, int>());
        current[0x20] = 0x5A;
        BitConverter.GetBytes(75_000).CopyTo(current, 0x44 + (70 * 0x24) + 0x14);

        byte[] written = NvTuning.PrepareClockBoostWriteTable(
            current, new Dictionary<int, int> { [5] = 50 });

        Assert.Equal(0x5A, written[0x20]);
        Assert.Equal(75_000, BitConverter.ToInt32(written, 0x44 + (70 * 0x24) + 0x14));
        Assert.Equal(50_000, BitConverter.ToInt32(written, 0x44 + (5 * 0x24) + 0x14));

        // point 5 only: the read windows' 0xFF bytes must not survive into this write
        Assert.Equal(0x20, written[0x04]);
        for (int i = 1; i < NvTuning.ClockBoostMaskBytes; i++)
            Assert.Equal(0, written[0x04 + i]);
    }

    [Fact]
    public void LocalFallbackBuffer_WritesNothingOutsideTheMaskAndTheDeltas()
    {
        // Every apply builds a fresh zero buffer; a stray write anywhere else in the
        // 0x2420 bytes is a corrupted struct field the driver may accept silently.
        byte[] expected = new byte[BufferSize];
        new byte[] { 0x20, 0x24, 0x01, 0x00 }.CopyTo(expected, 0);
        expected[0x04] = 0x20; // point 5
        BitConverter.GetBytes(50_000).CopyTo(expected, 0x44 + (5 * 0x24) + 0x14);

        byte[] buffer = NvTuning.BuildClockBoostTableBuffer(
            new Dictionary<int, int> { [-1] = 100, [127] = 100, [5] = 50 });

        Assert.Equal(expected, buffer);
    }
}
