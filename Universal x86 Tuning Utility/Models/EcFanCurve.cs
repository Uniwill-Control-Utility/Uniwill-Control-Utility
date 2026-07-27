using System;
using System.Collections.Generic;
using System.Linq;

namespace Universal_x86_Tuning_Utility.Models
{
    /// <summary>
    /// Fan curve profile for the Uniwill EC (RAMFAN2 format).
    /// Per XMG Control Center UserFanTables JSON:
    ///   - The EC has 16 slots (IDs 0-15), but only 0-10 are active (11 points).
    ///   - ID 0: always Duty=0 (fan off below threshold).
    ///   - IDs 1-10: user-editable, spanning ~54-97 °C.
    ///   - IDs 11-15: padded with 255 (inactive/reserved).
    ///   - Each point has UpT (ramp up), DownT (ramp down for hysteresis), and Duty (0-255, 255 = sentinel).
    /// </summary>
    public class EcFanCurve
    {
        /// <summary>
        /// Default temperature labels for the 11 EC zones (IDs 0-10) — CPU fan curve.
        /// Index 0 = below threshold (fan off, always 0% duty).
        /// Indices 1-9 = XMG Control Center 9-point scale (55-95 in 5°C steps).
        /// Index 10 = high-temp overflow (97°C).
        /// </summary>
        public static readonly int[] CpuTemperatures = { 0, 55, 60, 65, 70, 75, 80, 85, 90, 95, 97 };

        /// <summary>
        /// Default temperature labels for the 11 EC zones (IDs 0-10) — GPU fan curve.
        /// Same 9-point scale as CPU (XMG Control Center default).
        /// </summary>
        public static readonly int[] GpuTemperatures = { 0, 55, 60, 65, 70, 75, 80, 85, 90, 95, 97 };

        /// <summary>
        /// The "Reset to Default" temperature thresholds — XMG Control Center 9-point scale.
        /// Zone 0 is OFF, zone 10 is overflow.
        /// </summary>
        public static readonly int[] DefaultTemperatures = { 0, 55, 60, 65, 70, 75, 80, 85, 90, 95, 97 };

        /// <summary>Duty percentage (0-100) for each zone index 0-10. Index 0 is always 0.</summary>
        public List<int> Duties { get; set; } = new();

        /// <summary>Human-readable name.</summary>
        public string Name { get; set; } = string.Empty;

        public EcFanCurve()
        {
            Duties = new List<int>(new int[CpuTemperatures.Length]);
        }

        /// <summary>Set duty (0-100%) for a given zone index (0-10).</summary>
        public void SetDuty(int zoneIndex, int percent)
        {
            Duties[zoneIndex] = Math.Clamp(percent, 0, 100);
        }

        /// <summary>Get duty (0-100%) for a given zone index.</summary>
        public int GetDuty(int zoneIndex) => Duties[zoneIndex];

        /// <summary>Enforce monotonically non-decreasing duty values.</summary>
        public void EnforceMonotonicity(int changedIndex, int newValue)
        {
            Duties[changedIndex] = newValue;
            for (int i = changedIndex + 1; i < Duties.Count; i++)
                if (Duties[i] < newValue) Duties[i] = newValue;
            for (int i = changedIndex - 1; i >= 0; i--)
                if (Duties[i] > Duties[i + 1]) Duties[i] = Duties[i + 1];
        }

        /// <summary>
        /// Converts this curve to the 16-byte RAMFAN2 duty table (0-200 scale).
        /// Zones 0-10 = user curve, zones 11-15 = max duty padding.
        /// Zone 0 is always 0 (fan off below threshold).
        /// </summary>
        public byte[] ToEcDutyTable()
        {
            var table = new byte[16];

            for (int i = 0; i < Duties.Count && i < 16; i++)
            {
                // Zone 0 is always 0 (fan off).
                // EC uses 0-200 scale; UI uses 0-100%.
                table[i] = (byte)(i == 0 ? 0 : Duties[i] * 2);
            }

            // Zones 11-15: pad with max duty.
            byte maxDuty = table.Max();
            for (int i = 11; i < 16; i++)
                table[i] = maxDuty;

            return table;
        }

        #region Presets
        /// Each preset has 11 duty values for zones 0-10.
        /// Zone 0 is always 0 (fan off below ~52°C).
        /// Zones 1-10 map to temps 54-97°C.

        public static EcFanCurve CreateSilent() => new()
        {
            Name = "Silent",
            Duties = new List<int> { 0, 20, 25, 30, 38, 43, 48, 53, 58, 58, 58 }
        };

        public static EcFanCurve CreateBalanced() => new()
        {
            Name = "Balanced",
            Duties = new List<int> { 0, 30, 32, 36, 40, 47, 55, 62, 72, 84, 100 }
        };

        public static EcFanCurve CreatePerformance() => new()
        {
            Name = "Performance",
            Duties = new List<int> { 0, 40, 50, 60, 70, 80, 88, 95, 100, 100, 100 }
        };

        public static EcFanCurve CreateFullSpeed() => new()
        {
            Name = "Full Speed",
            Duties = new List<int> { 0, 100, 100, 100, 100, 100, 100, 100, 100, 100, 100 }
        };

        public static EcFanCurve CreateOff() => new()
        {
            Name = "Off",
            Duties = new List<int> { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }
        };

        #endregion
    }
}
