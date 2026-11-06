using UCU.Core.Ec;
using UCU.Hardware.Ec;

namespace UCU.Hardware.Tests;

/// <summary>
/// Battery reads: the EC exposes a little-endian block whose fields need derived math
/// (Kelvin, health percentage), and the charging-mode field is a three-bit field with
/// non-sequential raw values. The dashboard shows these numbers directly, so the
/// decode is pinned here at the register level.
/// </summary>
public sealed class EcEngineBatteryTests
{
    [Theory]
    [InlineData(0x08, 0)]   // High Capacity
    [InlineData(0x18, 1)]   // Balanced
    [InlineData(0x28, 2)]   // Stationary
    [InlineData(0x00, 0)]   // no mode set
    [InlineData(0x3F, 0)]   // field set to a value this firmware profile does not define
    [InlineData(0x1B, 1)]   // other register bits set alongside the mode
    public void BatteryMode_IsDecodedFromTheFieldIgnoringOtherBits(byte raw, int expectedMode)
    {
        var transport = new FakeEcTransport();
        transport.Preset(EcEngine.REG_BATTERY_MODE, raw);
        using var engine = new EcEngine(transport, EcPlatform.Amd);
        engine.Initialize();

        Assert.Equal(expectedMode, engine.GetBatteryMode());
    }

    [Theory]
    [InlineData(0, 0x08)]
    [InlineData(1, 0x18)]
    [InlineData(2, 0x28)]
    public void BatteryMode_WritesTheRawFieldForTheMode(int mode, byte expectedRaw)
    {
        var transport = new FakeEcTransport();
        using var engine = new EcEngine(transport, EcPlatform.Amd);
        engine.Initialize();

        engine.SetBatteryMode(mode);

        Assert.Equal($"0x07A6=0x{expectedRaw:X2}", transport.SequenceOf(EcEngine.REG_BATTERY_MODE));
    }

    [Fact]
    public void BatteryMode_RejectsAModeTheFirmwareDoesNotDefine()
    {
        var transport = new FakeEcTransport();
        using var engine = new EcEngine(transport, EcPlatform.Amd);
        engine.Initialize();

        Assert.Throws<ArgumentOutOfRangeException>(() => engine.SetBatteryMode(3));
        Assert.Empty(transport.WritesAt(EcEngine.REG_BATTERY_MODE));
    }

    [Fact]
    public void ReadBatteryTelemetry_MapsEveryFieldFromTheLittleEndianBlock()
    {
        var transport = new FakeEcTransport();
        transport.Preset(EcEngine.REG_BATTERY_POWER_SOURCE, 0x01);
        transport.Preset(EcEngine.REG_BATTERY_SOC, 55);
        transport.Preset(EcEngine.REG_BATTERY_CYCLE_COUNT_LO, 0x2C, 0x00);          // 44
        transport.Preset(EcEngine.REG_BATTERY_TEMP_LO, 0xA5, 0x0B);                 // 2981 = 24 °C
        transport.Preset(EcEngine.REG_BATTERY_PRESENT_VOLTAGE_LO, 0xD0, 0x0B);      // 3024 mV
        transport.Preset(EcEngine.REG_BATTERY_CHARGE_RATE_LO, 0x10, 0x00);          // 16 mA
        transport.Preset(EcEngine.REG_BATTERY_DESIGN_CAP_LO, 0x88, 0x13);           // 5000 mAh
        transport.Preset(EcEngine.REG_BATTERY_FULL_CHARGE_CAP_LO, 0x00, 0x0F);      // 3840 mAh
        using var engine = new EcEngine(transport, EcPlatform.Amd);
        engine.Initialize();

        BatteryTelemetry? telemetry = engine.ReadBatteryTelemetry();

        Assert.NotNull(telemetry);
        Assert.True(telemetry!.Value.IsAcPowered);
        Assert.Equal(55, telemetry.Value.StateOfCharge);
        Assert.Equal(44, telemetry.Value.CycleCount);
        Assert.Equal(24, telemetry.Value.TemperatureC);
        Assert.Equal(3024, telemetry.Value.PresentVoltageMv);
        Assert.Equal(16, telemetry.Value.ChargeRateMa);
        Assert.Equal(76, telemetry.Value.HealthPercent);
    }

    [Fact]
    public void ReadBatteryTelemetry_ReportsNoHealthWhenDesignCapacityIsUnreadable()
    {
        var transport = new FakeEcTransport();
        transport.Preset(EcEngine.REG_BATTERY_FULL_CHARGE_CAP_LO, 0x00, 0x0F);
        using var engine = new EcEngine(transport, EcPlatform.Amd);
        engine.Initialize();

        BatteryTelemetry? telemetry = engine.ReadBatteryTelemetry();

        Assert.NotNull(telemetry);
        Assert.Equal(0, telemetry!.Value.HealthPercent);
    }

    [Fact]
    public void ReadBatteryTelemetry_ReturnsNullWhenTheBlockCannotBeRead()
    {
        // The real case is a driver handle that goes stale mid-read (EC unresponsive,
        // or the ACPI device re-enumerated on resume). The read throws inside the
        // telemetry read, and the dashboard keeps showing its last known values
        // instead of a crash.
        var transport = new LimitedGoodReadTransport(goodReads: 3);
        using var engine = new EcEngine(transport, EcPlatform.Amd);
        engine.Initialize();

        Assert.Null(engine.ReadBatteryTelemetry());
    }

    [Fact]
    public void ReadBatteryTemperatureC_TruncatesTheKelvinConversionLikeTheDashboard()
    {
        var transport = new FakeEcTransport();
        transport.Preset(EcEngine.REG_BATTERY_TEMP_LO, 0xA5, 0x0B); // 298.1 K = 24.95 °C
        using var engine = new EcEngine(transport, EcPlatform.Amd);
        engine.Initialize();

        Assert.Equal(24, engine.ReadBatteryTemperatureC());
    }

    // ---- U-103 status reads: gate bit, derived thresholds, platform override ----

    [Theory]
    [InlineData(0x12, true)]  // gate bit set beside other flags of the multiplexed byte
    [InlineData(0x02, false)] // the lab box's idle reading (probe 2026-10-30)
    public void BatteryGateStatus_DecodesBit4OfTheGateRegister(byte raw, bool expectedActive)
    {
        var transport = new FakeEcTransport();
        transport.Preset(EcEngine.REG_BATTERY_GATE_STATUS, raw);
        using var engine = new EcEngine(transport, EcPlatform.Amd);
        engine.Initialize();

        Assert.Equal(expectedActive, engine.GetBatteryGateStatus());
        Assert.Empty(transport.Writes); // a status row never writes
    }

    [Theory]
    [InlineData(0x08, 100, 95)] // High Capacity
    [InlineData(0x18, 80, 75)]  // Balanced
    [InlineData(0x28, 60, 55)]  // Stationary (the lab box's live reading)
    public void BatteryThresholds_AreDerivedFromTheModeRegister(byte modeRaw, int up, int down)
    {
        var transport = new FakeEcTransport();
        transport.Preset(EcEngine.REG_BATTERY_MODE, modeRaw);
        using var engine = new EcEngine(transport, EcPlatform.Amd);
        engine.Initialize();

        (int upPercent, int downPercent) = engine.GetBatteryThresholds();

        Assert.Equal(up, upPercent);
        Assert.Equal(down, downPercent);
        // No threshold registers exist on this chassis family — the read must
        // not go near 0x07C9/0x07D0 (the refuted open-revo layout, probe 2026-10-30).
        Assert.Empty(transport.Writes);
        Assert.Empty(transport.WritesAt(EcEngine.REG_BATTERY_LIMIT_UP));
        Assert.Empty(transport.WritesAt(EcEngine.REG_BATTERY_LIMIT_DOWN));
    }

    [Theory]
    [InlineData(0xFF, false)] // the documented reset (the lab box idles here)
    [InlineData(0x05, true)]  // the tools' pin value
    public void BatteryPlatformOverride_DecodesTheOverrideRegister(byte raw, bool expectedActive)
    {
        var transport = new FakeEcTransport();
        transport.Preset(EcEngine.REG_BATTERY_PLATFORM_OVERRIDE, raw);
        using var engine = new EcEngine(transport, EcPlatform.Amd);
        engine.Initialize();

        Assert.Equal(expectedActive, engine.GetBatteryPlatformOverride());
        Assert.Empty(transport.Writes);
    }

    /// <summary>Serves <paramref name="goodReads"/> reads, then reports the EC unresponsive.</summary>
    private sealed class LimitedGoodReadTransport : IEcTransport
    {
        private int _goodReads;

        public LimitedGoodReadTransport(int goodReads) => _goodReads = goodReads;

        public bool IsOpen => true;

        public void Open()
        {
        }

        public void Reconnect()
        {
        }

        public byte Read(ushort address)
        {
            if (_goodReads-- <= 0)
                throw new System.ComponentModel.Win32Exception(5, "EC unresponsive");

            return 0;
        }

        public void Write(ushort address, byte value)
        {
        }

        public void Dispose()
        {
        }
    }
}
