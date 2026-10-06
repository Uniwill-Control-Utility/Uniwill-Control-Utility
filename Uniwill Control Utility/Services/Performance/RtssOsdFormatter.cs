using System;
using System.Collections.Generic;
using System.Text;
using Uniwill_Control_Utility.Models;

namespace Uniwill_Control_Utility.Services.Performance
{
    /// <summary>
    /// Builds RTSS OSD-formatted text strings from hardware metrics.
    /// Output uses a columnar layout: each category is one row with a colored
    /// label on the left and right-aligned values in columns.
    /// </summary>
    internal static class RtssOsdFormatter
    {
        // Font sizes
        private const int FpsFontSize = 48;
        private const int MetricFontSize = 28;
        private const int UnitFontSize = 14;

        // Colors (hex RGB)
        private const string ColorWhite = "FFFFFF";
        private const string ColorGpu = "00F5FF";
        private const string ColorCpu = "F13CFF";
        private const string ColorMemory = "80FF80";
        private const string ColorVram = "FFD700";
        private const string ColorBattery = "39FF14";

        // Column widths
        private const int ValueWidth = 5;  // width for the numeric value (right-aligned)
        private const int UnitWidth = 4;   // width for the unit label
        private const int LabelWidth = 4;  // fixed-width label column for alignment

        /// <summary>
        /// Shortens the graphics API label to a compact form (DX12, DX11, VULKAN).
        /// </summary>
        private static string ShortApiLabel(string api)
        {
            if (string.IsNullOrEmpty(api) || api == "N/A")
                return string.Empty;

            string s = api.ToUpperInvariant();
            if (s.Contains("DIRECTX 12") || s.Contains("DX12"))
                return "DX12";
            if (s.Contains("DIRECTX 11") || s.Contains("DX11"))
                return "DX11";
            if (s.Contains("VULKAN"))
                return "VULKAN";

            return api;
        }

        /// <summary>
        /// Build the complete OSD string from current metrics and options.
        /// Row order: CPU → RAM → GPU → VRAM → FPS (with API label).
        /// Returns null if no metrics should be shown.
        /// </summary>
        internal static string? Build(
            HardwareMetricsSnapshot hardware,
            FpsMetricsSnapshot fps,
            OverlayOptions options)
        {
            var sb = new StringBuilder(1024);
            bool hasContent = false;

            // --- CPU row ---
            {
                var cells = new List<Cell>();

                if (options.ShowCpuTemperature && hardware.CpuTemperature > 0)
                    cells.Add(ValueCell(ColorCpu, hardware.CpuTemperature.ToString(), "°C"));

                if (options.ShowCpuUsage)
                    cells.Add(ValueCell(ColorCpu, hardware.CpuUsage.ToString("F0"), "%"));

                if (options.ShowCpuClock && hardware.CpuClockMhz > 0)
                    cells.Add(ValueCell(ColorCpu, (hardware.CpuClockMhz / 1000).ToString("F1"), "GHz"));

                if (options.ShowCpuPower && hardware.CpuPowerWatts > 0)
                    cells.Add(ValueCell(ColorCpu, hardware.CpuPowerWatts.ToString(), "W"));

                if (cells.Count > 0)
                {
                    sb.Append(BuildRow(ColorCpu, "CPU", cells));
                    hasContent = true;
                }
            }

            // --- RAM row ---
            {
                var cells = new List<Cell>();

                if (options.ShowMemory && hardware.SystemMemoryTotalGb > 0)
                {
                    int usedMb = (int)(hardware.SystemMemoryUsedGb * 1024);
                    cells.Add(ValueCell(ColorMemory, usedMb.ToString(), "MB"));
                }

                // Memory temperatures (per module)
                if (hardware.MemoryTemperature0 > 0)
                {
                    if (hardware.MemoryTemperature1 > 0)
                        cells.Add(ValueCell(ColorMemory, $"{hardware.MemoryTemperature0}/{hardware.MemoryTemperature1}", "°C"));
                    else
                        cells.Add(ValueCell(ColorMemory, hardware.MemoryTemperature0.ToString(), "°C"));
                }

                if (cells.Count > 0)
                {
                    sb.Append(BuildRow(ColorMemory, "RAM", cells));
                    hasContent = true;
                }
            }

            // --- GPU row ---
            {
                var cells = new List<Cell>();

                if (options.ShowGpuTemperature && hardware.GpuTemperature > 0)
                    cells.Add(ValueCell(ColorGpu, hardware.GpuTemperature.ToString(), "°C"));

                if (options.ShowGpuUsage)
                    cells.Add(ValueCell(ColorGpu, hardware.GpuUsage.ToString("F0"), "%"));

                if (options.ShowGpuClock && hardware.GpuClockMhz > 0)
                    cells.Add(ValueCell(ColorGpu, (hardware.GpuClockMhz / 1000).ToString("F1"), "GHz"));

                if (options.ShowGpuPower && hardware.GpuPowerWatts > 0)
                    cells.Add(ValueCell(ColorGpu, hardware.GpuPowerWatts.ToString(), "W"));

                if (cells.Count > 0)
                {
                    sb.Append(BuildRow(ColorGpu, "GPU", cells));
                    hasContent = true;
                }
            }

            // --- VRAM row ---
            if (options.ShowGpuVram && hardware.GpuMemoryTotalGb > 0)
            {
                var cells = new List<Cell>();
                int usedMb = (int)(hardware.GpuMemoryUsedGb * 1024);
                cells.Add(ValueCell(ColorVram, usedMb.ToString(), "MB"));

                sb.Append(BuildRow(ColorVram, "VRAM", cells));
                hasContent = true;
            }

            // --- FPS rows (with short API label) ---
            // Row 1: current FPS + frame time
            // Row 2: 1% low FPS + frame time
            // Row 3: 0.1% low FPS + frame time
            if (fps.IsAvailable)
            {
                string apiLabel = ShortApiLabel(fps.GraphicsApi);

                // Row 1: current FPS + frame time
                {
                    var cells = new List<Cell>();

                    if (options.ShowFps)
                        cells.Add(FpsCellLarge("<FR>"));

                    if (options.ShowFrameTimeCurrent)
                        cells.Add(ValueCell(ColorWhite, fps.FrameTimeMs.ToString("F1"), "ms"));

                    if (cells.Count > 0)
                    {
                        var label = string.IsNullOrEmpty(apiLabel) ? "FPS" : apiLabel;
                        sb.Append(BuildRow(ColorWhite, label, cells, compactLabel: true));
                        hasContent = true;
                    }
                }

                // Row 2: 1% low FPS + frame time
                {
                    var cells = new List<Cell>();

                    if (options.ShowFpsOnePercentLow)
                        cells.Add(FpsCell(fps.OnePercentLowFps.ToString()));

                    if (options.ShowFrameTimeOnePercent)
                        cells.Add(ValueCell(ColorWhite, fps.OnePercentFrameTimeMs.ToString("F1"), "ms"));

                    if (cells.Count > 0)
                    {
                        sb.Append(BuildRow(ColorWhite, "1%", cells, compactLabel: true));
                        hasContent = true;
                    }
                }

                // Row 3: 0.1% low FPS + frame time
                {
                    var cells = new List<Cell>();

                    if (options.ShowFpsPointOnePercentLow)
                        cells.Add(FpsCell(fps.PointOnePercentLowFps.ToString()));

                    if (options.ShowFrameTimePointOnePercent)
                        cells.Add(ValueCell(ColorWhite, fps.PointOnePercentFrameTimeMs.ToString("F1"), "ms"));

                    if (cells.Count > 0)
                    {
                        sb.Append(BuildRow(ColorWhite, "0.1%", cells, compactLabel: true));
                        hasContent = true;
                    }
                }
            }

            // --- Battery row ---
            if (hardware.HasBattery)
            {
                var cells = new List<Cell>();

                if (options.ShowBatteryPercent)
                    cells.Add(ValueCell(ColorBattery, hardware.BatteryPercent.ToString(), "%"));

                if (options.ShowBatteryPower && hardware.BatteryPowerWatts > 0)
                    cells.Add(ValueCell(ColorBattery, hardware.BatteryPowerWatts.ToString("F1"), "W"));

                if (options.ShowBatteryStatus)
                {
                    string status = hardware.IsBatteryFullyCharged ? "Full"
                        : hardware.IsBatteryCharging ? "Charging"
                        : "Discharging";
                    cells.Add(new Cell { Text = status.PadLeft(ValueWidth + UnitWidth), FontSize = MetricFontSize, Color = ColorBattery, IsRaw = true });
                }

                if (options.ShowBatteryTime && hardware.BatteryTimeRemainingMinutes > 0 && !hardware.IsBatteryCharging)
                {
                    int hours = hardware.BatteryTimeRemainingMinutes / 60;
                    int mins = hardware.BatteryTimeRemainingMinutes % 60;
                    cells.Add(new Cell { Text = $"{hours}h {mins:D2}m".PadLeft(ValueWidth + UnitWidth), FontSize = MetricFontSize, Color = ColorBattery, IsRaw = true });
                }

                if (cells.Count > 0)
                {
                    sb.Append(BuildRow(ColorBattery, "BAT", cells));
                    hasContent = true;
                }
            }

            return hasContent ? sb.ToString() : null;
        }

       /// <summary>
        /// Builds a cell with a colored value and white unit, each in fixed-width columns.
        /// </summary>
        private static Cell ValueCell(string valueColor, string valueText, string unit)
        {
            string valuePart = $"<C={valueColor}><S0={MetricFontSize}>{valueText.PadLeft(ValueWidth)}";
            string unitPart = string.IsNullOrEmpty(unit)
                ? new string(' ', UnitWidth)
                : $"<C={ColorWhite}><S0={UnitFontSize}>{unit.PadRight(UnitWidth)}";
            return new Cell { Text = valuePart + unitPart, FontSize = MetricFontSize, Color = valueColor, IsRaw = true };
        }

        /// <summary>
        /// Builds an FPS cell at metric font size with "FPS" unit.
        /// Uses ValueWidth+1 so the numeric value aligns 1 char right of hardware rows.
        /// </summary>
        private static Cell FpsCell(string valueText)
        {
            string valuePart = $"<C={ColorWhite}><S0={MetricFontSize}>{valueText.PadLeft(ValueWidth + 1)}";
            string unitPart = $"<C={ColorWhite}><S0={UnitFontSize}>" + "FPS".PadRight(UnitWidth);
            return new Cell { Text = valuePart + unitPart, FontSize = MetricFontSize, Color = ColorWhite, IsRaw = true };
        }

        /// <summary>
        /// Builds the large current-FPS cell using the &lt;FR&gt; RTSS replacement tag.
        /// Uses ValueWidth+2 so the value aligns 2 chars right of hardware rows.
        /// </summary>
        private static Cell FpsCellLarge(string replacementTag)
        {
            string valuePart = $"<C={ColorWhite}><S0={FpsFontSize}>{replacementTag.PadLeft(ValueWidth + 2)}";
            string unitPart = $"<C={ColorWhite}><S0={UnitFontSize}>" + "FPS".PadRight(UnitWidth);
            return new Cell { Text = valuePart + unitPart, FontSize = FpsFontSize, Color = ColorWhite, IsRaw = true };
        }

        /// <summary>
        /// Builds a row with a fixed-width label column so all value columns
        /// align horizontally regardless of label length.
        /// </summary>
        private static string BuildRow(string color, string label, IList<Cell> cells, bool compactLabel = false)
        {
            var sb = new StringBuilder();
            string paddedLabel = label.PadRight(LabelWidth);
            sb.Append($"<A0=-5>");
            sb.Append($"<C={color}><S0={MetricFontSize}>{paddedLabel}");

            int labelGap = compactLabel ? 1 : 2;
            sb.Append(new string(' ', labelGap));

            for (int i = 0; i < cells.Count; i++)
            {
                if (i > 0)
                    sb.Append(' '); // single space between value columns
                sb.Append(cells[i].Text);
            }

            sb.Append("<A><S>\n");
            return sb.ToString();
        }

        /// <summary>
        /// Pre-formatted cell for direct concatenation.
        /// </summary>
        private struct Cell
        {
            public string Text;
            public int FontSize;
            public string Color;
            public bool IsRaw;
        }
    }
}
