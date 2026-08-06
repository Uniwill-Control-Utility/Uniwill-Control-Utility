using System;
using System.Collections.Generic;
using System.Management;
using System.Text;
using Universal_x86_Tuning_Utility.Models;

namespace Universal_x86_Tuning_Utility.Scripts
{
    /// <summary>
    /// Detects and formats laptop system specs (RAM, storage, display) for the dashboard.
    /// </summary>
    public static class SystemSpecs
    {
        /// <summary>
        /// Returns formatted RAM string, e.g. "32 GB DDR5 @ 5600 MT/s".
        /// </summary>
        public static string GetRamString()
        {
            try
            {
                ulong totalBytes = 0;
                ushort? memoryType = null;
                uint clockSpeed = 0;

                using (var searcher = new ManagementObjectSearcher(
                    "root\\CIMV2",
                    "SELECT Capacity, SMBIOSMemoryType, ConfiguredClockSpeed FROM Win32_PhysicalMemory"))
                {
                    foreach (ManagementObject mem in searcher.Get())
                    {
                        totalBytes += Convert.ToUInt64(mem["Capacity"]);

                        if (!memoryType.HasValue)
                            memoryType = Convert.ToUInt16(mem["SMBIOSMemoryType"]);

                        if (clockSpeed == 0)
                            clockSpeed = Convert.ToUInt32(mem["ConfiguredClockSpeed"]);
                    }
                }

                if (totalBytes == 0)
                    return string.Empty;

                int gb = (int)(totalBytes / (1024UL * 1024UL * 1024UL));
                string typeStr = GetMemoryTypeName(memoryType ?? 0);
                string speedStr = clockSpeed > 0 ? $" @ {clockSpeed} MT/s" : string.Empty;

                return $"{gb} GB {typeStr}{speedStr}";
            }
            catch
            {
                return string.Empty;
            }
        }

        /// <summary>
        /// Returns formatted storage string, e.g. "2 TB M.2 NVMe" or "2 TB + 1 TB M.2 NVMe".
        /// Only includes internal fixed drives.
        /// </summary>
        public static string GetStorageString()
        {
            try
            {
                var sizes = new List<long>();

                using (var searcher = new ManagementObjectSearcher(
                    "root\\CIMV2",
                    "SELECT Model, Size, MediaType FROM Win32_DiskDrive"))
                {
                    foreach (ManagementObject disk in searcher.Get())
                    {
                        string mediaType = Convert.ToString(disk["MediaType"]) ?? "";
                        if (mediaType != "Fixed hard disk media")
                            continue;

                        string model = Convert.ToString(disk["Model"]) ?? "";
                        if (string.IsNullOrEmpty(model))
                            continue;

                        long sizeBytes = Convert.ToInt64(disk["Size"]);
                        if (sizeBytes <= 0)
                            continue;

                        sizes.Add(sizeBytes);
                    }
                }

                if (sizes.Count == 0)
                    return string.Empty;

                var sizeStrings = new List<string>();
                foreach (long s in sizes)
                {
                    long tb = s / (1000L * 1000L * 1000L * 1000L);
                    if (tb >= 1)
                        sizeStrings.Add($"{tb} TB");
                    else
                    {
                        long gb = s / (1000L * 1000L * 1000L);
                        sizeStrings.Add($"{gb} GB");
                    }
                }

                return $"{string.Join(" + ", sizeStrings)} M.2 NVMe";
            }
            catch
            {
                return string.Empty;
            }
        }

        /// <summary>
        /// Returns formatted display string, e.g. "2560x1600 @ 300 Hz | MiniLED".
        /// </summary>
        public static string GetDisplayString()
        {
            try
            {
                // --- Panel type and refresh rate from WmiMonitorID ---
                string? panelModel = null;
                string? hardwareId = null;

                try
                {
                    using (var monitorSearcher = new ManagementObjectSearcher(
                        "root\\wmi", "SELECT * FROM WmiMonitorID"))
                    {
                        foreach (ManagementObject monitor in monitorSearcher.Get())
                        {
                            if (!(bool)monitor["Active"])
                                continue;

                            // User-friendly panel model name
                            byte[]? friendlyNameBytes = monitor["UserFriendlyName"] as byte[];
                            int nameLen = Convert.ToInt32(monitor["UserFriendlyNameLength"]);
                            if (friendlyNameBytes != null && nameLen > 0)
                            {
                                string name = Encoding.ASCII.GetString(friendlyNameBytes)
                                    .TrimEnd('\0', '\r', '\n', ' ');
                                if (!string.IsNullOrEmpty(name))
                                    panelModel = name;
                            }

                            // Hardware ID from InstanceName: "DISPLAY\BOE0D5A\5&..."
                            string instanceName = Convert.ToString(monitor["InstanceName"]) ?? "";
                            if (!string.IsNullOrEmpty(instanceName))
                            {
                                int bs1 = instanceName.IndexOf('\\');
                                int bs2 = instanceName.IndexOf('\\', bs1 + 1);
                                if (bs1 >= 0 && bs2 > bs1)
                                {
                                    hardwareId = instanceName.Substring(bs1 + 1, bs2 - bs1 - 1);
                                }
                            }

                            // First active monitor is the internal panel
                            break;
                        }
                    }
                }
                catch { /* root\wmi not available */ }

                // Look up panel info from database
                PanelInfo? panelInfo = null;

                if (!string.IsNullOrEmpty(hardwareId))
                {
                    panelInfo = PanelDatabase.LookupByHardwareId(hardwareId);
                }

                if (panelInfo == null && !string.IsNullOrEmpty(panelModel))
                {
                    panelInfo = PanelDatabase.LookupByModel(panelModel);
                }

                string panelType = panelInfo != null ? panelInfo.Value.PanelType : "IPS";
                int refreshRate = panelInfo != null ? panelInfo.Value.MaxRefreshRateHz : 165;

                // --- Resolution from Win32_VideoController ---
                // On laptops with Mux/AGS the iGPU drives the panel (correct resolution),
                // while the dGPU may report an external monitor's resolution.
                // Strategy: prefer the iGPU (AMD Radeon Graphics / Intel) resolution
                // because it's the one actually driving the laptop panel.
                int width = 0;
                int height = 0;

                try
                {
                    using (var gpuSearcher = new ManagementObjectSearcher(
                        "root\\CIMV2",
                        "SELECT Name, CurrentHorizontalResolution, CurrentVerticalResolution FROM Win32_VideoController"))
                    {
                        // Collect all GPUs first
                        var gpus = new List<ManagementObject>();
                        foreach (ManagementObject gpu in gpuSearcher.Get())
                            gpus.Add(gpu);

                        // First pass: find iGPU (AMD Radeon Graphics or Intel — not discrete)
                        foreach (ManagementObject gpu in gpus)
                        {
                            string name = Convert.ToString(gpu["Name"]) ?? "";
                            bool isIgpu = name.Contains("Radeon Graphics", StringComparison.OrdinalIgnoreCase) ||
                                          name.Contains("Radeon(TM)", StringComparison.OrdinalIgnoreCase) ||
                                          name.Contains("Intel", StringComparison.OrdinalIgnoreCase) ||
                                          name.Contains("UHD Graphics", StringComparison.OrdinalIgnoreCase) ||
                                          name.Contains("Iris", StringComparison.OrdinalIgnoreCase);

                            if (isIgpu)
                            {
                                width = Convert.ToInt32(gpu["CurrentHorizontalResolution"]);
                                height = Convert.ToInt32(gpu["CurrentVerticalResolution"]);
                                break;
                            }
                        }

                        // Fallback: use first GPU with a reasonable resolution
                        if (width == 0 || height == 0)
                        {
                            foreach (ManagementObject gpu in gpus)
                            {
                                string name = Convert.ToString(gpu["Name"]) ?? "";
                                if (name.Contains("Microsoft", StringComparison.OrdinalIgnoreCase))
                                    continue;
                                if (name.Contains("SudoMaker", StringComparison.OrdinalIgnoreCase))
                                    continue;

                                width = Convert.ToInt32(gpu["CurrentHorizontalResolution"]);
                                height = Convert.ToInt32(gpu["CurrentVerticalResolution"]);
                                break;
                            }
                        }
                    }
                }
                catch { /* WMI not available */ }

                if (width > 0 && height > 0)
                    return $"{width}x{height} @ {refreshRate} Hz | {panelType}";

                return $"{refreshRate} Hz | {panelType}";
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string GetMemoryTypeName(ushort smbiosType)
        {
            return smbiosType switch
            {
                20 => "DDR",
                21 => "DDR2",
                24 => "DDR3",
                26 => "DDR4",
                30 => "LPDDR4",
                34 => "DDR5",
                35 => "LPDDR5",
                36 => "LPDDR5X",
                _ => "DDR"
            };
        }
    }
}
