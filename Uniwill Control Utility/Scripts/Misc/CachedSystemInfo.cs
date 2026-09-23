namespace Uniwill_Control_Utility.Scripts.Misc
{
    /// <summary>
    /// Holds pre-fetched system information populated at app startup.
    /// Populated by App.xaml.cs DetectHardwareAsync() on a background thread,
    /// so the Settings page can read instantly without running WMI queries.
    /// </summary>
    public static class CachedSystemInfo
    {
        // Device info
        public static string DeviceName = "";
        public static string DeviceModel = "";
        public static string DeviceProducer = "";

        // CPU info
        public static string CpuName = "";
        public static string CpuDescription = "";
        public static string CpuManufacturer = "";
        public static string CpuCodeName = "";
        public static string CpuBaseClock = "";
        public static string CpuInstructions = "";
        public static int CpuCores = 0;
        public static int CpuLogicalProcessors = 0;
        public static double CpuL2Size = 0;
        public static double CpuL3Size = 0;
        public static decimal CpuL1Cache = 0;
        public static decimal CpuL2Cache = 0;

        // RAM info
        public static double RamCapacity = 0;
        public static int RamSpeed = 0;
        public static int RamType = 0;
        public static int RamWidth = 0;
        public static int RamSlots = 0;
        public static string RamProducer = "";
        public static string RamModel = "";
    }
}
