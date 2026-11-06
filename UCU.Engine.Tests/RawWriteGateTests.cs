using System.Text.Json;
using UCU.Contracts;
using UCU.Core.Ec;
using UCU.Hardware.Ec;

namespace UCU.Engine.Tests;

/// <summary>
/// The raw-verb chokepoint (U-102): the server side decides what a caller-chosen
/// address/GUID may be before the EC moves. Acceptance matrix: out-of-range values
/// are reported (InvalidValue, the codec Validate* seam), raw addresses outside
/// the allowlist are refused (BadRequest, the gate), the fan-planner domain keeps
/// working, the OEM NVRAM GUID family passes while foreign GUIDs are refused, and
/// every refusal leaves the transport log empty.
/// </summary>
[Collection("EngineHostTiming")]
public sealed class RawWriteGateTests
{
    /// <summary>
    /// Trigger the gate (driver open + InitializeEc handshake) and clear the log,
    /// so the Empty assertions below cover only what the tested call itself wrote
    /// (same idiom as the golden curve test).
    /// </summary>
    private static async Task PrimeGateAsync(EngineHostFixture host)
    {
        await host.Fan.GetStatusAsync();
        host.Fake.ClearLog();
    }

    // ---- (b) the raw address allowlist over the wire ---------------------------
    //
    // The theories here run their cases in one host per test (loop) rather than
    // one host per InlineData: every case would boot a full engine host, and the
    // suite is host-heavy — 8 hosts per theory starves the timing-sensitive
    // tests in the same assembly. Coverage per case is identical.

    [Fact]
    public async Task TelemetryWriteRegister_PowerMagicMux_AddressesAreRefused()
    {
        ushort[] refused =
        {
            0x0783, // PL1
            0x0784, // PL2
            0x0785, // PL4
            0x0786, // TCC
            0x0788, // PL5
            0x07AB, // mode index
            0x072A, // mux
        };

        await using var host = await EngineHostFixture.StartAsync();
        await PrimeGateAsync(host);

        foreach (ushort address in refused)
        {
            EngineException ex = await Assert.ThrowsAsync<EngineException>(
                () => host.Telemetry.WriteRegisterAsync(address, 0xFF));

            Assert.True(ex.Code == EngineErrorCode.BadRequest,
                $"0x{address:X4} must be refused as BadRequest, got {ex.Code}");
            Assert.Empty(host.Fake.Writes); // EC untouched
        }
    }

    [Fact]
    public async Task TelemetryWriteRegister_AllowedAddress_ReachesTheTransport()
    {
        await using var host = await EngineHostFixture.StartAsync();

        await host.Telemetry.WriteRegisterAsync(EcEngine.REG_BATTERY_MODE, 0x18);

        Assert.Equal((byte)0x18, host.Fake.LastWriteAt(EcEngine.REG_BATTERY_MODE)!.Value);
    }

    [Fact]
    public async Task FanWriteRegisterBlocks_PowerRangeBlock_IsRefused_EntirelyUntouched()
    {
        await using var host = await EngineHostFixture.StartAsync();
        await PrimeGateAsync(host);

        // 16 bytes at PL1: every byte of the block is refused, not just the
        // out-of-domain tail (the gate runs over the whole plan before the write).
        EngineException ex = await Assert.ThrowsAsync<EngineException>(
            () => host.Fan.WriteRegisterBlocksAsync(new[]
            {
                new EcRegisterBlockDto { Address = 0x0783, Data = new byte[16] },
            }));

        Assert.Equal(EngineErrorCode.BadRequest, ex.Code);
        Assert.Empty(host.Fake.Writes);
    }

    [Fact]
    public async Task FanWriteRegisterBlocks_MixedPlan_IsRefusedBeforeTheFirstWrite()
    {
        await using var host = await EngineHostFixture.StartAsync();
        await PrimeGateAsync(host);

        // One legal fan-table block plus one power-range block: the whole call is
        // refused, so a planner bug cannot half-apply (fans moved, power not).
        EngineException ex = await Assert.ThrowsAsync<EngineException>(
            () => host.Fan.WriteRegisterBlocksAsync(new[]
            {
                new EcRegisterBlockDto { Address = EcFanRegisters.CpuFanSpeedTable, Data = new byte[] { 0xC8 } },
                new EcRegisterBlockDto { Address = 0x0783, Data = new byte[] { 0x2D } },
            }));

        Assert.Equal(EngineErrorCode.BadRequest, ex.Code);
        Assert.Empty(host.Fake.Writes);
    }

    [Fact]
    public async Task FanWriteRegisterBlocks_FanTablePlan_Applies()
    {
        await using var host = await EngineHostFixture.StartAsync();

        // The planner's real shape: the two duty tables (EcSmartFanWritePlan).
        byte[] duty = { 0, 20, 30, 40, 50, 60, 70, 80, 90, 100, 110, 110, 110, 110, 110, 110 };
        await host.Fan.WriteRegisterBlocksAsync(new[]
        {
            new EcRegisterBlockDto { Address = EcFanRegisters.CpuFanSpeedTable, Data = duty },
            new EcRegisterBlockDto { Address = EcFanRegisters.GpuFanSpeedTable, Data = duty },
        });

        Assert.Equal(duty, host.Fake.TableAt(EcFanRegisters.CpuFanSpeedTable));
        Assert.Equal(duty, host.Fake.TableAt(EcFanRegisters.GpuFanSpeedTable));
    }

    // ---- (d) NVRAM GUID allowlist ----------------------------------------------

    [Fact]
    public async Task SetNvramValue_ForeignGuid_IsRefused_StoreUntouched()
    {
        var nvram = new FakeFwNvramService();
        await using var host = await EngineHostFixture.StartAsync(
            configure: o => o with { FwNvramOverride = nvram });

        EngineException ex = await Assert.ThrowsAsync<EngineException>(
            () => host.Device.SetNvramValueAsync("BootOrder",
                "{8BE4DF61-93CA-11D2-AA0D-00E098032B8C}", new byte[] { 0x01 }));

        Assert.Equal(EngineErrorCode.BadRequest, ex.Code);
        Assert.Null(nvram.Read("BootOrder", "{8BE4DF61-93CA-11D2-AA0D-00E098032B8C}"));
    }

    [Fact]
    public async Task SetNvramValue_OemMagicGuid_Applies()
    {
        var nvram = new FakeFwNvramService();
        await using var host = await EngineHostFixture.StartAsync(
            configure: o => o with { FwNvramOverride = nvram });

        bool ok = await host.Device.SetNvramValueAsync(
            "OemMagicVariable", EcWriteGatePolicy.OemMagicVariableGuid, new byte[] { 42 });

        Assert.True(ok);
        Assert.Equal((byte)42, Assert.Single(nvram.Read("OemMagicVariable", EcWriteGatePolicy.OemMagicVariableGuid)!));
    }

    // ---- (a) + (e) the codec Validate* seam is wired (report, don't clamp) -----

    [Fact]
    public async Task SetSpeedPercent_OutOfRangeDuty_IsRefused_EcUntouched()
    {
        await using var host = await EngineHostFixture.StartAsync();
        await PrimeGateAsync(host);

        EngineException ex = await Assert.ThrowsAsync<EngineException>(
            () => host.Fan.SetSpeedPercentAsync(150));

        Assert.Equal(EngineErrorCode.InvalidValue, ex.Code);
        Assert.Empty(host.Fake.Writes); // the handshake did not even run
    }

    [Fact]
    public async Task SemanticVerbs_OutOfRangeValues_AreReportedNotClamped()
    {
        (string Verb, string Json, string Why)[] cases =
        {
            ("fan.set_speed_percent", "{\"percent\":150}", "duty > 100 %"),
            ("power.set_power_limit", "{\"kind\":0,\"watts\":300}", "PL > 255 W"),
            ("power.set_tcc_offset", "{\"value\":256}", "TCC > 255"),
            ("power.set_mode", "{\"mode\":7}", "mode outside 0-3"),
            ("power.apply_stock_mode", "{\"modeCase\":5}", "stock outside 0-2"),
            ("battery.set_mode", "{\"mode\":5}", "battery mode outside 0-2"),
            ("keyboard.set_brightness", "{\"brightness\":9}", "brightness outside 0-7"),
            ("keyboard.set_color", "{\"red\":51,\"green\":0,\"blue\":0}", "channel > 50"),
            ("fan.set_pwm", "{\"fanIndex\":0,\"duty\":201}", "EC duty > 200"),
            ("fan.set_switch_speed", "{\"rpm\":50}", "RPM below 100"),
        };

        await using var host = await EngineHostFixture.StartAsync();
        await PrimeGateAsync(host);

        foreach ((string verb, string json, string why) in cases)
        {
            JsonElement parameters = JsonDocument.Parse(json).RootElement.Clone();

            EngineException ex = await Assert.ThrowsAsync<EngineException>(
                () => host.Client.InvokeAsync<object>(verb, parameters));

            // The value is rejected, the register keeps its previous byte: nothing moved.
            Assert.True(ex.Code == EngineErrorCode.InvalidValue,
                $"{verb} ({why}) must be reported as InvalidValue, got {ex.Code}: {ex.Message}");
            Assert.Empty(host.Fake.Writes);
        }
    }

    [Fact]
    public async Task ApplyCpuPowerLimits_BadPl2_IsReported_BeforeTheActivationSequence()
    {
        await using var host = await EngineHostFixture.StartAsync();
        await PrimeGateAsync(host);

        EngineException ex = await Assert.ThrowsAsync<EngineException>(
            () => host.Power.ApplyCpuPowerLimitsAsync(80, 300));

        Assert.Equal(EngineErrorCode.InvalidValue, ex.Code);
        // No 0x0726/0x0727/0x0751 activation writes, no PL writes: validation
        // precedes the whole sequence, the EC is exactly as it was.
        Assert.Empty(host.Fake.Writes);
    }

    [Fact]
    public async Task ApplyCurve_NonAscendingTempSteps_IsRefused_EcUntouched()
    {
        await using var host = await EngineHostFixture.StartAsync();
        await PrimeGateAsync(host);

        var curve = new FanCurveDto { Name = "Custom", DutyPercentByZone = new int[] { 0, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30 } };

        // Zone 2 == zone 1: the EC walks the tables in ascending order, so the
        // codec refuses the plateau instead of clamping it into the EC.
        EngineException ex = await Assert.ThrowsAsync<EngineException>(
            () => host.Fan.ApplyCurveAsync(curve, curve,
                new int[] { 0, 40, 40, 60, 70, 80, 90, 95, 100, 105 },
                new int[] { 0, 40, 40, 60, 70, 80, 90, 95, 100, 105 }));

        Assert.Equal(EngineErrorCode.InvalidValue, ex.Code);
        Assert.Empty(host.Fake.Writes);
    }

    [Fact]
    public async Task ApplySmartFanDuty_DecreasingTable_IsRefused_EcUntouched()
    {
        await using var host = await EngineHostFixture.StartAsync();
        await PrimeGateAsync(host);

        byte[] descending = { 0, 100, 90, 90, 90, 90, 90, 90, 90, 90, 90, 90, 90, 90, 90, 90 };

        EngineException ex = await Assert.ThrowsAsync<EngineException>(
            () => host.Fan.ApplySmartFanDutyAsync(descending, descending));

        Assert.Equal(EngineErrorCode.InvalidValue, ex.Code);
        Assert.Empty(host.Fake.Writes);
    }

    // ---- param shape: schema failures are BadRequest, never Internal -----------

    [Fact]
    public async Task MalformedParams_AreBadRequest()
    {
        (string Verb, string Json)[] cases =
        {
            ("fan.set_speed_percent", "{\"percent\":\"hot\"}"),     // wrong type
            ("fan.write_register_blocks", "{\"blocks\":\"nope\"}"), // wrong shape
            ("device.set_nvram_value", "{\"guid\":{}}"),            // nested junk
        };

        await using var host = await EngineHostFixture.StartAsync();

        foreach ((string verb, string json) in cases)
        {
            JsonElement parameters = JsonDocument.Parse(json).RootElement.Clone();

            EngineException ex = await Assert.ThrowsAsync<EngineException>(
                () => host.Client.InvokeAsync<object>(verb, parameters));

            Assert.True(ex.Code == EngineErrorCode.BadRequest,
                $"{verb} with {json} must be BadRequest, got {ex.Code}");
        }
    }

    // ---- the field-debugging escape valve --------------------------------------

    [Fact]
    public async Task ReportOnlyGate_LogsAndAllows_TheEnforceDefaultRefuses()
    {
        await using var reportOnly = await EngineHostFixture.StartAsync(
            configure: o => o with { RawWriteGate = RawWriteGateMode.ReportOnly });

        // The escape valve exists for field debugging of a too-strict allowlist;
        // the codec range checks above are NOT gated (bad values stay reported).
        await reportOnly.Telemetry.WriteRegisterAsync(0x0783, 0x2D);
        Assert.Equal((byte)0x2D, reportOnly.Fake.LastWriteAt(0x0783)!.Value);

        // The enforce default (asserted by every test above) refuses the same call.
        await using var enforcing = await EngineHostFixture.StartAsync();
        await Assert.ThrowsAsync<EngineException>(
            () => enforcing.Telemetry.WriteRegisterAsync(0x0783, 0x2D));
    }
}
