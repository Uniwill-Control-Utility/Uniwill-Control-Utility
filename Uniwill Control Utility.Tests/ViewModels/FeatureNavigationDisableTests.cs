using Uniwill_Control_Utility.ViewModels;

namespace Uniwill_Control_Utility.Tests.ViewModels;

public sealed class FeatureNavigationDisableTests
{
    [Fact]
    public void OptionalNavigationFeature_enum_has_all_five_features()
    {
        var values = Enum.GetValues<OptionalNavigationFeature>();

        Assert.Contains(values, v => v == OptionalNavigationFeature.Adaptive);
        Assert.Contains(values, v => v == OptionalNavigationFeature.HydroUi);
        Assert.Contains(values, v => v == OptionalNavigationFeature.LightBar);
        Assert.Contains(values, v => v == OptionalNavigationFeature.Overlay);
        Assert.Contains(values, v => v == OptionalNavigationFeature.Auto);
        Assert.Equal(5, values.Length);
    }

    [Theory]
    [InlineData(0)] // Adaptive
    [InlineData(1)] // HydroUi
    [InlineData(2)] // LightBar
    [InlineData(3)] // Overlay
    [InlineData(4)] // Auto
    public void Disabling_a_feature_makes_it_invisible(int featureIndex)
    {
        OptionalNavigationFeature feature = (OptionalNavigationFeature)featureIndex;
        var preferences = new FeatureNavigationPreferences(
            Adaptive: feature != OptionalNavigationFeature.Adaptive,
            HydroUi: feature != OptionalNavigationFeature.HydroUi,
            LightBar: feature != OptionalNavigationFeature.LightBar,
            Overlay: feature != OptionalNavigationFeature.Overlay,
            Auto: feature != OptionalNavigationFeature.Auto);

        Assert.False(FeatureNavigationPolicy.IsVisible(feature, preferences, hydroUiAvailable: true, lightBarAvailable: true));
    }

    [Theory]
    [InlineData(0)] // Adaptive
    [InlineData(2)] // LightBar
    [InlineData(3)] // Overlay
    [InlineData(4)] // Auto
    public void Disabling_one_feature_does_not_affect_others(int disabledFeatureIndex)
    {
        OptionalNavigationFeature disabledFeature = (OptionalNavigationFeature)disabledFeatureIndex;
        var preferences = new FeatureNavigationPreferences(
            Adaptive: disabledFeature != OptionalNavigationFeature.Adaptive,
            HydroUi: disabledFeature != OptionalNavigationFeature.HydroUi,
            LightBar: disabledFeature != OptionalNavigationFeature.LightBar,
            Overlay: disabledFeature != OptionalNavigationFeature.Overlay,
            Auto: disabledFeature != OptionalNavigationFeature.Auto);

        // All features other than the disabled one should still be visible.
        foreach (var feature in Enum.GetValues<OptionalNavigationFeature>())
        {
            if (feature == disabledFeature)
                continue;

            Assert.True(
                FeatureNavigationPolicy.IsVisible(feature, preferences, hydroUiAvailable: true, lightBarAvailable: true),
                $"{feature} should be visible when only {disabledFeature} is disabled");
        }
    }
}
