namespace Uniwill_Control_Utility.Models
{
    public sealed record OverlayOptions
    {
        public bool Enabled { get; init; }
        public bool ShowOnStartup { get; init; }
        public string ToggleOverlayHotkey { get; init; } = "Ctrl+Shift+O";
        public string ToggleContentsHotkey { get; init; } = "Ctrl+Shift+I";
        public int RefreshIntervalMs { get; init; } = 500;
        public double Scale { get; init; } = 1;
        /// <summary>Legacy — kept for backwards compatibility with saved settings. Ignored if BackgroundOpacity or ContentOpacity are set.</summary>
        public double Opacity { get; init; } = 0.95;
        public double BackgroundOpacity { get; init; } = 0.95;
        public double ContentOpacity { get; init; } = 1.0;
        public bool ShowFps { get; init; } = true;
        public bool ShowFrameTimes { get; init; } = true;
        public bool ShowGpu { get; init; } = true;
        public bool ShowCpu { get; init; } = true;
        public bool ShowMemory { get; init; } = true;
        public bool ShowBattery { get; init; } = true;
        public bool ShowGraphs { get; init; } = true;
        public bool HideWhenNoGame { get; init; } = true;
        public bool VisibleOnLastShutdown { get; init; }
        public double Left { get; init; } = double.NaN;
        public double Top { get; init; } = double.NaN;
    }
}
