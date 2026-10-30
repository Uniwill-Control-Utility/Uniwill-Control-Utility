using UCU.Core.Ec;

namespace UCU.Core.Tests.Ec;

/// <summary>
/// The raw-write gate allowlist (U-102): the domains the raw register verbs may
/// touch, and the OEM NVRAM GUID family. The engine enforces this policy at the
/// verb boundary; these are the boundary cases (ranges, straddles, GUID formats)
/// that the enforcement inherits.
/// </summary>
public sealed class EcWriteGatePolicyTests
{
    // ---- fan-domain tables (the planner's own addresses) ----------------------

    [Theory]
    [InlineData(EcFanRegisters.CpuFanSpeedTable)]   // 0x0F20
    [InlineData(EcFanRegisters.GpuFanSpeedTable)]   // 0x0F50
    public void FanDutyTables_SingleByteAndFullTable_AreAllowed(ushort address)
    {
        Assert.True(EcWriteGatePolicy.IsRawWriteAllowed(address, 1));
        Assert.True(EcWriteGatePolicy.IsRawWriteAllowed(address, EcDutyCodec.TableLength));
    }

    [Fact]
    public void WholeFanTableRegion_IsAllowed()
    {
        // The six 16-byte tables: temp-up/down and duty for both fans (0x0F00-0x0F5F).
        Assert.True(EcWriteGatePolicy.IsRawWriteAllowed(0x0F00, 0x60));
        Assert.True(EcWriteGatePolicy.IsRawWriteAllowed(0x0F00, 1));
        Assert.True(EcWriteGatePolicy.IsRawWriteAllowed(0x0F5F, 1));
    }

    // ---- power / mode / magic / mux are the denied domains --------------------

    [Theory]
    [InlineData(0x0783)] // PL1
    [InlineData(0x0784)] // PL2
    [InlineData(0x0785)] // PL4
    [InlineData(0x0786)] // TCC offset
    [InlineData(0x0788)] // PL5
    [InlineData(0x07AB)] // performance-mode index
    [InlineData(0x0726)] // power-management magic
    [InlineData(0x0727)] // custom-profile magic
    [InlineData(0x045B)] // OEM mode flag
    [InlineData(0x072A)] // mux state
    [InlineData(0x0751)] // manual fan control (mode walk, turbo, USER)
    [InlineData(0x07C5)] // universal fan control
    [InlineData(0x07C6)] // AP OEM 6 (ENABLE_CUSTOM)
    [InlineData(0x0741)] // manual-mode gate / Q-key echo arm
    public void PowerModeMagicMux_Registers_AreRefused(ushort address)
    {
        Assert.False(EcWriteGatePolicy.IsRawWriteAllowed(address, 1));
        Assert.False(EcWriteGatePolicy.IsRawWriteAllowed(address, 16));
    }

    [Fact]
    public void FanTableBlock_CannotStraddleIntoForbiddenGround()
    {
        // 16 bytes at the end of the table region runs past 0x0F5F: the whole
        // block is refused, so no tail byte can slip into unlisted territory.
        Assert.False(EcWriteGatePolicy.IsRawWriteAllowed(0x0F5F, 2));
        // 5 bytes at the first PWM block run past its end (0x1808/0x1809 split).
        Assert.False(EcWriteGatePolicy.IsRawWriteAllowed(0x1806, 5));
    }

    [Fact]
    public void PwmWriteableBlocks_AreAllowed()
    {
        Assert.True(EcWriteGatePolicy.IsRawWriteAllowed(0x1804, 5));
        Assert.True(EcWriteGatePolicy.IsRawWriteAllowed(0x1809, 5));
    }

    [Fact]
    public void KeyboardRgbSet_AndBatteryMode_AreAllowed()
    {
        Assert.True(EcWriteGatePolicy.IsRawWriteAllowed(0x0767, 1)); // trigger
        Assert.True(EcWriteGatePolicy.IsRawWriteAllowed(0x0769, 3)); // R+G+B
        Assert.True(EcWriteGatePolicy.IsRawWriteAllowed(0x078C, 1)); // brightness status
        Assert.True(EcWriteGatePolicy.IsRawWriteAllowed(0x07A6, 1)); // battery charge mode
    }

    [Fact]
    public void DegenerateLengths_AreRefused()
    {
        Assert.False(EcWriteGatePolicy.IsRawWriteAllowed(0x0F20, 0));
        Assert.False(EcWriteGatePolicy.IsRawWriteAllowed(0x0F20, -1));
        // A length that overflows the address space cannot be inside any range.
        Assert.False(EcWriteGatePolicy.IsRawWriteAllowed(0xFFFF, 2));
    }

    // ---- NVRAM GUID allowlist --------------------------------------------------

    [Theory]
    [InlineData("{9F33F85C-13CA-4FD1-9C4A-96217722C593}")] // the app's braced form
    [InlineData("9F33F85C-13CA-4FD1-9C4A-96217722C593")]   // bare
    [InlineData("{9f33f85c-13ca-4fd1-9c4a-96217722c593}")] // lowercase
    public void OemMagicGuid_IsAllowed_InEveryFormat(string guid)
    {
        Assert.True(EcWriteGatePolicy.IsNvramWriteAllowed(guid));
    }

    [Theory]
    [InlineData("{00000000-0000-0000-0000-000000000000}")]
    [InlineData("{8BE4DF61-93CA-11D2-AA0D-00E098032B8C}")] // the EFI global GUID
    [InlineData("")]
    [InlineData("not-a-guid")]
    public void ForeignGuids_AreRefused(string guid)
    {
        Assert.False(EcWriteGatePolicy.IsNvramWriteAllowed(guid));
    }
}
