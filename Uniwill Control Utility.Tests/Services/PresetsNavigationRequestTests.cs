using Uniwill_Control_Utility.Services;

namespace Uniwill_Control_Utility.Tests.Services;

public sealed class PresetsNavigationRequestTests
{
    [Theory]
    [InlineData(PresetsTuningSection.Cpu)]
    [InlineData(PresetsTuningSection.Gpu)]
    public void Requested_section_is_consumed_once(PresetsTuningSection section)
    {
        var request = new PresetsNavigationRequest();

        request.Request(section);

        Assert.True(request.TryConsume(out PresetsTuningSection consumed));
        Assert.Equal(section, consumed);
        Assert.False(request.TryConsume(out _));
    }

    [Fact]
    public void Latest_request_replaces_an_older_pending_section()
    {
        var request = new PresetsNavigationRequest();

        request.Request(PresetsTuningSection.Cpu);
        request.Request(PresetsTuningSection.Gpu);

        Assert.True(request.TryConsume(out PresetsTuningSection consumed));
        Assert.Equal(PresetsTuningSection.Gpu, consumed);
    }
}
