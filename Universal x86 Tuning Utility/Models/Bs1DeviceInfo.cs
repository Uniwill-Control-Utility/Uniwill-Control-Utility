namespace Universal_x86_Tuning_Utility.Models
{
    /// <summary>
    /// Represents a discovered Flydigi BS1 cooling pad device.
    /// The BS1 is BLE-only and has no product ID — it is identified by its
    /// BLE advertising name.
    /// </summary>
    public class Bs1DeviceInfo
    {
        /// <summary>Windows DeviceId (e.g. "\\?\Bluetooth#...") or raw Bluetooth address (e.g. "001A7DDA7113").</summary>
        public string Address { get; set; } = string.Empty;

        /// <summary>BLE advertising local name (e.g. "Flydigi BS1").</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>Signal strength in dBm from the last advertisement received.</summary>
        public int Rssi { get; set; }

        /// <summary>Human-readable model name (always "BS1" for this type).</summary>
        public string ModelName => "BS1";
    }
}
