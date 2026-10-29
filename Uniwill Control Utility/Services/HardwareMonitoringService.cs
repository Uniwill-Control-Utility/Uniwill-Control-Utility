using LibreHardwareMonitor.Hardware;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Uniwill_Control_Utility.Models;

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
        /// <summary>Whether Smart Mode is currently active on the watercooler.</summary>
        public bool HydroUiSmartModeActive { get; init; }

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

    /// <summary>
    /// Which GPU type is currently providing sensor data.
    /// Used to update the GPU metric card label when the active GPU changes
    /// (e.g., dGPU disabled by MUX switch or iGPU Power Saving Mode).
    /// </summary>
    public enum ActiveGpuType
    {
        None = 0,
        Nvidia = 1,
        Amd = 2,
        Intel = 3
    }

    /// <summary>
    /// Where a snapshot's CPU temperature came from. The CPU's own sensor (MSR-derived:
    /// Tctl/Tdie, TjMax offset) needs the PawnIO kernel driver, whose device ACL is
    /// System + Administrators - so an asInvoker process has no CPU sensor. The embedded
    /// controller's reading (register 0x043E, the sensor the EC fan curve runs on) reaches
    /// the app through the LocalSystem engine with no privilege at all. None = neither
    /// answered, and the UI shows "--".
    /// </summary>
    public enum CpuTemperatureSource
    {
        None = 0,
        CpuSensor = 1,
        EcSensor = 2
    }

    public sealed record HardwareMetricsSnapshot
    {
        public int CpuTemperature { get; init; }

        /// <summary>Which sensor produced CpuTemperature (0 values pair with None).</summary>
        public CpuTemperatureSource CpuTemperatureSource { get; init; }
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
        /// <summary>Which GPU type is currently providing sensor data.</summary>
        public ActiveGpuType ActiveGpuType { get; init; }
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

    internal readonly record struct HardwareSensorReading(
        HardwareType HardwareType,
        SensorType SensorType,
        string Name,
        float Value);

    internal sealed record HardwareMonitoringState(
        HardwareMetricsSnapshot Metrics,
        IReadOnlyList<HardwareSensorReading> Sensors)
    {
        public static HardwareMonitoringState Empty { get; } =
            new(new HardwareMetricsSnapshot(), Array.Empty<HardwareSensorReading>());

        public float ReadSensor(HardwareType hardwareType, SensorType sensorType, string sensorName)
        {
            foreach (HardwareSensorReading sensor in Sensors)
            {
                if (sensor.HardwareType == hardwareType &&
                    sensor.SensorType == sensorType &&
                    sensor.Name.Contains(sensorName, StringComparison.OrdinalIgnoreCase))
                {
                    return sensor.Value;
                }
            }

            return 0;
        }
    }

    public interface IHardwareMonitoringService
    {
        IDisposable Acquire(HardwareMonitoringCategory categories);
        void AcquirePersistent(HardwareMonitoringCategory categories);
        HardwareMetricsSnapshot ReadSnapshot();
        float ReadSensor(HardwareType hardwareType, SensorType sensorType, string sensorName);
        void ResetComputer();
    }

    public sealed partial class HardwareMonitoringService : IHardwareMonitoringService, IDisposable
    {
        private static readonly TimeSpan SnapshotMaxAge = TimeSpan.FromSeconds(1);

        /// <summary>
        /// Producer staleness budget: how long a completed snapshot may be
        /// served as live before the probe must complete a new pass. Healthy
        /// cadence is fast - the cache expires at <see cref="SnapshotMaxAge"/>,
        /// every consumer read renews the refresh, a healthy pass completes in
        /// ~the 1 s cadence - so 5 s of no completed pass means the single-
        /// flight refresh is PARKED: the pass is stuck inside the LHM gate
        /// (field report 2026-10-29: in-game the pass parks behind the
        /// foreground title that owns the GPU - NVAPI/storage calls under AV
        /// filter drivers; alt-tab unblocks it for 1-2 s, refocusing wedges it
        /// again). The cache's single-flight contract then queues no further
        /// pass, and serving the last completed state past the budget paints
        /// frozen numbers as live on every consumer (the RTSS OSD froze
        /// mid-game, updating only around focus changes). Same statue class the
        /// engine-metrics gate closed at its source, closed the same way: past
        /// the budget the LHM-only fields are no readings ("--"), and the
        /// fresh sources - OS, EC, engine - keep answering through the OS-only
        /// path beside them.
        /// </summary>
        private static readonly TimeSpan ProbeStalenessBudget = TimeSpan.FromSeconds(5);

        internal static bool ProducerIsStale(TimeSpan producerAge) =>
            producerAge > ProbeStalenessBudget;

        /// <summary>
        /// SMART temperature cadence for Storage devices. The field breadcrumb
        /// (2026-10-29) measured the park: a SMART DeviceIoControl to
        /// "CT2000P310SSD8" sat 21044 ms behind the AV storage filter driver -
        /// inside the probe gate, which serializes the WHOLE producer, so
        /// every fast source (the full Computer open is 1.4 s steady-state on
        /// this machine, measured) queued behind a quasi-static temperature,
        /// the producer aged past the staleness budget, and the LHM-fed
        /// fields blanked on every consumer (dashboard, tray, FanControl,
        /// OSD). Storage temperature moves on a minutes scale: update it once
        /// a minute, and initialize the clock to construction so the FIRST
        /// snapshot never queues behind SMART (the storage card shows "--"
        /// for the first minute while every faster field fills in seconds).
        /// The clock moves AFTER the update completes, so a park restarts the
        /// window instead of retrying into the same filter-driver storm.
        /// </summary>
        private static readonly TimeSpan StorageUpdateInterval = TimeSpan.FromMinutes(1);

        internal static bool StorageUpdateDue(DateTime now, DateTime lastCompleted) =>
            now - lastCompleted >= StorageUpdateInterval;

        /// <summary>Storage SMART cadence clock (see StorageUpdateInterval);
        /// starts at construction time so the first update is deferred.</summary>
        private DateTime _lastStorageUpdateUtc = DateTime.UtcNow;

        /// <summary>
        /// Producer stage breadcrumbs (field report 2026-10-29: the LHM-fed
        /// fields - CPU utilization, GPU temp/usage/clock/power - appeared
        /// only ~50 s after launch; everything the OS/EC/engine feeds filled
        /// in seconds, so the first full probe pass is the gate, and its
        /// Open() serializes every driver touch: NVAPI init, the Ring0/PawnIO
        /// attempt, SMART per drive, SuperIO, under the security product's
        /// driver guard). Which touch holds the 50 s is measured, not
        /// guessed: a slow open names the enabled set, a slow device update
        /// names the device, a slow pass carries the build/capture split.
        /// Warning severity (the app log's field level), debounced so a
        /// persistently slow chain stays one line per minute.
        /// </summary>
        private const long SlowProbeStageMs = 2000;
        private const long SlowProbePassMs = 5000;
        private static readonly TimeSpan ProbeSlowLogDebounce = TimeSpan.FromSeconds(60);

        private DateTime _lastProbeSlowLogUtc = DateTime.MinValue;

        private void LogProbeSlow(string message)
        {
            DateTime now = DateTime.UtcNow;
            if (now - _lastProbeSlowLogUtc < ProbeSlowLogDebounce)
                return;

            _lastProbeSlowLogUtc = now;
            try { Serilog.Log.Warning("[LHM] probe: " + message); } catch { }
        }

        /// <summary>
        /// Lease bookkeeping only (leases, lease ids, disposed flag). Never
        /// held across hardware I/O, so Acquire/Release from a UI thread are
        /// bounded by dictionary work.
        /// </summary>
        private readonly object _sync = new();

        /// <summary>
        /// Exclusive right to touch the LibreHardwareMonitor Computer
        /// (Open/Update/Close/sensor reads). A storage DeviceIoControl inside
        /// the update pass can be held indefinitely by a filter driver
        /// (field-proven on an AV-guarded machine: the disk open hung, the
        /// pass held the update lock, the UI thread's Acquire waited on it,
        /// the whole app froze with the dashboard up). Pool threads may block
        /// on this gate; every app-thread consumer uses Monitor.TryEnter with
        /// a zero/bounded timeout and degrades instead.
        /// </summary>
        private readonly object _probeGate = new();

        private readonly Dictionary<long, HardwareMonitoringCategory> _leases = new();
        private readonly StaleWhileRevalidateCache<HardwareMonitoringState> _stateCache;

        /// <summary>EC CPU temperature source; null = no engine (dev/test composition).</summary>
        private readonly IEcCpuTemperatureSource? _ecCpuTemperature;

        /// <summary>Engine kernel-side sensor source (power/clocks/SPD); null = no
        /// engine. Merged into every produced snapshot at the zero fields (see ctor).</summary>
        private readonly IEcEngineMetricsSource? _ecEngineMetrics;

        /// <summary>Last published state; read by the OS-only fallback path (volatile).</summary>
        private HardwareMonitoringState _lastState = HardwareMonitoringState.Empty;

        /// <summary>Categories requested by leases (UI writes under _sync, pool reads).</summary>
        private volatile HardwareMonitoringCategory _requestedCategories;

        /// <summary>Categories the Computer instance was built for (gate-owned).</summary>
        private HardwareMonitoringCategory _configuredCategories;

        /// <summary>A ResetComputer() the gate owner must apply on its next pass.</summary>
        private int _resetPending;

        private Computer? _computer;
        private DateTime _lastUpdateUtc = DateTime.MinValue;
        private long _nextLeaseId;
        private bool _disposed;

        /// <summary>
        /// How long AcquirePersistent waits for the first snapshot on the
        /// startup worker. A healthy probe opens in 1-2 s; a hung driver
        /// probe must not stretch the splash, the dashboard fills from the
        /// OS-only path and completes when the probe ever returns.
        /// Field-tuned (2026-10-28): 20 s gave a healthy machine 10x margin
        /// and handed a guarded machine (a probe that hangs indefinitely) a
        /// 20 s splash on every launch. 6 s keeps a 3x healthy margin and
        /// caps the guarded-box splash; the fill contract is unchanged.
        /// </summary>
        private static readonly TimeSpan StartupSnapshotBudget = TimeSpan.FromSeconds(6);

        /// <summary>
        /// How long Dispose waits for the gate to close the Computer. A hung
        /// probe keeps the gate; the app must still exit.
        /// </summary>
        private static readonly TimeSpan ShutdownGateBudget = TimeSpan.FromSeconds(3);

        /// <summary>Consecutive UpdateHardware failures (reset on success).</summary>
        private int _updateFailureCount;

        /// <summary>When the last UpdateHardware failure occurred.</summary>
        private DateTime _lastUpdateFailureUtc = DateTime.MinValue;

        /// <summary>When ResetComputer() was last called — used to enforce a cooldown before reopening.</summary>
        private DateTime _lastResetUtc = DateTime.MinValue;

        /// <summary>How long to wait after ResetComputer() before attempting to reopen (gives GPU driver time to settle).</summary>
        private static readonly TimeSpan ResetCooldown = TimeSpan.FromSeconds(3);

        /// <param name="ecCpuTemperature">
        /// The embedded controller's CPU temperature, used when the in-process MSR sensor
        /// path cannot answer: the CPU sensor needs the PawnIO driver, whose device ACL
        /// admits System and Administrators, so the asInvoker app installed per-machine has
        /// no CPU sensor. Optional, so a composition without an engine keeps the
        /// single-source behaviour.
        /// </param>
        /// <param name="engineMetrics">
        /// The engine's kernel-side sensor readings (Ring0 power/clocks, SPD speed
        /// and DIMM temps), merged into every snapshot the service produces for
        /// the fields the in-process probe left zero - the same ACL rule as the
        /// EC temperature, and the reason the field dashboard showed "--" on CPU
        /// Power, Clock Speed and RAM speed for the whole session. Merged HERE,
        /// at the single snapshot producer (not at a page's timer), so every
        /// consumer - dashboard cards, tray flyout, FanControl cards, the RTSS
        /// OSD - gets the engine-fed fields through the same
        /// <see cref="ReadSnapshot"/>; a per-page merge left all the others
        /// showing zeros (field report: the OSD stayed missing what the
        /// dashboard showed, 2026-10-29). Freshness is gated at the source
        /// (<see cref="IEcEngineMetricsSource"/>): a wedged engine probe answers
        /// Empty, the fields stay "--", never a frozen number.
        /// Optional: a composition without an engine keeps the single-source
        /// behaviour.
        /// </param>
        public HardwareMonitoringService(
            IEcCpuTemperatureSource? ecCpuTemperature = null,
            IEcEngineMetricsSource? engineMetrics = null)
        {
            _ecCpuTemperature = ecCpuTemperature;
            _ecEngineMetrics = engineMetrics;

            _stateCache = new StaleWhileRevalidateCache<HardwareMonitoringState>(
                HardwareMonitoringState.Empty,
                SnapshotMaxAge,
                _ => Task.Run(ProbeOnce),
                onRefreshError: ex =>
                {
                    try { Serilog.Log.Warning(ex, "[LHM] Background snapshot refresh failed"); }
                    catch { /* Serilog may not be ready */ }
                });
        }

        /// <summary>
        /// Acquires a persistent lease that is never automatically released.
        /// Used during startup to keep the Computer instance alive from pre-UI
        /// detection through dashboard navigation, avoiding a costly re-probe.
        /// </summary>
        public void AcquirePersistent(HardwareMonitoringCategory categories)
        {
            if (categories == HardwareMonitoringCategory.None)
                return;

            bool categoriesChanged;
            lock (_sync)
            {
                ThrowIfDisposed();
                _leases[long.MinValue] = categories; // persistent lease id
                categoriesChanged = RequestRequestedCategoriesLocked();
            }

            if (categoriesChanged)
                _stateCache.Invalidate();

            // The probe runs on the pool under the gate; the startup worker
            // waits for the first snapshot with a bounded budget. A hung
            // driver probe parks the gate, not the splash: the dashboard
            // fills from the OS-only path and completes later.
            try
            {
                if (!_stateCache.RefreshAsync().Wait(StartupSnapshotBudget))
                {
                    try { Serilog.Log.Warning("[LHM] First snapshot not ready within the startup budget; the probe is hung or slow, metrics fill as it completes"); } catch { }
                }
            }
            catch
            {
                // A failed warm is a metrics gap, never a startup failure.
            }
        }

        public IDisposable Acquire(HardwareMonitoringCategory categories)
        {
            if (categories == HardwareMonitoringCategory.None)
                return EmptyLease.Instance;

            bool categoriesChanged;
            MonitoringLease lease;
            lock (_sync)
            {
                ThrowIfDisposed();
                long id = ++_nextLeaseId;
                _leases[id] = categories;
                categoriesChanged = RequestRequestedCategoriesLocked();
                lease = new MonitoringLease(this, id);
            }

            // Bookkeeping only up to here: the UI thread must never wait on
            // hardware I/O (field-proven freeze: a storage probe inside the
            // update pass held the old single lock forever). The kick is
            // fire-and-forget; the probe serializes on the gate on the pool.
            if (categoriesChanged)
                _stateCache.Invalidate();

            _ = _stateCache.RefreshAsync();
            return lease;
        }

        public HardwareMetricsSnapshot ReadSnapshot()
        {
            HardwareMonitoringState state = _stateCache.Read();
            bool producerStale = ProducerIsStale(_stateCache.Age);

            // Two faces of "the producer is not answering": never completed
            // (startup, first pass parked on the gate) and completed-then-
            // wedged (the refresh is parked, no pass completes - see
            // ProbeStalenessBudget). Both take the fresh path: OS numbers
            // rebuilt now, EC temperature, engine sensor readings; the LHM-
            // only fields of a wedged producer are no readings, not statues.
            if (ReferenceEquals(state, HardwareMonitoringState.Empty) || producerStale)
                return BuildOsOnlySnapshot(state.Metrics, producerStale);

            return state.Metrics;
        }

        public float ReadSensor(HardwareType hardwareType, SensorType sensorType, string sensorName)
        {
            HardwareMonitoringState state = _stateCache.Read();

            // Same freshness rule as ReadSnapshot, and harsher consumers: the
            // adaptive fan loop feeds on these readings, and a frozen fan RPM
            // is worse than the 0 a missing reading already means.
            if (ProducerIsStale(_stateCache.Age))
                return 0;

            if (ReferenceEquals(state, HardwareMonitoringState.Empty))
                state = Volatile.Read(ref _lastState);

            return state.ReadSensor(hardwareType, sensorType, sensorName);
        }

        /// <summary>
        /// Closes the LibreHardwareMonitor Computer to release stale GPU driver handles.
        /// Call this proactively after events that invalidate GPU driver state:
        /// monitor hotplug (display topology change), hibernation resume, etc.
        ///
        /// The reset is deferred, never performed on the caller: Close() is
        /// LHM driver I/O that a filter driver can hold indefinitely, and the
        /// gate owner applies it on the next probe pass. The cache
        /// invalidation immediately re-serves the OS-only/last-state view.
        /// </summary>
        public void ResetComputer()
        {
            lock (_sync)
            {
                ThrowIfDisposed();
            }

            try { Serilog.Log.Information("[LHM] ResetComputer called — releasing stale handles, reopening deferred to next refresh"); }
            catch { /* Serilog may not be ready */ }

            Interlocked.Exchange(ref _resetPending, 1);

            // Invalidate cache so the next ReadSnapshot() triggers a background
            // refresh which will reopen the Computer with fresh handles.
            _stateCache.Invalidate();
        }

        private void Release(long id)
        {
            bool categoriesChanged;
            lock (_sync)
            {
                if (_disposed || !_leases.Remove(id))
                    return;

                categoriesChanged = RequestRequestedCategoriesLocked();
            }

            if (categoriesChanged)
                _stateCache.Invalidate();
        }

        /// <summary>
        /// Must be called while holding _sync. Bookkeeping only: recompute the
        /// union of the lease categories and publish it for the gate owner.
        /// Rebuilding the Computer is the probe's job (UpdateHardware): the
        /// old version closed and reopened drivers here, under the same lock
        /// the UI thread's Acquire took, which is how one hung storage open
        /// froze the whole app.
        /// </summary>
        private bool RequestRequestedCategoriesLocked()
        {
            HardwareMonitoringCategory requested = HardwareMonitoringCategory.None;
            foreach (HardwareMonitoringCategory lease in _leases.Values)
                requested |= lease;

            if (requested == _requestedCategories)
                return false;

            _requestedCategories = requested;
            return true;
        }

        /// <summary>
        /// The probe pass: one serialized touch of the Computer, on a pool
        /// thread (the cache factory's Task.Run). Blocking on the gate here
        /// is safe and is the whole point: the cache's single-flight contract
        /// keeps at most one pass queued, a hung driver parks only that pass,
        /// and every app-thread consumer keeps reading the last state plus
        /// the OS-only snapshot.
        /// </summary>
        private HardwareMonitoringState ProbeOnce()
        {
            Stopwatch passSw = Stopwatch.StartNew();
            Monitor.Enter(_probeGate);
            try
            {
                ApplyPendingReset();
                UpdateHardware();

                Stopwatch buildSw = Stopwatch.StartNew();
                var state = new HardwareMonitoringState(BuildMetricsSnapshot(), CaptureSensorReadings());
                buildSw.Stop();

                Volatile.Write(ref _lastState, state);

                if (passSw.ElapsedMilliseconds >= SlowProbePassMs)
                    LogProbeSlow($"full pass {passSw.ElapsedMilliseconds}ms (build+capture {buildSw.ElapsedMilliseconds}ms)");

                return state;
            }
            finally
            {
                Monitor.Exit(_probeGate);
            }
        }

        /// <summary>
        /// Gate-owned: performs the Close() that ResetComputer() deferred,
        /// then starts the settle cooldown from this moment (the driver gets
        /// its window after the handles are actually released).
        /// </summary>
        private void ApplyPendingReset()
        {
            if (Interlocked.Exchange(ref _resetPending, 0) == 0)
                return;

            _computer?.Close();
            _computer = null;
            _lastUpdateUtc = DateTime.MinValue;
            _updateFailureCount = 0;
            _lastResetUtc = DateTime.UtcNow;
        }

        /// <summary>Must be called while holding _probeGate.</summary>
        private HardwareMetricsSnapshot BuildMetricsSnapshot()
        {
            IHardware? cpu = FindHardware(HardwareType.Cpu);
            (IHardware? gpu, ActiveGpuType gpuType) = FindPreferredGpuWithFallback();
            IHardware? battery = FindHardware(HardwareType.Battery);
            GetSystemPowerStatus(out SystemPowerStatus powerStatus);

            double gpuMemoryUsedMb = ReadFirstValue(gpu, SensorType.SmallData, "GPU Memory Used", "D3D Dedicated Memory Used");
            double gpuMemoryTotalMb = ReadFirstValue(gpu, SensorType.SmallData, "GPU Memory Total", "D3D Dedicated Memory Total");
            var (memoryUsed, memoryTotal, memoryCommitted, pagefileTotal) = ReadPhysicalMemory();
            (int memTemp0, int memTemp1) = ReadMemoryTemperatures();
            int storageTemp = ReadStorageTemperature();

            var metrics = new HardwareMetricsSnapshot
            {
                CpuTemperature = Round(ReadFirstValue(cpu, SensorType.Temperature, "Core Average", "CPU Package", "Core")),
                CpuUsage = Round(ReadFirstValue(cpu, SensorType.Load, "CPU Total", "Total")),
                CpuPowerWatts = Round(ReadFirstValue(cpu, SensorType.Power, "CPU Package", "Package")),
                CpuClockMhz = Round(ReadAverage(cpu, SensorType.Clock, "Core")),
                GpuTemperature = Round(ReadFirstValue(gpu, SensorType.Temperature, "GPU Core", "Core")),
                GpuUsage = Round(ReadFirstValue(gpu, SensorType.Load, "GPU Core", "D3D 3D", "Core")),
                GpuPowerWatts = Round(ReadFirstValue(gpu, SensorType.Power, "GPU Package", "GPU Power", "Core")),
                GpuClockMhz = Round(ReadFirstValue(gpu, SensorType.Clock, "GPU Core", "Core")),
                GpuMemoryUsedGb = gpuMemoryUsedMb / 1024d,
                GpuMemoryTotalGb = gpuMemoryTotalMb / 1024d,
                ActiveGpuType = gpuType,
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

            return ApplyEngineSensorSource(
                ApplyCpuTemperatureSource(metrics, _ecCpuTemperature?.ReadCpuTemperature() ?? 0),
                _ecEngineMetrics?.ReadEngineMetrics() ?? EngineMetricsSnapshot.Empty);
        }

        /// <summary>
        /// CPU temperature source precedence: the CPU's own sensor wins when it answered
        /// (Tctl/Tdie is the number the CPU's thermal behaviour is defined by). When it is
        /// absent - the case for an unelevated app, which cannot open PawnIO, whose device
        /// ACL is System + Administrators - the embedded controller's reading replaces it,
        /// and the source is recorded so the UI can say where the number came from. Both
        /// absent stays 0/None, which the UI renders as "--".
        /// </summary>
        internal static HardwareMetricsSnapshot ApplyCpuTemperatureSource(
            HardwareMetricsSnapshot metrics,
            int ecCpuTemperature)
        {
            if (metrics.CpuTemperature > 0)
                return metrics with { CpuTemperatureSource = CpuTemperatureSource.CpuSensor };

            if (ecCpuTemperature > 0)
            {
                return metrics with
                {
                    CpuTemperature = ecCpuTemperature,
                    CpuTemperatureSource = CpuTemperatureSource.EcSensor
                };
            }

            return metrics with { CpuTemperatureSource = CpuTemperatureSource.None };
        }

        /// <summary>
        /// Engine sensor overlay at the producer: the kernel-side readings fill
        /// ONLY the fields the in-process probe left at 0 (zero is the snapshot's
        /// "unavailable" convention, and a live in-process reading always beats
        /// the engine's cache - the dev app runs elevated, opens the sensor
        /// drivers itself, and its numbers are the fresher ones). GPU
        /// power/clock are deliberately untouched: the GPU stays app-side (NVAPI
        /// decision), there is no engine reading to overlay. RAM speed is not a
        /// snapshot field (the memory card reads the system-info cache): the
        /// engine's SPD value is published there when the WMI prefetch delivered
        /// nothing, an idempotent 0-to-value transition that fires the card's
        /// repaint exactly once.
        /// </summary>
        internal static HardwareMetricsSnapshot ApplyEngineSensorSource(
            HardwareMetricsSnapshot metrics, EngineMetricsSnapshot engine)
        {
            if (engine.MemorySpeedMts > 0)
                Scripts.Misc.CachedSystemInfo.PublishRamSpeedIfMissing(engine.MemorySpeedMts);

            return metrics with
            {
                CpuPowerWatts = FillMissing(metrics.CpuPowerWatts, engine.CpuPowerWatts),
                CpuClockMhz = FillMissing(metrics.CpuClockMhz, engine.CpuClockMhz),
                MemoryTemperature0 = FillMissing(metrics.MemoryTemperature0, engine.MemoryTemperature0),
                MemoryTemperature1 = FillMissing(metrics.MemoryTemperature1, engine.MemoryTemperature1),
            };
        }

        private static int FillMissing(int own, int engine) => own > 0 ? own : engine;

        /// <summary>
        /// Snapshot with no LibreHardwareMonitor touch: battery state from
        /// kernel32 GetSystemPowerStatus, memory from GlobalMemoryStatusEx,
        /// CPU temperature from the engine's EC telemetry - all cheap,
        /// filter-driver-free sources. The LHM-only fields (CPU usage/clock,
        /// GPU, battery watts, DIMM/storage temperatures) carry the last
        /// completed probe's values while the producer is alive, and stay 0
        /// (UI renders "--") when the probe has never finished - or when it
        /// has WEDGED (<paramref name="producerStale"/>), where carrying the
        /// values is painting a frozen frame. This is what keeps the dashboard
        /// alive while a hardware probe hangs in a filter driver.
        /// </summary>
        private HardwareMetricsSnapshot BuildOsOnlySnapshot(
            HardwareMetricsSnapshot last, bool producerStale)
        {
            HardwareMetricsSnapshot carried = producerStale ? ClearStaleSensorFields(last) : last;

            GetSystemPowerStatus(out SystemPowerStatus powerStatus);
            (double memoryUsed, double memoryTotal, double memoryCommitted, double pagefileTotal) = ReadPhysicalMemory();

            HardwareMetricsSnapshot metrics = carried with
            {
                SystemMemoryUsedGb = memoryUsed,
                SystemMemoryTotalGb = memoryTotal,
                SystemMemoryCommittedGb = memoryCommitted,
                SystemMemoryPagefileTotalGb = pagefileTotal,
                HasBattery = powerStatus.BatteryFlag != 128 && powerStatus.BatteryLifePercent <= 100,
                BatteryPercent = powerStatus.BatteryLifePercent <= 100 ? powerStatus.BatteryLifePercent : 0,
                IsBatteryCharging = (powerStatus.BatteryFlag & 8) != 0,
                IsBatteryFullyCharged = powerStatus.BatteryLifePercent == 100 && powerStatus.ACLineStatus == 1,
                BatteryTimeRemainingMinutes = powerStatus.BatteryLifeTime > 0 ? powerStatus.BatteryLifeTime / 60 : 0
            };

            return ApplyEngineSensorSource(
                ApplyCpuTemperatureSource(metrics, _ecCpuTemperature?.ReadCpuTemperature() ?? 0),
                _ecEngineMetrics?.ReadEngineMetrics() ?? EngineMetricsSnapshot.Empty);
        }

        /// <summary>
        /// Zeroes every reading only the LHM probe can feed, for the wedged-
        /// producer path: a parked pass keeps the values in the last state,
        /// and repainting them is a statue. Zero is the snapshot's
        /// "unavailable" convention, so the UI renders "--"/skips the cell,
        /// and the fresh sources refill what they can beside it: the EC CPU
        /// temperature and the engine's Ring0 power/clock and SPD/DIMM values
        /// merge right back (ApplyCpuTemperatureSource /
        /// ApplyEngineSensorSource run after this), the OS-fed fields are
        /// rebuilt there anyway. Kept: ActiveGpuType (a selection, not a
        /// reading), the battery flags (OS state), and the EC-fed fields
        /// (fan speeds, battery health/temperature - the EC cache answers
        /// those fresh on its own cadence).
        /// </summary>
        internal static HardwareMetricsSnapshot ClearStaleSensorFields(HardwareMetricsSnapshot last) =>
            last with
            {
                CpuTemperature = 0,
                CpuTemperatureSource = CpuTemperatureSource.None,
                CpuUsage = 0,
                CpuPowerWatts = 0,
                CpuClockMhz = 0,
                GpuTemperature = 0,
                GpuUsage = 0,
                GpuPowerWatts = 0,
                GpuClockMhz = 0,
                GpuMemoryUsedGb = 0,
                GpuMemoryTotalGb = 0,
                MemoryTemperature0 = 0,
                MemoryTemperature1 = 0,
                StorageTemperatureCelsius = 0,
                BatteryPowerWatts = 0,
            };

        /// <summary>Must be called while holding _probeGate.</summary>
        private IReadOnlyList<HardwareSensorReading> CaptureSensorReadings()
        {
            if (_computer is null)
                return Array.Empty<HardwareSensorReading>();

            var readings = new List<HardwareSensorReading>();
            foreach (IHardware hardware in _computer.Hardware)
            {
                foreach (ISensor sensor in EnumerateSensors(hardware))
                {
                    if (sensor.Value.HasValue)
                    {
                        readings.Add(new HardwareSensorReading(
                            hardware.HardwareType,
                            sensor.SensorType,
                            sensor.Name,
                            sensor.Value.Value));
                    }
                }
            }

            return readings.ToArray();
        }

        /// <summary>Must be called while holding _probeGate.</summary>
        private void UpdateHardware()
        {
            // Reconcile the Computer with what the leases ask for. This was
            // the lease path's job under _sync; it lives here now because
            // Open/Close is driver I/O a filter driver can hold forever, and
            // no app thread may ever wait behind it.
            HardwareMonitoringCategory requested = _requestedCategories;
            if (requested != _configuredCategories)
            {
                _computer?.Close();
                _computer = null;
                _configuredCategories = requested;
                _lastUpdateUtc = DateTime.MinValue;
                _updateFailureCount = 0;
            }

            // If _computer is null but categories are requested, attempt to reopen.
            // This can happen after ResetComputer() releases stale handles, or after
            // a previous failure cleared the computer. We only reopen if enough time
            // has passed since the last failure to avoid tight loops on broken drivers.
            if (_computer is null && _configuredCategories != HardwareMonitoringCategory.None)
            {
                // Enforce cooldown after ResetComputer() — give the GPU driver time to
                // settle after display topology changes before probing with fresh handles.
                if (DateTime.UtcNow - _lastResetUtc < ResetCooldown)
                {
                    return;
                }

                // Rate-limit reopen attempts to avoid tight loops if the GPU driver is broken.
                if (_updateFailureCount > 3 &&
                    DateTime.UtcNow - _lastUpdateFailureUtc < TimeSpan.FromSeconds(10))
                {
                    return;
                }

                Computer? computer = null;
                try
                {
                    computer = new Computer
                    {
                        IsCpuEnabled = _configuredCategories.HasFlag(HardwareMonitoringCategory.Cpu),
                        IsMemoryEnabled = _configuredCategories.HasFlag(HardwareMonitoringCategory.Memory),
                        IsGpuEnabled = _configuredCategories.HasFlag(HardwareMonitoringCategory.Gpu),
                        IsBatteryEnabled = _configuredCategories.HasFlag(HardwareMonitoringCategory.Battery),
                        IsMotherboardEnabled = true,
                        IsStorageEnabled = true,
                        IsControllerEnabled = true
                    };

                    // Open on this pool thread under the gate: it probes drivers,
                    // takes 1-2 s on healthy machines, and can be held by a
                    // filter driver on guarded ones - the gate absorbs all of it.
                    // The stage breadcrumb names the enabled set on a slow open:
                    // the field 50 s gate on the LHM-fed fields must say which
                    // driver touch the guard holds, not guess.
                    Stopwatch openSw = Stopwatch.StartNew();
                    computer.Open();
                    _computer = computer;
                    if (openSw.ElapsedMilliseconds >= SlowProbeStageMs)
                    {
                        LogProbeSlow(
                            $"Computer.Open {openSw.ElapsedMilliseconds}ms " +
                            $"(cpu={_configuredCategories.HasFlag(HardwareMonitoringCategory.Cpu)}, " +
                            $"mem={_configuredCategories.HasFlag(HardwareMonitoringCategory.Memory)}, " +
                            $"gpu={_configuredCategories.HasFlag(HardwareMonitoringCategory.Gpu)}, " +
                            $"battery={_configuredCategories.HasFlag(HardwareMonitoringCategory.Battery)}, " +
                            "motherboard/storage/controller=true)");
                    }
                }
                catch (Exception ex)
                {
                    /*
                     * ExecutionEngineException from NVML can corrupt the process before
                     * this catch block executes. However, when it doesn't, we log and
                     * rate-limit retries so the app stays alive.
                     */
                    try { Serilog.Log.Warning(ex, "[LHM] UpdateHardware: failed to reopen Computer"); } catch { }
                    try { computer?.Close(); } catch { /* half-opened instance */ }
                    _updateFailureCount++;
                    _lastUpdateFailureUtc = DateTime.UtcNow;
                    return;
                }
            }

            if (_computer is null || DateTime.UtcNow - _lastUpdateUtc < TimeSpan.FromMilliseconds(250))
                return;

            try
            {
                foreach (IHardware hardware in _computer.Hardware)
                {
                    bool storage = hardware.HardwareType == HardwareType.Storage;

                    // Storage SMART on its own cadence (see StorageUpdateInterval):
                    // one 21 s SMART park behind the AV storage filter must not
                    // serialize the whole producer behind a quasi-static value.
                    if (storage && !StorageUpdateDue(DateTime.UtcNow, _lastStorageUpdateUtc))
                        continue;

                    Stopwatch devSw = Stopwatch.StartNew();
                    UpdateHardwareTree(hardware);

                    if (storage)
                        _lastStorageUpdateUtc = DateTime.UtcNow;

                    if (devSw.ElapsedMilliseconds >= SlowProbeStageMs)
                        LogProbeSlow($"device update {hardware.HardwareType} \"{hardware.Name}\" {devSw.ElapsedMilliseconds}ms");
                }

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
                 * thread from taking down the process, then clear the computer so the next
                 * call to UpdateHardware() will attempt a reopen with fresh handles.
                 */
                _updateFailureCount++;
                _lastUpdateFailureUtc = DateTime.UtcNow;

                try { Serilog.Log.Warning(ex, "[LHM] UpdateHardware failed (failure #{Count}) — clearing Computer for next refresh", _updateFailureCount); } catch { /* Serilog may not be ready */ }

                // Close and clear — the next UpdateHardware() call will handle reopen.
                _computer?.Close();
                _computer = null;
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

        /// <summary>
        /// Finds the best GPU that has valid sensors.
        /// Priority: NVIDIA → AMD → Intel. If a GPU is enumerated but returns
        /// zero values (e.g., dGPU disabled by MUX switch or PnP hotswap),
        /// falls through to the next candidate.
        /// </summary>
        private (IHardware? hardware, ActiveGpuType type) FindPreferredGpuWithFallback()
        {
            if (_computer is null)
                return (null, ActiveGpuType.None);

            // Try NVIDIA first
            IHardware? nvidia = _computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.GpuNvidia);
            if (nvidia is not null && HasValidSensors(nvidia))
                return (nvidia, ActiveGpuType.Nvidia);

            // Try AMD second (covers both dGPU Radeon RX and iGPU Radeon 610M)
            IHardware? amd = _computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.GpuAmd);
            if (amd is not null && HasValidSensors(amd))
                return (amd, ActiveGpuType.Amd);

            // Try Intel third
            IHardware? intel = _computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.GpuIntel);
            if (intel is not null && HasValidSensors(intel))
                return (intel, ActiveGpuType.Intel);

            // Fallback: return whatever GPU exists even without valid sensors,
            // so the UI still shows the hardware name. Prefer by same priority.
            if (nvidia is not null)
                return (nvidia, ActiveGpuType.Nvidia);
            if (amd is not null)
                return (amd, ActiveGpuType.Amd);
            if (intel is not null)
                return (intel, ActiveGpuType.Intel);

            return (null, ActiveGpuType.None);
        }

        /// <summary>
        /// Returns true if the hardware has a valid temperature sensor AND a valid load sensor.
        /// Used to detect GPUs that are enumerated but have no active driver.
        /// A PnP-disabled dGPU still reports temperature (voltage regulation stays active)
        /// but returns 0 for load/power/clock — so we require both to consider a GPU "active".
        /// </summary>
        private static bool HasValidSensors(IHardware hardware)
        {
            bool hasTemperature = false;
            bool hasLoad = false;

            foreach (ISensor sensor in EnumerateSensors(hardware))
            {
                if (sensor.Value.HasValue && sensor.Value.Value > 0)
                {
                    if (sensor.SensorType == SensorType.Temperature)
                        hasTemperature = true;
                    if (sensor.SensorType == SensorType.Load)
                        hasLoad = true;
                }

                if (hasTemperature && hasLoad)
                    return true;
            }

            return false;
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

                _disposed = true;
                _leases.Clear();
            }

            _stateCache.Dispose();

            // Close is LHM driver I/O and the gate owner may be parked in a
            // filter driver forever; app exit must not wait on it. Bounded:
            // process exit releases the handles regardless.
            if (Monitor.TryEnter(_probeGate, ShutdownGateBudget))
            {
                try
                {
                    _computer?.Close();
                    _computer = null;
                }
                finally
                {
                    Monitor.Exit(_probeGate);
                }
            }
            else
            {
                try { Serilog.Log.Warning("[LHM] Dispose: hardware probe is parked, skipping Computer.Close at shutdown"); } catch { }
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
