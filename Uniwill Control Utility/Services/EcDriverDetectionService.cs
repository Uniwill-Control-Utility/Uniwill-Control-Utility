using System;
using System.IO;

namespace Uniwill_Control_Utility.Services
{
    /// <summary>
    /// Detects whether the UWACPIDriver.sys kernel driver is installed on the system.
    /// The driver is required for EC communication (fan control, battery, TGP, etc.).
    /// It is typically installed by the OEM Control Center and persists after uninstall.
    /// </summary>
    public static class EcDriverDetectionService
    {
        private const string DriverPath = @"C:\Windows\System32\drivers\UWACPIDriver.sys";

        /// <summary>
        /// Returns true if the driver file exists on disk.
        /// </summary>
        public static bool IsInstalled()
        {
            return File.Exists(DriverPath);
        }

        /// <summary>
        /// Returns the full path to the driver file.
        /// </summary>
        public static string DriverPathString => DriverPath;
    }
}
