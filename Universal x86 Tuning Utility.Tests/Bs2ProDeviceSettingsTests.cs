using Universal_x86_Tuning_Utility.Models;

namespace Universal_x86_Tuning_Utility.Tests;

/// <summary>
/// Tests for device settings frame building commands (power-on start, smart start/stop, gear light).
/// </summary>
public class Bs2ProDeviceSettingsCommandTests
{
    #region Power-On Start (0x0C)

    [Fact]
    public void PowerOnStart_Enabled_Frame_BuildsCorrectly()
    {
        // 0x0C with payload 0x01: 5A A5 0C 03 01 10
        var frame = Bs2ProFrame.Build(Bs2ProCommand.PowerOnStart, Bs2ProPowerOnStart.Enabled);
        Assert.Equal(new byte[] { 0x5A, 0xA5, 0x0C, 0x03, 0x01, 0x10 }, frame);
    }

    [Fact]
    public void PowerOnStart_Disabled_Frame_BuildsCorrectly()
    {
        // 0x0C with payload 0x02: 5A A5 0C 03 02 11
        var frame = Bs2ProFrame.Build(Bs2ProCommand.PowerOnStart, Bs2ProPowerOnStart.Disabled);
        Assert.Equal(new byte[] { 0x5A, 0xA5, 0x0C, 0x03, 0x02, 0x11 }, frame);
    }

    #endregion

    #region Smart Start/Stop (0x0D)

    [Fact]
    public void SmartStartStop_Off_Frame_BuildsCorrectly()
    {
        // 0x0D with payload 0x00: checksum = 0x0D + 0x03 + 0x00 = 0x10
        var frame = Bs2ProFrame.Build(Bs2ProCommand.SmartStartStop, Bs2ProSmartStartStop.Off);
        Assert.Equal(new byte[] { 0x5A, 0xA5, 0x0D, 0x03, 0x00, 0x10 }, frame);
    }

    [Fact]
    public void SmartStartStop_Immediate_Frame_BuildsCorrectly()
    {
        // 0x0D with payload 0x01: checksum = 0x0D + 0x03 + 0x01 = 0x11
        var frame = Bs2ProFrame.Build(Bs2ProCommand.SmartStartStop, Bs2ProSmartStartStop.Immediate);
        Assert.Equal(new byte[] { 0x5A, 0xA5, 0x0D, 0x03, 0x01, 0x11 }, frame);
    }

    [Fact]
    public void SmartStartStop_Delayed_Frame_BuildsCorrectly()
    {
        // 0x0D with payload 0x02: checksum = 0x0D + 0x03 + 0x02 = 0x12
        var frame = Bs2ProFrame.Build(Bs2ProCommand.SmartStartStop, Bs2ProSmartStartStop.Delayed);
        Assert.Equal(new byte[] { 0x5A, 0xA5, 0x0D, 0x03, 0x02, 0x12 }, frame);
    }

    #endregion

    #region Gear Light (0x48)

    [Fact]
    public void GearLight_On_Frame_BuildsCorrectly()
    {
        // 0x48 with payload 0x01: checksum = 0x48 + 0x03 + 0x01 = 0x4C
        var frame = Bs2ProFrame.Build(Bs2ProCommand.GearLight, Bs2ProGearLight.On);
        Assert.Equal(new byte[] { 0x5A, 0xA5, 0x48, 0x03, 0x01, 0x4C }, frame);
    }

    [Fact]
    public void GearLight_Off_Frame_BuildsCorrectly()
    {
        // 0x48 with payload 0x00: checksum = 0x48 + 0x03 + 0x00 = 0x4B
        var frame = Bs2ProFrame.Build(Bs2ProCommand.GearLight, Bs2ProGearLight.Off);
        Assert.Equal(new byte[] { 0x5A, 0xA5, 0x48, 0x03, 0x00, 0x4B }, frame);
    }

    #endregion

    #region Query Commands

    [Fact]
    public void QueryWorkMode_Frame_BuildsCorrectly()
    {
        // 0x25 with no payload: 5A A5 25 02 27
        var frame = Bs2ProFrame.Build(Bs2ProCommand.QueryWorkMode);
        Assert.Equal(new byte[] { 0x5A, 0xA5, 0x25, 0x02, 0x27 }, frame);
    }

    [Fact]
    public void QueryGearTable_Frame_BuildsCorrectly()
    {
        // 0x27 with no payload: 5A A5 27 02 29
        var frame = Bs2ProFrame.Build(Bs2ProCommand.QueryGearTable);
        Assert.Equal(new byte[] { 0x5A, 0xA5, 0x27, 0x02, 0x29 }, frame);
    }

    #endregion
}

/// <summary>
/// Tests for the Bs2ProDeviceSettings model.
/// </summary>
public class Bs2ProDeviceSettingsModelTests
{
    [Fact]
    public void WorkModeName_Manual_WhenEven()
    {
        var settings = new Bs2ProDeviceSettings { WorkMode = 0x04 };
        Assert.Equal("Manual", settings.WorkModeName);
    }

    [Fact]
    public void WorkModeName_Realtime_WhenOdd()
    {
        var settings = new Bs2ProDeviceSettings { WorkMode = 0x05 };
        Assert.Equal("Realtime", settings.WorkModeName);
    }

    [Fact]
    public void GearLightEnabled_DefaultsToTrue()
    {
        var settings = new Bs2ProDeviceSettings();
        Assert.True(settings.GearLightEnabled);
    }

    [Fact]
    public void PowerOnStart_DefaultsToFalse()
    {
        var settings = new Bs2ProDeviceSettings();
        Assert.False(settings.PowerOnStart);
    }

    [Fact]
    public void SmartStartStopMode_DefaultsToZero()
    {
        var settings = new Bs2ProDeviceSettings();
        Assert.Equal(0, settings.SmartStartStopMode);
    }
}

/// <summary>
/// Tests for command constant values.
/// </summary>
public class Bs2ProDeviceSettingsConstantTests
{
    [Fact]
    public void PowerOnStart_CommandByte_Is0x0C()
    {
        Assert.Equal(0x0C, Bs2ProCommand.PowerOnStart);
    }

    [Fact]
    public void SmartStartStop_CommandByte_Is0x0D()
    {
        Assert.Equal(0x0D, Bs2ProCommand.SmartStartStop);
    }

    [Fact]
    public void GearLight_CommandByte_Is0x48()
    {
        Assert.Equal(0x48, Bs2ProCommand.GearLight);
    }

    [Fact]
    public void QueryWorkMode_CommandByte_Is0x25()
    {
        Assert.Equal(0x25, Bs2ProCommand.QueryWorkMode);
    }

    [Fact]
    public void QueryGearTable_CommandByte_Is0x27()
    {
        Assert.Equal(0x27, Bs2ProCommand.QueryGearTable);
    }

    [Fact]
    public void PowerOnStart_Constants_AreCorrect()
    {
        Assert.Equal(0x01, Bs2ProPowerOnStart.Enabled);
        Assert.Equal(0x02, Bs2ProPowerOnStart.Disabled);
    }

    [Fact]
    public void SmartStartStop_Constants_AreCorrect()
    {
        Assert.Equal(0x00, Bs2ProSmartStartStop.Off);
        Assert.Equal(0x01, Bs2ProSmartStartStop.Immediate);
        Assert.Equal(0x02, Bs2ProSmartStartStop.Delayed);
    }

    [Fact]
    public void GearLight_Constants_AreCorrect()
    {
        Assert.Equal(0x00, Bs2ProGearLight.Off);
        Assert.Equal(0x01, Bs2ProGearLight.On);
    }
}
