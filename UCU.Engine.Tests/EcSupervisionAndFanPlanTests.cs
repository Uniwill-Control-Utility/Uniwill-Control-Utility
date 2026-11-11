using UCU.Contracts;
using UCU.Contracts.Capabilities;
using UCU.Core.Ec;
using UCU.Core.Ec.Generated;
using UCU.Hardware.Ec;

namespace UCU.Engine.Tests;

/// <summary>
/// EC supervision and the fan-plan boundary (US-08, US-09).
///
/// US-08: a lost activation bit or a resume must re-run the handshake. The rules
/// themselves are pure (EcSupervisionPolicyTests); here the gate is pinned to actually
/// re-initialise when told to.
///
/// US-09: the single-register telemetry verb must not reach the fan tables, because it
/// runs on another queue than the planner and a raw byte between two plan blocks leaves
/// the table half-written.
/// </summary>
public sealed class EcSupervisionAndFanPlanTests
{
    [Fact]
    public void Invalidate_ReRunsTheActivationHandshake_AndOnlyThen()
    {
        var transport = new FakeEcTransport();
        var options = new EngineRuntimeOptions
        {
            TransportOverride = transport,
            PlatformOverride = EcPlatform.Amd,
        };

        using var gate = new EcEngineGate(options);
        gate.Acquire();
        int afterFirst = transport.WritesAt(EcVault.REG_FAN_CURVE_PARAM_7C7).Count;
        Assert.True(afterFirst > 0, "the first Acquire must run the handshake");

        gate.Acquire();
        Assert.Equal(afterFirst, transport.WritesAt(EcVault.REG_FAN_CURVE_PARAM_7C7).Count);

        gate.Invalidate();
        gate.Acquire();

        Assert.Equal(afterFirst * 2, transport.WritesAt(EcVault.REG_FAN_CURVE_PARAM_7C7).Count);
    }

    [Fact]
    public async Task TelemetryRawWrite_CannotTouchTheFanTables()
    {
        await using var host = await EngineHostFixture.StartAsync();
        await host.Fan.GetStatusAsync();

        EngineException ex = await Assert.ThrowsAsync<EngineException>(
            () => host.Telemetry.WriteRegisterAsync(EcEngine.REG_CPU_FAN_SPEED_TABLE, 0xFF));

        Assert.Equal(EngineErrorCode.BadRequest, ex.Code);
    }

    [Fact]
    public async Task FanBlockWrite_StillOwnsTheTables()
    {
        await using var host = await EngineHostFixture.StartAsync();
        await host.Fan.GetStatusAsync();

        await host.Fan.WriteRegisterBlocksAsync(new[]
        {
            new EcRegisterBlockDto
            {
                Address = EcEngine.REG_CPU_FAN_SPEED_TABLE,
                Data = new byte[] { 0, 60, 64, 72, 80, 94, 110, 124, 144, 168, 200, 200, 200, 200, 200, 200 },
            },
        });

        Assert.Equal(60, host.Fake.TableAt(EcEngine.REG_CPU_FAN_SPEED_TABLE)[1]);
    }
}
