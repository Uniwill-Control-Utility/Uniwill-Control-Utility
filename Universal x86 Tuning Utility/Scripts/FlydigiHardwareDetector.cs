using System;
using System.Linq;
using System.Threading.Tasks;
using HidLibrary;
using Windows.Devices.Bluetooth.Advertisement;

namespace Universal_x86_Tuning_Utility.Scripts
{
    /// <summary>
    /// The type of Flydigi device currently connected (as reported by the FlydigiCooler page).
    /// </summary>
    public enum ConnectedDeviceType
    {
        None,
        BS1,        // BLE-only, no RGB, max 3000 RPM
        Hid,        // BS2/BS2 Pro/BS3/BS3 Pro (HID, with RGB)
    }

    /// <summary>
    /// Detects whether a Flydigi BS series cooling pad is currently connected.
    /// Supports HID devices (BS2/BS2 Pro/BS3/BS3 Pro) and BLE devices (BS1).
    /// </summary>
    public static class FlydigiHardwareDetector
    {
        private const int VendorId = 0x37D7;
        private static readonly int[] ProductIds = { 0x1001, 0x1002, 0x1003, 0x1004 };

        private static bool? _hasDevice;
        private static string? _cachedModelName;
        private static bool? _hasBs1;

        /// <summary>
        /// The currently connected Flydigi device type. Updated by the FlydigiCooler page
        /// when a device connects or disconnects.
        /// </summary>
        public static ConnectedDeviceType ConnectedDeviceType { get; private set; } = ConnectedDeviceType.None;

        /// <summary>
        /// The model name of the currently connected device (e.g., "BS1", "BS2 PRO").
        /// Set alongside ConnectedDeviceType when a device connects.
        /// </summary>
        public static string ConnectedModelName { get; private set; } = "Flydigi Cooler";

        /// <summary>
        /// Notifies the detector that a specific device type connected or disconnected.
        /// Called by the FlydigiCooler page on connection state changes.
        /// </summary>
        public static void SetConnectedDeviceType(ConnectedDeviceType type, string? modelName = null)
        {
            ConnectedDeviceType = type;
            ConnectedModelName = (type == ConnectedDeviceType.None)
                ? "Flydigi Cooler"
                : (modelName ?? (type == ConnectedDeviceType.BS1 ? "BS1" : "Flydigi Cooler"));
            InvalidateCache();
        }

        /// <summary>
        /// Returns the model name of the currently connected device, or "Flydigi Cooler" if none.
        /// </summary>
        public static string GetConnectedModelName()
        {
            return ConnectedModelName;
        }

        /// <summary>
        /// Returns true if any Flydigi device is currently connected (reported by the FlydigiCooler page).
        /// </summary>
        public static bool IsAnyDeviceConnected()
        {
            return ConnectedDeviceType != ConnectedDeviceType.None;
        }

        /// <summary>
        /// Clears the cached detection result. Call when device plug/unplug events occur.
        /// </summary>
        public static void InvalidateCache()
        {
            _hasDevice = null;
            _cachedModelName = null;
            _hasBs1 = null;
        }

        /// <summary>
        /// Returns true if a Flydigi BS series cooling pad is currently connected via HID.
        /// Result is cached after first call. Use InvalidateCache() to force re-check.
        /// </summary>
        public static bool IsDeviceAvailable()
        {
            if (_hasDevice.HasValue)
                return _hasDevice.Value;

            try
            {
                var devices = HidDevices.Enumerate(VendorId, ProductIds);
                bool available = devices.Any();
                _hasDevice = available;
                return available;
            }
            catch
            {
                // HidLibrary not available or enumeration failed
                _hasDevice = false;
                return false;
            }
        }

        /// <summary>
        /// Returns true if a Flydigi BS1 (BLE-only) cooling pad is currently available.
        /// Checks paired BLE devices first, then falls back to a brief BLE advertisement scan.
        /// </summary>
        public static async Task<bool> IsBs1AvailableAsync(int timeoutMs = 3000)
        {
            if (_hasBs1.HasValue)
                return _hasBs1.Value;

            try
            {
                // 1. Check paired BLE devices first (faster, works when device isn't advertising)
                var selector = Windows.Devices.Bluetooth.BluetoothLEDevice.GetDeviceSelectorFromPairingState(true);
                var pairedDevices = await Windows.Devices.Enumeration.DeviceInformation.FindAllAsync(selector);
                foreach (var deviceInfo in pairedDevices)
                {
                    var name = deviceInfo.Name;
                    if (!string.IsNullOrEmpty(name) && IsBs1AdvertisingName(name))
                    {
                        _hasBs1 = true;
                        return true;
                    }
                }

                // 2. Fall back to BLE advertisement scan for unpaired devices
                var watcher = new BluetoothLEAdvertisementWatcher();
                var found = new TaskCompletionSource<bool>();

                watcher.Received += (s, e) =>
                {
                    var name = e.Advertisement.LocalName;
                    if (!string.IsNullOrEmpty(name) && IsBs1AdvertisingName(name))
                    {
                        found.TrySetResult(true);
                    }
                };

                watcher.Start();

                // Wait for either a match or timeout
                bool matched = await found.Task.WaitAsync(TimeSpan.FromMilliseconds(timeoutMs));
                watcher.Stop();

                _hasBs1 = matched;
                return matched;
            }
            catch
            {
                // BLE not available or permission denied
                _hasBs1 = false;
                return false;
            }
        }

        /// <summary>
        /// Returns true if any Flydigi device (HID or BLE) is available.
        /// </summary>
        public static async Task<bool> IsAnyDeviceAvailableAsync()
        {
            bool hasHid = IsDeviceAvailable();
            if (hasHid)
                return true;

            return await IsBs1AvailableAsync();
        }

        /// <summary>
        /// Checks if a BLE advertising name matches a known Flydigi BS1 device.
        /// </summary>
        private static bool IsBs1AdvertisingName(string name)
        {
            string lower = name.ToLowerInvariant();
            return lower.Contains("flydigi") ||
                   lower.Contains("bs1") ||
                   lower.Contains("cooling pad");
        }

        /// <summary>
        /// Returns the model name of the first detected Flydigi cooling pad (e.g., "BS2 PRO").
        /// Returns "Flydigi Cooler" if no device is detected or model is unknown.
        /// </summary>
        public static string GetDetectedModelName()
        {
            if (_cachedModelName != null)
                return _cachedModelName;

            try
            {
                var devices = HidDevices.Enumerate(VendorId, ProductIds);
                var first = devices.FirstOrDefault();
                if (first != null)
                {
                    ushort productId = (ushort)first.Attributes.ProductId;
                    _cachedModelName = productId switch
                    {
                        0x1001 => "BS2",
                        0x1002 => "BS2 PRO",
                        0x1003 => "BS3",
                        0x1004 => "BS3 PRO",
                        _ => "Flydigi Cooler"
                    };
                }
                else
                {
                    _cachedModelName = "Flydigi Cooler";
                }
            }
            catch
            {
                _cachedModelName = "Flydigi Cooler";
            }

            return _cachedModelName;
        }
    }
}
