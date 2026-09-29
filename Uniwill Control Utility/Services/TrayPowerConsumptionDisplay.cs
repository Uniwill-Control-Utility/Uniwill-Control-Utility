namespace Uniwill_Control_Utility.Services
{
    internal sealed record TrayPowerConsumptionDisplay(string CpuTdp, string GpuTgp)
    {
        public static TrayPowerConsumptionDisplay Create(int cpuPowerWatts, int gpuPowerWatts) => new(
            Format(cpuPowerWatts),
            Format(gpuPowerWatts));

        private static string Format(int watts) => watts > 0 ? $"{watts} W" : "-- W";
    }
}
