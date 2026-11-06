using System;

namespace UCU.Hardware.Ec
{
    /// <summary>
    /// The EC fan-duty registers read in one lock, shared by every consumer that
    /// shows fan state (dashboard, fan page, tray, overlay). Raw values are on the
    /// EC 0-200 scale; <see cref="CpuFanPercent"/> converts them for display.
    /// </summary>
    public readonly record struct EcFanDutySnapshot(
        int CpuFanDuty,
        int GpuFanDuty)
    {
        public static EcFanDutySnapshot Empty { get; } = new();

        public int CpuFanPercent => DutyToPercent(CpuFanDuty);
        public int GpuFanPercent => DutyToPercent(GpuFanDuty);

        private static int DutyToPercent(int duty) =>
            (int)Math.Round(Math.Clamp(duty, 0, 200) * 100.0 / 200.0);
    }
}
