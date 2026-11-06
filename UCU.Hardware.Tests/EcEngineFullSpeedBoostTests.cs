using System.Collections.Generic;
using UCU.Core.Ec;
using UCU.Hardware.Ec;

namespace UCU.Hardware.Tests;

/// <summary>
/// The Full Speed preset has to assert the EC's hardware FanBoost bit, not just
/// ask for 100 % duty in the curve. A duty table is temperature-gated: the EC
/// picks the zone from the temperature first and zone 0 is the zero-RPM standby
/// step the firmware forces to 0, so a 100 % curve alone leaves the fans at their
/// floor at idle - the field bug "Full Speed preset but the fans sit at 26 %"
/// (reproduced on the dev box 2026-11-02; 0x0751 bit6 is what pins them).
/// </summary>
public sealed class EcEngineFullSpeedBoostTests
{
    private static readonly int[] FullSpeedDuties = { 0, 100, 100, 100, 100, 100, 100, 100, 100, 100, 100 };
    private static readonly int[] BalancedDuties = { 0, 30, 32, 36, 40, 47, 55, 62, 72, 84, 100 };

    private static (EcEngine Engine, FakeEcTransport Transport) CreateEngine()
    {
        var transport = new FakeEcTransport();
        var engine = new EcEngine(transport, EcPlatform.Amd);
        engine.Initialize();
        engine.InitializeEc();
        transport.ClearLog();
        return (engine, transport);
    }

    [Fact]
    public void FullSpeed_AssertsHardwareFanBoost()
    {
        var (engine, transport) = CreateEngine();

        engine.ApplyFanCurve(new EcFanCurveSpec("Full Speed", FullSpeedDuties), new EcFanCurveSpec("Full Speed", FullSpeedDuties));

        Assert.NotEqual(0, (int)(transport.Read(EcEngine.REG_MANUAL_FAN_CTRL) & EcEngine.FAN_MODE_BOOST));
    }

    [Fact]
    public void FullSpeed_KeepsTheOtherModeBitsItFound()
    {
        var (engine, transport) = CreateEngine();
        transport.Preset(EcEngine.REG_MANUAL_FAN_CTRL, EcEngine.FAN_MODE_TURBO);

        engine.ApplyFanCurve(new EcFanCurveSpec("Full Speed", FullSpeedDuties), new EcFanCurveSpec("Full Speed", FullSpeedDuties));

        Assert.Equal(
            (byte)(EcEngine.FAN_MODE_TURBO | EcEngine.FAN_MODE_BOOST),
            transport.LastWriteAt(EcEngine.REG_MANUAL_FAN_CTRL)!.Value);
    }

    [Fact]
    public void LeavingFullSpeed_ClearsHardwareFanBoost()
    {
        var (engine, transport) = CreateEngine();
        engine.ApplyFanCurve(new EcFanCurveSpec("Full Speed", FullSpeedDuties), new EcFanCurveSpec("Full Speed", FullSpeedDuties));

        engine.ApplyFanCurve(new EcFanCurveSpec("Balanced", BalancedDuties), new EcFanCurveSpec("Balanced", BalancedDuties));

        Assert.Equal(0, (int)(transport.Read(EcEngine.REG_MANUAL_FAN_CTRL) & EcEngine.FAN_MODE_BOOST));
    }

    [Fact]
    public void OtherPresets_NeverAssertFanBoost()
    {
        var (engine, transport) = CreateEngine();

        engine.ApplyFanCurve(new EcFanCurveSpec("Balanced", BalancedDuties), new EcFanCurveSpec("Silent", BalancedDuties));

        Assert.Equal(0, (int)(transport.Read(EcEngine.REG_MANUAL_FAN_CTRL) & EcEngine.FAN_MODE_BOOST));
    }
}
