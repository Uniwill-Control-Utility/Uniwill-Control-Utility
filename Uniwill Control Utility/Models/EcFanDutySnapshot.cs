using System;

namespace Uniwill_Control_Utility.Models
{
    /// <summary>
    /// The app's fan-duty view: raw values on the EC 0-200 scale from
    /// <see cref="UCU.Contracts.Capabilities.IEcTelemetryCapability"/>, converted for
    /// display by the percent properties. Shared by every consumer that shows fan
    /// state (dashboard, fan page, tray, overlay).
    ///
    /// This is a UI model, deliberately not the wire DTO: the DTO carries raw fields
    /// over the pipe, this type owns the display conversion the pages use.
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
