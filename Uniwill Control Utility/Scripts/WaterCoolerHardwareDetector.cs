using System.Management;

namespace Uniwill_Control_Utility.Scripts
{
    /// <summary>
    /// Detects whether the current laptop is a supported Tongfang/LCT watercooler model.
    /// Supported brands: XMG Oasis, PC Specialist, Eluktronics, TUXEDO Aquaris.
    /// </summary>
    public static class WaterCoolerHardwareDetector
    {
        private static bool? _hasWatercooler;

        /// <summary>
        /// Returns true if the system is a known Tongfang laptop with LCT watercooler support.
        /// Result is cached after first call.
        /// </summary>
        public static bool IsSupportedHardware()
        {
            if (_hasWatercooler.HasValue)
                return _hasWatercooler.Value;

            try
            {
                using var searcher = new ManagementObjectSearcher("root\\CIMV2", "SELECT * FROM Win32_ComputerSystem");

                foreach (ManagementObject obj in searcher.Get())
                {
                    var manufacturer = obj["Manufacturer"]?.ToString() ?? "";
                    var model = obj["Model"]?.ToString() ?? "";
                    var productName = obj["Name"]?.ToString() ?? "";

                    var combined = (manufacturer + " " + model + " " + productName).ToLower();

                    // Check for known supported brands/models
                    if (combined.Contains("tongfang") ||
                        combined.Contains("xmg") ||
                        combined.Contains("oasis") ||
                        combined.Contains("schenker") ||
                        combined.Contains("specialist") ||
                        combined.Contains("eluktronics") ||
                        combined.Contains("tuxedo") ||
                        combined.Contains("aquaris"))
                    {
                        _hasWatercooler = true;
                        return true;
                    }
                }
            }
            catch
            {
                // WMI not available - default to showing the feature
            }

            // If we can't detect, show the feature anyway (user can override)
            _hasWatercooler = false;
            return false;
        }
    }
}
