namespace Uniwill_Control_Utility.Models
{
    /// <summary>
    /// Represents a discovered LCT watercooler BLE device.
    /// </summary>
    public class WaterCoolerDeviceInfo
    {
        /// <summary>The BLE device address (UUID).</summary>
        public string Address { get; set; } = string.Empty;

        /// <summary>The friendly device name reported by the cooler.</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>Signal strength in dBm (negative value, closer to zero is stronger).</summary>
        public int Rssi { get; set; }

        /// <summary>The detected device model (e.g. LCT21001). Null if unknown.</summary>
        public string? Model { get; set; }
    }
}
