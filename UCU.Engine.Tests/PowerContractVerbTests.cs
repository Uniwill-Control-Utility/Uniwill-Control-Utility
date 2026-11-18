using UCU.Contracts;
using UCU.Hardware.Ec;

namespace UCU.Engine.Tests;

/// <summary>
/// The power-contract verbs end to end (U-107): client proxy → pipe →
/// dispatcher → power domain → EcEngine on a scripted transport. The truth
/// verb is the guardian's drift oracle (the EC's own bytes, since laptop
/// NVML answers NOT_SUPPORTED for power limits); the heal verb replays the
/// preset pipeline's own activation sequence behind per-half flags, with the
/// preset path's validators on the wire (out-of-range → InvalidValue, the
/// EC untouched by the refused half).
/// </summary>
public sealed class PowerContractVerbTests
{
    [Fact]
    public async Task GetContractTruth_RoundTripsTheEcRegisters()
    {
        var transport = new FakeEcTransport();
        transport.Preset(EcEngine.REG_CTGP_DB_ENABLE, 0x07);
        transport.Preset(EcEngine.REG_CTGP_DB_CTGP_OFFSET, 70);
        transport.Preset(EcEngine.REG_CTGP_DB_TPP_OFFSET, 0xFF);
        transport.Preset(EcEngine.REG_CTGP_DB_DB_OFFSET, 25);
        transport.Preset(EcEngine.REG_CUSTOM_PROFILE, 0x40);
        transport.Preset(EcEngine.REG_PL1_SETTING, 75);
        transport.Preset(EcEngine.REG_PL2_SETTING, 85);
        // Before the engine starts: the activation handshake warms the table once, so a
        // table scripted later would land after the cached answer.
        transport.PresetSpecTable(0xD2, 0xD2, 0xD2, 0x0A, 0x37, 0x19, 0x57, 0x5F, 0xFF);

        await using var host = await EngineHostFixture.StartAsync(transport: transport);

        PowerContractTruthDto truth = await host.Power.GetContractTruthAsync();

        Assert.Equal(0x07, truth.GpuEnable);
        Assert.Equal(70, truth.GpuCtgp);
        Assert.Equal(0xFF, truth.GpuTpp);
        Assert.Equal(25, truth.GpuDb);
        Assert.True(truth.GpuCustomProfileActive);
        Assert.Equal(75, truth.Pl1);
        Assert.Equal(85, truth.Pl2);

        // The GPU power gate rides the same power domain: with an ECSpec table the answer is
        // the board's DefaultTGP, the base the cTGP offset is measured from (0x0744 stores a
        // TGP offset, not a TGP). The refused-table path is pinned at the hardware layer,
        // EcEngineEcSpecTests, so this test does not need a second host for it.
        GpuPowerInfoDto info = await host.Power.GetGpuPowerInfoAsync();

        Assert.True(info.Supported);
        Assert.Equal(95, info.DefaultTgpWatts);
    }

    [Fact]
    public async Task ActivateGpuPowerControl_HandsTheEcCeilingToTheUnlockTarget()
    {
        // A preset apply writes the EC's cTGP offset and nothing else knows the ceiling: the
        // offset is over this board's DefaultTGP (ECSpec 95 W), so offset 110 is a 205 W EC
        // ceiling, and the unlock's target must follow it down rather than keep aiming higher.
        var transport = new FakeEcTransport();
        transport.PresetSpecTable(0xD2, 0xD2, 0xD2, 0x0A, 0x37, 0x19, 0x57, 0x5F, 0xFF);

        await using var host = await EngineHostFixture.StartAsync(transport: transport);

        await host.Power.ActivateGpuPowerControlAsync(ctgpOffset: 110, dbOffset: 0);

        Assert.Equal(new[] { 205_000 }, host.FakeRomOverrides.ClampCalls);
    }

    [Fact]
    public async Task ActivateGpuPowerControl_NoEcSpecTable_HandsNoCeilingToTheUnlock()
    {
        // Without a DefaultTGP the offset alone is not watts: the store is not given a number
        // invented from the 55 W fallback (which would clamp the target to a guess).
        await using var host = await EngineHostFixture.StartAsync();

        await host.Power.ActivateGpuPowerControlAsync(ctgpOffset: 110, dbOffset: 0);

        Assert.Empty(host.FakeRomOverrides.ClampCalls);
    }

    [Fact]
    public async Task HealContract_GpuHalf_ReplaysTheActivation_MovesNoCpuLimits()
    {
        await using var host = await EngineHostFixture.StartAsync();

        await host.Power.HealContractAsync(new HealContractDto
        {
            ApplyGpu = true,
            GpuCtgpOffset = 70,
            GpuDbOffset = 25,
        });

        PowerContractTruthDto truth = await host.Power.GetContractTruthAsync();
        Assert.Equal(0x07, truth.GpuEnable);
        Assert.Equal(70, truth.GpuCtgp);
        Assert.Equal(0xFF, truth.GpuTpp);
        Assert.Equal(25, truth.GpuDb);
        Assert.True(truth.GpuCustomProfileActive);

        // The CPU half behind its unset flag: PL registers never written.
        Assert.DoesNotContain(host.Fake.Writes, w => w.Address == EcEngine.REG_PL1_SETTING);
        Assert.DoesNotContain(host.Fake.Writes, w => w.Address == EcEngine.REG_PL2_SETTING);
    }

    [Fact]
    public async Task HealContract_CpuHalf_WritesThePlPairPlusCeiling()
    {
        await using var host = await EngineHostFixture.StartAsync();

        await host.Power.HealContractAsync(new HealContractDto
        {
            ApplyCpu = true,
            Pl1Watts = 75,
            Pl2Watts = 85,
        });

        PowerContractTruthDto truth = await host.Power.GetContractTruthAsync();
        Assert.Equal(75, truth.Pl1);
        Assert.Equal(85, truth.Pl2);

        // The GPU half behind its unset flag: no activation sequence.
        Assert.DoesNotContain(host.Fake.Writes, w => w.Address == EcEngine.REG_CTGP_DB_CTGP_OFFSET);
    }

    [Fact]
    public async Task HealContract_BothFlags_AppliesBothHalves()
    {
        await using var host = await EngineHostFixture.StartAsync();

        await host.Power.HealContractAsync(new HealContractDto
        {
            ApplyGpu = true, GpuCtgpOffset = 40, GpuDbOffset = 0,
            ApplyCpu = true, Pl1Watts = 45, Pl2Watts = 45,
        });

        PowerContractTruthDto truth = await host.Power.GetContractTruthAsync();
        Assert.Equal(0x05, truth.GpuEnable); // db-disabled path: no DB chase bit
        Assert.Equal(0x00, truth.GpuTpp);
        Assert.Equal(40, truth.GpuCtgp);
        Assert.Equal(45, truth.Pl1);
        Assert.Equal(45, truth.Pl2);
    }

    [Fact]
    public async Task HealContract_OutOfRangeGpuOffset_IsInvalidValue_AndTheCpuHalfIsNotWritten()
    {
        await using var host = await EngineHostFixture.StartAsync();

        EngineException ex = await Assert.ThrowsAsync<EngineException>(() =>
            host.Power.HealContractAsync(new HealContractDto
            {
                ApplyGpu = true, GpuCtgpOffset = 256, GpuDbOffset = 25,
                ApplyCpu = true, Pl1Watts = 75, Pl2Watts = 85,
            }));

        Assert.Equal(EngineErrorCode.InvalidValue, ex.Code);

        // The validator refuses inside ActivateGpuPowerControl before its
        // first write (U-102's refuse-at-the-boundary rule); the sequence
        // stops there, so the CPU half of the plan never executed.
        Assert.DoesNotContain(host.Fake.Writes, w => w.Address == EcEngine.REG_CTGP_DB_CTGP_OFFSET);
        Assert.DoesNotContain(host.Fake.Writes, w => w.Address == EcEngine.REG_PL1_SETTING);
    }

    [Fact]
    public async Task HealContract_NoFlags_LandsNothing()
    {
        await using var host = await EngineHostFixture.StartAsync();

        await host.Power.HealContractAsync(new HealContractDto());

        PowerContractTruthDto truth = await host.Power.GetContractTruthAsync();
        Assert.Equal(0, truth.GpuEnable);
        Assert.Equal(0, truth.Pl1);
    }
}
