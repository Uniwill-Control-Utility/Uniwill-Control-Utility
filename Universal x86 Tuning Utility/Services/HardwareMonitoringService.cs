using LibreHardwareMonitor.Hardware;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace Universal_x86_Tuning_Utility.Services
{
    [Flags]
    public enum HardwareMonitoringCategory
    {
        None = 0,
        Cpu = 1,
        Memory = 2,
        Gpu = 4,
        Battery = 8
    }

    /// <summary>
    /// Lightweight snapshot of connected device state (Hydro UI, Flydigi) for the dashboard.
    /// </summary>
    public sealed record DeviceMetricsSnapshot
    {
        /// <summary>Whether a Hydro UI (LCT) watercooler is connected.</summary>
        public bool HydroUiConnected { get; init; }
        /// <summary>Current pump voltage (7, 8, 11, or 0 for off).</summary>
        public int HydroUiPumpVoltage { get; init; }
        /// <summary>Current fan speed percentage (0-100, or 0 for off).</summary>
        public int HydroUiFanSpeed { get; init; }
        /// <summary>Current fan RPM from device status (0 if unknown).</summary>
        public int HydroUiFanRpm { get; init; }

        /// <summary>Whether a Flydigi cooler is connected.</summary>
        public bool FlydigiConnected { get; init; }
        /// <summary>Current fan RPM from device (0 if unknown).</summary>
        public int FlydigiFanRpm { get; init; }
        /// <summary>Current RGB mode name (e.g. "Off", "Static", "SmartTemp").</summary>
        public string FlydigiRgbMode { get; init; } = string.Empty;
    }

    public sealed record HardwareMetricsSnapshot
    {
        public int CpuTemperature { get; init; }
        public int CpuUsage { get; init; }
        public int CpuPowerWatts { get; init; }
        public int CpuClockMhz { get; init; }
        public int CpuFanSpeed { get; init; }
        public int GpuTemperature { get; init; }
        public double GpuUsage { get; init; }
        public int GpuPowerWatts { get; init; }
        public int GpuClockMhz { get; init; }
        public int GpuFanSpeed { get; init; }
        public double GpuMemoryUsedGb { get; init; }
        public double GpuMemoryTotalGb { get; init; }
        public double SystemMemoryUsedGb { get; init; }
        public double SystemMemoryTotalGb { get; init; }
        public double SystemMemoryCommittedGb { get; init; }
        public double SystemMemoryPagefileTotalGb { get; init; }
        public bool HasBattery { get; init; }
        public int BatteryPercent { get; init; }
        public bool IsBatteryCharging { get; init; }
        public bool IsBatteryFullyCharged { get; init; }
        public double BatteryPowerWatts { get; init; }
        public int BatteryTimeRemainingMinutes { get; init; }
    }

    public interface IHardwareMonitoringService
    {
        IDisposable Acquire(HardwareMonitoringCategory categories);
        HardwareMetricsSnapshot ReadSnapshot();
        float ReadSensor(HardwareType hardwareType, SensorType sensorType, string sensorName);
    }

    public sealed class HardwareMonitoringService : IHardwareMonitoringService, IDisposable
    {
        private readonly object _sync = new();
        private readonly Dictionary<long, HardwareMonitoringCategory> _leases = new();
        private Computer? _computer;
        private HardwareMonitoringCategory _activeCategories;
        private DateTime _lastUpdateUtc = DateTime.MinValue;
        private long _nextLeaseId;
        private bool _disposed;

        public IDisposable Acquire(HardwareMonitoringCategory categories)
        {
            if (categories == HardwareMonitoringCategory.None)
                return EmptyLease.Instance;

            lock (_sync)
            {
                ThrowIfDisposed();
                long id = ++_nextLeaseId;
                _leases[id] = categories;
                ApplyRequestedCategories();
                return new MonitoringLease(this, id);
            }
        }

        public HardwareMetricsSnapshot ReadSnapshot()
        {
            lock (_sync)
            {
                ThrowIfDisposed();
                UpdateHardware();

                IHardware? cpu = FindHardware(HardwareType.Cpu);
                IHardware? gpu = FindPreferredGpu();
                IHardware? battery = FindHardware(HardwareType.Battery);
                GetSystemPowerStatus(out SystemPowerStatus powerStatus);

                double gpuMemoryUsedMb = ReadFirstValue(gpu, SensorType.SmallData, "GPU Memory Used", "D3D Dedicated Memory Used");
                double gpuMemoryTotalMb = ReadFirstValue(gpu, SensorType.SmallData, "GPU Memory Total", "D3D Dedicated Memory Total");
                var (memoryUsed, memoryTotal, memoryCommitted, pagefileTotal) = ReadPhysicalMemory();

                return new HardwareMetricsSnapshot
                {
                    CpuTemperature = Round(ReadFirstValue(cpu, SensorType.Temperature, "Core Average", "CPU Package", "Core")),
                    CpuUsage = Round(ReadFirstValue(cpu, SensorType.Load, "CPU Total", "Total")),
                    CpuPowerWatts = Round(ReadFirstValue(cpu, SensorType.Power, "CPU Package", "Package")),
                    CpuClockMhz = Round(ReadAverage(cpu, SensorType.Clock, "Core")),
                    GpuTemperature = Round(ReadFirstValue(gpu, SensorType.Temperature, "GPU Core", "Core")),
                    GpuUsage = Round(ReadFirstValue(gpu, SensorType.Load, "GPU Core", "Core")),
                    GpuPowerWatts = Round(ReadFirstValue(gpu, SensorType.Power, "GPU Package", "GPU Power", "Core")),
                    GpuClockMhz = Round(ReadFirstValue(gpu, SensorType.Clock, "GPU Core", "Core")),
                    GpuMemoryUsedGb = gpuMemoryUsedMb / 1024d,
                    GpuMemoryTotalGb = gpuMemoryTotalMb / 1024d,
                    SystemMemoryUsedGb = memoryUsed,
                    SystemMemoryTotalGb = memoryTotal,
                    SystemMemoryCommittedGb = memoryCommitted,
                    SystemMemoryPagefileTotalGb = pagefileTotal,
                    HasBattery = powerStatus.BatteryFlag != 128 && powerStatus.BatteryLifePercent <= 100,
                    BatteryPercent = powerStatus.BatteryLifePercent <= 100 ? powerStatus.BatteryLifePercent : 0,
                    IsBatteryCharging = (powerStatus.BatteryFlag & 8) != 0,
                    IsBatteryFullyCharged = powerStatus.BatteryLifePercent == 100 && powerStatus.ACLineStatus == 1,
                    BatteryPowerWatts = Math.Abs(ReadFirstValue(battery, SensorType.Power, "Charge Rate", "Discharge Rate", "Power")),
                    BatteryTimeRemainingMinutes = powerStatus.BatteryLifeTime > 0 ? powerStatus.BatteryLifeTime / 60 : 0
                };
            }
        }

        public float ReadSensor(HardwareType hardwareType, SensorType sensorType, string sensorName)
        {
            lock (_sync)
            {
                ThrowIfDisposed();
                UpdateHardware();
                return (float)ReadValue(FindHardware(hardwareType), sensorType, sensorName);
            }
        }

        private void Release(long id)
        {
            lock (_sync)
            {
                if (_disposed || !_leases.Remove(id))
                    return;

                ApplyRequestedCategories();
            }
        }

        private void ApplyRequestedCategories()
        {
            HardwareMonitoringCategory requested = HardwareMonitoringCategory.None;
            foreach (HardwareMonitoringCategory lease in _leases.Values)
                requested |= lease;

            if (requested == _activeCategories)
                return;

            _computer?.Close();
            _computer = null;
            _activeCategories = requested;
            _lastUpdateUtc = DateTime.MinValue;

            if (requested == HardwareMonitoringCategory.None)
                return;

            _computer = new Computer
            {
                IsCpuEnabled = requested.HasFlag(HardwareMonitoringCategory.Cpu),
                IsMemoryEnabled = requested.HasFlag(HardwareMonitoringCategory.Memory),
                IsGpuEnabled = requested.HasFlag(HardwareMonitoringCategory.Gpu),
                IsBatteryEnabled = requested.HasFlag(HardwareMonitoringCategory.Battery),
                IsMotherboardEnabled = true,
                IsStorageEnabled = true,
                IsControllerEnabled = true
            };
            _computer.Open();
        }

        private void UpdateHardware()
        {
            if (_computer is null || DateTime.UtcNow - _lastUpdateUtc < TimeSpan.FromMilliseconds(250))
                return;

            foreach (IHardware hardware in _computer.Hardware)
                UpdateHardwareTree(hardware);

            _lastUpdateUtc = DateTime.UtcNow;
        }

        private static void UpdateHardwareTree(IHardware hardware)
        {
            hardware.Update();
            foreach (IHardware child in hardware.SubHardware)
                UpdateHardwareTree(child);
        }

        private IHardware? FindHardware(HardwareType type) =>
            _computer?.Hardware.FirstOrDefault(hardware => hardware.HardwareType == type);

        private IHardware? FindPreferredGpu()
        {
            if (_computer is null)
                return null;

            return _computer.Hardware.FirstOrDefault(hardware => hardware.HardwareType == HardwareType.GpuNvidia)
                ?? _computer.Hardware.FirstOrDefault(hardware => hardware.HardwareType == HardwareType.GpuAmd)
                ?? _computer.Hardware.FirstOrDefault(hardware => hardware.HardwareType == HardwareType.GpuIntel);
        }

        private static double ReadFirstValue(IHardware? hardware, SensorType type, params string[] names)
        {
            foreach (string name in names)
            {
                double value = ReadValue(hardware, type, name);
                if (Math.Abs(value) > double.Epsilon)
                    return value;
            }

            return 0;
        }

        private static double ReadValue(IHardware? hardware, SensorType type, string name)
        {
            if (hardware is null)
                return 0;

            ISensor? sensor = EnumerateSensors(hardware).FirstOrDefault(candidate =>
                candidate.SensorType == type && candidate.Name.Contains(name, StringComparison.OrdinalIgnoreCase));
            return sensor?.Value ?? 0;
        }

        private static double ReadAverage(IHardware? hardware, SensorType type, string name)
        {
            if (hardware is null)
                return 0;

            float[] values = EnumerateSensors(hardware)
                .Where(sensor => sensor.SensorType == type && sensor.Name.Contains(name, StringComparison.OrdinalIgnoreCase) && sensor.Value.HasValue)
                .Select(sensor => sensor.Value!.Value)
                .ToArray();
            return values.Length == 0 ? 0 : values.Average();
        }

        private static IEnumerable<ISensor> EnumerateSensors(IHardware hardware)
        {
            foreach (ISensor sensor in hardware.Sensors)
                yield return sensor;

            foreach (IHardware child in hardware.SubHardware)
            foreach (ISensor sensor in EnumerateSensors(child))
                yield return sensor;
        }

        private static int Round(double value) => (int)Math.Round(value);

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(HardwareMonitoringService));
        }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed)
                    return;

                _computer?.Close();
                _computer = null;
                _leases.Clear();
                _disposed = true;
            }
        }

        private sealed class MonitoringLease : IDisposable
        {
            private HardwareMonitoringService? _owner;
            private readonly long _id;

            public MonitoringLease(HardwareMonitoringService owner, long id)
            {
                _owner = owner;
                _id = id;
            }

            public void Dispose() => System.Threading.Interlocked.Exchange(ref _owner, null)?.Release(_id);
        }

        private sealed class EmptyLease : IDisposable
        {
            public static EmptyLease Instance { get; } = new();
            public void Dispose() { }
        }

        [DllImport("kernel32.dll")]
        private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);

        [DllImport("kernel32.dll")]
        private static extern void GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

        [StructLayout(LayoutKind.Sequential)]
        private struct SystemPowerStatus
        {
            public byte ACLineStatus;
            public byte BatteryFlag;
            public byte BatteryLifePercent;
            public byte SystemStatusFlag;
            public int BatteryLifeTime;
            public int BatteryFullLifeTime;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MemoryStatusEx
        {
            public uint Length;
            public uint MemoryLoad;
            public ulong TotalPhysical;
            public ulong AvailablePhysical;
            public ulong TotalPageFile;
            public ulong AvailablePageFile;
            public ulong TotalVirtual;
            public ulong AvailableVirtual;
            public ulong AvailableExtendedVirtual;
        }

        private static (double usedGb, double totalGb, double committedGb, double pagefileTotalGb) ReadPhysicalMemory()
        {
            const double gib = 1024d * 1024 * 1024;
            var mem = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
            GlobalMemoryStatusEx(ref mem);

            double total = mem.TotalPhysical / gib;
            double available = mem.AvailablePhysical / gib;
            double committed = (mem.TotalPageFile - mem.AvailablePageFile) / gib;
            double pagefileTotal = mem.TotalPageFile / gib;
            return (total - available, total, committed, pagefileTotal);
        }
    }
}
