using Uniwill_Control_Utility.ViewModels;

namespace Uniwill_Control_Utility.Services
{
    public sealed record TrayQuickControlPolicy(
        bool ShowTgp,
        bool ShowHydro,
        bool PresetEnabled,
        bool TdpEnabled,
        bool TgpEnabled,
        bool LocalDimmingEnabled,
        bool HydroEnabled,
        bool AdaptiveFanEnabled,
        /// <summary>Whether the Adaptive Mode toggle row should be visible (feature enabled in Settings).</summary>
        bool ShowAdaptiveMode,
        /// <summary>Whether the Hydro UI Smart Mode toggle should be visible (hardware + feature enabled).</summary>
        bool ShowHydroSmartMode,
        /// <summary>Whether the Hydro UI RGB toggle should be visible (hardware + feature enabled).</summary>
        bool ShowHydroRgb,
        /// <summary>Whether the Keyboard backlight toggle should be visible (feature enabled).</summary>
        bool ShowKeyboardBacklight,
        /// <summary>Whether the Light Bar toggle should be visible (hardware + feature enabled).</summary>
        bool ShowLightBar,
        /// <summary>Whether the Fan Smart Mode toggle should be visible (EC available + not overridden by Adaptive).</summary>
        bool ShowFanSmartMode,
        /// <summary>Whether the Local Dimming toggle should be visible (MiniLED panel + feature enabled).</summary>
        bool ShowLocalDimming,
        /// <summary>Whether the Overlay toggle should be visible (feature enabled).</summary>
        bool ShowOverlay,
        /// <summary>Whether the Flydigi RGB toggle should be visible (device + feature enabled).</summary>
        bool ShowFlydigiRgb,
        /// <summary>Whether the Fan Smart Mode toggle should be disabled (adaptive mode controls it).</summary>
        bool DisableFanSmartMode,
        /// <summary>Whether the Hydro UI Smart Mode toggle should be disabled (adaptive mode controls it).</summary>
        bool DisableHydroSmartMode,
        /// <summary>Whether the Hydro UI RGB toggle should be disabled (adaptive mode controls it).</summary>
        bool DisableHydroRgb,
        /// <summary>Whether the Keyboard RGB toggle should be disabled (adaptive mode controls it).</summary>
        bool DisableKeyboardBacklight,
        /// <summary>Whether the Light Bar toggle should be disabled (adaptive mode controls it).</summary>
        bool DisableLightBar,
        /// <summary>Whether the Flydigi RGB toggle should be disabled (adaptive mode controls it).</summary>
        bool DisableFlydigiRgb)
    {
        public static TrayQuickControlPolicy Create(
            bool hasNvidiaGpu,
            bool hasSupportedWaterCooler,
            bool waterCoolerConnected,
            bool adaptiveCpuEnabled,
            bool adaptiveGpuEnabled,
            bool adaptiveFanEnabled,
            bool adaptiveWaterCoolerEnabled,
            bool adaptiveKeyboardEnabled,
            bool adaptiveLightBarEnabled,
            bool adaptiveFlydigiEnabled,
            bool hasMiniLedPanel,
            bool flydigiDeviceAvailable,
            bool flydigiEnabled,
            FeatureNavigationPreferences features)
        {
            return new TrayQuickControlPolicy(
                ShowTgp: hasNvidiaGpu,
                ShowHydro: hasSupportedWaterCooler && waterCoolerConnected,
                PresetEnabled: !adaptiveCpuEnabled && !adaptiveGpuEnabled,
                TdpEnabled: !adaptiveCpuEnabled,
                TgpEnabled: hasNvidiaGpu && !adaptiveGpuEnabled,
                // Local dimming is a display-level toggle, not an adaptive profile.
                LocalDimmingEnabled: true,
                HydroEnabled: !adaptiveWaterCoolerEnabled,
                AdaptiveFanEnabled: adaptiveFanEnabled,
                ShowAdaptiveMode: features.Adaptive,
                ShowHydroSmartMode: features.HydroUi && hasSupportedWaterCooler && waterCoolerConnected,
                ShowHydroRgb: features.HydroUi && hasSupportedWaterCooler && waterCoolerConnected,
                ShowKeyboardBacklight: true,
                ShowLightBar: features.LightBar,
                ShowFanSmartMode: true,
                ShowLocalDimming: hasMiniLedPanel,
                ShowOverlay: features.Overlay,
                ShowFlydigiRgb: flydigiEnabled && flydigiDeviceAvailable,
                DisableFanSmartMode: adaptiveFanEnabled,
                DisableHydroSmartMode: adaptiveWaterCoolerEnabled,
                DisableHydroRgb: adaptiveWaterCoolerEnabled,
                DisableKeyboardBacklight: adaptiveKeyboardEnabled,
                DisableLightBar: adaptiveLightBarEnabled,
                DisableFlydigiRgb: adaptiveFlydigiEnabled);
        }
    }
}
