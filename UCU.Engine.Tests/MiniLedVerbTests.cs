using UCU.Contracts;
using UCU.Hardware.Devices;

namespace UCU.Engine.Tests;

/// <summary>
/// The MiniLED dimming verb end to end: client proxy → pipe → dispatcher →
/// device queue → scripted handshake. The row was silently dead in the
/// unelevated app — executing the OEM root\WMI method needs namespace write
/// access the app does not have, so the InvokeMethod threw access denied into
/// a catch returning a bare false. Over the pipe the engine performs the
/// handshake as LocalSystem. The suite pins the semantics the tray/display
/// rows depend on: the ordered pair of command words, the reply-byte
/// verification (a mismatch answers false, the firmware is the witness), and
/// the fault shape (the service's catch answers false with the reason in the
/// engine log, never a thrown wire fault).
/// </summary>
public sealed class MiniLedVerbTests
{
    [Fact]
    public async Task SetMiniLedDimming_On_RunsTheEnablePair_Verified()
    {
        var miniLed = new FakeMiniLedService
        {
            Step1Reply = MiniLedWmiService.ExpectedEnableStep1,
            Step2Reply = MiniLedWmiService.ExpectedEnableStep2,
        };

        await using var host = await EngineHostFixture.StartAsync(
            configure: o => o with { MiniLedOverride = miniLed });

        Assert.True(await host.Device.SetMiniLedDimmingAsync(true));
        Assert.Equal(
            new[] { MiniLedWmiService.CmdEnableStep1, MiniLedWmiService.CmdEnableStep2 },
            miniLed.Commands);
    }

    [Fact]
    public async Task SetMiniLedDimming_Off_RunsTheDisablePair_Verified()
    {
        var miniLed = new FakeMiniLedService
        {
            Step1Reply = MiniLedWmiService.ExpectedDisableStep1,
            Step2Reply = MiniLedWmiService.ExpectedDisableStep2,
        };

        await using var host = await EngineHostFixture.StartAsync(
            configure: o => o with { MiniLedOverride = miniLed });

        Assert.True(await host.Device.SetMiniLedDimmingAsync(false));
        Assert.Equal(
            new[] { MiniLedWmiService.CmdDisableStep1, MiniLedWmiService.CmdDisableStep2 },
            miniLed.Commands);
    }

    [Fact]
    public async Task SetMiniLedDimming_StepReplyMismatch_AnswersFalse_AndStopsThePair()
    {
        // The firmware's reply byte is the witness: a step answering the
        // wrong byte means the panel did not take the command, and the
        // sequence must not barrel on into the second step.
        var miniLed = new FakeMiniLedService { Step1Reply = 0x00 };

        await using var host = await EngineHostFixture.StartAsync(
            configure: o => o with { MiniLedOverride = miniLed });

        Assert.False(await host.Device.SetMiniLedDimmingAsync(true));
        Assert.Single(miniLed.Commands);
    }

    [Fact]
    public async Task SetMiniLedDimming_WmiFault_AnswersFalse_NotAWireFault()
    {
        // The production service catches the WMI fault (access-denied shape
        // included) and answers false with the reason logged; the verb must
        // carry that as an answer, and the app's EC-readback caller decides
        // what to show.
        var miniLed = new FakeMiniLedService
        {
            Fault = new InvalidOperationException("WMI access denied"),
        };

        await using var host = await EngineHostFixture.StartAsync(
            configure: o => o with { MiniLedOverride = miniLed });

        Assert.False(await host.Device.SetMiniLedDimmingAsync(true));
    }
}
