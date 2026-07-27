using Newtonsoft.Json;

namespace Universal_x86_Tuning_Utility.Models
{
    /// <summary>
    /// Persisted settings for the Fan Control page.
    /// Stored in %APPDATA%\UXTU\fancontrol_settings.json
    /// </summary>
    public class FanControlSettings
    {
        /// <summary>
        /// Whether Unified Fan Curve mode is enabled (single curve for both CPU &amp; GPU).
        /// </summary>
        public bool UnifiedMode { get; set; }

        /// <summary>
        /// Selected preset name for unified mode (Silent, Balanced, Performance, Full Speed, Off, Custom).
        /// </summary>
        public string UnifiedPreset { get; set; } = "Balanced";

        /// <summary>
        /// Custom duty values (0-100%) for unified mode, 11 zone indices.
        /// Only meaningful when UnifiedPreset == "Custom".
        /// </summary>
        public int[]? UnifiedDuties { get; set; }

        /// <summary>
        /// CPU preset name (Silent, Balanced, Performance, Full Speed, Off, Custom).
        /// Only used when UnifiedMode is false.
        /// </summary>
        public string CpuPreset { get; set; } = "Balanced";

        /// <summary>
        /// Custom duty values (0-100%) for CPU, 11 zone indices.
        /// Only meaningful when CpuPreset == "Custom".
        /// </summary>
        public int[]? CpuDuties { get; set; }

        /// <summary>
        /// GPU preset name (Silent, Balanced, Performance, Full Speed, Off, Custom).
        /// Only used when UnifiedMode is false.
        /// </summary>
        public string GpuPreset { get; set; } = "Balanced";

        /// <summary>
        /// Custom duty values (0-100%) for GPU, 11 zone indices.
        /// Only meaningful when GpuPreset == "Custom".
        /// </summary>
        public int[]? GpuDuties { get; set; }

        /// <summary>
        /// User-defined CPU temperature thresholds (temp_up values), 11 zone indices.
        /// Null means use defaults from EcFanCurve.CpuTemperatures.
        /// </summary>
        public int[]? CpuTempThresholds { get; set; }

        /// <summary>
        /// User-defined GPU temperature thresholds (temp_up values), 11 zone indices.
        /// Null means use defaults from EcFanCurve.GpuTemperatures.
        /// </summary>
        public int[]? GpuTempThresholds { get; set; }

        /// <summary>
        /// User-defined unified temperature thresholds (temp_up values), 11 zone indices.
        /// Used when UnifiedMode is true. Null means use defaults from EcFanCurve.CpuTemperatures.
        /// </summary>
        public int[]? UnifiedTempThresholds { get; set; }
    }
}
