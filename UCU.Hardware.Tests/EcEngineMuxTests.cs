using UCU.Core.Ec;
using UCU.Hardware.Ec;

namespace UCU.Hardware.Tests;

/// <summary>
/// GPU MUX writes: the encoding is platform-dependent and the write persists in the EC
/// until the next restart, so a wrong byte is a fault the user cannot undo from the
/// app. These tests pin the platform mapping at the point where it reaches hardware.
/// </summary>
public sealed class EcEngineMuxTests
{
    private static (EcEngine Engine, FakeEcTransport Transport) CreateEngine(EcPlatform platform, byte initialStatus)
    {
        var transport = new FakeEcTransport();
        transport.Preset(EcEngine.REG_GPU_STATUS, initialStatus);
        var engine = new EcEngine(transport, platform);
        engine.Initialize();
        return (engine, transport);
    }

    [Theory]
    [InlineData(GpuMuxMode.Hybrid, 0x00)]
    [InlineData(GpuMuxMode.DgpuOnly, 0x01)]
    [InlineData(GpuMuxMode.IgpuOnly, 0x02)]
    public void Amd_WritesTheNaturalRawByte(GpuMuxMode mode, byte expectedRaw)
    {
        var (engine, transport) = CreateEngine(EcPlatform.Amd, 0xFF);

        engine.SetGpuMuxMode(mode);

        Assert.Equal($"0x072A=0x{expectedRaw:X2}", transport.SequenceOf(EcEngine.REG_GPU_STATUS));
    }

    [Theory]
    [InlineData(GpuMuxMode.Hybrid, 0x04)]
    [InlineData(GpuMuxMode.DgpuOnly, 0x02)]
    [InlineData(GpuMuxMode.IgpuOnly, 0x01)]
    public void Intel_WritesTheBiosEncoding_WhichIsNotTheAmdOne(GpuMuxMode mode, byte expectedRaw)
    {
        var (engine, transport) = CreateEngine(EcPlatform.Intel, 0xFF);

        engine.SetGpuMuxMode(mode);

        Assert.Equal($"0x072A=0x{expectedRaw:X2}", transport.SequenceOf(EcEngine.REG_GPU_STATUS));
    }

    [Theory]
    [InlineData(EcPlatform.Amd)]
    [InlineData(EcPlatform.Intel)]
    public void AnUndefinedModeIsRefusedBeforeAnyWrite(EcPlatform platform)
    {
        // The mode arrives from a pipe verb and System.Text.Json accepts an undefined
        // numeric value. Raw 7 would persist in the EC across a reboot and can leave
        // the panel with no GPU; the write must never reach the transport.
        var (engine, transport) = CreateEngine(platform, 0xFF);

        Assert.Throws<ArgumentOutOfRangeException>(() => engine.SetGpuMuxMode((GpuMuxMode)7));

        Assert.Empty(transport.WritesAt(EcEngine.REG_GPU_STATUS));
    }

    [Fact]
    public void WriteIsSkippedWhenTheEcIsAlreadyAtTheTargetMode()    {
        var (engine, transport) = CreateEngine(EcPlatform.Amd, 0x01);

        engine.SetGpuMuxMode(GpuMuxMode.DgpuOnly);

        Assert.Empty(transport.WritesAt(EcEngine.REG_GPU_STATUS));
    }

    [Fact]
    public void Intel_SkipsTheWriteWhenTheRawByteAlreadyMatchesItsOwnEncoding()
    {
        // Raw 2 is dGPU-only on Intel and iGPU-only on AMD, so the skip decision is
        // made with the platform's own table: an engine built for the wrong platform
        // would skip a needed write, or issue the wrong one.
        var (engine, transport) = CreateEngine(EcPlatform.Intel, 0x02);

        engine.SetGpuMuxMode(GpuMuxMode.DgpuOnly);

        Assert.Empty(transport.WritesAt(EcEngine.REG_GPU_STATUS));
    }

    [Theory]
    [InlineData(EcPlatform.Amd, 0x01, GpuMuxMode.DgpuOnly)]
    [InlineData(EcPlatform.Amd, 0x02, GpuMuxMode.IgpuOnly)]
    [InlineData(EcPlatform.Intel, 0x04, GpuMuxMode.Hybrid)]
    [InlineData(EcPlatform.Intel, 0x01, GpuMuxMode.IgpuOnly)]
    [InlineData(EcPlatform.Intel, 0x02, GpuMuxMode.DgpuOnly)]
    public void GetCurrentGpuMuxMode_DecodesThroughThePlatform(EcPlatform platform, byte raw, GpuMuxMode expected)
    {
        var (engine, _) = CreateEngine(platform, raw);

        Assert.Equal(expected, engine.GetCurrentGpuMuxMode());
    }

    [Fact]
    public void ModeToRawAndRawToMode_AreInversePerPlatform()
    {
        var (engine, _) = CreateEngine(EcPlatform.Intel, 0x00);

        foreach (GpuMuxMode mode in Enum.GetValues<GpuMuxMode>())
            Assert.Equal(mode, engine.RawToMode(engine.ModeToRaw(mode)));
    }

    [Fact]
    public void ModeEncoding_MatchesTheCoreCodecForTheEnginePlatform()
    {
        var (intel, _) = CreateEngine(EcPlatform.Intel, 0x00);
        var (amd, _) = CreateEngine(EcPlatform.Amd, 0x00);

        // The UI encodes NVRAM bytes with the engine's helpers, the firmware writes go
        // through the codec: they must be the same function of the platform.
        Assert.Equal(EcMuxCodec.ModeToRaw(GpuMuxMode.Hybrid, EcPlatform.Intel), intel.ModeToRaw(GpuMuxMode.Hybrid));
        Assert.Equal(EcMuxCodec.ModeToRaw(GpuMuxMode.Hybrid, EcPlatform.Amd), amd.ModeToRaw(GpuMuxMode.Hybrid));
    }
}
