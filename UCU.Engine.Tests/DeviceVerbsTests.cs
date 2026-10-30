using UCU.Contracts;
using UCU.Hardware.Devices;

namespace UCU.Engine.Tests;

/// <summary>
/// The device verbs end to end: client proxy → pipe → dispatcher → device queue →
/// scripted device tree. These are the mutations that used to be a UAC prompt in
/// the unelevated app (webcam per click, dGPU additionally on automatic GPU
/// power-saving applies); over the pipe they need no consent at all, so this suite
/// is the proof the relocation keeps the semantics: the status vocabulary, the
/// present-device targeting, and the verify-by-re-read that answers the verb.
/// </summary>
public sealed class DeviceVerbsTests
{
    [Fact]
    public async Task GetWebcamStatus_RoundTripsTheDeviceState()
    {
        var devices = new FakePnpDeviceService().With("Camera", "USB\\VID_04F2&PID_0CCF\\7&1", present: true, running: true);

        await using var host = await EngineHostFixture.StartAsync(
            configure: o => o with { PnpDeviceOverride = devices });

        PnpDeviceStateDto state = (await host.Device.GetWebcamStateAsync())!;

        Assert.Equal("OK", state.Status);
        Assert.True(state.Present);
        Assert.StartsWith("USB\\VID_04F2", state.InstanceId);
    }

    [Fact]
    public async Task GetWebcamStatus_AbsentCamera_IsNullOverTheWire()
    {
        // No scripted Camera node: the verb answers null, and the client renders
        // null - the row's "no camera in this system" state, not an error.
        await using var host = await EngineHostFixture.StartAsync();

        Assert.Null(await host.Device.GetWebcamStateAsync());
    }

    [Fact]
    public async Task SetWebcamEnabled_DisablesThePresentDevice_Verifies_AndAnswersTrue()
    {
        var devices = new FakePnpDeviceService().With("Camera", "USB\\CAM\\1", present: true, running: true);

        await using var host = await EngineHostFixture.StartAsync(
            configure: o => o with { PnpDeviceOverride = devices });

        Assert.True(await host.Device.SetWebcamEnabledAsync(enabled: false));

        PnpDeviceStateDto after = (await host.Device.GetWebcamStateAsync())!;
        Assert.Equal("Disable", after.Status);
        Assert.Equal(new[] { ("Camera", false) }, devices.Mutations);
    }

    [Fact]
    public async Task SetWebcamEnabled_NoPresentDevice_VerifiesFalse()
    {
        // "No device present" is a false verification, not a success: the app's row
        // must not flip on a mutation that actuated nothing.
        var devices = new FakePnpDeviceService();

        await using var host = await EngineHostFixture.StartAsync(
            configure: o => o with { PnpDeviceOverride = devices });

        Assert.False(await host.Device.SetWebcamEnabledAsync(enabled: true));
    }

    [Fact]
    public async Task SetDgpuEnabled_RoundTripsTheEnable()
    {
        var devices = new FakePnpDeviceService().With("Display", "PCI\\VEN_10DE&DEV_2C00\\0", present: true, running: false);

        await using var host = await EngineHostFixture.StartAsync(
            configure: o => o with { PnpDeviceOverride = devices });

        Assert.True(await host.Device.SetDgpuEnabledAsync(enabled: true));

        PnpDeviceStateDto state = (await host.Device.GetDgpuStateAsync())!;
        Assert.Equal("OK", state.Status);
    }

    [Fact]
    public async Task SetDgpuEnabled_SequentialCalls_AreOrderedOnTheDeviceQueue()
    {
        // The device domain has one serial queue: a charger-transition apply that
        // lands while the startup apply is mid-verify must not interleave with it.
        // The observable proof on the scripted tree is that both calls complete
        // and the final state is the LAST call's, never a lost update.
        var devices = new FakePnpDeviceService().With("Display", "PCI\\NVIDIA\\0", present: true, running: true);

        await using var host = await EngineHostFixture.StartAsync(
            configure: o => o with { PnpDeviceOverride = devices });

        await host.Device.SetDgpuEnabledAsync(enabled: false);
        await host.Device.SetDgpuEnabledAsync(enabled: true);
        await host.Device.SetDgpuEnabledAsync(enabled: false);

        PnpDeviceStateDto state = (await host.Device.GetDgpuStateAsync())!;
        Assert.Equal("Disable", state.Status);
        Assert.Equal(3, devices.Mutations.Count);
    }
}
