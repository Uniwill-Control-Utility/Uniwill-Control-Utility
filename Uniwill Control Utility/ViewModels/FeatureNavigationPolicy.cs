namespace Uniwill_Control_Utility.ViewModels
{
    internal enum OptionalNavigationFeature
    {
        Adaptive,
        HydroUi,
        LightBar,
        Overlay,
        Auto
    }

    public sealed record FeatureNavigationPreferences(
        bool Adaptive,
        bool HydroUi,
        bool LightBar,
        bool Overlay,
        bool Auto)
    {
        public static FeatureNavigationPreferences Default { get; } = new(
            Adaptive: true,
            HydroUi: true,
            LightBar: true,
            Overlay: true,
            Auto: true);
    }

    internal static class FeatureNavigationPolicy
    {
        public static bool IsVisible(
            OptionalNavigationFeature feature,
            FeatureNavigationPreferences preferences,
            bool hydroUiAvailable,
            bool lightBarAvailable) => feature switch
            {
                OptionalNavigationFeature.Adaptive => preferences.Adaptive,
                OptionalNavigationFeature.HydroUi => preferences.HydroUi && hydroUiAvailable,
                OptionalNavigationFeature.LightBar => preferences.LightBar && lightBarAvailable,
                OptionalNavigationFeature.Overlay => preferences.Overlay,
                OptionalNavigationFeature.Auto => preferences.Auto,
                _ => false
            };
    }
}
