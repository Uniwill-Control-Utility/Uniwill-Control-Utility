using System;
using System.Collections.Generic;
using System.Linq;
using LibreHardwareMonitor.Hardware;

namespace UCU.Hardware.Metrics
{
    /// <summary>
    /// The kernel-side sensor probe the LocalSystem engine runs: the CPU's Ring0
    /// sensors (package power, core clocks - MSR access through the sensor driver,
    /// whose device ACL is System + Administrators, so the asInvoker app installed
    /// per-machine gets access denied and the dashboard shows "--" forever) and the
    /// DIMM SPD data (SMBus). The engine reads it on its own thread and serves the
    /// cached <see cref="SensorProbeReading"/> over the
    /// telemetry.get_metrics_snapshot verb; nothing here runs on the EC queues.
    ///
    /// Sensor naming is pinned by an empirical probe of the target machine
    /// (2026-10-29, elevated, same library version): AMD exposes power as
    /// "Package" (Intel: "CPU Package"), the real core clocks as "Core #N" plus the
    /// aggregate "Cores (Average)", and the "(Effective)" clocks are utilisation-
    /// weighted pseudo-clocks, NOT clocks. SPD has no "Speed" sensor in this
    /// library: the data rate lives in the timing "tCKAVGmin" (minimum cycle time,
    /// ns) - DDR moves data on both edges, so MT/s = 2 x (1e9 / tCK) / 1e6 =
    /// 2000 / tCK (field: 0.357 ns -> 5602, WMI ConfiguredClockSpeed 5600, same
    /// speed).
    ///
    /// The probe is CPU + Memory only. The GPU stays app-side (NVAPI decision) and
    /// the board/storage trees stay closed: the fewer drivers this opens on a
    /// guarded machine, the smaller the surface that can park.
    /// </summary>
    public sealed class LhmSensorProbe : ISensorProbe
    {
        private readonly object _gate = new();
        private Computer? _computer;
        private bool _openFailed;

        /// <summary>
        /// One probe cycle: opens the Computer on first use, updates the tree, and
        /// extracts the readings.
        /// </summary>
        public SensorProbeReading Read()
        {
            lock (_gate)
            {
                if (_computer is null)
                {
                    // A failed open is sticky: the service decides when to retry
                    // (Reset on a backoff boundary), the loop must not re-open a
                    // wedged stack every cycle.
                    if (_openFailed)
                        return new SensorProbeReading();

                    var computer = new Computer
                    {
                        IsCpuEnabled = true,
                        IsMemoryEnabled = true,
                        IsGpuEnabled = false,
                        IsMotherboardEnabled = false,
                        IsStorageEnabled = false,
                        IsControllerEnabled = false,
                        IsBatteryEnabled = false,
                    };

                    try
                    {
                        computer.Open();
                        _computer = computer;
                    }
                    catch (Exception)
                    {
                        _openFailed = true;
                        computer.Close();
                        return new SensorProbeReading();
                    }
                }

                return Extract(Collect(_computer));
            }
        }

        /// <summary>
        /// Drop the current tree (and the sticky open failure) so the next
        /// <see cref="Read"/> rebuilds it. The service calls this on a backoff
        /// boundary, never on the verb path.
        /// </summary>
        public void Reset()
        {
            lock (_gate)
            {
                _computer?.Close();
                _computer = null;
                _openFailed = false;
            }
        }

        public void Dispose() => Reset();

        private static List<SensorDatum> Collect(Computer computer)
        {
            var data = new List<SensorDatum>();

            foreach (IHardware hardware in Enumerate(computer.Hardware))
            {
                hardware.Update();

                foreach (ISensor sensor in hardware.Sensors)
                {
                    if (sensor.Value.HasValue)
                        data.Add(new SensorDatum(hardware.HardwareType, sensor.SensorType, sensor.Name, sensor.Value.Value));
                }
            }

            return data;
        }

        private static IEnumerable<IHardware> Enumerate(IEnumerable<IHardware> tree)
        {
            foreach (IHardware hardware in tree)
            {
                yield return hardware;

                foreach (IHardware sub in Enumerate(hardware.SubHardware))
                    yield return sub;
            }
        }

        /// <summary>
        /// The naming/units logic, pure so the field-pinned rules above are unit-
        /// tested against synthetic trees (the live tree needs the drivers and
        /// cannot run in CI).
        /// </summary>
        public static SensorProbeReading Extract(IReadOnlyList<SensorDatum> data)
        {
            // Package power: AMD names it "Package", Intel "CPU Package". Both are
            // the whole-socket number the dashboard card wants.
            float? power = FirstValue(data, HardwareType.Cpu, SensorType.Power, "CPU Package", "Package");

            // Core clock: the aggregate when the library provides it, else the mean
            // of the per-core clocks. "(Effective)" sensors are excluded: they are
            // utilisation-weighted, the field tree shows them near half the real
            // clock (2161 vs 5150), so averaging them in would halve the reading.
            int clock = (int)Math.Round(
                AverageCoreClock(data) ?? 0,
                MidpointRounding.AwayFromZero);            // SPD data rate: the fastest supported cycle time across all modules.
            float? tckNs = data
                .Where(d => d.Hardware == HardwareType.Memory
                    && d.Sensor == SensorType.Timing
                    && d.Name.StartsWith("tCKAVGmin", StringComparison.OrdinalIgnoreCase))
                .Select(d => (float?)d.Value)
                .Where(v => v > 0)
                .Min();

            // tCK in ns -> MT/s: 1e9/tck = Hz, x2 for DDR's two edges, /1e6 for
            // mega. Field: 0.357 ns -> 2000/0.357 = 5602 (WMI of the same machine: 5600).
            int speedMts = tckNs is > 0
                ? (int)Math.Round(2000d / tckNs.Value, MidpointRounding.AwayFromZero)
                : 0;

            // DIMM temperatures: the "DIMM #N" family assigned to the card's two
            // slots by discovery order. The exact "#0"/"#1" pair assumed 2-slot
            // numbering; the field Intel board (2026-11-01, Ultra 9 275HX)
            // populates "DIMM #0" + "DIMM #2" (4-slot channel layout), so the
            // second DIMM's temperature was silently dropped. The app's own
            // reader already pairs by order (its comment cites this machine);
            // the engine overlay fills the app's zeros, so it must pair the
            // same way - same rule, two readers, one contract.
            List<float> dimmTemps = data
                .Where(d => d.Hardware == HardwareType.Memory
                    && d.Sensor == SensorType.Temperature
                    && d.Name.StartsWith("DIMM #", StringComparison.OrdinalIgnoreCase)
                    && d.Value > 0)
                .Select(d => d.Value)
                .Take(2)
                .ToList();

            return new SensorProbeReading
            {
                CpuPowerWatts = (int)Math.Round(power ?? 0, MidpointRounding.AwayFromZero),
                CpuClockMhz = clock,
                MemorySpeedMts = speedMts,
                MemoryTemperature0 = (int)Math.Round(
                    dimmTemps.Count > 0 ? dimmTemps[0] : 0, MidpointRounding.AwayFromZero),
                MemoryTemperature1 = (int)Math.Round(
                    dimmTemps.Count > 1 ? dimmTemps[1] : 0, MidpointRounding.AwayFromZero),
                ProbeOpen = true,
            };
        }

        private static float? FirstValue(
            IReadOnlyList<SensorDatum> data, HardwareType hardware, SensorType sensor, params string[] namesInPriorityOrder)
        {
            foreach (string name in namesInPriorityOrder)
            {
                float? value = data
                    .Where(d => d.Hardware == hardware && d.Sensor == sensor
                        && string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase))
                    .Select(d => (float?)d.Value)
                    .FirstOrDefault(v => v > 0);

                if (value is > 0)
                    return value;
            }

            return null;
        }

        private static float? AverageCoreClock(IReadOnlyList<SensorDatum> data)
        {
            var aggregate = data
                .Where(d => d.Hardware == HardwareType.Cpu && d.Sensor == SensorType.Clock
                    && string.Equals(d.Name, "Cores (Average)", StringComparison.OrdinalIgnoreCase))
                .Select(d => (float?)d.Value)
                .FirstOrDefault(v => v > 0);

            if (aggregate is > 0)
                return aggregate;

            var cores = data
                .Where(d => d.Hardware == HardwareType.Cpu && d.Sensor == SensorType.Clock
                    && IsCoreClockName(d.Name)
                    && !d.Name.Contains("(Effective)", StringComparison.OrdinalIgnoreCase)
                    && d.Value > 0)
                .Select(d => d.Value)
                .ToList();

            return cores.Count > 0 ? cores.Average() : null;
        }

        /// <summary>
        /// Per-core clock names. AMD: "Core #N" (field 2026-10-29, Ryzen 9
        /// 9955HX3D). Intel hybrid: "P-Core #N" and "E-Core #N" - the field
        /// Intel tree (2026-11-01, Core Ultra 9 275HX) exposes NO "Core #N"
        /// and no "Cores (Average)" aggregate, so the AMD-only matcher found
        /// no clock sensor at all and the card/OSD rendered the blank ("--")
        /// while power answered fine on "CPU Package". The names are prefixes
        /// of exactly the per-core family: "Bus Speed" (99.10) stays out, and
        /// the iGPU's "GPU Core"/"GPU Memory" clocks hang off a Gpu-typed
        /// sub-hardware node, excluded by the caller's Cpu filter.
        /// </summary>
        private static bool IsCoreClockName(string name) =>
            name.StartsWith("Core #", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("P-Core #", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("E-Core #", StringComparison.OrdinalIgnoreCase);
    }
}
