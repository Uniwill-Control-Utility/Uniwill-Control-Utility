using Uniwill_Control_Utility.Services;

namespace Uniwill_Control_Utility.Tests.Services;

public sealed class FullFanControllerTests
{
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task SetAsync_TogglesTheHardwareModeWhenStateDiffers(bool current, bool requested)
    {
        bool active = current;
        int calls = 0;
        var controller = new FullFanController(
            () => active,
            () =>
            {
                calls++;
                active = !active;
                return Task.CompletedTask;
            });

        await controller.SetAsync(requested);

        Assert.Equal(requested, active);
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SetAsync_DoesNotToggleWhenHardwareAlreadyMatches(bool requested)
    {
        int calls = 0;
        var controller = new FullFanController(
            () => requested,
            () =>
            {
                calls++;
                return Task.CompletedTask;
            });

        await controller.SetAsync(requested);

        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task SetAsync_RejectsACommandThatDidNotChangeHardwareState()
    {
        var controller = new FullFanController(
            () => false,
            () => Task.CompletedTask);

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => controller.SetAsync(true));

        Assert.Contains("did not enter", error.Message, StringComparison.OrdinalIgnoreCase);
    }
}
