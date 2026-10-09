namespace Uniwill_Control_Utility.Models
{
    public sealed record OverlayOptions
    {
        public bool Enabled { get; init; }
        public bool ShowOnStartup { get; init; }
        public string ToggleOverlayHotkey { get; init; } = "Ctrl+Shift+O";
        public string ToggleContentsHotkey { get; init; } = "Ctrl+Shift+I";
        public int RefreshIntervalMs { get; init; } = 250;

        // Row toggles — each checkbox controls an entire row in the OSD.
        public bool ShowCpuRow { get; init; } = true;
        public bool ShowRamRow { get; init; } = true;
        public bool ShowGpuRow { get; init; } = true;
        public bool ShowVramRow { get; init; } = true;
        public bool ShowFpsCurrent { get; init; } = true;
        public bool ShowFpsOnePercentLow { get; init; } = true;
        public bool ShowFpsPointOnePercentLow { get; init; } = true;
        public bool ShowBatteryRow { get; init; } = true;

        // Peripheral rows
        public bool ShowHydroUi { get; init; } = true;
        public bool ShowFlydigi { get; init; } = true;
    }
}
