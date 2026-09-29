using System;

namespace Uniwill_Control_Utility.Services
{
    public sealed record TrayQuickTelemetrySnapshot(
        int CpuTemperature,
        int CpuUsage,
        int CpuPowerWatts,
        int CpuClockMhz,
        int CpuFanPercent,
        int GpuTemperature,
        double GpuUsage,
        int GpuPowerWatts,
        int GpuClockMhz,
        int GpuFanPercent);

    internal static class TrayQuickTelemetryComposer
    {
        public static TrayQuickTelemetrySnapshot Compose(
            HardwareMetricsSnapshot hardware,
            EcFanDutySnapshot ec)
        {
            ArgumentNullException.ThrowIfNull(hardware);

            return new TrayQuickTelemetrySnapshot(
                CpuTemperature: Math.Max(0, hardware.CpuTemperature),
                CpuUsage: Math.Clamp(hardware.CpuUsage, 0, 100),
                CpuPowerWatts: Math.Max(0, hardware.CpuPowerWatts),
                CpuClockMhz: Math.Max(0, hardware.CpuClockMhz),
                CpuFanPercent: ec.CpuFanPercent,
                GpuTemperature: Math.Max(0, hardware.GpuTemperature),
                GpuUsage: Math.Clamp(hardware.GpuUsage, 0, 100),
                GpuPowerWatts: Math.Max(0, hardware.GpuPowerWatts),
                GpuClockMhz: Math.Max(0, hardware.GpuClockMhz),
                GpuFanPercent: ec.GpuFanPercent);
        }
    }
}
