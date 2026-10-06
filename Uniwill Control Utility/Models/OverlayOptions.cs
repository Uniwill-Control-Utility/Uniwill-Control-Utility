namespace Uniwill_Control_Utility.Models
{
    public sealed record OverlayOptions
    {
        public bool Enabled { get; init; }
        public bool ShowOnStartup { get; init; }
        public string ToggleOverlayHotkey { get; init; } = "Ctrl+Shift+O";
        public string ToggleContentsHotkey { get; init; } = "Ctrl+Shift+I";
        public int RefreshIntervalMs { get; init; } = 250;

        // FPS metrics
        public bool ShowFps { get; init; } = true;
        public bool ShowFpsOnePercentLow { get; init; } = true;
        public bool ShowFpsPointOnePercentLow { get; init; } = true;

        // Frame time metrics
        public bool ShowFrameTimeCurrent { get; init; } = true;
        public bool ShowFrameTimeOnePercent { get; init; } = true;
        public bool ShowFrameTimePointOnePercent { get; init; } = true;

        // GPU metrics
        public bool ShowGpuTemperature { get; init; } = true;
        public bool ShowGpuUsage { get; init; } = true;
        public bool ShowGpuPower { get; init; } = true;
        public bool ShowGpuClock { get; init; } = true;
        public bool ShowGpuVram { get; init; } = true;

        // CPU metrics
        public bool ShowCpuTemperature { get; init; } = true;
        public bool ShowCpuUsage { get; init; } = true;
        public bool ShowCpuPower { get; init; } = true;
        public bool ShowCpuClock { get; init; } = true;

        // RAM metrics
        public bool ShowMemory { get; init; } = true;

        // Battery metrics
        public bool ShowBatteryPercent { get; init; } = true;
        public bool ShowBatteryStatus { get; init; } = true;
        public bool ShowBatteryTime { get; init; } = true;
        public bool ShowBatteryPower { get; init; } = true;
    }
}
