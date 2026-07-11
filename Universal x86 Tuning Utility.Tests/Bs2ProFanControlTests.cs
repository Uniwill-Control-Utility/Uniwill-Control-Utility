using Universal_x86_Tuning_Utility.Models;

namespace Universal_x86_Tuning_Utility.Tests;

/// <summary>
/// Tests that verify the fan speed control frame building matches THRM protocol.
/// These test the protocol-level frames that the service methods would produce.
/// </summary>
public class Bs2ProFanControlFrameTests
{
    #region Gear Mode (0x08) Frame Tests

    [Theory]
    [InlineData(Bs2ProGearMode.Quiet, new byte[] { 0x5A, 0xA5, 0x08, 0x03, 0x01, 0x0C })]
    [InlineData(Bs2ProGearMode.Standard, new byte[] { 0x5A, 0xA5, 0x08, 0x03, 0x02, 0x0D })]
    [InlineData(Bs2ProGearMode.Strong, new byte[] { 0x5A, 0xA5, 0x08, 0x03, 0x03, 0x0E })]
    [InlineData(Bs2ProGearMode.Overclock, new byte[] { 0x5A, 0xA5, 0x08, 0x03, 0x04, 0x0F })]
    public void GearMode_Frame_BuildsCorrectly(byte gear, byte[] expected)
    {
        var frame = Bs2ProFrame.Build(Bs2ProCommand.GearMode, gear);
        Assert.Equal(expected, frame);
    }

    #endregion

    #region SetGearRpm (0x26) Frame Tests

    [Fact]
    public void SetGearRpm_Gear0_1300_BuildsCorrectly()
    {
        // THRM example: 5A A5 26 05 00 14 05 44
        var frame = Bs2ProFrame.Build(Bs2ProCommand.SetGearRpm, 0x00, 0x14, 0x05);
        Assert.Equal(new byte[] { 0x5A, 0xA5, 0x26, 0x05, 0x00, 0x14, 0x05, 0x44 }, frame);
    }

    [Fact]
    public void SetGearRpm_Gear1_2400_BuildsCorrectly()
    {
        // Gear 1 Standard, 2400 = 0x0960
        var frame = Bs2ProFrame.Build(Bs2ProCommand.SetGearRpm, 0x01, 0x60, 0x09);
        Assert.Equal(new byte[] { 0x5A, 0xA5, 0x26, 0x05, 0x01, 0x60, 0x09, 0x95 }, frame);
    }

    [Fact]
    public void SetGearRpm_Gear2_3300_BuildsCorrectly()
    {
        // Gear 2 Strong, 3300 = 0x0CE4
        var frame = Bs2ProFrame.Build(Bs2ProCommand.SetGearRpm, 0x02, 0xE4, 0x0C);
        Assert.Equal(new byte[] { 0x5A, 0xA5, 0x26, 0x05, 0x02, 0xE4, 0x0C, 0x1D }, frame);
    }

    [Fact]
    public void SetGearRpm_Gear3_4000_BuildsCorrectly()
    {
        // THRM example: 5A A5 26 05 03 A0 0F DD
        var frame = Bs2ProFrame.Build(Bs2ProCommand.SetGearRpm, 0x03, 0xA0, 0x0F);
        Assert.Equal(new byte[] { 0x5A, 0xA5, 0x26, 0x05, 0x03, 0xA0, 0x0F, 0xDD }, frame);
    }

    #endregion

    #region Realtime RPM (0x21) Frame Tests

    [Fact]
    public void RealtimeRpm_1700_BuildsCorrectly()
    {
        // THRM example: 5A A5 21 04 A4 06 CF
        var frame = Bs2ProFrame.Build(Bs2ProCommand.RealtimeRpm, 0xA4, 0x06);
        Assert.Equal(new byte[] { 0x5A, 0xA5, 0x21, 0x04, 0xA4, 0x06, 0xCF }, frame);
    }

    [Fact]
    public void RealtimeRpm_3300_BuildsCorrectly()
    {
        // THRM example: 5A A5 21 04 E4 0C 15
        var frame = Bs2ProFrame.Build(Bs2ProCommand.RealtimeRpm, 0xE4, 0x0C);
        Assert.Equal(new byte[] { 0x5A, 0xA5, 0x21, 0x04, 0xE4, 0x0C, 0x15 }, frame);
    }

    [Fact]
    public void RealtimeRpm_0_BuildsCorrectly()
    {
        // Fan off: 5A A5 21 04 00 00 25
        var frame = Bs2ProFrame.Build(Bs2ProCommand.RealtimeRpm, 0x00, 0x00);
        Assert.Equal(new byte[] { 0x5A, 0xA5, 0x21, 0x04, 0x00, 0x00, 0x25 }, frame);
    }

    [Fact]
    public void RealtimeRpm_4000_BuildsCorrectly()
    {
        // Max RPM: 4000 = 0x0FA0, checksum = 0x21+0x04+0xA0+0x0F = 0xD4
        var frame = Bs2ProFrame.Build(Bs2ProCommand.RealtimeRpm, 0xA0, 0x0F);
        Assert.Equal(new byte[] { 0x5A, 0xA5, 0x21, 0x04, 0xA0, 0x0F, 0xD4 }, frame);
    }

    [Fact]
    public void RealtimeRpm_1300_BuildsCorrectly()
    {
        // Min RPM: 1300 = 0x0514, checksum = 0x21+0x04+0x14+0x05 = 0x3E
        var frame = Bs2ProFrame.Build(Bs2ProCommand.RealtimeRpm, 0x14, 0x05);
        Assert.Equal(new byte[] { 0x5A, 0xA5, 0x21, 0x04, 0x14, 0x05, 0x3E }, frame);
    }

    #endregion

    #region Mode Handshake Frame Tests

    [Fact]
    public void EnterRealtimeMode_Frame_BuildsCorrectly()
    {
        // THRM example: 5A A5 23 02 25
        var frame = Bs2ProFrame.Build(Bs2ProCommand.EnterRealtimeMode);
        Assert.Equal(new byte[] { 0x5A, 0xA5, 0x23, 0x02, 0x25 }, frame);
    }

    [Fact]
    public void ExitRealtimeMode_Frame_BuildsCorrectly()
    {
        // 5A A5 24 02 26
        var frame = Bs2ProFrame.Build(Bs2ProCommand.ExitRealtimeMode);
        Assert.Equal(new byte[] { 0x5A, 0xA5, 0x24, 0x02, 0x26 }, frame);
    }

    #endregion

    #region RPM Range Validation Tests

    [Theory]
    [InlineData((ushort)0)]      // fan off
    [InlineData((ushort)1300)]   // min
    [InlineData((ushort)2500)]   // middle
    [InlineData((ushort)4000)]   // max
    public void IsValidRpm_AcceptsValidValues(ushort rpm)
    {
        // These values should pass the service's IsValidRpm check
        Assert.True(rpm == 0 || (rpm >= Bs2ProDefaultGearRpm.MinRpm && rpm <= Bs2ProDefaultGearRpm.MaxRpm));
    }

    [Theory]
    [InlineData((ushort)500)]    // below min
    [InlineData((ushort)1299)]   // just below min
    [InlineData((ushort)4001)]   // just above max
    [InlineData((ushort)5000)]   // way above max
    public void IsValidRpm_RejectsOutOfRangeValues(ushort rpm)
    {
        Assert.False(rpm == 0 || (rpm >= Bs2ProDefaultGearRpm.MinRpm && rpm <= Bs2ProDefaultGearRpm.MaxRpm));
    }

    #endregion

    #region Default Gear RPM Table Tests

    [Fact]
    public void DefaultGearRpm_Gear0_Quiet_Values()
    {
        Assert.Equal((ushort)1300, Bs2ProDefaultGearRpm.Gear0Low);
        Assert.Equal((ushort)1700, Bs2ProDefaultGearRpm.Gear0Medium);
        Assert.Equal((ushort)1900, Bs2ProDefaultGearRpm.Gear0High);
    }

    [Fact]
    public void DefaultGearRpm_Gear1_Standard_Values()
    {
        Assert.Equal((ushort)2100, Bs2ProDefaultGearRpm.Gear1Low);
        Assert.Equal((ushort)2400, Bs2ProDefaultGearRpm.Gear1Medium);
        Assert.Equal((ushort)2700, Bs2ProDefaultGearRpm.Gear1High);
    }

    [Fact]
    public void DefaultGearRpm_Gear2_Strong_Values()
    {
        Assert.Equal((ushort)2800, Bs2ProDefaultGearRpm.Gear2Low);
        Assert.Equal((ushort)3000, Bs2ProDefaultGearRpm.Gear2Medium);
        Assert.Equal((ushort)3300, Bs2ProDefaultGearRpm.Gear2High);
    }

    [Fact]
    public void DefaultGearRpm_Gear3_Overclock_Values()
    {
        Assert.Equal((ushort)3500, Bs2ProDefaultGearRpm.Gear3Low);
        Assert.Equal((ushort)3700, Bs2ProDefaultGearRpm.Gear3Medium);
        Assert.Equal((ushort)4000, Bs2ProDefaultGearRpm.Gear3High);
    }

    [Fact]
    public void DefaultGearRpm_Range_Bounds()
    {
        Assert.Equal((ushort)1300, Bs2ProDefaultGearRpm.MinRpm);
        Assert.Equal((ushort)4000, Bs2ProDefaultGearRpm.MaxRpm);
    }

    #endregion
}

/// <summary>
/// Tests the FanRpmData struct.
/// </summary>
public class Bs2ProFanRpmDataTests
{
    [Fact]
    public void FanRpmData_DefaultValues()
    {
        var data = new Universal_x86_Tuning_Utility.Services.FanRpmData();
        Assert.Equal((ushort)0, data.TargetRpm);
        Assert.Equal((ushort)0, data.CurrentRpm);
        Assert.Null(data.Mode);
    }

    [Fact]
    public void FanRpmData_CanSetAllFields()
    {
        var data = new Universal_x86_Tuning_Utility.Services.FanRpmData
        {
            TargetRpm = (ushort)2500,
            CurrentRpm = (ushort)2480,
            Mode = "Realtime"
        };

        Assert.Equal((ushort)2500, data.TargetRpm);
        Assert.Equal((ushort)2480, data.CurrentRpm);
        Assert.Equal("Realtime", data.Mode);
    }

    [Theory]
    [InlineData("Off")]
    [InlineData("Gear")]
    [InlineData("Realtime")]
    public void FanRpmData_Mode_AcceptsAllModes(string mode)
    {
        var data = new Universal_x86_Tuning_Utility.Services.FanRpmData { Mode = mode };
        Assert.Equal(mode, data.Mode);
    }
}
