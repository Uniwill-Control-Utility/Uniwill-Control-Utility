using UCU.Contracts;
using UCU.Hardware.Cpu;

namespace UCU.Engine.Tests;

/// <summary>
/// The SMU verb end to end: client proxy → pipe → dispatcher → cpu queue →
/// scripted command table. These are the applies that were silently dead in
/// the unelevated app — PawnIO's device ACL admits System and Administrators,
/// so the app's driver open was access-denied, the preset row saved, and the
/// register never moved. Over the pipe the engine's token loads PawnIO and
/// the verb answers with the SMU's own status code. The suite pins the
/// semantics the preset interpreter depends on: the status-code round-trip,
/// the packed uint values (Curve Optimizer signs in the high bits, beyond the
/// int range), and the two fault shapes the mailbox protocol throws mapped to
/// the codes the app's routing reacts to (InvalidValue for an unknown command,
/// HardwareFault for a real SMU refusal — and only HardwareFault-class codes
/// keep the app from falling back).
/// </summary>
public sealed class CpuVerbsTests
{
    [Fact]
    public async Task ExecuteAmdSmuCommand_RoundTripsNameValue_AndAnswersStatus()
    {
        var smu = new FakeAmdSmuService().Answers("set-coper", 0x01);

        await using var host = await EngineHostFixture.StartAsync(
            configure: o => o with { AmdSmuOverride = smu });

        int status = await host.Smu.ExecuteCommandAsync("set-coper", 0x00000003);

        Assert.Equal(0x01, status);
        Assert.Equal(new[] { ("set-coper", 0x00000003u) }, smu.Calls);
    }

    [Fact]
    public async Task ExecuteAmdSmuCommand_PackedCurveOptimizerValue_SurvivesTheWire()
    {
        // The CO encoding signs in the high bits (0xE0000004): the DTO's uint
        // must ride the wire intact, not clipped to an int.
        var smu = new FakeAmdSmuService().Answers("set-coper");

        await using var host = await EngineHostFixture.StartAsync(
            configure: o => o with { AmdSmuOverride = smu });

        await host.Smu.ExecuteCommandAsync("set-coper", 0xE0000004);

        Assert.Equal(0xE0000004u, smu.Calls[0].Value);
    }

    [Fact]
    public async Task ExecuteAmdSmuCommand_UnknownCommand_IsInvalidValue()
    {
        // The preset author typed a command the socket table does not have:
        // a request fault, not a hardware fault, and the app's fallback-first
        // routing must NOT treat it as "engine not answering".
        await using var host = await EngineHostFixture.StartAsync();

        EngineException ex = await Assert.ThrowsAsync<EngineException>(
            () => host.Smu.ExecuteCommandAsync("no-such-command", 0));

        Assert.Equal(EngineErrorCode.InvalidValue, ex.Code);
    }

    [Fact]
    public async Task ExecuteAmdSmuCommand_SmuRefusal_IsHardwareFault()
    {
        // PawnIO init failure / SMU non-OK status throw InvalidOperationException
        // on the service (verbatim from the ported stack); the client sees
        // HardwareFault, which the app's route reports instead of falling back.
        var smu = new FakeAmdSmuService().Fails("ppt-limit",
            new InvalidOperationException("SMU command 'ppt-limit' failed with status FAILED."));

        await using var host = await EngineHostFixture.StartAsync(
            configure: o => o with { AmdSmuOverride = smu });

        EngineException ex = await Assert.ThrowsAsync<EngineException>(
            () => host.Smu.ExecuteCommandAsync("ppt-limit", 17000));

        Assert.Equal(EngineErrorCode.HardwareFault, ex.Code);
    }

    [Fact]
    public async Task ExecuteAmdSmuCommand_RunsOnTheCpuQueue_InOrder()
    {
        // Two applies for one preset batch must not interleave on the shared
        // mailbox: the domain queue serialises the whole handler.
        var smu = new FakeAmdSmuService().Answers("a-cmd").Answers("b-cmd");

        await using var host = await EngineHostFixture.StartAsync(
            configure: o => o with { AmdSmuOverride = smu });

        await host.Smu.ExecuteCommandAsync("a-cmd", 1);
        await host.Smu.ExecuteCommandAsync("b-cmd", 2);

        Assert.Equal(new[] { ("a-cmd", 1u), ("b-cmd", 2u) }, smu.Calls);
    }
}
