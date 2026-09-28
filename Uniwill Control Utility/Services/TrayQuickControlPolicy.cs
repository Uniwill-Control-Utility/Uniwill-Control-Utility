namespace Uniwill_Control_Utility.Services
{
    public sealed record TrayQuickControlPolicy(
        bool ShowTgp,
        bool ShowHydro,
        bool PresetEnabled,
        bool TdpEnabled,
        bool TgpEnabled,
        bool FullFanEnabled,
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
                // Full fan is an explicit user command and remains available even
                // while Adaptive Mode owns the normal EC fan profile.
                FullFanEnabled: true,
                HydroEnabled: !adaptiveWaterCoolerEnabled);
        }
    }
}
