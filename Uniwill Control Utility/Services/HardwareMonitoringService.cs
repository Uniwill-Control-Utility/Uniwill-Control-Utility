using LibreHardwareMonitor.Hardware;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace Uniwill_Control_Utility.Services
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
        /// <summary>True if the connected device is a BS1 (BLE), false for BS2/BS2 Pro/BS3/BS3 Pro (HID).</summary>
        public bool FlydigiIsBs1 { get; init; }
        /// <summary>Current RGB mode name (e.g. "Off", "Static", "SmartTemp").</summary>
        public string FlydigiRgbMode { get; init; } = string.Empty;
        /// <summary>Human-readable model name (e.g. "BS1", "BS2 PRO", "BS3").</summary>
        public string FlydigiModelName { get; init; } = string.Empty;
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
        public int MemoryTemperature0 { get; init; }
        public int MemoryTemperature1 { get; init; }
        public int StorageTemperatureCelsius { get; init; }
        public bool HasBattery { get; init; }
        public int BatteryPercent { get; init; }
        public bool IsBatteryCharging { get; init; }
        public bool IsBatteryFullyCharged { get; init; }
        public double BatteryPowerWatts { get; init; }
        public int BatteryTimeRemainingMinutes { get; init; }
        public int BatteryHealthPercent { get; init; }
        public int BatteryTemperatureC { get; init; }
        public int BatteryChargeLimit { get; init; } // 0 = 100%, 1 = 80%, 2 = 60%
    }

    public interface IHardwareMonitoringService
    {
        IDisposable Acquire(HardwareMonitoringCategory categories);
        HardwareMetricsSnapshot ReadSnapshot();
        float ReadSensor(HardwareType hardwareType, SensorType sensorType, string sensorName);
    }

    public sealed partial class HardwareMonitoringService : IHardwareMonitoringService, IDisposable
    {
        private readonly object _sync = new();
        private readonly Dictionary<long, HardwareMonitoringCategory> _leases = new();
        private Computer? _computer;
        private HardwareMonitoringCategory _activeCategories;
        private DateTime _lastUpdateUtc = DateTime.MinValue;
        private long _nextLeaseId;
        private bool _disposed;

        /// <summary>Consecutive UpdateHardware failures (reset on success).</summary>
        private int _updateFailureCount;

        /// <summary>When the last UpdateHardware failure occurred.</summary>
        private DateTime _lastUpdateFailureUtc = DateTime.MinValue;

        public IDisposable Acquire(HardwareMonitoringCategory categories)
        {
            if (categories == HardwareMonitoringCategory.None)
                return EmptyLease.Instance;

            lock (_sync)
            {
                ThrowIfDisposed();
                long id = ++_nextLeaseId;
                _leases[id] = categories;
                ApplyRequestedCategoriesLocked();
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

                (int memTemp0, int memTemp1) = ReadMemoryTemperatures();
                int storageTemp = ReadStorageTemperature();

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
                    MemoryTemperature0 = memTemp0,
                    MemoryTemperature1 = memTemp1,
                    StorageTemperatureCelsius = storageTemp,
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

                ApplyRequestedCategoriesLocked();
            }
        }

        /// <summary>Must be called while holding _sync.</summary>
        private void ApplyRequestedCategoriesLocked()
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

            // Build the Computer under lock but open it outside to avoid
            // blocking other Acquire() callers during driver initialization.
            var computer = new Computer
            {
                IsCpuEnabled = requested.HasFlag(HardwareMonitoringCategory.Cpu),
                IsMemoryEnabled = requested.HasFlag(HardwareMonitoringCategory.Memory),
                IsGpuEnabled = requested.HasFlag(HardwareMonitoringCategory.Gpu),
                IsBatteryEnabled = requested.HasFlag(HardwareMonitoringCategory.Battery),
                IsMotherboardEnabled = true,
                IsStorageEnabled = true,
                IsControllerEnabled = true
            };

            // Open outside the lock — this probes hardware drivers and can take
            // 1-2 seconds on first call (NVML, ADL, WMI provider initialization).
            Monitor.Exit(_sync);
            try
            {
                computer.Open();
            }
            finally
            {
                Monitor.Enter(_sync);
            }

            // Another caller may have changed _activeCategories while we held
            // the lock open. Only assign if our categories are still current.
            if (_activeCategories == requested)
                _computer = computer;
            else
                computer.Close();
        }

        private void UpdateHardware()
        {
            if (_computer is null || DateTime.UtcNow - _lastUpdateUtc < TimeSpan.FromMilliseconds(250))
                return;

            try
            {
                foreach (IHardware hardware in _computer.Hardware)
                    UpdateHardwareTree(hardware);

                _lastUpdateUtc = DateTime.UtcNow;
                _updateFailureCount = 0;
            }
            catch (Exception ex)
            {
                /*
                 * LHM's hardware.Update() calls into GPU driver interop (NVML for Nvidia,
                 * ADL/AMDL for AMD). When a display topology changes (e.g. monitor hotplug),
                 * the GPU driver can invalidate those handles, causing ExecutionEngineException
                 * or other fatal interop failures. Catch everything here to prevent the timer
                 * thread from taking down the process, then reopen the Computer to get fresh
                 * driver handles.
                 *
                 * We also rate-limit resets to avoid tight loops if the driver stays broken.
                 */
                _updateFailureCount++;
                _lastUpdateFailureUtc = DateTime.UtcNow;

                try { Serilog.Log.Warning(ex, "[LHM] UpdateHardware failed (failure #{Count}) — attempting Computer reset", _updateFailureCount); } catch { /* Serilog may not be ready */ }

                // Only attempt a reset if at least 2 seconds have passed since the last failure
                // to avoid tight loops during sustained driver issues.
                if (DateTime.UtcNow - _lastUpdateFailureUtc > TimeSpan.FromSeconds(2) || _updateFailureCount <= 3)
                {
                    try
                    {
                        _computer?.Close();
                        _computer = null;

                        // Reopen with the currently active categories
                        if (_activeCategories != HardwareMonitoringCategory.None)
                        {
                            _computer = new Computer
                            {
                                IsCpuEnabled = _activeCategories.HasFlag(HardwareMonitoringCategory.Cpu),
                                IsMemoryEnabled = _activeCategories.HasFlag(HardwareMonitoringCategory.Memory),
                                IsGpuEnabled = _activeCategories.HasFlag(HardwareMonitoringCategory.Gpu),
                                IsBatteryEnabled = _activeCategories.HasFlag(HardwareMonitoringCategory.Battery),
                                IsMotherboardEnabled = true,
                                IsStorageEnabled = true,
                                IsControllerEnabled = true
                            };
                            _computer.Open();
                        }
                    }
                    catch
                    {
                        // If reopening also fails, give up and let the next tick retry later.
                        _computer = null;
                    }
                }
            }
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
            {
                foreach (ISensor sensor in EnumerateSensors(child))
                    yield return sensor;
            }
        }

        private static int Round(double value) => (int)Math.Round(value);

        private void ThrowIfDisposed()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
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

        [LibraryImport("kernel32.dll")]
        private static partial int GetSystemPowerStatus(out SystemPowerStatus status);

        [LibraryImport("kernel32.dll")]
        private static partial void GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

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

        private (int temp0, int temp1) ReadMemoryTemperatures()
        {
            if (_computer is null)
                return (0, 0);

            // DIMMs appear as top-level hardware items with HardwareType.Memory.
            // Each DIMM has a temperature sensor named "DIMM #N" if it's DDR4/DDR5 with a thermal sensor.
            // On some laptops (e.g., PCSpecialist Recoil 3 16), DIMMs may be at slots #0 and #2
            // instead of #0 and #1. So we collect all DIMM temperatures and assign by order.
            int temp0 = 0;
            int temp1 = 0;
            int index = 0;

            foreach (IHardware hardware in _computer.Hardware)
            {
                if (hardware.HardwareType != HardwareType.Memory)
                    continue;

                foreach (ISensor sensor in hardware.Sensors)
                {
                    if (sensor.SensorType != SensorType.Temperature || !sensor.Value.HasValue)
                        continue;

                    // Match any "DIMM #N" sensor name
                    string name = sensor.Name;
                    if (!name.Equals("DIMM #0", StringComparison.OrdinalIgnoreCase) &&
                        !name.Equals("DIMM #1", StringComparison.OrdinalIgnoreCase) &&
                        !name.Equals("DIMM #2", StringComparison.OrdinalIgnoreCase) &&
                        !name.Equals("DIMM #3", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    int val = Round(sensor.Value.Value);
                    if (index == 0)
                        temp0 = val;
                    else if (index == 1)
                        temp1 = val;

                    index++;
                }
            }

            return (temp0, temp1);
        }

        private int ReadStorageTemperature()
        {
            if (_computer is null)
                return 0;

            // Storage hardware contains individual drives (HardwareType.Storage).
            // Each drive may have a "Temperature" sensor. We return the maximum across all drives.
            int maxTemp = 0;
            foreach (IHardware storage in _computer.Hardware)
            {
                if (storage.HardwareType != HardwareType.Storage)
                    continue;

                double temp = ReadFirstValue(storage, SensorType.Temperature, "Temperature");
                if (temp > maxTemp)
                    maxTemp = Round(temp);
            }

            return maxTemp;
        }

        private static unsafe (double usedGb, double totalGb, double committedGb, double pagefileTotalGb) ReadPhysicalMemory()
        {
            const double gib = 1024d * 1024 * 1024;
            var mem = new MemoryStatusEx { Length = (uint)sizeof(MemoryStatusEx) };
            GlobalMemoryStatusEx(ref mem);

            double total = mem.TotalPhysical / gib;
            double available = mem.AvailablePhysical / gib;
            double committed = (mem.TotalPageFile - mem.AvailablePageFile) / gib;
            double pagefileTotal = mem.TotalPageFile / gib;
            return (total - available, total, committed, pagefileTotal);
        }
    }
}
