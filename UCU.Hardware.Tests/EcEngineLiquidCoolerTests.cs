using UCU.Core.Ec;
using UCU.Hardware.Ec;

namespace UCU.Hardware.Tests;

/// <summary>
/// 0x07C7 bit 1 is the OEM's liquid-cooler-attached (LCSE) flag. Bits 2-4 of the same
/// byte are the EC's OCPL level (3 = normal) and belong to the firmware, so the write
/// must be bit-scoped - MechControl's own init writes the whole byte and clobbers them.
///
/// Measured baseline on X6FR5xxY (EC 116.0) and X6AR558Y (EC 90.0): 0x07A4 = 0x80
/// (liquid cooling supported) and 0x07C7 = 0x0C with no cooler attached.
/// </summary>
public sealed class EcEngineLiquidCoolerTests
{
    private const ushort Support = 0x07A4;
    private const ushort CoolingMode = 0x07C7;

    private static (EcEngine Engine, FakeEcTransport Transport) CreateEngine(
        byte support = 0x80, byte coolingMode = 0x0C)
    {
        var transport = new FakeEcTransport();
        transport.Preset(Support, support);
        transport.Preset(CoolingMode, coolingMode);
        var engine = new EcEngine(transport, EcPlatform.Amd);
        engine.Initialize();
        transport.ClearLog();
        return (engine, transport);
    }

    [Fact]
    public void Attaching_SetsOnlyBit1()
    {
        var (engine, transport) = CreateEngine(coolingMode: 0x0C);

        Assert.True(engine.SetLiquidCoolerAttached(true));

        Assert.Equal(new byte[] { 0x0E }, transport.WritesAt(CoolingMode));
        Assert.Equal(0x0E, transport.Read(CoolingMode));
    }

    [Fact]
    public void Detaching_ClearsOnlyBit1()
    {
        var (engine, transport) = CreateEngine(coolingMode: 0x0E);

        Assert.True(engine.SetLiquidCoolerAttached(false));

        Assert.Equal(new byte[] { 0x0C }, transport.WritesAt(CoolingMode));
    }

    [Fact]
    public void Attaching_PreservesTheFirmwareBitsAroundIt()
    {
        // 0x0D = bit 0 (unused) + OCPL bits 2-3 = 3 (normal). Only bit 1 may change.
        var (engine, transport) = CreateEngine(coolingMode: 0x0D);

        Assert.True(engine.SetLiquidCoolerAttached(true));

        Assert.Equal(new byte[] { 0x0F }, transport.WritesAt(CoolingMode));
    }

    [Fact]
    public void Refuses_WhenTheModelDoesNotDeclareLiquidCooling()
    {
        var (engine, transport) = CreateEngine(support: 0x00);

        Assert.False(engine.SetLiquidCoolerAttached(true));

        Assert.Empty(transport.WritesAt(CoolingMode));
    }

    [Fact]
    public void WritesNothing_WhenTheFlagIsAlreadyInTheRequestedState()
    {
        var (engine, transport) = CreateEngine(coolingMode: 0x0E);

        Assert.True(engine.SetLiquidCoolerAttached(true));

        Assert.Empty(transport.Writes);
    }
}
