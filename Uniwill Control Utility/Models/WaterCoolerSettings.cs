using System;
using Newtonsoft.Json;

namespace Uniwill_Control_Utility.Models
{
    /// <summary>
    /// Persistent settings for watercooler control.
    /// Stored as JSON alongside other UCU configuration files.
    /// </summary>
    public class WaterCoolerSettings
    {
        [JsonProperty("pumpVoltage")]
        public string PumpVoltage { get; set; } = "Off";

        [JsonProperty("fanSpeed")]
        public string FanSpeed { get; set; } = "Off";

        [JsonProperty("rgbMode")]
        public string RgbMode { get; set; } = "Static";

        [JsonProperty("rgbColor")]
        public string RgbColor { get; set; } = "Red";

        [JsonProperty("pumpEnabled")]
        public bool PumpEnabled { get; set; }

        [JsonProperty("fanEnabled")]
        public bool FanEnabled { get; set; }

        [JsonProperty("rgbEnabled")]
        public bool RgbEnabled { get; set; } = true;

        [JsonProperty("autoConnect")]
        public bool AutoConnect { get; set; } = false;

        [JsonProperty("lastDeviceAddress")]
        public string LastDeviceAddress { get; set; } = "";

        /* ------------------------------------------------------------------ */
        /*  Smart Mode — temperature-linked pump and fan speed                   */
        /* ------------------------------------------------------------------ */

        [JsonProperty("smartModeEnabled", NullValueHandling = NullValueHandling.Ignore)]
        public bool SmartModeEnabled { get; set; }

        /// <summary>Temperature source: "cpu", "gpu", or "max" (highest of the two).</summary>
        [JsonProperty("smartModeTempSource", NullValueHandling = NullValueHandling.Ignore)]
        public string SmartModeTempSource { get; set; } = "max";

        /// <summary>Low threshold (°C) — below this, pump/fan run at IdleSpeed.</summary>
        [JsonProperty("smartModeLowThreshold", NullValueHandling = NullValueHandling.Ignore)]
        public int SmartModeLowThreshold { get; set; } = 50;

        /// <summary>High threshold (°C) — above this, pump/fan run at BoostSpeed.</summary>
        [JsonProperty("smartModeHighThreshold", NullValueHandling = NullValueHandling.Ignore)]
        public int SmartModeHighThreshold { get; set; } = 70;

        /// <summary>
        /// Hysteresis deadzone in °C — fixed at 10°C to prevent oscillation.
        /// The actual hysteresis is provided by the gap between low and high thresholds.
        /// This field is persisted but no longer exposed in the UI.
        /// </summary>
        [JsonProperty("smartModeHysteresis", NullValueHandling = NullValueHandling.Ignore)]
        public int SmartModeHysteresis { get; set; } = 10;

        /// <summary>Pump voltage when temperature is below the low threshold.</summary>
        [JsonProperty("smartModeIdlePump", NullValueHandling = NullValueHandling.Ignore)]
        public string SmartModeIdlePump { get; set; } = "V7";

        /// <summary>Pump voltage when temperature is above the high threshold.</summary>
        [JsonProperty("smartModeBoostPump", NullValueHandling = NullValueHandling.Ignore)]
        public string SmartModeBoostPump { get; set; } = "V11";

        /// <summary>Fan speed when temperature is below the low threshold.</summary>
        [JsonProperty("smartModeIdleFan", NullValueHandling = NullValueHandling.Ignore)]
        public string SmartModeIdleFan { get; set; } = "Percent50";

        /// <summary>Fan speed when temperature is above the high threshold.</summary>
        [JsonProperty("smartModeBoostFan", NullValueHandling = NullValueHandling.Ignore)]
        public string SmartModeBoostFan { get; set; } = "Percent100";

        /* -- Obsolete legacy properties (Smart Pump Mode) -- */
        // These are deserialized from old settings files so we can migrate them.
        // They are NOT written back to disk (Ignore = True on serialize).

        [JsonProperty("smartPumpEnabled", DefaultValueHandling = DefaultValueHandling.Ignore,
                       NullValueHandling = NullValueHandling.Ignore)]
        [Obsolete("Migrated to SmartModeEnabled")]
        private bool? _legacySmartPumpEnabled { get; set; }

        [JsonProperty("smartPumpTempSource", DefaultValueHandling = DefaultValueHandling.Ignore,
                       NullValueHandling = NullValueHandling.Ignore)]
        [Obsolete("Migrated to SmartModeTempSource")]
        private string? _legacySmartPumpTempSource { get; set; }

        [JsonProperty("smartPumpLowThreshold", DefaultValueHandling = DefaultValueHandling.Ignore,
                       NullValueHandling = NullValueHandling.Ignore)]
        [Obsolete("Migrated to SmartModeLowThreshold")]
        private int? _legacySmartPumpLowThreshold { get; set; }

        [JsonProperty("smartPumpHighThreshold", DefaultValueHandling = DefaultValueHandling.Ignore,
                       NullValueHandling = NullValueHandling.Ignore)]
        [Obsolete("Migrated to SmartModeHighThreshold")]
        private int? _legacySmartPumpHighThreshold { get; set; }

        [JsonProperty("smartPumpIdleSpeed", DefaultValueHandling = DefaultValueHandling.Ignore,
                       NullValueHandling = NullValueHandling.Ignore)]
        [Obsolete("Migrated to SmartModeIdlePump")]
        private string? _legacySmartPumpIdleSpeed { get; set; }

        [JsonProperty("smartPumpBoostSpeed", DefaultValueHandling = DefaultValueHandling.Ignore,
                       NullValueHandling = NullValueHandling.Ignore)]
        [Obsolete("Migrated to SmartModeBoostPump")]
        private string? _legacySmartPumpBoostSpeed { get; set; }

        /// <summary>
        /// Migrates legacy Smart Pump Mode settings to Smart Mode if the new fields
        /// have not been populated (i.e., the settings file still uses old JSON keys).
        /// Only migrates individual fields when the new field is still at its default value,
        /// so user-set Smart Mode values are never overwritten by stale legacy data.
        /// Call once after deserialization.
        /// </summary>
        public void MigrateLegacySettings()
        {
            // SmartModeEnabled default is false — legacy could also be false, so only
            // migrate when the legacy value is explicitly true (user had it enabled).
            if (_legacySmartPumpEnabled == true && SmartModeEnabled == false)
                SmartModeEnabled = true;

            // String fields have non-null defaults ("max", "V7", "V11") — only migrate
            // when the new field is still at its default, meaning it wasn't written to disk yet.
            if (_legacySmartPumpTempSource != null && SmartModeTempSource == "max")
                SmartModeTempSource = _legacySmartPumpTempSource;

            if (_legacySmartPumpLowThreshold.HasValue && SmartModeLowThreshold == 50)
                SmartModeLowThreshold = _legacySmartPumpLowThreshold.Value;

            if (_legacySmartPumpHighThreshold.HasValue && SmartModeHighThreshold == 70)
                SmartModeHighThreshold = _legacySmartPumpHighThreshold.Value;

            if (_legacySmartPumpIdleSpeed != null && SmartModeIdlePump == "V7")
                SmartModeIdlePump = _legacySmartPumpIdleSpeed;

            if (_legacySmartPumpBoostSpeed != null && SmartModeBoostPump == "V11")
                SmartModeBoostPump = _legacySmartPumpBoostSpeed;
        }

        /// <summary>Returns the enum value for the stored idle pump speed in Smart Mode.</summary>
        public PumpVoltage GetSmartModeIdlePump()
        {
            return Enum.TryParse<PumpVoltage>(SmartModeIdlePump, true, out var v)
                ? v : Models.PumpVoltage.V7;
        }

        /// <summary>Returns the enum value for the stored boost pump speed in Smart Mode.</summary>
        public PumpVoltage GetSmartModeBoostPump()
        {
            return Enum.TryParse<PumpVoltage>(SmartModeBoostPump, true, out var v)
                ? v : Models.PumpVoltage.V11;
        }

        /// <summary>Returns the enum value for the stored idle fan speed in Smart Mode.</summary>
        public FanSpeed GetSmartModeIdleFan()
        {
            return Enum.TryParse<FanSpeed>(SmartModeIdleFan, true, out var f)
                ? f : Models.FanSpeed.Percent50;
        }

        /// <summary>Returns the enum value for the stored boost fan speed in Smart Mode.</summary>
        public FanSpeed GetSmartModeBoostFan()
        {
            return Enum.TryParse<FanSpeed>(SmartModeBoostFan, true, out var f)
                ? f : Models.FanSpeed.Percent100;
        }

        /// <summary>
        /// Returns the enum value for the stored pump voltage setting.
        /// </summary>
        public PumpVoltage GetPumpVoltage()
        {
            return Enum.TryParse<PumpVoltage>(PumpVoltage, true, out var v) ? v : Uniwill_Control_Utility.Models.PumpVoltage.Off;
        }

        /// <summary>
        /// Returns the enum value for the stored fan speed setting.
        /// </summary>
        public FanSpeed GetFanSpeed()
        {
            return Enum.TryParse<FanSpeed>(FanSpeed, true, out var f) ? f : Uniwill_Control_Utility.Models.FanSpeed.Off;
        }

        /// <summary>
        /// Returns the enum value for the stored RGB mode setting.
        /// </summary>
        public RgbState GetRgbMode()
        {
            return Enum.TryParse<RgbState>(RgbMode, true, out var m) ? m : Uniwill_Control_Utility.Models.RgbState.Static;
        }

        /// <summary>
        /// Returns the enum value for the stored RGB color setting.
        /// </summary>
        public RgbColor GetRgbColor()
        {
            return Enum.TryParse<RgbColor>(RgbColor, true, out var c) ? c : Uniwill_Control_Utility.Models.RgbColor.Red;
        }
    }
}
