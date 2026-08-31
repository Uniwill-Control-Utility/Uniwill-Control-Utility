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
