using UCU.Contracts;
using UCU.Contracts.Capabilities;
using UCU.Engine.Tests;

namespace UCU.Engine.Tests;

/// <summary>
/// EC sensor bytes are gated before they reach a page or an overlay: an implausible
/// temperature or fan speed becomes -1 ("unknown"), which every consumer already
/// renders as "--" or omits.
///
/// The EC answers 0xFF while it reinitialises and 0 for a channel it has switched off
/// (a dGPU parked by GPU Power Saving), so both directions need a rule.
/// </summary>
public sealed class SensorPlausibilityVerbTests
{
    [Fact]
    public async Task FanStatus_ReportsGarbageTemperaturesAsUnknown()
    {
        await using var host = await EngineHostFixture.StartAsync();
        await host.Fan.GetStatusAsync(); // runs the handshake so the fake is warm

        // 0xFA = 250 °C, 0x00 = "no channel"; 0xFFFF on the tach pairs.
        host.Fake.Preset(EcRegisters.CpuTemperature, 0xFA);
        host.Fake.Preset(EcRegisters.GpuTemperature, 0x00);
        host.Fake.Preset(EcRegisters.CpuRpmHigh, 0xFF);
        host.Fake.Preset(EcRegisters.CpuRpmLow, 0xFF);

        FanStatusDto status = await host.Fan.GetStatusAsync();

        Assert.Equal(EcRegisters.Unknown, status.CpuTemperature);
        Assert.Equal(EcRegisters.Unknown, status.GpuTemperature);
        Assert.Equal(EcRegisters.Unknown, status.MainFanRpm);
    }

    [Fact]
    public async Task Telemetry_ReportsGarbageTemperaturesAsUnknown()
    {
        await using var host = await EngineHostFixture.StartAsync();
        await host.Fan.GetStatusAsync();

        host.Fake.Preset(EcRegisters.CpuTemperature, 0xFF);

        Assert.Equal(EcRegisters.Unknown, await host.Telemetry.GetCpuTemperatureAsync());
    }

    [Fact]
    public async Task FanStatus_KeepsPlausibleReadingsUntouched()
    {
        await using var host = await EngineHostFixture.StartAsync();
        await host.Fan.GetStatusAsync();

        host.Fake.Preset(EcRegisters.CpuTemperature, 61);
        host.Fake.Preset(EcRegisters.GpuTemperature, 1);
        host.Fake.Preset(EcRegisters.CpuRpmHigh, 0x0B);
        host.Fake.Preset(EcRegisters.CpuRpmLow, 0xB8); // 3000 rpm

        FanStatusDto status = await host.Fan.GetStatusAsync();

        Assert.Equal(61, status.CpuTemperature);
        Assert.Equal(1, status.GpuTemperature);
        Assert.Equal(3000, status.MainFanRpm);
    }

    /// <summary>Register addresses and the unknown sentinel, kept out of the tests' bodies.</summary>
    private static class EcRegisters
    {
        public const ushort CpuTemperature = 0x043E;
        public const ushort GpuTemperature = 0x044F;
        public const ushort CpuRpmLow = 0x0465;
        public const ushort CpuRpmHigh = 0x0464;
        public const int Unknown = -1;
    }
}
