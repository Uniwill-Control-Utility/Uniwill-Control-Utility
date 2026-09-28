using System.Configuration;
using System.Reflection;
using Uniwill_Control_Utility.Properties;
using Uniwill_Control_Utility.ViewModels;

namespace Uniwill_Control_Utility.Tests.ViewModels;

public sealed class FeatureNavigationPolicyTests
{
    [Fact]
    public void Default_preferences_show_every_optional_navigation_feature()
    {
        var preferences = FeatureNavigationPreferences.Default;

        Assert.True(FeatureNavigationPolicy.IsVisible(OptionalNavigationFeature.Adaptive, preferences, hydroUiAvailable: true, lightBarAvailable: true));
        Assert.True(FeatureNavigationPolicy.IsVisible(OptionalNavigationFeature.HydroUi, preferences, hydroUiAvailable: true, lightBarAvailable: true));
        Assert.True(FeatureNavigationPolicy.IsVisible(OptionalNavigationFeature.LightBar, preferences, hydroUiAvailable: true, lightBarAvailable: true));
        Assert.True(FeatureNavigationPolicy.IsVisible(OptionalNavigationFeature.Overlay, preferences, hydroUiAvailable: true, lightBarAvailable: true));
        Assert.True(FeatureNavigationPolicy.IsVisible(OptionalNavigationFeature.Auto, preferences, hydroUiAvailable: true, lightBarAvailable: true));
    }

    [Fact]
    public void Disabled_preferences_hide_only_the_selected_features()
    {
        var preferences = new FeatureNavigationPreferences(
            Adaptive: false,
            HydroUi: true,
            LightBar: false,
            Overlay: true,
            Auto: false);

        Assert.False(FeatureNavigationPolicy.IsVisible(OptionalNavigationFeature.Adaptive, preferences, hydroUiAvailable: true, lightBarAvailable: true));
        Assert.True(FeatureNavigationPolicy.IsVisible(OptionalNavigationFeature.HydroUi, preferences, hydroUiAvailable: true, lightBarAvailable: true));
        Assert.False(FeatureNavigationPolicy.IsVisible(OptionalNavigationFeature.LightBar, preferences, hydroUiAvailable: true, lightBarAvailable: true));
        Assert.True(FeatureNavigationPolicy.IsVisible(OptionalNavigationFeature.Overlay, preferences, hydroUiAvailable: true, lightBarAvailable: true));
        Assert.False(FeatureNavigationPolicy.IsVisible(OptionalNavigationFeature.Auto, preferences, hydroUiAvailable: true, lightBarAvailable: true));
    }

    [Fact]
    public void Hardware_dependent_features_stay_hidden_when_unavailable()
    {
        var preferences = FeatureNavigationPreferences.Default;

        Assert.False(FeatureNavigationPolicy.IsVisible(OptionalNavigationFeature.HydroUi, preferences, hydroUiAvailable: false, lightBarAvailable: true));
        Assert.False(FeatureNavigationPolicy.IsVisible(OptionalNavigationFeature.LightBar, preferences, hydroUiAvailable: true, lightBarAvailable: false));
    }

    [Theory]
    [InlineData(nameof(Settings.EnableAdaptiveNavigation))]
    [InlineData(nameof(Settings.EnableHydroUiNavigation))]
    [InlineData(nameof(Settings.EnableLightBarNavigation))]
    [InlineData(nameof(Settings.EnableOverlayNavigation))]
    [InlineData(nameof(Settings.EnableAutoNavigation))]
    public void Persisted_navigation_settings_default_to_enabled(string propertyName)
    {
        PropertyInfo property = typeof(Settings).GetProperty(propertyName)
            ?? throw new InvalidOperationException($"Missing setting {propertyName}");
        var defaultValue = property.GetCustomAttribute<DefaultSettingValueAttribute>();

        Assert.NotNull(defaultValue);
        Assert.Equal("True", defaultValue!.Value);
    }
}
