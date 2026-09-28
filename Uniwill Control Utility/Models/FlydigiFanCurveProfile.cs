using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;

namespace Uniwill_Control_Utility.Models
{
    /// <summary>
    /// A single point on a fan curve mapping temperature to RPM.
    /// </summary>
    public struct FanCurvePoint
    {
        /// <summary>Temperature in degrees Celsius.</summary>
        public int Temperature { get; init; }

        /// <summary>Target fan speed in RPM.</summary>
        public ushort Rpm { get; init; }
    }

    /// <summary>
    /// Fan curve profile with built-in presets and linear interpolation.
    /// Maps temperature readings to target RPM values for Flydigi cooler devices.
    /// </summary>
    public class FlydigiFanCurveProfile
    {
        /// <summary>Human-readable name of the profile.</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>Unique identifier for the profile.</summary>
        public string Id { get; set; } = string.Empty;

        /// <summary>Ordered list of temperature-to-RPM mapping points.</summary>
        public List<FanCurvePoint> Points { get; set; } = new List<FanCurvePoint>();

        /// <summary>Minimum supported RPM (1300).</summary>
        public const ushort MinRpm = 1300;

        /// <summary>Maximum supported RPM (4000).</summary>
        public const ushort MaxRpm = 4000;

        /// <summary>
        /// Computes the target RPM for the given temperature using linear interpolation
        /// between the nearest curve points. Result is clamped to the 1300–4000 RPM range.
        /// </summary>
        /// <param name="temp">Temperature in degrees Celsius.</param>
        /// <returns>Target RPM clamped to the valid range.</returns>
        public ushort GetRpmForTemperature(double temp)
        {
            if (Points.Count == 0)
                return MinRpm;

            // Sort points by temperature to ensure correct interpolation
            var sorted = Points.OrderBy(p => p.Temperature).ToList();

            // Below first point — return first point's RPM
            if (temp <= sorted[0].Temperature)
                return ClampRpm(sorted[0].Rpm);

            // Above last point — return last point's RPM
            if (temp >= sorted[sorted.Count - 1].Temperature)
                return ClampRpm(sorted[sorted.Count - 1].Rpm);

            // Find bracketing points
            FanCurvePoint lower = sorted[0];
            FanCurvePoint upper = sorted[sorted.Count - 1];

            for (int i = 0; i < sorted.Count - 1; i++)
            {
                if (temp >= sorted[i].Temperature && temp < sorted[i + 1].Temperature)
                {
                    lower = sorted[i];
                    upper = sorted[i + 1];
                    break;
                }
            }

            // Linear interpolation
            double range = upper.Temperature - lower.Temperature;
            if (range == 0)
                return ClampRpm(lower.Rpm);

            double fraction = (temp - lower.Temperature) / range;
            double rpm = lower.Rpm + fraction * (upper.Rpm - lower.Rpm);

            return ClampRpm((ushort)Math.Round(rpm));
        }

        /// <summary>
        /// Creates a silent profile: starts rising at 65°C, max 3000 RPM.
        /// Points: 0°C→1300, 65°C→1300, 90°C→3000, 100°C→3000.
        /// </summary>
        public static FlydigiFanCurveProfile CreateSilent()
        {
            return new FlydigiFanCurveProfile
            {
                Name = "Silent",
                Id = Guid.NewGuid().ToString(),
                Points = new List<FanCurvePoint>
                {
                    new FanCurvePoint { Temperature = 0, Rpm = 1300 },
                    new FanCurvePoint { Temperature = 65, Rpm = 1300 },
                    new FanCurvePoint { Temperature = 90, Rpm = 3000 },
                    new FanCurvePoint { Temperature = 100, Rpm = 3000 },
                }
            };
        }

        /// <summary>
        /// Creates a balanced profile: starts rising at 50°C, max 3500 RPM.
        /// Points: 0°C→1300, 50°C→1300, 85°C→3500, 100°C→3500.
        /// </summary>
        public static FlydigiFanCurveProfile CreateBalanced()
        {
            return new FlydigiFanCurveProfile
            {
                Name = "Balanced",
                Id = Guid.NewGuid().ToString(),
                Points = new List<FanCurvePoint>
                {
                    new FanCurvePoint { Temperature = 0, Rpm = 1300 },
                    new FanCurvePoint { Temperature = 50, Rpm = 1300 },
                    new FanCurvePoint { Temperature = 85, Rpm = 3500 },
                    new FanCurvePoint { Temperature = 100, Rpm = 3500 },
                }
            };
        }

        /// <summary>
        /// Creates a performance profile: starts rising at 40°C, max 4000 RPM.
        /// Points: 0°C→1300, 40°C→1300, 80°C→4000, 100°C→4000.
        /// </summary>
        public static FlydigiFanCurveProfile CreatePerformance()
        {
            return new FlydigiFanCurveProfile
            {
                Name = "Performance",
                Id = Guid.NewGuid().ToString(),
                Points = new List<FanCurvePoint>
                {
                    new FanCurvePoint { Temperature = 0, Rpm = 1300 },
                    new FanCurvePoint { Temperature = 40, Rpm = 1300 },
                    new FanCurvePoint { Temperature = 80, Rpm = 4000 },
                    new FanCurvePoint { Temperature = 100, Rpm = 4000 },
                }
            };
        }

        /// <summary>
        /// Creates a quiet profile: starts rising at 55°C, max 3000 RPM.
        /// Fills the gap between Silent (65°C start) and Balanced (50°C start).
        /// Points: 0°C→1300, 55°C→1300, 80°C→3000, 100°C→3000.
        /// </summary>
        public static FlydigiFanCurveProfile CreateQuiet()
        {
            return new FlydigiFanCurveProfile
            {
                Name = "Quiet",
                Id = Guid.NewGuid().ToString(),
                Points = new List<FanCurvePoint>
                {
                    new FanCurvePoint { Temperature = 0, Rpm = 1300 },
                    new FanCurvePoint { Temperature = 55, Rpm = 1300 },
                    new FanCurvePoint { Temperature = 80, Rpm = 3000 },
                    new FanCurvePoint { Temperature = 100, Rpm = 3000 },
                }
            };
        }

        /// <summary>
        /// Creates a freezy profile: 1300 RPM baseline at all temps,
        /// aggressive ramp to 4000 RPM by 70°C. Maximum cooling.
        /// Points: 0°C→1300, 30°C→1300, 70°C→4000, 100°C→4000.
        /// </summary>
        public static FlydigiFanCurveProfile CreateFreezy()
        {
            return new FlydigiFanCurveProfile
            {
                Name = "Freezy",
                Id = Guid.NewGuid().ToString(),
                Points = new List<FanCurvePoint>
                {
                    new FanCurvePoint { Temperature = 0, Rpm = 1300 },
                    new FanCurvePoint { Temperature = 30, Rpm = 1300 },
                    new FanCurvePoint { Temperature = 70, Rpm = 4000 },
                    new FanCurvePoint { Temperature = 100, Rpm = 4000 },
                }
            };
        }

        /// <summary>
        /// Inverse lookup: returns the temperature that corresponds to the given RPM
        /// on this curve. Used by the learning engine to compute the implied target
        /// temperature for a commanded RPM. Returns the midpoint temperature of the
        /// profile if no match is found.
        /// </summary>
        public double GetTemperatureForRpm(ushort rpm)
        {
            if (Points.Count == 0)
                return 50.0;

            var sorted = Points.OrderBy(p => p.Temperature).ToList();

            // Exact match on a point
            foreach (var p in sorted)
            {
                if (p.Rpm == rpm)
                    return p.Temperature;
            }

            // Find bracketing RPM points and interpolate temperature
            for (int i = 0; i < sorted.Count - 1; i++)
            {
                int lo = Math.Min(sorted[i].Rpm, sorted[i + 1].Rpm);
                int hi = Math.Max(sorted[i].Rpm, sorted[i + 1].Rpm);
                if (rpm >= lo && rpm <= hi)
                {
                    var a = sorted[i];
                    var b = sorted[i + 1];
                    if (a.Rpm == b.Rpm)
                        return a.Temperature;
                    double frac = (rpm - a.Rpm) * 1.0 / (b.Rpm - a.Rpm);
                    return a.Temperature + frac * (b.Temperature - a.Temperature);
                }
            }

            // Out of range — clamp to nearest endpoint
            if (rpm <= sorted[0].Rpm)
                return sorted[0].Temperature;
            return sorted[sorted.Count - 1].Temperature;
        }

        /// <summary>
        /// Serializes the profile to a JSON string.
        /// </summary>
        public string ToJSON()
        {
            return JsonConvert.SerializeObject(this, Formatting.Indented);
        }

        /// <summary>
        /// Deserializes a profile from a JSON string.
        /// </summary>
        public static FlydigiFanCurveProfile FromJSON(string json)
        {
            return JsonConvert.DeserializeObject<FlydigiFanCurveProfile>(json)
                ?? new FlydigiFanCurveProfile();
        }

        /// <summary>
        /// Creates a deep copy of this profile with a new GUID.
        /// </summary>
        public FlydigiFanCurveProfile Clone()
        {
            return new FlydigiFanCurveProfile
            {
                Name = this.Name,
                Id = Guid.NewGuid().ToString(),
                Points = new List<FanCurvePoint>(this.Points)
            };
        }

        private static ushort ClampRpm(ushort rpm)
        {
            if (rpm < MinRpm)
                return MinRpm;
            if (rpm > MaxRpm)
                return MaxRpm;
            return rpm;
        }
    }
}
