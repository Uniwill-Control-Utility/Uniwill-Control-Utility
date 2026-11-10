using UCU.Contracts;
using UCU.Hardware.Gpu;

namespace UCU.Hardware.Tests;

/// <summary>
/// Golden bytes for the two NVAPI buffers. The layouts are the app-side working
/// path's (Scripts/GPUs/NVIDIA/NvTuning.cs and GpuPowerSaving.cs), moved
/// engine-side: the version word, the point mask at 0x04, the kHz delta at
/// 0x44 + idx*0x24 + 0x14, and the GPU type as a uint32 at offset 0 of the
/// 64-byte DRS buffer. A wrong offset here writes another field of the struct,
/// which the driver accepts silently — so the bytes are pinned, not the shape.
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
