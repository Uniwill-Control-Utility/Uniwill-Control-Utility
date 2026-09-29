using Uniwill_Control_Utility.Services;

namespace Uniwill_Control_Utility.Tests.Services;

public sealed class DisplayTargetSelectorTests
{
    [Fact]
    public void Choose_UsesTheInternalDisplayAtTheMatchingScreenIndex()
    {
        string? selected = DisplayTargetSelector.Choose(
            internalDisplayIndex: 1,
            screenDeviceNames: [@"\\.\DISPLAY1", @"\\.\DISPLAY2"],
            primaryDeviceName: @"\\.\DISPLAY1");

        Assert.Equal(@"\\.\DISPLAY2", selected);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    public void Choose_FallsBackToPrimaryWhenInternalDisplayCannotBeMatched(int internalDisplayIndex)
    {
        string? selected = DisplayTargetSelector.Choose(
            internalDisplayIndex,
            [@"\\.\DISPLAY1", @"\\.\DISPLAY2"],
            @"\\.\DISPLAY1");

        Assert.Equal(@"\\.\DISPLAY1", selected);
    }
}
