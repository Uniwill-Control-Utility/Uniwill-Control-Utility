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
        public static string BaseboardProduct = "";

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

        /// <summary>
        /// Raised when <see cref="RamSpeed"/> transitions from "no reading" (0) to
        /// a real value after the pages have already read it. The startup source is
        /// WMI Win32_PhysicalMemory.ConfiguredClockSpeed, which is late under a
        /// busy WMI (resident AV: the startup prefetch's query can run for many
        /// seconds) and 0 on platforms whose SMBIOS provider omits it - the field
        /// machine shows the dashboard's RAM speed card as "Not available" at
        /// launch from exactly these. The dashboard's engine-metrics merge fills
        /// from the SPD reading (the engine's kernel-side probe) and publishes
        /// through here, so the card repaints without a page reload.
        /// </summary>
        public static event System.Action? RamSpeedChanged;

        /// <summary>
        /// Publish an SPD-sourced speed when the WMI prefetch never delivered one.
        /// A WMI value that did land wins: WMI reports the configured speed the
        /// platform itself declares, SPD the module's supported rate, and the card
        /// has always shown the WMI number. Fires <see cref="RamSpeedChanged"/> on
        /// the transition, from whichever thread read the engine.
        /// </summary>
        public static void PublishRamSpeedIfMissing(int megatransfersPerSecond)
        {
            if (megatransfersPerSecond <= 0)
                return;

            if (System.Threading.Interlocked.CompareExchange(ref RamSpeed, megatransfersPerSecond, 0) != 0)
                return;

            RamSpeedChanged?.Invoke();
        }
    }
}
