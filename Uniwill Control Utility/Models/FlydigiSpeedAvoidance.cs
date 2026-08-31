using System;

namespace Uniwill_Control_Utility.Models
{
    public static class FlydigiSpeedAvoidance
    {
        /// <summary>
        /// Applies speed avoidance to a target RPM.
        /// If the target falls within [start, end], it jumps to end+100.
        /// Emergency bypass: if temperature >= 85°C, returns target unchanged.
        /// </summary>
        public static ushort Apply(ushort targetRpm, bool enabled, ushort startRpm, ushort endRpm, double? currentTemp)
        {
            if (!enabled)
                return targetRpm;

            if (currentTemp >= 85.0)
                return targetRpm;

            if (targetRpm >= startRpm && targetRpm <= endRpm)
                return (ushort)Math.Min(4000, endRpm + 100);

            return targetRpm;
        }
    }
}
