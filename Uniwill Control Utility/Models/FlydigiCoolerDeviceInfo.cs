namespace Uniwill_Control_Utility.Models
{
    /// <summary>
    /// Represents a discovered Flydigi BS series cooling pad device (BS2, BS2 PRO, BS3, BS3 PRO).
    /// </summary>
    public class FlydigiCoolerDeviceInfo
    {
        /// <summary>The HID device path (e.g. "\\?\hid#..."). Matches HidDevice.DevicePath.</summary>
        public string DevicePath { get; set; } = string.Empty;

        /// <summary>The product name from the device (e.g. "BS2 PRO").</summary>
        public string ProductName { get; set; } = string.Empty;

        /// <summary>The manufacturer string from the device.</summary>
        public string Manufacturer { get; set; } = string.Empty;

        /// <summary>The serial number from the device.</summary>
        public string SerialNumber { get; set; } = string.Empty;

        /// <summary>Product ID (e.g. 0x1002 for BS2PRO).</summary>
        public ushort ProductId { get; set; }

        /// <summary>Human-readable device model name.</summary>
        public string ModelName => ProductId switch
        {
            Bs2ProProductId.B2 => "BS2",
            Bs2ProProductId.B2Pro => "BS2 PRO",
            Bs2ProProductId.B3 => "BS3",
            Bs2ProProductId.B3Pro => "BS3 PRO",
            _ => $"Unknown (0x{ProductId:X4})"
        };
    }
}
