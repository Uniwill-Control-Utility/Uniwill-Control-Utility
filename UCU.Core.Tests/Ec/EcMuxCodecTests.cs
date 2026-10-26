using UCU.Core.Ec;

namespace UCU.Core.Tests.Ec;

public sealed class EcMuxCodecTests
{
    [Theory]
    [InlineData(GpuMuxMode.Hybrid, 0x00)]
    [InlineData(GpuMuxMode.DgpuOnly, 0x01)]
    [InlineData(GpuMuxMode.IgpuOnly, 0x02)]
    public void ModeToRaw_Amd_UsesTheNaturalByteOrder(GpuMuxMode mode, byte expected)
    {
        Assert.Equal(expected, EcMuxCodec.ModeToRaw(mode, EcPlatform.Amd));
    }

    [Theory]
    [InlineData(GpuMuxMode.Hybrid, 0x04)]
    [InlineData(GpuMuxMode.DgpuOnly, 0x02)]
    [InlineData(GpuMuxMode.IgpuOnly, 0x01)]
    public void ModeToRaw_Intel_EncodesHybridAsDynamic_AndSwapsTheDirectModes(GpuMuxMode mode, byte expected)
    {
        Assert.Equal(expected, EcMuxCodec.ModeToRaw(mode, EcPlatform.Intel));
    }

    [Theory]
    [InlineData(GpuMuxMode.Hybrid, EcPlatform.Amd)]
    [InlineData(GpuMuxMode.DgpuOnly, EcPlatform.Amd)]
    [InlineData(GpuMuxMode.IgpuOnly, EcPlatform.Amd)]
    [InlineData(GpuMuxMode.Hybrid, EcPlatform.Intel)]
    [InlineData(GpuMuxMode.DgpuOnly, EcPlatform.Intel)]
    [InlineData(GpuMuxMode.IgpuOnly, EcPlatform.Intel)]
    public void RoundTrip_SemanticToRawToSemantic(GpuMuxMode mode, EcPlatform platform)
    {
        byte raw = EcMuxCodec.ModeToRaw(mode, platform);
        Assert.Equal(mode, EcMuxCodec.RawToMode(raw, platform));
        Assert.True(EcMuxCodec.IsKnownRaw(raw, platform));
    }

    [Theory]
    [InlineData(0x00, GpuMuxMode.Hybrid)]
    [InlineData(0x01, GpuMuxMode.DgpuOnly)]
    [InlineData(0x02, GpuMuxMode.IgpuOnly)]
    public void RawToMode_Amd_IsTheIdentity(byte raw, GpuMuxMode expected)
    {
        Assert.Equal(expected, EcMuxCodec.RawToMode(raw, EcPlatform.Amd));
    }

    [Theory]
    [InlineData(0x04, GpuMuxMode.Hybrid)]
    [InlineData(0x01, GpuMuxMode.IgpuOnly)]
    [InlineData(0x02, GpuMuxMode.DgpuOnly)]
    public void RawToMode_Intel_DecodesDynamicAndTheSwappedPair(byte raw, GpuMuxMode expected)
    {
        Assert.Equal(expected, EcMuxCodec.RawToMode(raw, EcPlatform.Intel));
    }

    [Fact]
    public void RawToMode_UnknownByte_FallsThroughToItsNumericValue()
    {
        // Historical read behaviour: an unmodelled firmware value is surfaced, not
        // swallowed. Callers report it through IsKnownRaw.
        Assert.Equal((GpuMuxMode)0x07, EcMuxCodec.RawToMode(0x07, EcPlatform.Amd));
        Assert.False(EcMuxCodec.IsKnownRaw(0x07, EcPlatform.Amd));
    }

    [Fact]
    public void IsKnownRaw_DistinguishesThePlatformByteSets()
    {
        Assert.True(EcMuxCodec.IsKnownRaw(0x00, EcPlatform.Amd));
        Assert.False(EcMuxCodec.IsKnownRaw(0x00, EcPlatform.Intel));
        Assert.True(EcMuxCodec.IsKnownRaw(0x04, EcPlatform.Intel));
        Assert.False(EcMuxCodec.IsKnownRaw(0x04, EcPlatform.Amd));
    }

    [Fact]
    public void SemanticValues_MatchTheHistoricalNestedAppEnum()
    {
        // The app declares its own nested GpuMuxMode; the wire contract uses this one.
        // A drift in these numbers would silently swap dGPU and iGPU on Intel.
        Assert.Equal(0x00, (int)GpuMuxMode.Hybrid);
        Assert.Equal(0x01, (int)GpuMuxMode.DgpuOnly);
        Assert.Equal(0x02, (int)GpuMuxMode.IgpuOnly);
    }
}
