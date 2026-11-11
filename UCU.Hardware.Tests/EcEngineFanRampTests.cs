using UCU.Core.Ec;
using UCU.Hardware.Ec;

namespace UCU.Hardware.Tests;

/// <summary>
/// Register 0x0787 is ONE byte: bit 7 = enable, bits 0-6 = ramp time in 100 ms steps.
/// It is not a u16 RPM value.
///
/// Measured on X6FR5xxY (EC 116.0) 2026-11-10: writes of 0x83 and 0x8F echo back
/// exactly and leave 0x0788 untouched. The previous u16 form wrote its high byte into
/// 0x0788, which is the EC's cTGP offset (NVML enforced TGP minus 95), not a limit.
/// These tests pin both halves: the encoding, and that the neighbour is never written.
/// </summary>
public sealed class EcEngineFanRampTests
{
    /// <summary>0x0788 — the register a u16 write would have corrupted.</summary>
    private const ushort NextRegister = 0x0788;

    private static (EcEngine Engine, FakeEcTransport Transport) CreateEngine()
    {
        var transport = new FakeEcTransport();
        var engine = new EcEngine(transport, EcPlatform.Amd);
        engine.Initialize();
        engine.InitializeEc();
        transport.ClearLog();
        return (engine, transport);
    }

    [Theory]
    [InlineData(100, 0x81)]
    [InlineData(300, 0x83)]
    [InlineData(1500, 0x8F)]
    [InlineData(12700, 0xFF)]
    public void Ramp_EncodesEnablePlusHundredMillisecondSteps_InOneByte(int milliseconds, byte expected)
    {
        var (engine, transport) = CreateEngine();

        engine.SetFanSwitchSpeed(milliseconds);

        Assert.Equal(new byte[] { expected }, transport.WritesAt(EcEngine.REG_FAN_SWITCH_SPEED));
        Assert.Empty(transport.WritesAt(NextRegister));
    }

    [Fact]
    public void Ramp_ZeroDisablesTheRegister()
    {
        var (engine, transport) = CreateEngine();

        engine.SetFanSwitchSpeed(0);

        Assert.Equal(new byte[] { 0x00 }, transport.WritesAt(EcEngine.REG_FAN_SWITCH_SPEED));
        Assert.Empty(transport.WritesAt(NextRegister));
    }

    [Theory]
    [InlineData(150)]    // not a whole 100 ms step
    [InlineData(99)]     // below the encoded minimum
    [InlineData(12800)]  // above the 7-bit field
    [InlineData(-100)]
    public void Ramp_RejectsValuesTheFieldCannotRepresent_WithoutTouchingTheEc(int milliseconds)
    {
        var (engine, transport) = CreateEngine();

        Assert.Throws<ArgumentOutOfRangeException>(() => engine.SetFanSwitchSpeed(milliseconds));

        Assert.Empty(transport.Writes);
    }
}
