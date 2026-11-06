using UCU.Core.Ec;
using UCU.Hardware.Ec;

namespace UCU.Hardware.Tests;

/// <summary>
/// The GPU power-control truth read (U-107): the registers the app's
/// guardian compares its applied contract against. Pins two things: the read
/// answers with the EC's bytes for 0x0743-0x0746 and the 0x0727 custom bit,
/// writes nothing; and it reports exactly what ActivateGpuPowerControl leaves
/// behind (db-enabled AND db-disabled paths) — the parity that makes the
/// guardian's "clean" state reachable, stated at the hardware layer (the
/// Core policy pins the same encoding rules on the decision side).
/// </summary>
public sealed class EcEngineGpuPowerTruthTests
{
    [Fact]
    public void GetGpuPowerControlState_ReadsTheRegisters_WritesNothing()
    {
        var transport = new FakeEcTransport();
        transport.Preset(EcEngine.REG_CTGP_DB_ENABLE, 0x07);
        transport.Preset(EcEngine.REG_CTGP_DB_CTGP_OFFSET, 0x46);
        transport.Preset(EcEngine.REG_CTGP_DB_TPP_OFFSET, 0xFF);
        transport.Preset(EcEngine.REG_CTGP_DB_DB_OFFSET, 0x19);
        transport.Preset(EcEngine.REG_CUSTOM_PROFILE, 0x40); // bit6 set

        using var engine = new EcEngine(transport, EcPlatform.Amd);
        engine.Initialize();
        transport.ClearLog();

        var state = engine.GetGpuPowerControlState();

        Assert.Equal(0x07, state.Enable);
        Assert.Equal(0x46, state.Ctgp);
        Assert.Equal(0xFF, state.Tpp);
        Assert.Equal(0x19, state.Db);
        Assert.True(state.CustomProfileActive);
        Assert.Empty(transport.Writes);
    }

    [Fact]
    public void GetGpuPowerControlState_CustomBitOnly_IgnoresOtherBits()
    {
        var transport = new FakeEcTransport();
        transport.Preset(EcEngine.REG_CUSTOM_PROFILE, 0x01); // other bits set, bit6 clear

        using var engine = new EcEngine(transport, EcPlatform.Amd);
        engine.Initialize();

        Assert.False(engine.GetGpuPowerControlState().CustomProfileActive);
    }

    [Fact]
    public void Activate_DbEnabled_TruthReportsTheActivationContract()
    {
        var transport = new FakeEcTransport();

        using var engine = new EcEngine(transport, EcPlatform.Amd);
        engine.Initialize();

        engine.ActivateGpuPowerControl(ctgpOffset: 70, dbOffset: 25);

        var state = engine.GetGpuPowerControlState();
        Assert.Equal(0x07, state.Enable);   // GENERAL | CTGP | DB
        Assert.Equal(70, state.Ctgp);
        Assert.Equal(255, state.Tpp);       // pool open on the DB-enabled path
        Assert.Equal(25, state.Db);
        Assert.True(state.CustomProfileActive);
    }

    [Fact]
    public void Activate_DbDisabled_TruthReportsNoDbBitAndClosedPool()
    {
        var transport = new FakeEcTransport();

        using var engine = new EcEngine(transport, EcPlatform.Amd);
        engine.Initialize();

        engine.ActivateGpuPowerControl(ctgpOffset: 70, dbOffset: 0);

        var state = engine.GetGpuPowerControlState();
        Assert.Equal(0x05, state.Enable);   // GENERAL | CTGP, no DB chase
        Assert.Equal(70, state.Ctgp);
        Assert.Equal(0, state.Tpp);         // pool closed
        Assert.Equal(0, state.Db);
    }

    [Fact]
    public void Deactivate_TruthReportsClearedEnable()
    {
        var transport = new FakeEcTransport();

        using var engine = new EcEngine(transport, EcPlatform.Amd);
        engine.Initialize();

        engine.ActivateGpuPowerControl(ctgpOffset: 70, dbOffset: 25);
        engine.DeactivateGpuPowerControl();

        var state = engine.GetGpuPowerControlState();
        Assert.Equal(0x00, state.Enable);   // feature off: the guardian's drift signal
    }
}
