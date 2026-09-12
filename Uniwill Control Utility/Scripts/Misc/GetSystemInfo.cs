using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Management;
using System.Runtime.Intrinsics.X86;
using static Uniwill_Control_Utility.Scripts.Family;

#pragma warning disable S1751 // Elements should be separated by a comma and a line break — WMI loops return on first result
#pragma warning disable S3217 // Ternary/assignment should be simplified — WMI enumeration pattern

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
                    return Convert.ToString(obj["Name"]) ?? "";
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
                        return Convert.ToString(obj["Name"]) ?? "";
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
                foreach (ManagementObject obj in motherboardSearcher.Get())
                {
                    return GetAvailability(Convert.ToUInt32(obj[nameof(Availability)]));
                }
                return "";
            }
        }

        static public bool HostingBoard
        {
            get
            {
                foreach (ManagementObject obj in baseboardSearcher.Get())
                {
                    return Convert.ToBoolean(obj[nameof(HostingBoard)]);
                }
                return false;
            }
        }

        static public string InstallDate
        {
            get
            {
                foreach (ManagementObject queryObj in baseboardSearcher.Get())
                {
                    string? value = Convert.ToString(queryObj[nameof(InstallDate)]);
                    if (value == null) return "";
                    return ConvertToDateTime(value);
                }
                return "";
            }
        }

        static public string Manufacturer
        {
            get
            {
                foreach (ManagementObject queryObj in baseboardSearcher.Get())
                {
                    return Convert.ToString(queryObj[nameof(Manufacturer)]) ?? "";
                }
                return "";
            }
        }

        static public string Model
        {
            get
            {
                foreach (ManagementObject queryObj in baseboardSearcher.Get())
                {
                    return Convert.ToString(queryObj[nameof(Model)]) ?? "";
                }
                return "";
            }
        }

        static public string PartNumber
        {
            get
            {
                foreach (ManagementObject queryObj in baseboardSearcher.Get())
                {
                    return Convert.ToString(queryObj[nameof(PartNumber)]) ?? "";
                }
                return "";
            }
        }

        static public string PNPDeviceID
        {
            get
            {
                foreach (ManagementObject queryObj in motherboardSearcher.Get())
                {
                    return Convert.ToString(queryObj[nameof(PNPDeviceID)]) ?? "";
                }
                return "";
            }
        }

        static public string PrimaryBusType
        {
            get
            {
                foreach (ManagementObject queryObj in motherboardSearcher.Get())
                {
                    return Convert.ToString(queryObj[nameof(PrimaryBusType)]) ?? "";
                }
                return "";
            }
        }

        static public string Product
        {
            get
            {
                using RegistryKey? biosKey = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
                string? productName = biosKey?.GetValue("SystemProductName")?.ToString();
                if (!string.IsNullOrWhiteSpace(productName))
                    return productName;

                foreach (ManagementObject queryObj in ComputerSsystemInfo.Get())
                {
                    return Convert.ToString(queryObj["Name"]) ?? "";
                }
                return "";
            }
        }

        static public bool Removable
        {
            get
            {
                foreach (ManagementObject obj in baseboardSearcher.Get())
                {
                    return Convert.ToBoolean(obj[nameof(Removable)]);
                }
                return false;
            }
        }

        static public bool Replaceable
        {
            get
            {
                foreach (ManagementObject obj in baseboardSearcher.Get())
                {
                    return Convert.ToBoolean(obj[nameof(Replaceable)]);
                }
                return false;
            }
        }

        static public string RevisionNumber
        {
            get
            {
                foreach (ManagementObject queryObj in motherboardSearcher.Get())
                {
                    return Convert.ToString(queryObj[nameof(RevisionNumber)]) ?? "";
                }
                return "";
            }
        }

        static public string SecondaryBusType
        {
            get
            {
                foreach (ManagementObject queryObj in motherboardSearcher.Get())
                {
                    return Convert.ToString(queryObj[nameof(SecondaryBusType)]) ?? "";
                }
                return "";
            }
        }

        static public string SerialNumber
        {
            get
            {
                foreach (ManagementObject queryObj in baseboardSearcher.Get())
                {
                    return Convert.ToString(queryObj[nameof(SerialNumber)]) ?? "";
                }
                return "";
            }
        }

        static public string Status
        {
            get
            {
                foreach (ManagementObject querObj in baseboardSearcher.Get())
                {
                    return Convert.ToString(querObj[nameof(Status)]) ?? "";
                }
                return "";
            }
        }

        static public string SystemName
        {
            get
            {
                foreach (ManagementObject queryObj in motherboardSearcher.Get())
                {
                    return Convert.ToString(queryObj[nameof(SystemName)]) ?? "";
                }
                return "";
            }
        }

        static public string Version
        {
            get
            {
                foreach (ManagementObject queryObj in baseboardSearcher.Get())
                {
                    return Convert.ToString(queryObj[nameof(Version)]) ?? "";
                }
                return "";
            }
        }

        private static string GetAvailability(uint availability)
        {
            switch (availability)
            {
                case 1u: return "Other";
                case 2u: return "Unknown";
                case 3u: return "Running or Full Power";
                case 4u: return "Warning";
                case 5u: return "In Test";
                case 6u: return "Not Applicable";
                case 7u: return "Power Off";
                case 8u: return "Off Line";
                case 9u: return "Off Duty";
                case 10u: return "Degraded";
                case 11u: return "Not Installed";
                case 12u: return "Install Error";
                case 13u: return "Power Save - Unknown";
                case 14u: return "Power Save - Low Power Mode";
                case 15u: return "Power Save - Standby";
                case 16u: return "Power Cycle";
                case 17u: return "Power Save - Warning";
                default: return "Unknown";
            }
        }

        private static string ConvertToDateTime(string unconvertedTime)
        {
            string convertedTime = "";
            int year = int.Parse(unconvertedTime.AsSpan(0, 4));
            int month = int.Parse(unconvertedTime.AsSpan(4, 2));
            int date = int.Parse(unconvertedTime.AsSpan(6, 2));
            int hours = int.Parse(unconvertedTime.AsSpan(8, 2));
            int minutes = int.Parse(unconvertedTime.AsSpan(10, 2));
            int seconds = int.Parse(unconvertedTime.AsSpan(12, 2));
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
            ManagementScope scope = new ManagementScope("root\\WMI");
            ObjectQuery query = new ObjectQuery("SELECT * FROM BatteryStatus");

            using ManagementObjectSearcher searcher = new ManagementObjectSearcher(scope, query);
            foreach (ManagementObject obj in searcher.Get())
            {
                decimal chargeRate = Convert.ToDecimal(obj["ChargeRate"]);
                decimal dischargeRate = Convert.ToDecimal(obj["DischargeRate"]);
                return chargeRate > 0 ? chargeRate : -dischargeRate;
            }
            return 0;
        }

        public static decimal ReadFullChargeCapacity()
        {
            ManagementScope scope = new ManagementScope("root\\WMI");
            ObjectQuery query = new ObjectQuery("SELECT * FROM BatteryFullChargedCapacity");

            using ManagementObjectSearcher searcher = new ManagementObjectSearcher(scope, query);
            foreach (ManagementObject obj in searcher.Get())
            {
                return Convert.ToDecimal(obj["FullChargedCapacity"]);
            }
            return 0;
        }

        public static decimal ReadDesignCapacity()
        {
            ManagementScope scope = new ManagementScope("root\\WMI");
            ObjectQuery query = new ObjectQuery("SELECT * FROM BatteryStaticData");

            using ManagementObjectSearcher searcher = new ManagementObjectSearcher(scope, query);
            foreach (ManagementObject obj in searcher.Get())
            {
                return Convert.ToDecimal(obj["DesignedCapacity"]);
            }
            return 0;
        }

        public static int GetBatteryCycle()
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

            foreach (ManagementObject obj in moc)
            {
                if (Convert.ToUInt16(obj.Properties["Level"].Value) == (ushort)level)
                {
                    cacheSizes.Add(Convert.ToUInt32(obj.Properties["MaxCacheSize"].Value));
                }
            }

            return cacheSizes;
        }

        public static string GetWindowsEdition()
        {
            using (var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
            {
                return key?.GetValue("EditionID")?.ToString() ?? "";
            }
        }

        public static string GetWindowsVersion()
        {
            using (var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
            {
                return key?.GetValue("CurrentVersion")?.ToString() ?? "";
            }
        }

        public static DateTime GetWindowsInstallDate()
        {
            using (var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
            {
                string? installDateValue = key?.GetValue("InstallDate")?.ToString();
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
                return key?.GetValue("ProductName")?.ToString() ?? "";
            }
        }

        public static string Codename()
        {
            string cpuName = CPUName;
            if (Family.TYPE == Family.ProcessorType.Intel)
            {
                if (cpuName.Contains("6th", StringComparison.Ordinal)) return "Skylake";
                if (cpuName.Contains("7th", StringComparison.Ordinal)) return "Kaby Lake";
                if (cpuName.Contains("8th", StringComparison.Ordinal) && cpuName.Contains('G')) return "Kaby Lake";
                else if (cpuName.Contains("8121U", StringComparison.Ordinal) || cpuName.Contains("8114Y", StringComparison.Ordinal)) return "Cannon Lake";
                else if (cpuName.Contains("8th", StringComparison.Ordinal)) return "Coffee Lake";
                if (cpuName.Contains("9th", StringComparison.Ordinal)) return "Coffee Lake";
                if (cpuName.Contains("10th", StringComparison.Ordinal) && cpuName.Contains('G')) return "Ice Lake";
                else if (cpuName.Contains("10th", StringComparison.Ordinal)) return "Comet Lake";
                if ((cpuName.Contains("11th", StringComparison.Ordinal) && cpuName.Contains('G')) || (cpuName.Contains("11th", StringComparison.Ordinal) && cpuName.Contains('U')) || (cpuName.Contains("11th", StringComparison.Ordinal) && cpuName.Contains('H')) || (cpuName.Contains("11th", StringComparison.Ordinal) && cpuName.Contains("KB", StringComparison.Ordinal))) return "Tiger Lake";
                else if (cpuName.Contains("11th", StringComparison.Ordinal)) return "Rocket Lake";
                if (cpuName.Contains("12th", StringComparison.Ordinal)) return "Alder Lake";
                if (cpuName.Contains("13th", StringComparison.Ordinal) || cpuName.Contains("14th", StringComparison.Ordinal) || (cpuName.Contains("Core", StringComparison.Ordinal) && cpuName.Contains("100", StringComparison.Ordinal) && !cpuName.Contains("th", StringComparison.Ordinal))) return "Raptor Lake";
                if (cpuName.Contains("Core", StringComparison.Ordinal) && cpuName.Contains("Ultra", StringComparison.Ordinal) && cpuName.Contains("100", StringComparison.Ordinal)) return "Meteor Lake";
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

        private static string RemoveCommaSpaceFromStart(ReadOnlySpan<char> input)
        {
            const string prefixToRemove = ", ";
            if (input.StartsWith(prefixToRemove.AsSpan()))
            {
                return input.Slice(prefixToRemove.Length).ToString();
            }
            return input.ToString();
        }

        private static bool IsVirtualizationEnabled()
        {
            try
            {
                ManagementObjectSearcher searcher = new ManagementObjectSearcher("root\\CIMV2", "SELECT * FROM Win32_Processor");

                foreach (ManagementObject queryObj in searcher.Get())
                {
                    // Check if virtualization is enabled
                    if (queryObj["VirtualizationFirmwareEnabled"] is int virtualizationFirmwareEnabled && virtualizationFirmwareEnabled == 1)
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
#pragma warning restore S1751
#pragma warning restore S3217

 #pragma warning disable S3217 // "AS" should not be used when the type is already known
    // Portability issue with unsafe code — intentional for CPUID/GetSystemInfo P/Invoke
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
#pragma warning restore S3217
}
