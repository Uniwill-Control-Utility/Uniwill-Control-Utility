using Uniwill_Control_Utility.Services;

namespace Uniwill_Control_Utility.Tests.Services;

public sealed class TrayWidgetControllerTests
{
    [Fact]
    public async Task ToggleAsync_CreatesWidgetOnlyOnFirstUse()
    {
        int createCount = 0;
        var widget = new FakeTrayWidget();
        var controller = new TrayWidgetController(() =>
        {
            createCount++;
            return widget;
        });

        Assert.Equal(0, createCount);

        await controller.ToggleAsync();
        await controller.ToggleAsync();

        Assert.Equal(1, createCount);
        Assert.Equal(2, widget.ToggleCount);
    }

    private sealed class FakeTrayWidget : ITrayWidget
    {
        public int ToggleCount { get; private set; }
        public bool IsVisible { get; private set; }

        public Task ToggleAsync()
        {
            ToggleCount++;
            IsVisible = !IsVisible;
            return Task.CompletedTask;
        }

        public void Hide() => IsVisible = false;
    }
}
