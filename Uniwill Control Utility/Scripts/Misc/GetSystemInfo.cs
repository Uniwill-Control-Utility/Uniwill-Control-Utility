using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Runtime.Intrinsics.X86;
using static Uniwill_Control_Utility.Scripts.Family;

namespace Uniwill_Control_Utility.Scripts.Misc
{
    internal class GetSystemInfo
    {
        private static ManagementObjectSearcher baseboardSearcher = new ManagementObjectSearcher("root\\CIMV2", "SELECT * FROM Win32_BaseBoard");
        private static ManagementObjectSearcher motherboardSearcher = new ManagementObjectSearcher("root\\CIMV2", "SELECT * FROM Win32_MotherboardDevice");
        private static ManagementObjectSearcher ComputerSsystemInfo = new ManagementObjectSearcher("root\\CIMV2", "SELECT * FROM Win32_ComputerSystemProduct");

        public static bool IsGPUPresent(string gpuName)
        {
            // Create a query to search for GPU devices
            var query = new SelectQuery("SELECT * FROM Win32_VideoController");

            // Create a ManagementObjectSearcher object with the query
            using (var searcher = new ManagementObjectSearcher(query))
            {
                // Execute the query and get the collection of ManagementObject
                var results = searcher.Get();

                // Iterate through each ManagementObject in the collection
                foreach (var result in results)
                {
                    // Get the Name property of the GPU
                    var name = result["Name"]?.ToString();

                    // Check if the GPU name matches the specified name
                    if (!string.IsNullOrEmpty(name) && name.Contains(gpuName, StringComparison.OrdinalIgnoreCase))
                    {
                        // GPU with the specified name found, return true
                        return true;
                    }
                }
            }

            // GPU with the specified name not found, return false
            return false;
        }

        public static string GetCPUName()
        {
            try
            {
                ManagementObjectSearcher searcher = new ManagementObjectSearcher("root\\CIMV2", "SELECT * FROM Win32_Processor");
                ManagementObjectCollection collection = searcher.Get();
                foreach (ManagementObject obj in collection)
                {
                    return obj["Name"].ToString();
                }
            }
            catch (ManagementException ex)
            {
                DiagnosticLogger.LogError(ex, "Failed to get CPU name");
            }
            return "";
        }
        public static string GetGPUName(int i)
        {
            try
            {
                int count = 0;
                ManagementObjectSearcher searcher = new ManagementObjectSearcher("root\\CIMV2", $"SELECT * FROM Win32_VideoController"); // Change AdapterCompatibility as per your requirement
                ManagementObjectCollection collection = searcher.Get();

                foreach (ManagementObject obj in collection)
                {
                    if (count == i)
                    {
                        Garbage.Garbage_Collect();
                        return obj["Name"].ToString();
                    }
                    count++;
                }
            }
            catch (ManagementException ex)
            {
                DiagnosticLogger.LogError(ex, "Failed to get GPU name");
            }

            Garbage.Garbage_Collect();
            return "";
        }

        static public string Availability
        {
            get
            {
                try
                {
                    var result = motherboardSearcher.Get().Cast<ManagementObject>().FirstOrDefault();
                    if (result == null) return "";
                    return GetAvailability(Convert.ToInt32(result[nameof(Availability)]));
                }
                catch (ManagementException)
                {
                    return "";
                }
            }
        }

        static public bool HostingBoard
        {
            get
            {
                try
                {
                    var result = baseboardSearcher.Get().Cast<ManagementObject>().FirstOrDefault();
                    return result != null && Convert.ToBoolean(result[nameof(HostingBoard)]);
                }
                catch (ManagementException)
                {
                    return false;
                }
            }
        }

        static public string InstallDate
        {
            get
            {
                try
                {
                    foreach (ManagementObject queryObj in baseboardSearcher.Get())
                    {
                        return ConvertToDateTime(queryObj["InstallDate"].ToString());
                    }
                    return "";
                }
                catch (ManagementException)
                {
                    return "";
                }
            }
        }

        static public string Manufacturer
        {
            get
            {
                try
                {
                    foreach (ManagementObject queryObj in baseboardSearcher.Get())
                    {
                        return queryObj["Manufacturer"].ToString();
                    }
                    return "";
                }
                catch (ManagementException)
                {
                    return "";
                }
            }
        }

        static public string Model
        {
            get
            {
                try
                {
                    foreach (ManagementObject queryObj in baseboardSearcher.Get())
                    {
                        return Convert.ToString(queryObj["Model"]);
                    }
                    return "";
                }
                catch (ManagementException)
                {
                    return "";
                }
            }
        }

        static public string PartNumber
        {
            get
            {
                try
                {
                    foreach (ManagementObject queryObj in baseboardSearcher.Get())
                    {
                        return queryObj["PartNumber"].ToString();
                    }
                    return "";
                }
                catch (ManagementException)
                {
                    return "";
                }
            }
        }

        static public string PNPDeviceID
        {
            get
            {
                try
                {
                    foreach (ManagementObject queryObj in motherboardSearcher.Get())
                    {
                        return queryObj["PNPDeviceID"].ToString();
                    }
                    return "";
                }
                catch (ManagementException)
                {
                    return "";
                }
            }
        }

        static public string PrimaryBusType
        {
            get
            {
                try
                {
                    foreach (ManagementObject queryObj in motherboardSearcher.Get())
                    {
                        return queryObj["PrimaryBusType"].ToString();
                    }
                    return "";
                }
                catch (ManagementException)
                {
                    return "";
                }
            }
        }

        static public string Product
        {
            get
            {
                try
                {
                    using RegistryKey? biosKey = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
                    string? productName = biosKey?.GetValue("SystemProductName")?.ToString();
                    if (!string.IsNullOrWhiteSpace(productName))
                        return productName;

                    foreach (ManagementObject queryObj in ComputerSsystemInfo.Get())
                    {
                        return queryObj["Name"].ToString();
                    }
                    return "";
                }
                catch (ManagementException)
                {
                    return "";
                }
            }
        }

        static public bool Removable
        {
            get
            {
                try
                {
                    var result = baseboardSearcher.Get().Cast<ManagementObject>().FirstOrDefault();
                    return result != null && Convert.ToBoolean(result[nameof(Removable)]);
                }
                catch (ManagementException)
                {
                    return false;
                }
            }
        }

        static public bool Replaceable
        {
            get
            {
                try
                {
                    var result = baseboardSearcher.Get().Cast<ManagementObject>().FirstOrDefault();
                    return result != null && Convert.ToBoolean(result[nameof(Replaceable)]);
                }
                catch (ManagementException)
                {
                    return false;
                }
            }
        }

        static public string RevisionNumber
        {
            get
            {
                try
                {
                    foreach (ManagementObject queryObj in motherboardSearcher.Get())
                    {
                        return queryObj["RevisionNumber"].ToString();
                    }
                    return "";
                }
                catch (ManagementException)
                {
                    return "";
                }
            }
        }

        static public string SecondaryBusType
        {
            get
            {
                try
                {
                    foreach (ManagementObject queryObj in motherboardSearcher.Get())
                    {
                        return queryObj["SecondaryBusType"].ToString();
                    }
                    return "";
                }
                catch (ManagementException)
                {
                    return "";
                }
            }
        }

        static public string SerialNumber
        {
            get
            {
                try
                {
                    foreach (ManagementObject queryObj in baseboardSearcher.Get())
                    {
                        return queryObj["SerialNumber"].ToString();
                    }
                    return "";
                }
                catch (ManagementException)
                {
                    return "";
                }
            }
        }

        static public string Status
        {
            get
            {
                try
                {
                    foreach (ManagementObject querObj in baseboardSearcher.Get())
                    {
                        return querObj["Status"].ToString();
                    }
                    return "";
                }
                catch (ManagementException)
                {
                    return "";
                }
            }
        }

        static public string SystemName
        {
            get
            {
                try
                {
                    foreach (ManagementObject queryObj in motherboardSearcher.Get())
                    {
                        return queryObj["SystemName"].ToString();
                    }
                    return "";
                }
                catch (ManagementException)
                {
                    return "";
                }
            }
        }

        static public string Version
        {
            get
            {
                try
                {
                    foreach (ManagementObject queryObj in baseboardSearcher.Get())
                    {
                        return queryObj["Version"].ToString();
                    }
                    return "";
                }
                catch (ManagementException)
                {
                    return "";
                }
            }
        }

        private static string GetAvailability(int availability)
        {
            switch (availability)
            {
                case 1: return "Other";
                case 2: return "Unknown";
                case 3: return "Running or Full Power";
                case 4: return "Warning";
                case 5: return "In Test";
                case 6: return "Not Applicable";
                case 7: return "Power Off";
                case 8: return "Off Line";
                case 9: return "Off Duty";
                case 10: return "Degraded";
                case 11: return "Not Installed";
                case 12: return "Install Error";
                case 13: return "Power Save - Unknown";
                case 14: return "Power Save - Low Power Mode";
                case 15: return "Power Save - Standby";
                case 16: return "Power Cycle";
                case 17: return "Power Save - Warning";
                default: return "Unknown";
            }
        }

        private static string ConvertToDateTime(string unconvertedTime)
        {
            string convertedTime = "";
            int year = int.Parse(unconvertedTime.Substring(0, 4));
            int month = int.Parse(unconvertedTime.Substring(4, 2));
            int date = int.Parse(unconvertedTime.Substring(6, 2));
            int hours = int.Parse(unconvertedTime.Substring(8, 2));
            int minutes = int.Parse(unconvertedTime.Substring(10, 2));
            int seconds = int.Parse(unconvertedTime.Substring(12, 2));
            string meridian = "AM";
            if (hours > 12)
            {
                hours -= 12;
                meridian = "PM";
            }
            convertedTime = date.ToString() + "/" + month.ToString() + "/" + year.ToString() + " " +
            hours.ToString() + ":" + minutes.ToString() + ":" + seconds.ToString() + " " + meridian;
            return convertedTime;
        }

        public static decimal GetBatteryRate()
        {
            try
            {
                ManagementScope scope = new ManagementScope("root\\WMI");
                ObjectQuery query = new ObjectQuery("SELECT * FROM BatteryStatus");

                using ManagementObjectSearcher searcher = new ManagementObjectSearcher(scope, query);
                var result = searcher.Get().Cast<ManagementObject>().FirstOrDefault();
                if (result == null) return 0;
                decimal chargeRate = Convert.ToDecimal(result["ChargeRate"]);
                decimal dischargeRate = Convert.ToDecimal(result["DischargeRate"]);
                return chargeRate > 0 ? chargeRate : -dischargeRate;
            }
            catch (ManagementException)
            {
                return 0;
            }
        }

        public static decimal ReadFullChargeCapacity()
        {
            try
            {
                ManagementScope scope = new ManagementScope("root\\WMI");
                ObjectQuery query = new ObjectQuery("SELECT * FROM BatteryFullChargedCapacity");

                using ManagementObjectSearcher searcher = new ManagementObjectSearcher(scope, query);
                foreach (ManagementObject obj in searcher.Get().Cast<ManagementObject>())
                {
                    return Convert.ToDecimal(obj["FullChargedCapacity"]);
                }
                return 0;
            }
            catch (ManagementException)
            {
                return 0;
            }
        }

        public static decimal ReadDesignCapacity()
        {
            try
            {
                ManagementScope scope = new ManagementScope("root\\WMI");
                ObjectQuery query = new ObjectQuery("SELECT * FROM BatteryStaticData");

                using ManagementObjectSearcher searcher = new ManagementObjectSearcher(scope, query);
                foreach (ManagementObject obj in searcher.Get().Cast<ManagementObject>())
                {
                    return Convert.ToDecimal(obj["DesignedCapacity"]);
                }
                return 0;
            }
            catch (ManagementException)
            {
                return 0;
            }
        }

        public static int GetBatteryCycle()
        {
            try
            {
                ManagementObjectSearcher searcher =
                    new ManagementObjectSearcher("root\\WMI",
                    "SELECT * FROM BatteryCycleCount");

                foreach (ManagementObject queryObj in searcher.Get())
                {

                    return Convert.ToInt32(queryObj["CycleCount"]);
                }
                return 0;
            }
            catch (ManagementException e)
            {
                return 0;
            }
        }

        public static decimal GetBatteryHealth()
        {
            decimal designCap = ReadDesignCapacity();
            decimal fullCap = ReadFullChargeCapacity();

            return fullCap / designCap;
        }

        public enum CacheLevel : ushort
        {
            Level1 = 3,
            Level2 = 4,
            Level3 = 5,
        }

        public static List<uint> GetCacheSizes(CacheLevel level)
        {
            ManagementClass mc = new ManagementClass("Win32_CacheMemory");
            ManagementObjectCollection moc = mc.GetInstances();
            List<uint> cacheSizes = new List<uint>(moc.Count);

            cacheSizes.AddRange(moc
              .Cast<ManagementObject>()
              .Where(p => Convert.ToUInt16(p.Properties["Level"].Value) == (ushort)level)
              .Select(p => Convert.ToUInt32(p.Properties["MaxCacheSize"].Value)));

            return cacheSizes;
        }

        public static string GetWindowsEdition()
        {
            using (var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
            {
                return key?.GetValue("EditionID")?.ToString();
            }
        }

        public static string GetWindowsVersion()
        {
            using (var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
            {
                return key?.GetValue("CurrentVersion")?.ToString();
            }
        }

        public static DateTime GetWindowsInstallDate()
        {
            using (var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
            {
                string installDateValue = key?.GetValue("InstallDate")?.ToString();
                if (installDateValue != null && long.TryParse(installDateValue, out long installDateTicks))
                {
                    return DateTime.FromFileTime(installDateTicks);
                }
            }

            return DateTime.MinValue;
        }

        public static string GetWindowsFeaturePack()
        {
            using (var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
            {
                return key?.GetValue("ProductName")?.ToString();
            }
        }

        public static string Codename()
        {
            string cpuName = CPUName;
            if (Family.TYPE == Family.ProcessorType.Intel)
            {
                if (cpuName.Contains("6th")) return "Skylake";
                if (cpuName.Contains("7th")) return "Kaby Lake";
                if (cpuName.Contains("8th") && cpuName.Contains("G")) return "Kaby Lake";
                else if (cpuName.Contains("8121U") || cpuName.Contains("8114Y")) return "Cannon Lake";
                else if (cpuName.Contains("8th")) return "Coffee Lake";
                if (cpuName.Contains("9th")) return "Coffee Lake";
                if (cpuName.Contains("10th") && cpuName.Contains("G")) return "Ice Lake";
                else if (cpuName.Contains("10th")) return "Comet Lake";
                if (cpuName.Contains("11th") && cpuName.Contains("G") || cpuName.Contains("11th") && cpuName.Contains("U") || cpuName.Contains("11th") && cpuName.Contains("H") || cpuName.Contains("11th") && cpuName.Contains("KB")) return "Tiger Lake";
                else if (cpuName.Contains("11th")) return "Rocket Lake";
                if (cpuName.Contains("12th")) return "Alder Lake";
                if (cpuName.Contains("13th") || cpuName.Contains("14th") || cpuName.Contains("Core") && cpuName.Contains("100") && !cpuName.Contains("th")) return "Raptor Lake";
                if (cpuName.Contains("Core") && cpuName.Contains("Ultra") && cpuName.Contains("100")) return "Meteor Lake";
            }
            else
            {
                switch (Family.FAM)
                {
                    case RyzenFamily.FireRange:
                        return "Fire Range";
                    default:
                        return "";
                }
            }
            return "";
        }

        public static string getBigLITTLE(int cores, double l2)
        {
            return cores.ToString();
        }

        public static string InstructionSets()
        {
            string list = "";
            if (IsMMXSupported()) list = list + "MMX";
            if (Sse.IsSupported) list = list + ", SSE";
            if (Sse2.IsSupported) list = list + ", SSE2";
            if (Sse3.IsSupported) list = list + ", SSE3";
            if (Ssse3.IsSupported) list = list + ", SSSE3";
            if (Sse41.IsSupported) list = list + ", SSE4.1";
            if (Sse42.IsSupported) list = list + ", SSE4.2";
            if (IsEM64TSupported()) list = list + ", EM64T";
            if (Environment.Is64BitProcess) list = list + ", x86-64";
            if (IsVirtualizationEnabled() && Family.TYPE == Family.ProcessorType.Intel) list = list + ", VT-x";
            else if (IsVirtualizationEnabled()) list = list + ", AMD-V";
            if (Aes.IsSupported) list = list + ", AES";
            if (Avx.IsSupported) list = list + ", AVX";
            if (Avx2.IsSupported) list = list + ", AVX2";
            if (CheckAVX512Support()) list = list + ", AVX512";
            if (Fma.IsSupported) list = list + ", FMA3";

            string result = RemoveCommaSpaceFromStart(list);
            list = result;

            return list;
        }

        private static string RemoveCommaSpaceFromStart(string input)
        {
            string prefixToRemove = ", ";
            if (input.StartsWith(prefixToRemove))
            {
                input = input.Remove(0, prefixToRemove.Length);
            }
            return input;
        }

        private static bool IsVirtualizationEnabled()
        {
            try
            {
                ManagementObjectSearcher searcher = new ManagementObjectSearcher("root\\CIMV2", "SELECT * FROM Win32_Processor");

                foreach (ManagementObject queryObj in searcher.Get())
                {
                    int? virtualizationFirmwareEnabled = queryObj["VirtualizationFirmwareEnabled"] as int?;

                    // Check if virtualization is enabled
                    if (virtualizationFirmwareEnabled == 1)
                    {
                        return true;
                    }
                }
            }
            catch (ManagementException ex)
            {
                DiagnosticLogger.LogError(ex, "Failed to check virtualization status");
            }

            return false;
        }

        public static bool IsEM64TSupported()
        {
            using ManagementObject mo = new ManagementObject("Win32_Processor.DeviceID='CPU0'");
            // Architecture: 0=x86, 1=MIPS, 2=Alpha, 3=PowerPC, 5=IA64, 6=Alpha21364, 9=x64
            const ushort ArchitectureX64 = 9;
            ushort architecture = Convert.ToUInt16(mo["Architecture"]);

            return architecture == ArchitectureX64;
        }

        private static bool CheckAVX512Support()
        {
            try
            {
                if (TYPE != ProcessorType.Intel && FAM != RyzenFamily.FireRange) return false;
                return NativeMethods.IsProcessorFeaturePresent(NativeMethods.PF_AVX512F_INSTRUCTIONS_AVAILABLE);
            }
            catch (DllNotFoundException)
            {
                // cpuid_x64.dll is not present; AVX-512 cannot be verified, assume not supported.
                return false;
            }
        }

        private static bool IsMMXSupported()
        {
            if (Environment.Is64BitProcess)
            {
                // For 64-bit processes, MMX is always supported on Windows.
                return true;
            }
            else
            {
                // For 32-bit processes, check for MMX support on Windows.
                return NativeMethods.IsProcessorFeaturePresent(NativeMethods.PF_MMX_INSTRUCTIONS_AVAILABLE);
            }
        }
    }

    public static class NativeMethods
    {
        // Import the CPUID intrinsic (Intel x86 instruction)
        [System.Runtime.InteropServices.DllImport("cpuid_x64.dll")]
        public static extern void Cpuid(int leafNumber, int subleafNumber, ref int eax, ref int ebx, ref int ecx, ref int edx);

        public const int PF_MMX_INSTRUCTIONS_AVAILABLE = 3;
        public const int PF_AVX512F_INSTRUCTIONS_AVAILABLE = 49;

        // Import the GetSystemInfo function (Windows API) to check MMX support.
        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        public static extern void GetSystemInfo(out SYSTEM_INFO lpSystemInfo);

        // Helper struct for GetSystemInfo function.
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        public struct SYSTEM_INFO
        {
            public ushort wProcessorArchitecture;
            public ushort wReserved;
            public uint dwPageSize;
            public System.IntPtr lpMinimumApplicationAddress;
            public System.IntPtr lpMaximumApplicationAddress;
            public System.IntPtr dwActiveProcessorMask;
            public uint dwNumberOfProcessors;
            public uint dwProcessorType;
            public uint dwAllocationGranularity;
            public ushort wProcessorLevel;
            public ushort wProcessorRevision;
        }

        // Helper method to check MMX support on Windows.
        public static bool IsProcessorFeaturePresent(int processorFeature)
        {
            GetSystemInfo(out SYSTEM_INFO sysInfo);
            return (sysInfo.wProcessorArchitecture == PROCESSOR_ARCHITECTURE_INTEL ||
                    sysInfo.wProcessorArchitecture == PROCESSOR_ARCHITECTURE_AMD64) &&
                   (sysInfo.wProcessorLevel & processorFeature) != 0;
        }

        private const ushort PROCESSOR_ARCHITECTURE_INTEL = 0;
        private const ushort PROCESSOR_ARCHITECTURE_AMD64 = 9;
    }
}
