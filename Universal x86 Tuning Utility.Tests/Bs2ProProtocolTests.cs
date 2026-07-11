using Universal_x86_Tuning_Utility.Models;

namespace Universal_x86_Tuning_Utility.Tests;

public class Bs2ProFrameBuildTests
{
    [Fact]
    public void Build_RealtimeRpm_1700_ProducesExpectedFrame()
    {
        // THRM example: 5A A5 21 04 A4 06 CF  (1700 RPM = 0x06A4 little-endian)
        var frame = Bs2ProFrame.Build(Bs2ProCommand.RealtimeRpm, 0xA4, 0x06);

        Assert.Equal(new byte[] { 0x5A, 0xA5, 0x21, 0x04, 0xA4, 0x06, 0xCF }, frame);
    }

    [Fact]
    public void Build_RealtimeRpm_3300_ProducesExpectedFrame()
    {
        // THRM example: 5A A5 21 04 E4 0C 15  (3300 RPM = 0x0CE4)
        var frame = Bs2ProFrame.Build(Bs2ProCommand.RealtimeRpm, 0xE4, 0x0C);

        Assert.Equal(new byte[] { 0x5A, 0xA5, 0x21, 0x04, 0xE4, 0x0C, 0x15 }, frame);
    }

    [Fact]
    public void Build_EnterRealtimeMode_ProducesExpectedFrame()
    {
        // THRM example: 5A A5 23 02 25
        var frame = Bs2ProFrame.Build(Bs2ProCommand.EnterRealtimeMode);

        Assert.Equal(new byte[] { 0x5A, 0xA5, 0x23, 0x02, 0x25 }, frame);
    }

    [Fact]
    public void Build_GearMode_Quiet_ProducesExpectedFrame()
    {
        // THRM example: 5A A5 08 03 01 0C
        var frame = Bs2ProFrame.Build(Bs2ProCommand.GearMode, Bs2ProGearMode.Quiet);

        Assert.Equal(new byte[] { 0x5A, 0xA5, 0x08, 0x03, 0x01, 0x0C }, frame);
    }

    [Fact]
    public void Build_GearMode_Overclock_ProducesExpectedFrame()
    {
        // THRM example: 5A A5 08 03 04 0F
        var frame = Bs2ProFrame.Build(Bs2ProCommand.GearMode, Bs2ProGearMode.Overclock);

        Assert.Equal(new byte[] { 0x5A, 0xA5, 0x08, 0x03, 0x04, 0x0F }, frame);
    }

    [Fact]
    public void Build_PowerOnStart_Enable_ProducesExpectedFrame()
    {
        // THRM example: 5A A5 0C 03 01 10
        var frame = Bs2ProFrame.Build(Bs2ProCommand.PowerOnStart, Bs2ProPowerOnStart.Enabled);

        Assert.Equal(new byte[] { 0x5A, 0xA5, 0x0C, 0x03, 0x01, 0x10 }, frame);
    }

    [Fact]
    public void Build_SmartStartStop_Off_ProducesExpectedFrame()
    {
        // THRM example: 5A A5 0D 03 00 10
        var frame = Bs2ProFrame.Build(Bs2ProCommand.SmartStartStop, Bs2ProSmartStartStop.Off);

        Assert.Equal(new byte[] { 0x5A, 0xA5, 0x0D, 0x03, 0x00, 0x10 }, frame);
    }

    [Fact]
    public void Build_RgbOff_ProducesExpectedFrame()
    {
        // THRM example: 5A A5 46 03 00 49
        var frame = Bs2ProFrame.Build(Bs2ProCommand.RgbEnable, Bs2ProRgbEnable.Off);

        Assert.Equal(new byte[] { 0x5A, 0xA5, 0x46, 0x03, 0x00, 0x49 }, frame);
    }

    [Fact]
    public void Build_RgbOn_ProducesExpectedFrame()
    {
        // THRM example: 5A A5 46 03 01 4A
        var frame = Bs2ProFrame.Build(Bs2ProCommand.RgbEnable, Bs2ProRgbEnable.On);

        Assert.Equal(new byte[] { 0x5A, 0xA5, 0x46, 0x03, 0x01, 0x4A }, frame);
    }

    [Fact]
    public void Build_GearLight_Off_ProducesExpectedFrame()
    {
        // THRM example: 5A A5 48 03 00 4B
        var frame = Bs2ProFrame.Build(Bs2ProCommand.GearLight, Bs2ProGearLight.Off);

        Assert.Equal(new byte[] { 0x5A, 0xA5, 0x48, 0x03, 0x00, 0x4B }, frame);
    }

    [Fact]
    public void Build_SetGearRpm_Gear0_1300_ProducesExpectedFrame()
    {
        // THRM example: 5A A5 26 05 00 14 05 44  (gear 0, 1300 = 0x0514)
        var frame = Bs2ProFrame.Build(Bs2ProCommand.SetGearRpm, 0x00, 0x14, 0x05);

        Assert.Equal(new byte[] { 0x5A, 0xA5, 0x26, 0x05, 0x00, 0x14, 0x05, 0x44 }, frame);
    }

    [Fact]
    public void Build_SetGearRpm_Gear3_4000_ProducesExpectedFrame()
    {
        // THRM example: 5A A5 26 05 03 A0 0F DD  (gear 3, 4000 = 0x0FA0)
        var frame = Bs2ProFrame.Build(Bs2ProCommand.SetGearRpm, 0x03, 0xA0, 0x0F);

        Assert.Equal(new byte[] { 0x5A, 0xA5, 0x26, 0x05, 0x03, 0xA0, 0x0F, 0xDD }, frame);
    }

    [Fact]
    public void BuildReport_PrependsReportId_AndPadsToControlLength()
    {
        var frame = Bs2ProFrame.Build(Bs2ProCommand.EnterRealtimeMode);
        var report = Bs2ProFrame.BuildReport(frame, Bs2ProReportLength.Control);

        Assert.Equal(Bs2ProReportLength.Control, report.Length);
        Assert.Equal(Bs2ProReportId.Output, report[0]);
        // Frame starts at index 1
        Assert.Equal(Bs2ProMagic.Start0, report[1]);
        Assert.Equal(Bs2ProMagic.Start1, report[2]);
    }

    [Fact]
    public void BuildReport_AutoSizesWhenReportLengthTooSmall()
    {
        var frame = Bs2ProFrame.Build(Bs2ProCommand.EnterRealtimeMode); // 5 bytes
        var report = Bs2ProFrame.BuildReport(frame, 2); // too small

        // Should auto-size to frame.Length + 1 = 6
        Assert.Equal(6, report.Length);
        Assert.Equal(Bs2ProReportId.Output, report[0]);
    }
}

public class Bs2ProFrameParseTests
{
    [Fact]
    public void Parse_ValidFrameWithoutReportId_ReturnsParsedFrame()
    {
        // 5A A5 21 04 A4 06 CF
        var data = new byte[] { 0x5A, 0xA5, 0x21, 0x04, 0xA4, 0x06, 0xCF };
        var parsed = Bs2ProFrame.Parse(data);

        Assert.NotNull(parsed);
        Assert.Equal(0, parsed.Value.ReportId);
        Assert.Equal(Bs2ProCommand.RealtimeRpm, parsed.Value.Command);
        Assert.Equal(new byte[] { 0xA4, 0x06 }, parsed.Value.Payload);
        Assert.True(parsed.Value.ChecksumValid);
    }

    [Fact]
    public void Parse_FrameWithReportIdPrefix_ReturnsParsedFrame()
    {
        // 02 5A A5 23 02 25 (Report ID 0x02 prefix)
        var data = new byte[] { 0x02, 0x5A, 0xA5, 0x23, 0x02, 0x25 };
        var parsed = Bs2ProFrame.Parse(data);

        Assert.NotNull(parsed);
        Assert.Equal(Bs2ProReportId.Output, parsed.Value.ReportId);
        Assert.Equal(Bs2ProCommand.EnterRealtimeMode, parsed.Value.Command);
        Assert.Empty(parsed.Value.Payload);
        Assert.True(parsed.Value.ChecksumValid);
    }

    [Fact]
    public void Parse_StatusNotification_ReturnsCorrectFields()
    {
        // THRM example: 5A A5 EF 0B 68 05 05 C4 09 8D 0F A1 01 77
        // gear_settings=0x68, work_mode=0x05, reserved=0x05
        // current_rpm=0x09C4=2500, target_rpm=0x0F8D=3981
        var data = new byte[]
        {
            0x5A, 0xA5, 0xEF, 0x0B,
            0x68, 0x05, 0x05, 0xC4, 0x09, 0x8D, 0x0F, 0xA1, 0x01, 0x77
        };
        var parsed = Bs2ProFrame.Parse(data);

        Assert.NotNull(parsed);
        Assert.Equal(Bs2ProCommand.StatusNotify, parsed.Value.Command);
        Assert.True(parsed.Value.ChecksumValid);

        // Now parse the status payload
        var status = Bs2ProStatusParser.Parse(parsed.Value.Payload);
        Assert.NotNull(status);
        Assert.Equal(0x68, status.Value.GearSettings);
        Assert.Equal(Bs2ProWorkMode.Realtime, status.Value.WorkMode);
        Assert.Equal((ushort)2500, status.Value.CurrentRpm);
        Assert.Equal((ushort)3981, status.Value.TargetRpm);
        Assert.True(status.Value.IsRealtimeMode);
    }

    [Fact]
    public void Parse_InvalidMagicBytes_ReturnsNull()
    {
        var data = new byte[] { 0x00, 0x00, 0x21, 0x04, 0xA4, 0x06, 0xCF };
        Assert.Null(Bs2ProFrame.Parse(data));
    }

    [Fact]
    public void Parse_TooShort_ReturnsNull()
    {
        var data = new byte[] { 0x5A, 0xA5, 0x21 };
        Assert.Null(Bs2ProFrame.Parse(data));
    }

    [Fact]
    public void Parse_BadChecksum_ReturnsInvalidFlag()
    {
        var data = new byte[] { 0x5A, 0xA5, 0x21, 0x04, 0xA4, 0x06, 0x00 }; // wrong checksum
        var parsed = Bs2ProFrame.Parse(data);

        Assert.NotNull(parsed);
        Assert.False(parsed.Value.ChecksumValid);
    }

    [Fact]
    public void Parse_EmptyArray_ReturnsNull()
    {
        Assert.Null(Bs2ProFrame.Parse(Array.Empty<byte>()));
    }

    [Fact]
    public void Parse_Null_ReturnsNull()
    {
        Assert.Null(Bs2ProFrame.Parse(null!));
    }
}

public class Bs2ProStatusParserTests
{
    [Fact]
    public void Parse_MinimalPayload_ReturnsCorrectValues()
    {
        // 7-byte payload: gear_settings, work_mode, reserved, current_rpm_le, target_rpm_le
        var payload = new byte[] { 0x68, 0x05, 0x05, 0xC4, 0x09, 0x8D, 0x0F };

        var status = Bs2ProStatusParser.Parse(payload);
        Assert.NotNull(status);
        Assert.Equal((ushort)2500, status.Value.CurrentRpm);
        Assert.Equal((ushort)3981, status.Value.TargetRpm);
    }

    [Fact]
    public void Parse_TooShort_ReturnsNull()
    {
        var payload = new byte[] { 0x68, 0x05, 0x05 };
        Assert.Null(Bs2ProStatusParser.Parse(payload));
    }

    [Fact]
    public void Parse_Null_ReturnsNull()
    {
        Assert.Null(Bs2ProStatusParser.Parse(null!));
    }

    [Theory]
    [InlineData(0x2, "Standard")]
    [InlineData(0x4, "Performance")]
    [InlineData(0x6, "Extreme")]
    [InlineData(0xF, "Unknown(0xF)")]
    public void MaxGearName_DecodesCorrectly(byte code, string expected)
    {
        Assert.Equal(expected, Bs2ProStatusParser.MaxGearName(code));
    }

    [Theory]
    [InlineData(0x8, "Quiet")]
    [InlineData(0xA, "Standard")]
    [InlineData(0xC, "Performance")]
    [InlineData(0xE, "Extreme")]
    [InlineData(0x0, "Unknown(0x0)")]
    public void SelectedGearName_DecodesCorrectly(byte code, string expected)
    {
        Assert.Equal(expected, Bs2ProStatusParser.SelectedGearName(code));
    }

    [Theory]
    [InlineData(0x02, "Off")]
    [InlineData(0x04, "Immediate")]
    [InlineData(0x08, "Delayed")]
    [InlineData(0x00, "")]
    public void SmartStartStopName_DecodesCorrectly(byte workMode, string expected)
    {
        Assert.Equal(expected, Bs2ProStatusParser.SmartStartStopName(workMode));
    }
}

public class Bs2ProGearTableParserTests
{
    [Fact]
    public void Parse_ValidTable_ReturnsFourRpmValues()
    {
        // 4 gear entries, each 2 bytes LE: 1300, 2100, 2800, 3500
        var payload = new byte[]
        {
            0x14, 0x05, // 1300
            0x34, 0x08, // 2100
            0xF0, 0x0A, // 2800
            0xAC, 0x0D  // 3500
        };

        var table = Bs2ProGearTableParser.Parse(payload);
        Assert.NotNull(table);
        Assert.Equal(4, table.Length);
        Assert.Equal((ushort)1300, table[0]);
        Assert.Equal((ushort)2100, table[1]);
        Assert.Equal((ushort)2800, table[2]);
        Assert.Equal((ushort)3500, table[3]);
    }

    [Fact]
    public void Parse_TooShort_ReturnsNull()
    {
        var payload = new byte[] { 0x14, 0x05, 0x34 };
        Assert.Null(Bs2ProGearTableParser.Parse(payload));
    }

    [Fact]
    public void Parse_Null_ReturnsNull()
    {
        Assert.Null(Bs2ProGearTableParser.Parse(null!));
    }
}

public class Bs2ProStatusNotificationTests
{
    [Fact]
    public void IsRealtimeMode_TrueForOddWorkMode()
    {
        var status = new Bs2ProStatusNotification { WorkMode = 0x05 };
        Assert.True(status.IsRealtimeMode);
    }

    [Fact]
    public void IsRealtimeMode_FalseForEvenWorkMode()
    {
        var status = new Bs2ProStatusNotification { WorkMode = 0x04 };
        Assert.False(status.IsRealtimeMode);
    }

    [Fact]
    public void MaxGearCode_ExtractsHighNibble()
    {
        var status = new Bs2ProStatusNotification { GearSettings = 0x68 };
        Assert.Equal(0x6, status.MaxGearCode);
    }

    [Fact]
    public void SelectedGearCode_ExtractsLowNibble()
    {
        var status = new Bs2ProStatusNotification { GearSettings = 0x68 };
        Assert.Equal(0x8, status.SelectedGearCode);
    }
}

public class Bs2ProConstantsTests
{
    [Fact]
    public void DefaultGearRpm_MatchesThrmDefaults()
    {
        Assert.Equal((ushort)1300, Bs2ProDefaultGearRpm.Gear0Low);
        Assert.Equal((ushort)1700, Bs2ProDefaultGearRpm.Gear0Medium);
        Assert.Equal((ushort)1900, Bs2ProDefaultGearRpm.Gear0High);

        Assert.Equal((ushort)2100, Bs2ProDefaultGearRpm.Gear1Low);
        Assert.Equal((ushort)2400, Bs2ProDefaultGearRpm.Gear1Medium);
        Assert.Equal((ushort)2700, Bs2ProDefaultGearRpm.Gear1High);

        Assert.Equal((ushort)2800, Bs2ProDefaultGearRpm.Gear2Low);
        Assert.Equal((ushort)3000, Bs2ProDefaultGearRpm.Gear2Medium);
        Assert.Equal((ushort)3300, Bs2ProDefaultGearRpm.Gear2High);

        Assert.Equal((ushort)3500, Bs2ProDefaultGearRpm.Gear3Low);
        Assert.Equal((ushort)3700, Bs2ProDefaultGearRpm.Gear3Medium);
        Assert.Equal((ushort)4000, Bs2ProDefaultGearRpm.Gear3High);
    }

    [Fact]
    public void DefaultGearRpm_RangeIsCorrect()
    {
        Assert.Equal((ushort)1300, Bs2ProDefaultGearRpm.MinRpm);
        Assert.Equal((ushort)4000, Bs2ProDefaultGearRpm.MaxRpm);
    }

    [Fact]
    public void ProductIds_MatchThrmVendorTable()
    {
        Assert.Equal(0x37D7, Bs2ProProductId.VendorId);
        Assert.Equal(0x1001, Bs2ProProductId.B2);
        Assert.Equal(0x1002, Bs2ProProductId.B2Pro);
        Assert.Equal(0x1003, Bs2ProProductId.B3);
        Assert.Equal(0x1004, Bs2ProProductId.B3Pro);
        Assert.Equal(4, Bs2ProProductId.AllProductIds.Length);
    }
}
