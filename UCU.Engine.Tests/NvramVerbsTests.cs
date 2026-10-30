using UCU.Contracts;
using UCU.Hardware.Firmware;

namespace UCU.Engine.Tests;

/// <summary>
/// The NVRAM verbs end to end: client proxy → pipe → dispatcher → device queue →
/// scripted firmware variable store. These are the reads and writes that were
/// silently dead in the unelevated app — SeSystemEnvironmentPrivilege cannot be
/// enabled there, so the AP-menu row's support probe found nothing and its write
/// answered access denied, with no prompt to explain. Over the pipe the engine's
/// token answers the same calls. The suite pins the semantics the app's rows
/// depend on: the null-vs-bytes read shape, the whole-buffer write, and the
/// AP-menu pair-write verified by re-read (the two offsets can never
/// half-update).
/// </summary>
public sealed class NvramVerbsTests
{
    [Fact]
    public async Task GetNvramValue_RoundTripsTheBuffer()
    {
        var nvram = new FakeFwNvramService().WithOemMagic(supportByte: 1);

        await using var host = await EngineHostFixture.StartAsync(
            configure: o => o with { FwNvramOverride = nvram });

        byte[]? data = await host.Device.GetNvramValueAsync(
            FwNvramService.OemMagicVariable, FwNvramService.OemMagicVariableGuid);

        Assert.NotNull(data);
        Assert.Equal(120, data.Length);
        Assert.Equal(1, data[FwNvramService.OffsetCpuApMenuSupport]);
    }

    [Fact]
    public async Task GetNvramValue_UnknownVariable_IsNullOverTheWire()
    {
        // "No such variable" is the null buffer, not an error: that is the
        // shape the app's rows treat as "NVRAM unavailable".
        await using var host = await EngineHostFixture.StartAsync();

        Assert.Null(await host.Device.GetNvramValueAsync("NoSuchVariable", FwNvramService.OemMagicVariableGuid));
    }

    [Fact]
    public async Task SetNvramValue_WritesTheWholeBuffer_AndAnswersTrue()
    {
        var nvram = new FakeFwNvramService();

        await using var host = await EngineHostFixture.StartAsync(
            configure: o => o with { FwNvramOverride = nvram });

        byte[] payload = new byte[] { 1, 2, 3, 4, 250 };

        Assert.True(await host.Device.SetNvramValueAsync("OemTest", FwNvramService.OemMagicVariableGuid, payload));

        byte[]? readback = await host.Device.GetNvramValueAsync("OemTest", FwNvramService.OemMagicVariableGuid);
        Assert.Equal(payload, readback);
        Assert.Equal(new[] { ("OemTest", 5) }, nvram.Writes);
    }

    [Fact]
    public async Task GetSupportsCpuApMenu_RoundTripsTheFirmwareSupportByte()
    {
        var supported = new FakeFwNvramService().WithOemMagic(supportByte: 1);
        await using var host = await EngineHostFixture.StartAsync(
            configure: o => o with { FwNvramOverride = supported });
        Assert.True(await host.Device.GetSupportsCpuApMenuAsync());
        await host.DisposeAsync();

        var amd = new FakeFwNvramService().WithOemMagic(supportByte: 0);
        await using var host2 = await EngineHostFixture.StartAsync(
            configure: o => o with { FwNvramOverride = amd });
        Assert.False(await host2.Device.GetSupportsCpuApMenuAsync());
    }

    [Fact]
    public async Task SetCpuApMenuUnlock_PatchesBothOffsetsInLockstep_VerifiesByReRead()
    {
        var nvram = new FakeFwNvramService().WithOemMagic(supportByte: 1, apSwitch: 0, memOcSwitch: 0);

        await using var host = await EngineHostFixture.StartAsync(
            configure: o => o with { FwNvramOverride = nvram });

        Assert.True(await host.Device.SetCpuApMenuUnlockAsync(true));

        byte[]? after = await host.Device.GetNvramValueAsync(
            FwNvramService.OemMagicVariable, FwNvramService.OemMagicVariableGuid);
        Assert.NotNull(after);
        Assert.Equal(1, after[FwNvramService.OffsetCpuApMenuSwitch]);
        Assert.Equal(1, after[FwNvramService.OffsetMemOcSwitch]);

        Assert.True(await host.Device.SetCpuApMenuUnlockAsync(false));
        byte[]? off = await host.Device.GetNvramValueAsync(
            FwNvramService.OemMagicVariable, FwNvramService.OemMagicVariableGuid);
        Assert.NotNull(off);
        Assert.Equal(0, off[FwNvramService.OffsetCpuApMenuSwitch]);
        Assert.Equal(0, off[FwNvramService.OffsetMemOcSwitch]);
    }

    [Fact]
    public async Task SetCpuApMenuUnlock_NoVariable_AnswersFalse()
    {
        // The row must not flip on a write that actuated nothing (the unelevated
        // app's exact failure — minus the silence).
        await using var host = await EngineHostFixture.StartAsync();

        Assert.False(await host.Device.SetCpuApMenuUnlockAsync(true));
    }
}
