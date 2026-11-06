using UCU.Core.Ec;
using UCU.Hardware.Ec;

namespace UCU.Hardware.Tests;

/// <summary>
/// Golden write-sequence tests for the fan-curve path: the exact bytes the EC must
/// receive for the firmware to run a custom curve, in the order it must receive them.
///
/// These are the phase-1/2 safety net. The encoders moved to UCU.Core and the driver
/// access moved behind <see cref="IEcTransport"/>; a byte-level regression in that
/// move is invisible to the UI (the fans simply spin at the wrong speed) and shows up
/// here instead.
/// </summary>
public sealed class EcEngineFanCurveTests
{
    private static readonly int[] BalancedDuties = { 0, 30, 32, 36, 40, 47, 55, 62, 72, 84, 100 };
    private static readonly int[] SilentDuties = { 0, 20, 25, 30, 38, 43, 48, 53, 58, 58, 58 };

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
    public void CpuCurve_WritesTheExactTempUpTempDownAndDutyTables()
    {
        var (engine, transport) = CreateEngine();

        engine.ApplyFanCurve(new EcFanCurveSpec("Balanced", BalancedDuties), new EcFanCurveSpec("Silent", SilentDuties));

        Assert.Equal(
            new byte[] { 0, 56, 61, 66, 71, 76, 81, 86, 91, 96, 98, 255, 255, 255, 255, 255 },
            transport.TableAt(EcEngine.REG_CPU_TEMP_END_TABLE));
        Assert.Equal(
            new byte[] { 0, 54, 59, 64, 69, 74, 79, 84, 89, 94, 96, 255, 255, 255, 255, 255 },
            transport.TableAt(EcEngine.REG_CPU_TEMP_START_TABLE));
        Assert.Equal(
            new byte[] { 0, 60, 64, 72, 80, 94, 110, 124, 144, 168, 200, 200, 200, 200, 200, 200 },
            transport.TableAt(EcEngine.REG_CPU_FAN_SPEED_TABLE));
    }

    [Fact]
    public void GpuCurve_WritesItsOwnTablesOnTheGpuRegisters()
    {
        var (engine, transport) = CreateEngine();

        engine.ApplyFanCurve(new EcFanCurveSpec("Balanced", BalancedDuties), new EcFanCurveSpec("Silent", SilentDuties));

        Assert.Equal(
            new byte[] { 0, 40, 50, 60, 76, 86, 96, 106, 116, 116, 116, 116, 116, 116, 116, 116 },
            transport.TableAt(EcEngine.REG_GPU_FAN_SPEED_TABLE));
        Assert.Equal(
            new byte[] { 0, 56, 61, 66, 71, 76, 81, 86, 91, 96, 98, 255, 255, 255, 255, 255 },
            transport.TableAt(EcEngine.REG_GPU_TEMP_END_TABLE));
    }

    [Fact]
    public void CpuTablesAreWrittenBeforeGpuTables()
    {
        // The EC shares an internal buffer between the two fan-table regions; writing
        // them back to back drops the GPU writes, which is why the sequence is ordered
        // and separated by a delay.
        var (engine, transport) = CreateEngine();

        engine.ApplyFanCurve(new EcFanCurveSpec("Balanced", BalancedDuties), new EcFanCurveSpec("Silent", SilentDuties));

        Assert.True(transport.LastWriteIndex(EcEngine.REG_CPU_FAN_SPEED_TABLE) <
                    transport.FirstWriteIndex(EcEngine.REG_GPU_TEMP_END_TABLE));
    }

    [Fact]
    public void EnableCustomBitIsToggledAfterTheTablesSoTheEcReReadsThem()
    {
        var (engine, transport) = CreateEngine();

        engine.ApplyFanCurve(new EcFanCurveSpec("Balanced", BalancedDuties), new EcFanCurveSpec("Silent", SilentDuties));

        IReadOnlyList<byte> oem6 = transport.WritesAt(EcEngine.REG_AP_OEM_6);
        Assert.Equal(new byte[] { 0x00, 0x04 }, oem6.ToArray());
    }

    [Fact]
    public void SplitTablesBitIsSetSoTheEcUsesTheCustomTables()
    {
        var (engine, transport) = CreateEngine();

        engine.ApplyFanCurve(new EcFanCurveSpec("Balanced", BalancedDuties), new EcFanCurveSpec("Silent", SilentDuties));

        Assert.NotEqual(0, (int)(transport.Read(EcEngine.REG_UNIVERSAL_FAN_CTRL) & EcEngine.SPLIT_TABLES));
    }

    [Fact]
    public void BoostModeIsClearedWhenACustomCurveTakesOver()
    {
        var (engine, transport) = CreateEngine();
        transport.Preset(EcEngine.REG_MANUAL_FAN_CTRL, EcEngine.FAN_MODE_BOOST);

        engine.ApplyFanCurve(new EcFanCurveSpec("Balanced", BalancedDuties), new EcFanCurveSpec("Silent", SilentDuties));

        Assert.Equal((byte)0x00, transport.LastWriteAt(EcEngine.REG_MANUAL_FAN_CTRL)!.Value);
    }

    [Fact]
    public void ManualModeIsUnlockedWithoutDroppingTheFirmwareBootBit()
    {
        // The bisect in 2026-10-11: a literal write to 0x0741 that cleared bit2 froze
        // the Performance button. The unlock has to be read-modify-write.
        var (engine, transport) = CreateEngine();
        transport.Preset(EcEngine.REG_AP_OEM, EcEngine.AP_OEM_FIRMWARE_BOOT_BIT);

        engine.ApplyFanCurve(new EcFanCurveSpec("Balanced", BalancedDuties), new EcFanCurveSpec("Silent", SilentDuties));

        Assert.Equal(
            (byte)(EcEngine.MANUAL_MODE_ENABLE | EcEngine.AP_OEM_FIRMWARE_BOOT_BIT),
            transport.FirstWriteAt(EcEngine.REG_AP_OEM));
    }

    [Fact]
    public void ManualModeIsNotRewrittenWhenTheEcIsAlreadyUnlocked()
    {
        var (engine, transport) = CreateEngine();
        transport.Preset(EcEngine.REG_AP_OEM, EcEngine.MANUAL_MODE_ENABLE | EcEngine.AP_OEM_FIRMWARE_BOOT_BIT);

        engine.ApplyFanCurve(new EcFanCurveSpec("Balanced", BalancedDuties), new EcFanCurveSpec("Silent", SilentDuties));

        Assert.Empty(transport.WritesAt(EcEngine.REG_AP_OEM));
    }

    [Fact]
    public void CustomTemperatureSteps_UseOneDegreeHysteresisAndZeroForTheOffZone()
    {
        var (engine, transport) = CreateEngine();
        int[] steps = { 0, 50, 60, 70, 80, 90, 95, 99 };

        engine.ApplyFanCurve(
            new EcFanCurveSpec("Balanced", BalancedDuties),
            new EcFanCurveSpec("Silent", SilentDuties),
            steps,
            steps);

        Assert.Equal(
            new byte[] { 0, 51, 61, 71, 81, 91, 96, 100, 255, 255, 255, 255, 255, 255, 255, 255 },
            transport.TableAt(EcEngine.REG_CPU_TEMP_END_TABLE));
        Assert.Equal(
            new byte[] { 0, 49, 59, 69, 79, 89, 94, 98, 255, 255, 255, 255, 255, 255, 255, 255 },
            transport.TableAt(EcEngine.REG_CPU_TEMP_START_TABLE));
    }

    [Fact]
    public void ResetFanState_LeavesTheFansAtFullSpeed()
    {
        // The escape hatch for a curve the firmware misbehaves with: safe tables at the
        // top of the duty range, so a failed experiment never leaves the fans idle.
        var (engine, transport) = CreateEngine();

        engine.ResetFanState();

        byte[] duty = transport.TableAt(EcEngine.REG_CPU_FAN_SPEED_TABLE);
        Assert.Equal(0, duty[0]);
        Assert.Equal(200, duty[9]);
        Assert.Equal(180, duty[15]);
    }
}
