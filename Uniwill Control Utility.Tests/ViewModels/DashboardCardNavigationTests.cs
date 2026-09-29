using Uniwill_Control_Utility.ViewModels;
using Uniwill_Control_Utility.Services;

namespace Uniwill_Control_Utility.Tests.ViewModels;

public sealed class DashboardCardNavigationTests
{
    [Theory]
    [InlineData(DashboardCard.Cpu, "presets", PresetsTuningSection.Cpu)]
    [InlineData(DashboardCard.Gpu, "presets", PresetsTuningSection.Gpu)]
    [InlineData(DashboardCard.Battery, "battery", null)]
    [InlineData(DashboardCard.HydroUi, "watercooler", null)]
    public void Interactive_cards_resolve_to_the_expected_destination(
        DashboardCard card,
        string pageTag,
        PresetsTuningSection? tuningSection)
    {
        Assert.True(DashboardCardNavigation.TryResolve(card, out DashboardCardDestination destination));
        Assert.Equal(pageTag, destination.PageTag);
        Assert.Equal(tuningSection, destination.TuningSection);
    }

    [Fact]
    public void Memory_card_has_no_navigation_destination()
    {
        Assert.False(DashboardCardNavigation.TryResolve(DashboardCard.Memory, out _));
    }
}
