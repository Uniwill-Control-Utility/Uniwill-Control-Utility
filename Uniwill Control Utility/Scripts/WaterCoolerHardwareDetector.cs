using System.Management;

namespace Uniwill_Control_Utility.Scripts
{
    /// <summary>
    /// Detects whether the current laptop is a supported TongFeng/LCT watercooler chassis.
    ///
    /// Instead of matching brand names (which vary per rebrand — XMG, Schenker, Mifcom,
    /// PCSpecialist, TUXEDO, Eluktronics, etc.), this checks the baseboard product
    /// identifier from Win32_BaseBoard. The first 4 characters uniquely identify the
    /// TongFeng chassis and are unaffected by OEM BIOS rebranding:
    ///
    ///   X6FR — AMD TongFeng chassis (e.g. NB05)
    ///   X6AR — Intel TongFeng chassis (e.g. NB02)
    /// </summary>
    public static class WaterCoolerHardwareDetector
    {
        /// <summary>
        /// Baseboard product prefixes that identify a TongFeng watercooler-capable chassis.
        /// </summary>
        private static readonly string[] SupportedBaseBoardPrefixes =
        {
            "X6FR", // AMD TongFeng (NB05)
            "X6AR", // Intel TongFeng (NB02)
        };

        private static bool? _hasWatercooler;

        /// <summary>
        /// Returns true if the system has a TongFeng baseboard with LCT watercooler support.
        /// Result is cached after first call.
        /// </summary>
        public static bool IsSupportedHardware()
        {
            if (_hasWatercooler.HasValue)
                return _hasWatercooler.Value;

            try
            {
                using var searcher = new ManagementObjectSearcher(
                    "root\\CIMV2",
                    "SELECT Product FROM Win32_BaseBoard");

                foreach (ManagementObject obj in searcher.Get())
                {
                    var product = obj["Product"]?.ToString()?.Trim();
                    if (string.IsNullOrEmpty(product))
                        continue;

                    foreach (var prefix in SupportedBaseBoardPrefixes)
                    {
                        if (product.StartsWith(prefix, System.StringComparison.OrdinalIgnoreCase))
                        {
                            _hasWatercooler = true;
                            return true;
                        }
                    }
                }
            }
            catch
            {
                // WMI not available — default to showing the feature so users can
                // still attempt a BLE connection manually.
            }

            _hasWatercooler = true;
            return true;
        }
    }
}
