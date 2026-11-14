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
    public async Task ReadAmdSmuCommand_AnswersTheMailboxResponseArguments()
    {
        var smu = new FakeAmdSmuService()
            .AnswersRead("get-coper-options", 0x01, 0x11, 0x22, 0x33, 0x44, 0x55);

        await using var host = await EngineHostFixture.StartAsync(
            configure: o => o with { AmdSmuOverride = smu });

        uint[] answer = await host.Smu.ReadCommandAsync("get-coper-options", 0);

        Assert.Equal(new[] { 0x01u, 0x11u, 0x22u, 0x33u, 0x44u, 0x55u }, answer);
        Assert.Equal(new[] { ("get-coper-options", 0u) }, smu.Calls);
    }

    [Fact]
    public async Task ReadAmdSmuCommand_RefusesAWriteCommand()
    {
        // The read verb carries read mailboxes only: a "set-" command must not be
        // reachable through it, and the refusal happens before the hardware seam.
        var smu = new FakeAmdSmuService().Answers("set-coper").AnswersRead("get-coper-options", 0x01);

        await using var host = await EngineHostFixture.StartAsync(
            configure: o => o with { AmdSmuOverride = smu });

        await Assert.ThrowsAsync<EngineException>(
            () => host.Smu.ReadCommandAsync("set-coper", 1));

        Assert.Empty(smu.Calls);
    }

    [Fact]
    public async Task SetCoperMany_AppliesEveryValueInOrderAndAnswersOk()
    {
        var smu = new FakeAmdSmuService().Answers("set-coper");

        await using var host = await EngineHostFixture.StartAsync(
            configure: o => o with { AmdSmuOverride = smu });

        int status = await host.Smu.ExecuteCoperBatchAsync(new uint[] { 0x11, 0x22, 0x33 });

        Assert.Equal(0x01, status);
        Assert.Equal(
            new[] { ("set-coper", 0x11u), ("set-coper", 0x22u), ("set-coper", 0x33u) },
            smu.Calls);
    }

    [Fact]
    public async Task SetCoperMany_RefusesAnEmptyBatch()
    {
        var smu = new FakeAmdSmuService().Answers("set-coper");

        await using var host = await EngineHostFixture.StartAsync(
            configure: o => o with { AmdSmuOverride = smu });

        await Assert.ThrowsAsync<EngineException>(
            () => host.Smu.ExecuteCoperBatchAsync(Array.Empty<uint>()));

        Assert.Empty(smu.Calls);
    }

    [Fact]
    public async Task SetCoperMany_StopsAtTheFirstNonOkStatus()
    {
        // The firmware refuses the first mailbox, so the rest must not be written - that is
        // the half-applied map the batch exists to prevent.
        var smu = new FakeAmdSmuService().Answers("set-coper", 0x02);

        await using var host = await EngineHostFixture.StartAsync(
            configure: o => o with { AmdSmuOverride = smu });

        int status = await host.Smu.ExecuteCoperBatchAsync(new uint[] { 0x11, 0x22, 0x33 });

        Assert.Equal(0x02, status);
        Assert.Single(smu.Calls);
    }

    [Fact]
    public async Task ExecuteAmdSmuCommand_RefusesAReadCommand()
    {
        // ...and the other direction: the write verb's allowlist must not admit a
        // query, so a query can never be smuggled through the register-write path.
        var smu = new FakeAmdSmuService().Answers("set-coper").AnswersRead("get-coper-options", 0x01);

        await using var host = await EngineHostFixture.StartAsync(
            configure: o => o with { AmdSmuOverride = smu });

        await Assert.ThrowsAsync<EngineException>(
            () => host.Smu.ExecuteCommandAsync("get-coper-options", 0));

        Assert.Empty(smu.Calls);
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

    [Theory]
    [InlineData("enable-oc")]
    [InlineData("oc-volt")]
    [InlineData("oc-clk")]
    [InlineData("per-core-oc-clk")]
    [InlineData("set-vddoff-vid")]
    [InlineData("set-cofall")]
    public async Task ExecuteAmdSmuCommand_OverclockingMailbox_IsRefusedBeforeTheMailbox(string name)
    {
        // The SMU table carries these, no row of the app sends them, and a wrong
        // value hangs the CPU until the battery is pulled. The name arrives from the
        // pipe, so the verb refuses it before the mailbox is touched.
        var smu = new FakeAmdSmuService();
        await using var host = await EngineHostFixture.StartAsync(
            configure: o => o with { AmdSmuOverride = smu });

        EngineException ex = await Assert.ThrowsAsync<EngineException>(
            () => host.Smu.ExecuteCommandAsync(name, 0xFFFFFFFF));

        Assert.Equal(EngineErrorCode.InvalidValue, ex.Code);
        Assert.Empty(smu.Calls);
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
    public async Task ExecuteAmdSmuCommand_PawnIoInitFailure_CarriesTheWin32BlockCodeInThePayload()
    {
        // U-110: the init-failure message embeds the Win32 open code (1275
        // names Core Isolation as the blocker — the code OpenRevo recognizes
        // from StartService), and the dispatcher propagates the
        // InvalidOperationException message verbatim, so the app explains
        // the block with the code in hand, not a bare "failed to initialise".
        var smu = new FakeAmdSmuService().Fails("set-coper",
            new InvalidOperationException(
                "AMD PawnIO failed to initialise. (Win32 1275 opening \\\\?\\GLOBALROOT\\Device\\PawnIO)"));

        await using var host = await EngineHostFixture.StartAsync(
            configure: o => o with { AmdSmuOverride = smu });

        EngineException ex = await Assert.ThrowsAsync<EngineException>(
            () => host.Smu.ExecuteCommandAsync("set-coper", 3));

        Assert.Equal(EngineErrorCode.HardwareFault, ex.Code);
        Assert.Contains("Win32 1275", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteAmdSmuCommand_RunsOnTheCpuQueue_InOrder()
    {
        // Two applies for one preset batch must not interleave on the shared
        // mailbox: the domain queue serialises the whole handler.
        var smu = new FakeAmdSmuService().Answers("slow-limit").Answers("fast-limit");

        await using var host = await EngineHostFixture.StartAsync(
            configure: o => o with { AmdSmuOverride = smu });

        await host.Smu.ExecuteCommandAsync("slow-limit", 1);
        await host.Smu.ExecuteCommandAsync("fast-limit", 2);

        Assert.Equal(new[] { ("slow-limit", 1u), ("fast-limit", 2u) }, smu.Calls);
    }
}
