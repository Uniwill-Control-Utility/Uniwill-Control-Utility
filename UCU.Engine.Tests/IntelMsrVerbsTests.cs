using UCU.Hardware.Cpu.Intel;

namespace UCU.Engine.Tests;

/// <summary>
/// The Intel MSR verbs end to end: client proxy → pipe → dispatcher → cpu
/// queue → scripted register file. These are the Intel applies that were
/// silently dead in the unelevated app — the MSR registers ride the same
/// PawnIO device as the SMU (ACL: System and Administrators), so the app's
/// driver open was access-denied and its methods swallowed that into a log
/// line. Over the pipe the engine writes the MSRs as LocalSystem, and the
/// verb answers "the write landed". The suite pins the encodings the preset
/// interpreter depends on (the TDP pair, the plane command words, the packed
/// ratio bytes) and the honest answer shapes: a failed write is a false, not
/// a silent success; out-of-range is the ported stack's silent no-op (true,
/// nothing written).
/// </summary>
public sealed class IntelMsrVerbsTests
{
    [Fact]
    public async Task SetTdp_PacksThePairIntoTheMsrEncoding()
    {
        var msr = new FakeIntelMsrService();

        await using var host = await EngineHostFixture.StartAsync(
            configure: o => o with { IntelMsrOverride = msr });

        Assert.True(await host.IntelMsr.SetTdpAsync(28, 44));

        // The ported stack's encoding: PL2*8 in the high dword (base 0x00438000),
        // (PL1-1)*8 in the low (base 0x00DD8000).
        ulong value = msr.Registers[0x610];
        uint high = (uint)(value >> 32);
        uint low = (uint)value;
        Assert.Equal((uint)(0x00438000 | (44 * 8)), high);
        Assert.Equal((uint)(0x00DD8000 | (27 * 8)), low);
    }

    [Fact]
    public async Task SetPowerBalance_SelectsThePlaneRegister_AndKeepsTheNoOp()
    {
        var msr = new FakeIntelMsrService();

        await using var host = await EngineHostFixture.StartAsync(
            configure: o => o with { IntelMsrOverride = msr });

        Assert.True(await host.IntelMsr.SetPowerBalanceAsync(20, 0));
        Assert.Equal(20ul, msr.Registers[0x63A]);

        Assert.True(await host.IntelMsr.SetPowerBalanceAsync(11, 1));
        Assert.Equal(11ul, msr.Registers[0x642]);

        // Out of range is the ported stack's silent no-op (true, nothing
        // written) — the verb must not report it as a failure.
        Assert.True(await host.IntelMsr.SetPowerBalanceAsync(50, 0));
        Assert.Equal(20ul, msr.Registers[0x63A]);
    }

    [Fact]
    public async Task SetVoltageOffset_SelectsThePlaneCommandWord()
    {
        var msr = new FakeIntelMsrService();

        await using var host = await EngineHostFixture.StartAsync(
            configure: o => o with { IntelMsrOverride = msr });

        Assert.True(await host.IntelMsr.SetVoltageOffsetAsync(-100, 2));

        ulong value = msr.Registers[0x150];
        uint command = (uint)(value >> 32);
        uint data = (uint)value;

        Assert.Equal(0x80000211u, command); // cache plane
        Assert.Equal(unchecked((uint)(-102 << 21)), data); // round(-100 * 1.024) << 21
    }

    [Fact]
    public async Task SetClockRatio_PacksTheRatioBytes_OverTheWire()
    {
        var msr = new FakeIntelMsrService();

        await using var host = await EngineHostFixture.StartAsync(
            configure: o => o with { IntelMsrOverride = msr });

        int[] ratios = new[] { 40, 40, 38, 38, 36, 36, 34, 34 };

        Assert.True(await host.IntelMsr.SetClockRatiosAsync(ratios));

        Assert.Equal(0x2828262624242222ul, msr.Registers[0x1AD]);
    }

    [Fact]
    public async Task GetClockRatios_RoundTripsTheScriptedBytes()
    {
        var msr = new FakeIntelMsrService { ClockRatios = new[] { 45, 45, 45, 45, 40, 40, 35, 30 } };

        await using var host = await EngineHostFixture.StartAsync(
            configure: o => o with { IntelMsrOverride = msr });

        Assert.Equal(msr.ClockRatios, await host.IntelMsr.GetClockRatiosAsync());
    }

    [Fact]
    public async Task WriteFailure_AnswersFalse_NotASilentSuccess()
    {
        // The honest signal the app route never had: the MSR write failed
        // (HRESULT in the engine log) and the verb carries that as false,
        // which the app's route renders as "landed=False" — no fallback,
        // because the engine's token IS the privileged one.
        var msr = new FakeIntelMsrService();
        msr.FailingWrites.Add(0x610);

        await using var host = await EngineHostFixture.StartAsync(
            configure: o => o with { IntelMsrOverride = msr });

        Assert.False(await host.IntelMsr.SetTdpAsync(28, 44));
    }

    [Fact]
    public async Task ReadFailure_AnswersEmpty_LikeThePortedStacksShape()
    {
        var msr = new FakeIntelMsrService { GetClockRatiosFails = true };

        await using var host = await EngineHostFixture.StartAsync(
            configure: o => o with { IntelMsrOverride = msr });

        Assert.Empty(await host.IntelMsr.GetClockRatiosAsync());
    }
}
