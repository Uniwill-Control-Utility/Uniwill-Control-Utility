using UCU.Contracts;
using UCU.Contracts.Capabilities;
using UCU.Core.Ec;
using UCU.Hardware.Ec;

namespace UCU.Engine.Tests;

/// <summary>
/// The RPC end-to-end suite: client proxy call → pipe → dispatcher → domain queue →
/// EcEngine → fake transport. The byte-level expectations are the same golden
/// sequences UCU.Hardware.Tests pins for the in-process engine, so a phase-2
/// regression can only be in the transport layer, never in the EC logic.
/// </summary>
public sealed class EngineRpcTests
{
    private static readonly int[] BalancedDuties = { 0, 30, 32, 36, 40, 47, 55, 62, 72, 84, 100 };
    private static readonly int[] SilentDuties = { 0, 20, 25, 30, 38, 43, 48, 53, 58, 58, 58 };

    [Fact]
    public async Task Ping_RoundTrips()
    {
        await using var host = await EngineHostFixture.StartAsync();

        Assert.True(await host.Engine.PingAsync());
    }

    [Fact]
    public async Task NotifyAppExit_RoundTrips()
    {
        await using var host = await EngineHostFixture.StartAsync();

        // The supervisor's graceful-vs-crash input: the app sends this on its way
        // out (App.OnExit), and the engine records it in AppExitSignal.
        Assert.True(await host.Engine.NotifyAppExitAsync());
    }

    [Fact]
    public async Task GetInfo_ReportsProtocolPlatformAndAvailability()
    {
        await using var host = await EngineHostFixture.StartAsync();

        EngineInfoDto info = await host.Engine.GetInfoAsync();

        Assert.Equal(EngineProtocol.Version, info.ProtocolVersion);
        Assert.Equal(EcPlatform.Amd, info.Platform);
        Assert.True(info.EcAvailable);
        Assert.False(string.IsNullOrEmpty(info.EngineVersion));
        Assert.Equal(Environment.ProcessId, info.ProcessId);
    }

    [Fact]
    public async Task ApplyCurve_WritesTheGoldenEcSequenceThroughRpc()
    {
        await using var host = await EngineHostFixture.StartAsync();

        // Trigger the gate (driver open + activation handshake) and clear the log, so
        // the assertions below cover only the curve writes.
        await host.Fan.GetStatusAsync();
        host.Fake.ClearLog();

        await host.Fan.ApplyCurveAsync(
            new FanCurveDto { Name = "Balanced", DutyPercentByZone = BalancedDuties },
            new FanCurveDto { Name = "Silent", DutyPercentByZone = SilentDuties });

        Assert.Equal(
            new byte[] { 0, 60, 64, 72, 80, 94, 110, 124, 144, 168, 200, 200, 200, 200, 200, 200 },
            host.Fake.TableAt(EcEngine.REG_CPU_FAN_SPEED_TABLE));
        Assert.Equal(
            new byte[] { 0, 40, 50, 60, 76, 86, 96, 106, 116, 116, 116, 116, 116, 116, 116, 116 },
            host.Fake.TableAt(EcEngine.REG_GPU_FAN_SPEED_TABLE));

        // The ENABLE_CUSTOM toggle that makes the EC re-read the tables, after the tables.
        Assert.Equal(new byte[] { 0x00, 0x04 }, host.Fake.WritesAt(EcEngine.REG_AP_OEM_6).ToArray());
        Assert.True(
            host.Fake.LastWriteIndex(EcEngine.REG_CPU_FAN_SPEED_TABLE) <
            host.Fake.FirstWriteIndex(EcEngine.REG_AP_OEM_6));
    }

    [Fact]
    public async Task WriteRegisterBlocks_ArrivesInOrderOnTheFanQueue()
    {
        await using var host = await EngineHostFixture.StartAsync();

        // Two sequential planner calls: the queue must preserve the callers' order.
        await host.Fan.WriteRegisterBlocksAsync(new[] { new EcRegisterBlockDto { Address = 0x0600, Data = new byte[] { 0xAA } } });
        await host.Fan.WriteRegisterBlocksAsync(new[] { new EcRegisterBlockDto { Address = 0x0600, Data = new byte[] { 0xBB } } });

        Assert.Equal(new byte[] { 0xAA, 0xBB }, host.Fake.WritesAt(0x0600).ToArray());
    }

    [Fact]
    public async Task TelemetryReads_DecodeFromPresetRegisters()
    {
        var transport = new FakeEcTransport();
        transport.Preset(EcEngine.REG_CPU_TEMP, 61);
        transport.Preset(EcEngine.REG_GPU_TEMP, 55);
        transport.Preset(EcEngine.REG_PWM_1, 90);
        await using var host = await EngineHostFixture.StartAsync(transport: transport);

        Assert.Equal(61, await host.Telemetry.GetCpuTemperatureAsync());
        Assert.Equal(55, await host.Telemetry.GetGpuTemperatureAsync());
        Assert.Equal(90, (int)await host.Telemetry.ReadRegisterAsync(EcEngine.REG_PWM_1));
    }

    [Fact]
    public async Task ReadRegisters_ServesTheWholeBlock()
    {
        var transport = new FakeEcTransport();
        transport.Preset(0x0402, 1, 2, 3, 4);
        await using var host = await EngineHostFixture.StartAsync(transport: transport);

        Assert.Equal(new byte[] { 1, 2, 3, 4 }, await host.Telemetry.ReadRegistersAsync(0x0402, 4));
    }

    [Fact]
    public async Task WriteRegister_ReachesTheTransport()
    {
        await using var host = await EngineHostFixture.StartAsync();

        await host.Telemetry.WriteRegisterAsync(0x07FF, 0x5A);

        Assert.Equal((byte)0x5A, host.Fake.LastWriteAt(0x07FF)!.Value);
    }

    [Theory]
    [InlineData(GpuMuxMode.Hybrid, 0x04)]
    [InlineData(GpuMuxMode.DgpuOnly, 0x02)]
    [InlineData(GpuMuxMode.IgpuOnly, 0x01)]
    public async Task SetMuxMode_UsesThePlatformTheEngineResolved(GpuMuxMode mode, byte expectedRaw)
    {
        var transport = new FakeEcTransport();
        transport.Preset(EcEngine.REG_GPU_STATUS, 0xFF);
        await using var host = await EngineHostFixture.StartAsync(transport: transport, platform: EcPlatform.Intel);

        await host.Mux.SetModeAsync(mode);

        Assert.Equal($"0x072A=0x{expectedRaw:X2}", transport.SequenceOf(EcEngine.REG_GPU_STATUS));
    }

    [Fact]
    public async Task GetMuxMode_CarriesTheEnginePlatformForNvramEncoding()
    {
        var transport = new FakeEcTransport();
        transport.Preset(EcEngine.REG_GPU_STATUS, 0x04);
        await using var host = await EngineHostFixture.StartAsync(transport: transport, platform: EcPlatform.Intel);

        MuxStateDto state = await host.Mux.GetStateAsync();

        Assert.Equal(GpuMuxMode.Hybrid, state.Mode);
        Assert.Equal(EcPlatform.Intel, state.Platform);
    }

    [Fact]
    public async Task BatteryMode_RoundTripsThroughTheWire()
    {
        var transport = new FakeEcTransport();
        transport.Preset(EcEngine.REG_BATTERY_MODE, 0x18);
        await using var host = await EngineHostFixture.StartAsync(transport: transport);

        Assert.Equal(1, await host.Battery.GetModeAsync());

        await host.Battery.SetModeAsync(2);

        Assert.Equal("0x07A6=0x28", transport.SequenceOf(EcEngine.REG_BATTERY_MODE));
    }

    [Fact]
    public async Task BatteryTelemetry_DecodesThePresetBlock()
    {
        var transport = new FakeEcTransport();
        transport.Preset(EcEngine.REG_BATTERY_SOC, 55);
        transport.Preset(EcEngine.REG_BATTERY_TEMP_LO, 0xA5, 0x0B); // 2981 = 24 C
        transport.Preset(EcEngine.REG_BATTERY_DESIGN_CAP_LO, 0x88, 0x13);   // 5000
        transport.Preset(EcEngine.REG_BATTERY_FULL_CHARGE_CAP_LO, 0x00, 0x0F); // 3840
        await using var host = await EngineHostFixture.StartAsync(transport: transport);

        BatteryTelemetryDto? telemetry = await host.Battery.GetTelemetryAsync();

        Assert.NotNull(telemetry);
        Assert.Equal(55, telemetry.StateOfCharge);
        Assert.Equal(24, telemetry.TemperatureC);
        Assert.Equal(76, telemetry.HealthPercent);
    }

    [Fact]
    public async Task FanStatus_AggregatesOneConsistentSnapshot()
    {
        var transport = new FakeEcTransport();
        transport.Preset(EcEngine.REG_CPU_TEMP, 61);
        transport.Preset(EcEngine.REG_SUPPORT_5, EcEngine.FAN_SUPPORT);
        transport.Preset(EcEngine.REG_MODE_INDEX, 0x01);
        await using var host = await EngineHostFixture.StartAsync(transport: transport);

        FanStatusDto status = await host.Fan.GetStatusAsync();

        Assert.Equal(61, status.CpuTemperature);
        Assert.True(status.FanControlSupported);
    }
}
