using UCU.Contracts;
using UCU.Contracts.Capabilities;

namespace UCU.Engine.Tests;

/// <summary>
/// The support snapshot verb (US-10): identity, limits, cooling flags, sensors and the
/// six fan tables in one read-only pass. It is what a bug report needs to be answerable
/// without a conversation - EC version and the exact table bytes first.
/// </summary>
public sealed class TelemetryDumpVerbTests
{
    [Fact]
    public async Task DumpState_ReturnsIdentitySensorsAndTables()
    {
        await using var host = await EngineHostFixture.StartAsync();
        await host.Fan.GetStatusAsync(); // runs the handshake so the fake EC is warm

        host.Fake.Preset(0x0454, 0x74); // EC 116
        host.Fake.Preset(0x047D, 0x00);
        host.Fake.Preset(0x043E, 61);
        host.Fake.Preset(0x044F, 44);
        host.Fake.Preset(0x0F20, new byte[] { 0x00, 0x3C, 0x40, 0x48 });

        EcHardwareDumpDto dump = await host.Client.InvokeAsync<EcHardwareDumpDto>(
            EngineVerbs.Telemetry.DumpState, CancellationToken.None);

        Assert.Equal(116, dump.EcVersionMajor);
        Assert.Equal(0, dump.EcVersionMinor);
        Assert.Equal(61, dump.CpuTemperature);
        Assert.Equal(44, dump.GpuTemperature);
        Assert.Equal(16, dump.CpuDuty.Length);
        Assert.Equal(0x3C, dump.CpuDuty[1]);
        Assert.Equal(16, dump.GpuDuty.Length);
    }

    [Fact]
    public async Task DumpState_IsReadOnly()
    {
        await using var host = await EngineHostFixture.StartAsync();
        await host.Fan.GetStatusAsync();
        host.Fake.ClearLog();

        await host.Client.InvokeAsync<EcHardwareDumpDto>(EngineVerbs.Telemetry.DumpState, CancellationToken.None);

        Assert.Empty(host.Fake.Writes);
    }
}
