namespace Uniwill_Control_Utility.Services
{
    public sealed record TrayQuickControlPolicy(
        bool ShowTgp,
        bool ShowHydro,
        bool PresetEnabled,
        bool TdpEnabled,
        bool TgpEnabled,
        bool LocalDimmingEnabled,
        bool HydroEnabled)
    {
        public static TrayQuickControlPolicy Create(
            bool hasNvidiaGpu,
            bool hasSupportedWaterCooler,
            bool waterCoolerConnected,
            bool adaptiveCpuEnabled,
            bool adaptiveGpuEnabled,
            bool adaptiveFanEnabled,
            bool adaptiveWaterCoolerEnabled)
        {
            return new TrayQuickControlPolicy(
                ShowTgp: hasNvidiaGpu,
                ShowHydro: hasSupportedWaterCooler && waterCoolerConnected,
                PresetEnabled: !adaptiveCpuEnabled && !adaptiveGpuEnabled,
                TdpEnabled: !adaptiveCpuEnabled,
                TgpEnabled: hasNvidiaGpu && !adaptiveGpuEnabled,
                // Local dimming is a display-level toggle, not an adaptive profile.
                LocalDimmingEnabled: true,
                HydroEnabled: !adaptiveWaterCoolerEnabled);
        }
    }
}
