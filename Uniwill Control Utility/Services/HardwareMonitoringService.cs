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

        /// <summary>Bit the slow group uses for its always-on system devices
        /// (motherboard/storage/controller) - the public lease enum has no flag
        /// for them. Group-internal mask bit only, never leaves the service.</summary>
        private const HardwareMonitoringCategory SystemDevicesMask = (HardwareMonitoringCategory)16;

        /// <summary>What the FAST group serves: the subsystems feeding the
        /// fields the user watches (CPU usage, GPU temp/usage/clock/power,
        /// battery watts). Measured open cost on the field box, standalone and
        /// unelevated: cpu 97 ms, gpu 1123 ms, battery 33 ms - the visible
        /// metrics are a ~1.2 s open, and the split is what keeps the 21 s
        /// SMART park and the cold-open cost out of their way.</summary>
        internal const HardwareMonitoringCategory FastCategories =
            HardwareMonitoringCategory.Cpu | HardwareMonitoringCategory.Gpu | HardwareMonitoringCategory.Battery;

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
        internal const long SlowProbeStageMs = 2000;
        internal const long SlowProbePassMs = 5000;
        private static readonly TimeSpan ProbeSlowLogDebounce = TimeSpan.FromSeconds(60);

        /// <summary>Breadcrumb sink: severity Warning (the app log's field
        /// level), prefixed with the group's name. The debounce clock is per
        /// group, so one group parked does not hide the other's breadcrumb.</summary>
        private void LogProbeSlow(string message)
        {
            try { Serilog.Log.Warning("[LHM] probe: " + message); } catch { }
        }

        /// <summary>
        /// Lease bookkeeping only (leases, lease ids, disposed flag). Never
        /// held across hardware I/O, so Acquire/Release from a UI thread are
        /// bounded by dictionary work.
        /// </summary>
        private readonly object _sync = new();

        /// <summary>
        /// The two probe groups. One LibreHardwareMonitor Computer serializes
        /// EVERY subsystem behind ONE gate, which gave the whole producer ONE
        /// age: the measured 21 s SMART park behind the AV storage filter
        /// (field breadcrumb 2026-10-29, `device update Storage 21044ms`)
        /// blanked the CPU/GPU fields on every consumer, and the in-app open
        /// cost (64.6 s first launch of a fresh pack, 22.3 s the next, 1.4 s
        /// for the identical config in a standalone harness - the security
        /// product on the app process, not the subsystems) delayed the
        /// CPU/GPU cards by the whole sum. The split puts the subsystems that
        /// feed the watched fields (CPU usage, GPU temp/usage/clock/power,
        /// battery watts: ~1.2 s to open, measured) in the FAST group with its
        /// own gate, cadence and age, and the slow system touches (DIMM SPD,
        /// SMART per drive, SuperIO motherboard/controller) in the SLOW group.
        /// Each group serves and goes stale on its own, so a parked SMART read
        /// is now two "--" cells instead of the whole dashboard.
        ///
        /// Each group owns the exclusive right to touch ITS Computer
        /// (Open/Update/Close/sensor reads). A storage DeviceIoControl inside
        /// an update pass can be held indefinitely by a filter driver
        /// (field-proven on an AV-guarded machine: the disk open hung, the
        /// pass held the update lock, the UI thread's Acquire waited on it,
        /// the whole app froze with the dashboard up). Pool threads may block
        /// on a group gate; no app thread ever does - every consumer reads
        /// the published state and degrades (see the freshness rule).
        /// </summary>
        private readonly ProbeGroup _fast;
        private readonly ProbeGroup _slow;

        private readonly StaleWhileRevalidateCache<HardwareMonitoringState> _fastCache;
        private readonly StaleWhileRevalidateCache<HardwareMonitoringState> _slowCache;

        private readonly Dictionary<long, HardwareMonitoringCategory> _leases = new();

        /// <summary>Category union the leases ask for, published to both groups
        /// (UI writes under _sync, group gates read it volatile).</summary>
        private volatile HardwareMonitoringCategory _requestedCategories;

        /// <summary>EC CPU temperature source; null = no engine (dev/test composition).</summary>
        private readonly IEcCpuTemperatureSource? _ecCpuTemperature;

        /// <summary>Engine kernel-side sensor source (power/clocks/SPD); null = no
        /// engine. Merged into every produced snapshot at the zero fields (see ctor).</summary>
        private readonly IEcEngineMetricsSource? _ecEngineMetrics;

        /// <summary>Lease ids: monotonic under _sync.</summary>
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

        /// <summary>How long a group waits after ResetComputer() before
        /// reopening its Computer (gives the GPU driver time to settle).
        /// The per-group clocks (failure count, last reset) live in ProbeGroup.</summary>
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

            _fast = new ProbeGroup("fast", systemDevices: false, this);
            _slow = new ProbeGroup("slow", systemDevices: true, this);

            _fastCache = NewGroupCache(_fast, "fast");
            _slowCache = NewGroupCache(_slow, "slow");
        }

        /// <summary>
        /// A group's stale-while-revalidate cache: the gate-serialized pass
        /// runs on the pool, and the cache's single-flight contract keeps at
        /// most one pass queued PER GROUP - a hung driver parks one group's
        /// pass while the other group keeps answering, which is the whole
        /// point of the split.
        /// </summary>
        private static StaleWhileRevalidateCache<HardwareMonitoringState> NewGroupCache(ProbeGroup group, string name) =>
            new(
                HardwareMonitoringState.Empty,
                SnapshotMaxAge,
                _ => Task.Run(group.Probe),
                onRefreshError: ex =>
                {
                    try { Serilog.Log.Warning(ex, $"[LHM] Background snapshot refresh failed ({name} group)"); }
                    catch { /* Serilog may not be ready */ }
                });

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
                InvalidateCaches();

            KickRefreshes();

            // The probe runs on the pool under the group's gate; the startup
            // worker waits for the FAST group's first snapshot with a bounded
            // budget - that is the group feeding CPU usage and the GPU row. A
            // hung driver parks that group's gate, not the splash: the
            // dashboard fills from the OS/EC/engine sources and the group's
            // fields land when its pass ever returns.
            try
            {
                if (!_fastCache.RefreshAsync().Wait(StartupSnapshotBudget))
                {
                    try { Serilog.Log.Warning("[LHM] First fast-group snapshot not ready within the startup budget; the probe is hung or slow, metrics fill as it completes"); } catch { }
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
            // fire-and-forget; each group serializes on its own gate on the pool.
            if (categoriesChanged)
                InvalidateCaches();

            KickRefreshes();
            return lease;
        }

        /// <summary>
        /// The merged snapshot every consumer reads (dashboard cards, tray
        /// flyout, FanControl cards, the RTSS OSD). Each group contributes the
        /// fields its own subsystems feed, on its OWN freshness: a group past
        /// the staleness budget has no readings (its fields render "--", never
        /// a frozen frame), and the fresh sources - OS, EC, engine - refill
        /// their fields beside them in <see cref="ApplyOsSources"/>. That is
        /// what makes a parked SMART read two cells instead of a statue of a
        /// dashboard.
        /// </summary>
        public HardwareMetricsSnapshot ReadSnapshot()
        {
            HardwareMonitoringState fast = _fastCache.Read();
            HardwareMonitoringState slow = _slowCache.Read();

            return ApplyOsSources(MergeGroupSnapshots(
                fast.Metrics, ProducerIsStale(_fastCache.Age),
                slow.Metrics, ProducerIsStale(_slowCache.Age)));
        }

        /// <summary>
        /// The group merge, split by ownership: the fast group's fields (CPU
        /// usage/temperature/power/clock, the GPU row, battery watts) and the
        /// slow group's (DIMM temperatures, storage temperature) clear
        /// independently. A group that has never completed a pass carries the
        /// zeros that "unavailable" already means.
        /// </summary>
        internal static HardwareMetricsSnapshot MergeGroupSnapshots(
            HardwareMetricsSnapshot fast, bool fastStale,
            HardwareMetricsSnapshot slow, bool slowStale)
        {
            HardwareMetricsSnapshot merged = fastStale ? ClearFastStaleFields(fast) : fast;
            if (slowStale)
                return ClearSlowStaleFields(merged);

            return merged with
            {
                MemoryTemperature0 = slow.MemoryTemperature0,
                MemoryTemperature1 = slow.MemoryTemperature1,
                StorageTemperatureCelsius = slow.StorageTemperatureCelsius,
            };
        }

        public float ReadSensor(HardwareType hardwareType, SensorType sensorType, string sensorName)
        {
            // Route the read to the group that owns the hardware and apply
            // THAT group's freshness, and harsher consumers: the adaptive fan
            // loop feeds on these readings, and a frozen fan RPM is worse than
            // the 0 a missing reading already means.
            (ProbeGroup group, StaleWhileRevalidateCache<HardwareMonitoringState> cache) =
                GroupFor(hardwareType);

            if (ProducerIsStale(cache.Age))
                return 0;

            HardwareMonitoringState state = cache.Read();
            if (ReferenceEquals(state, HardwareMonitoringState.Empty))
                state = group.LastState;

            return state.ReadSensor(hardwareType, sensorType, sensorName);
        }

        /// <summary>Which group owns a hardware type's sensors: CPU/GPU/battery
        /// in the fast group, everything else (motherboard and controller
        /// SuperIO fans, storage, DIMM) in the slow group.</summary>
        internal static bool IsFastHardware(HardwareType type) =>
            type is HardwareType.Cpu
                or HardwareType.GpuNvidia
                or HardwareType.GpuAmd
                or HardwareType.GpuIntel
                or HardwareType.Battery;

        private (ProbeGroup Group, StaleWhileRevalidateCache<HardwareMonitoringState> Cache) GroupFor(HardwareType type) =>
            IsFastHardware(type) ? (_fast, _fastCache) : (_slow, _slowCache);

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

            // Both groups release their driver handles (the GPU ones are the
            // reason for the reset; the slow group's SuperIO/SMART handles are
            // cheap to recreate) and the next read renews both refreshes.
            _fast.RequestReset();
            _slow.RequestReset();
            InvalidateCaches();
            KickRefreshes();
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
                InvalidateCaches();

            KickRefreshes();
        }

        /// <summary>
        /// Must be called while holding _sync. Bookkeeping only: recompute the
        /// union of the lease categories and publish it to BOTH groups - each
        /// group derives its own enablement mask from it (the fast group drops
        /// the lease it does not serve, the slow group keeps its always-on
        /// system devices while any lease is live). Rebuilding a Computer is
        /// the group's probe job (ProbeGroup.UpdateHardware): the old version
        /// closed and reopened drivers here, under the same lock the UI
        /// thread's Acquire took, which is how one hung storage open froze the
        /// whole app.
        /// </summary>
        private bool RequestRequestedCategoriesLocked()
        {
            HardwareMonitoringCategory requested = HardwareMonitoringCategory.None;
            foreach (HardwareMonitoringCategory lease in _leases.Values)
                requested |= lease;

            if (requested == _requestedCategories)
                return false;

            _requestedCategories = requested;
            _fast.RequestCategories(requested);
            _slow.RequestCategories(requested);
            return true;
        }

        /// <summary>Drop both groups' published snapshots (category set
        /// changed, driver handles invalidated): the next read re-serves the
        /// fresh-source view and renews both refreshes.</summary>
        private void InvalidateCaches()
        {
            _fastCache.Invalidate();
            _slowCache.Invalidate();
        }

        /// <summary>
        /// Kick both groups' refresh, FAST FIRST: the fast group's Open() is
        /// the one that fills CPU usage and the GPU row, so it wins the
        /// driver/security-product race at startup; the slow group's open
        /// starts when the fast pass settles. A parked pass delays only the
        /// fields its own group feeds.
        /// </summary>
        private void KickRefreshes()
        {
            Task fast = _fastCache.RefreshAsync();
            if (fast.IsCompleted)
            {
                SafeRefresh(_slowCache);
                return;
            }

            _ = fast.ContinueWith(
                _ => SafeRefresh(_slowCache),
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default);
        }

        private static void SafeRefresh(StaleWhileRevalidateCache<HardwareMonitoringState> cache)
        {
            try { _ = cache.RefreshAsync(); }
            catch (ObjectDisposedException) { /* shutdown already started */ }
            catch { /* a metrics kick is never a failure */ }
        }

        /// <summary>
        /// The fast group's contribution: every field the CPU/GPU/battery
        /// subsystems feed. Field ownership is the split's contract - this
        /// builder must never read DIMM/storage fields, and
        /// <see cref="BuildSlowSnapshot"/> must never touch the GPU row, so
        /// each group's staleness can clear exactly its own cells.
        /// Must be called while holding the group's gate.
        /// </summary>
        internal static HardwareMetricsSnapshot BuildFastSnapshot(Computer? computer)
        {
            IHardware? cpu = FindHardware(computer, HardwareType.Cpu);
            (IHardware? gpu, ActiveGpuType gpuType) = FindPreferredGpuWithFallback(computer);
            IHardware? battery = FindHardware(computer, HardwareType.Battery);

            double gpuMemoryUsedMb = ReadFirstValue(gpu, SensorType.SmallData, "GPU Memory Used", "D3D Dedicated Memory Used");
            double gpuMemoryTotalMb = ReadFirstValue(gpu, SensorType.SmallData, "GPU Memory Total", "D3D Dedicated Memory Total");

            return new HardwareMetricsSnapshot
            {
                CpuTemperature = Round(ReadFirstValue(cpu, SensorType.Temperature, "Core Average", "CPU Package", "Core")),
                CpuUsage = Round(ReadFirstValue(cpu, SensorType.Load, "CPU Total", "Total")),
                CpuPowerWatts = Round(ReadFirstValue(cpu, SensorType.Power, "CPU Package", "Package")),
                CpuClockMhz = CoreClockMhzFrom(cpu),
                GpuTemperature = Round(ReadFirstValue(gpu, SensorType.Temperature, "GPU Core", "Core")),
                GpuUsage = Round(ReadFirstValue(gpu, SensorType.Load, "GPU Core", "D3D 3D", "Core")),
                GpuPowerWatts = PlausibleGpuPowerWatts(
                    ReadFirstValue(gpu, SensorType.Power, "GPU Package", "GPU Power", "Core"), gpuType, gpu),
                GpuClockMhz = Round(ReadFirstValue(gpu, SensorType.Clock, "GPU Core", "Core")),
                GpuMemoryUsedGb = gpuMemoryUsedMb / 1024d,
                GpuMemoryTotalGb = gpuMemoryTotalMb / 1024d,
                ActiveGpuType = gpuType,
                BatteryPowerWatts = Math.Abs(ReadFirstValue(battery, SensorType.Power, "Charge Rate", "Discharge Rate", "Power"))
            };
        }

        /// <summary>
        /// The slow group's contribution: DIMM temperatures (SPD/SMBIOS) and
        /// the SMART storage temperature - the touches the storage-filter and
        /// cold-open parks live in, and the reason they are not in the fast
        /// builder. Must be called while holding the group's gate.
        /// </summary>
        internal static HardwareMetricsSnapshot BuildSlowSnapshot(Computer? computer)
        {
            (int memTemp0, int memTemp1) = ReadMemoryTemperatures(computer);

            return new HardwareMetricsSnapshot
            {
                MemoryTemperature0 = memTemp0,
                MemoryTemperature1 = memTemp1,
                StorageTemperatureCelsius = ReadStorageTemperature(computer)
            };
        }

        /// <summary>
        /// The sources that never wait on a filter driver, applied to every
        /// served snapshot: memory from GlobalMemoryStatusEx, battery state
        /// from kernel32 GetSystemPowerStatus, the EC's CPU temperature, and
        /// the engine's Ring0 power/clock and SPD/DIMM readings. Applied last,
        /// so it refills the cells a stale group just cleared (the engine
        /// overlay fills only zeros) and the OS-fed numbers are always current
        /// - which is what keeps the dashboard live while a group is parked.
        /// </summary>
        private HardwareMetricsSnapshot ApplyOsSources(HardwareMetricsSnapshot metrics)
        {
            GetSystemPowerStatus(out SystemPowerStatus powerStatus);
            (double memoryUsed, double memoryTotal, double memoryCommitted, double pagefileTotal) = ReadPhysicalMemory();

            HardwareMetricsSnapshot filled = metrics with
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
                ApplyCpuTemperatureSource(filled, _ecCpuTemperature?.ReadCpuTemperature() ?? 0),
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
        /// Ceiling for a plausible laptop GPU draw.
        ///
        /// The value comes from LibreHardwareMonitor, whose vendor back-ends can
        /// emit garbage: its Intel iGPU "GPU Power" is a RAPL energy-counter
        /// derivative with an unchecked 32-bit subtraction fed 0 by a failed
        /// PawnIO read, and the NVIDIA path produced the same class of sample on
        /// the field 5080 laptop (field, 2026-11-02: 1.9M W alternating with
        /// sane readings on battery, and 190286 W on the Intel iGPU). Nothing a
        /// laptop GPU can draw, so it is dropped - 0 renders as "--" everywhere -
        /// and logged. The guard is deliberately vendor-blind: the sensor, not
        /// the vendor, is what lies.
        /// </summary>
        internal const int MaxPlausibleGpuWatts = 600;

        private static DateTime _lastGpuPowerWarnUtc = DateTime.MinValue;

        /// <summary>The single GPU-power gate: implausible readings become 0
        /// ("unavailable") instead of a number no laptop GPU can produce.</summary>
        internal static int PlausibleGpuPowerWatts(double watts, ActiveGpuType gpuType, IHardware? gpu)
        {
            if (watts <= MaxPlausibleGpuWatts)
                return Round(watts);

            DateTime now = DateTime.UtcNow;
            if (now - _lastGpuPowerWarnUtc >= TimeSpan.FromMinutes(1))
            {
                _lastGpuPowerWarnUtc = now;
                try
                {
                    Serilog.Log.Warning(
                        $"[LHM] Rejected GPU power {watts} W from {gpuType} '{gpu?.Name}' (ceiling {MaxPlausibleGpuWatts} W) - no laptop GPU draws that; the sensor produced a bogus sample");
                }
                catch { /* Serilog may not be ready */ }
            }

            return 0;
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
        /// Zeroes every reading only the FAST group's subsystems feed, for the
        /// wedged-fast-group path: a parked pass keeps the values in the last
        /// state, and repainting them is a statue. Zero is the snapshot's
        /// "unavailable" convention, so the UI renders "--"/skips the cell,
        /// and the fresh sources refill what they can beside it: the EC CPU
        /// temperature and the engine's Ring0 power/clock merge right back in
        /// ApplyOsSources. Kept: ActiveGpuType (a selection, not a reading)
        /// and the EC-fed fields (fan speeds, battery health/temperature - the
        /// EC cache answers those fresh on its own cadence).
        /// </summary>
        internal static HardwareMetricsSnapshot ClearFastStaleFields(HardwareMetricsSnapshot last) =>
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
                BatteryPowerWatts = 0,
            };

        /// <summary>
        /// Zeroes the readings only the SLOW group's subsystems feed (DIMM
        /// temperatures from SPD/SMBIOS, the SMART storage temperature).
        /// Independent of the fast group on purpose: the measured 21 s SMART
        /// park parks THIS group, and the two cells it feeds are what go to
        /// "--" - the CPU usage and GPU row keep streaming from the fast
        /// group beside them. The engine's live DIMM readings merge right back
        /// (ApplyEngineSensorSource fills only zeros), the storage card has no
        /// second source and stays "--" until the group answers.
        /// </summary>
        internal static HardwareMetricsSnapshot ClearSlowStaleFields(HardwareMetricsSnapshot last) =>
            last with
            {
                MemoryTemperature0 = 0,
                MemoryTemperature1 = 0,
                StorageTemperatureCelsius = 0,
            };

        /// <summary>
        /// One LibreHardwareMonitor Computer with its own gate, its own
        /// open/update cadence, its own deferred SMART clock, its own reset
        /// cooldown and its own completed-pass clock (the age the freshness
        /// rule gates on). The service pairs two of these - the fast group
        /// (CPU/GPU/battery, the watched fields) and the slow group (DIMM SPD,
        /// SMART, SuperIO system devices) - and merges what they publish, so a
        /// driver park is confined to the fields its own group feeds.
        ///
        /// LHM's kernel side (WinRing0/PawnIO) is process-global, so a group's
        /// Close() releases the shared handle for the whole process; group
        /// rebuilds are therefore driven by the group's OWN effective mask
        /// (a lease change the group does not serve never rebuilds it), and
        /// the persistent lease keeps the visible set open for the session.
        /// </summary>
        private sealed class ProbeGroup
        {
            private readonly string _name;

            /// <summary>True for the system-device group (motherboard/storage/
            /// controller always on, plus DIMM when leased); false for the
            /// CPU/GPU/battery group.</summary>
            private readonly bool _systemDevices;

            private readonly HardwareMonitoringService _owner;
            private readonly object _gate = new();

            /// <summary>Lease union published by the lease paths (UI writes
            /// under _sync, the gate owner reads it volatile).</summary>
            private volatile HardwareMonitoringCategory _requested;

            /// <summary>The enablement mask the current Computer was built for
            /// (gate-owned). A change here is the ONLY trigger for a rebuild.</summary>
            private HardwareMonitoringCategory _configured;

            private Computer? _computer;
            private DateTime _lastUpdateUtc = DateTime.MinValue;

            /// <summary>Storage SMART cadence clock (see StorageUpdateInterval);
            /// starts at construction so the first update is deferred.</summary>
            private DateTime _lastStorageUpdateUtc = DateTime.UtcNow;

            /// <summary>Consecutive update failures (reset on success) and when
            /// the last one happened: rate-limits reopen attempts on a broken
            /// driver so the pass never becomes a tight loop.</summary>
            private int _updateFailureCount;
            private DateTime _lastUpdateFailureUtc = DateTime.MinValue;

            /// <summary>When this group's Close() ran (settle window for the
            /// GPU driver before a reopen).</summary>
            private DateTime _lastResetUtc = DateTime.MinValue;

            /// <summary>A ResetComputer() this group's gate owner applies on its next pass.</summary>
            private int _resetPending;

            /// <summary>Per-group breadcrumb debounce clock, so one group
            /// parked never hides the other group's breadcrumb.</summary>
            private DateTime _lastSlowLogUtc = DateTime.MinValue;

            /// <summary>Last published state for this group (volatile).</summary>
            private HardwareMonitoringState _lastState = HardwareMonitoringState.Empty;

            public ProbeGroup(string name, bool systemDevices, HardwareMonitoringService owner)
            {
                _name = name;
                _systemDevices = systemDevices;
                _owner = owner;
            }

            /// <summary>The last snapshot this group published, for the read
            /// path when its cache never completed.</summary>
            public HardwareMonitoringState LastState => Volatile.Read(ref _lastState);

            /// <summary>Publish the lease union; the gate owner reconciles the
            /// Computer with it on the next pass (no driver I/O here).</summary>
            public void RequestCategories(HardwareMonitoringCategory requested) =>
                _requested = requested;

            /// <summary>Defer a Close() to this group's gate owner: Close is
            /// driver I/O a filter driver can hold, and no app thread may ever
            /// wait behind it.</summary>
            public void RequestReset() => Interlocked.Exchange(ref _resetPending, 1);

            /// <summary>What the group's Computer is built for, derived from
            /// the lease union: the fast group takes only the CPU/GPU/battery
            /// flags, the slow group its system-device bit plus DIMM when
            /// leased. The system devices ride whenever ANY lease is live
            /// (parity with the single instance: FanControl's SuperIO fans are
            /// not something a category lease asks for).</summary>
            private HardwareMonitoringCategory EffectiveMask(HardwareMonitoringCategory requested)
            {
                if (!_systemDevices)
                    return requested & FastCategories;

                if (requested == HardwareMonitoringCategory.None)
                    return HardwareMonitoringCategory.None;

                HardwareMonitoringCategory mask = SystemDevicesMask;
                if (requested.HasFlag(HardwareMonitoringCategory.Memory))
                    mask |= HardwareMonitoringCategory.Memory;

                return mask;
            }

            /// <summary>
            /// The group's probe pass: one serialized touch of ITS Computer, on
            /// a pool thread (its cache's Task.Run). Blocking on the gate here
            /// is safe and is the point: the cache's single-flight contract
            /// keeps at most one pass queued for this group, a hung driver
            /// parks only that pass, and every app-thread consumer keeps
            /// reading this group's last state through the merge.
            /// </summary>
            public HardwareMonitoringState Probe()
            {
                Stopwatch passSw = Stopwatch.StartNew();
                Monitor.Enter(_gate);
                try
                {
                    ApplyPendingReset();
                    UpdateHardware();

                    Stopwatch buildSw = Stopwatch.StartNew();
                    HardwareMetricsSnapshot partial = _systemDevices
                        ? BuildSlowSnapshot(_computer)
                        : BuildFastSnapshot(_computer);
                    var state = new HardwareMonitoringState(partial, CaptureSensorReadings());
                    buildSw.Stop();

                    Volatile.Write(ref _lastState, state);

                    if (passSw.ElapsedMilliseconds >= SlowProbePassMs)
                        LogSlow($"full pass {passSw.ElapsedMilliseconds}ms (build+capture {buildSw.ElapsedMilliseconds}ms)");

                    return state;
                }
                finally
                {
                    Monitor.Exit(_gate);
                }
            }

            /// <summary>Gate-owned: performs the Close() that ResetComputer()
            /// deferred, then starts the settle cooldown from this moment (the
            /// driver gets its window after the handles are actually released).
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

            /// <summary>Breadcrumb for this group, debounced on its own
            /// clock: a persistently slow chain stays one line per minute.</summary>
            private void LogSlow(string message)
            {
                DateTime now = DateTime.UtcNow;
                if (now - _lastSlowLogUtc < ProbeSlowLogDebounce)
                    return;

                _lastSlowLogUtc = now;
                _owner.LogProbeSlow($"[{_name}] {message}");
            }

            /// <summary>Shutdown: close this group's Computer if the gate is
            /// free; a probe parked in a filter driver forever must not block
            /// app exit (process exit releases the handles regardless).</summary>
            public void Close(TimeSpan budget)
            {
                if (Monitor.TryEnter(_gate, budget))
                {
                    try
                    {
                        _computer?.Close();
                        _computer = null;
                    }
                    finally
                    {
                        Monitor.Exit(_gate);
                    }
                }
                else
                {
                    try { Serilog.Log.Warning($"[LHM] Dispose: {_name} group probe is parked, skipping Computer.Close at shutdown"); } catch { }
                }
            }

            /// <summary>Must be called while holding this group's gate.</summary>
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
            // Reconcile the Computer with what the leases ask for, through
            // THIS group's own mask: a lease change the group does not serve
            // must never rebuild it (LHM's kernel side is process-global, so
            // a rebuild also releases the shared handle the other group is
            // using). This was the lease path's job under _sync; it lives here
            // now because Open/Close is driver I/O a filter driver can hold
            // forever, and no app thread may ever wait behind it.
            HardwareMonitoringCategory mask = EffectiveMask(_requested);
            if (mask != _configured)
            {
                _computer?.Close();
                _computer = null;
                _configured = mask;
                _lastUpdateUtc = DateTime.MinValue;
                _updateFailureCount = 0;
            }

            // If _computer is null but the mask asks for devices, attempt to
            // reopen. This can happen after ResetComputer() releases stale
            // handles, or after a previous failure cleared the computer. We
            // only reopen if enough time has passed since the last failure to
            // avoid tight loops on broken drivers.
            if (_computer is null && _configured != HardwareMonitoringCategory.None)
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
                        IsCpuEnabled = _configured.HasFlag(HardwareMonitoringCategory.Cpu),
                        IsMemoryEnabled = _configured.HasFlag(HardwareMonitoringCategory.Memory),
                        IsGpuEnabled = _configured.HasFlag(HardwareMonitoringCategory.Gpu),
                        IsBatteryEnabled = _configured.HasFlag(HardwareMonitoringCategory.Battery),
                        IsMotherboardEnabled = _configured.HasFlag(SystemDevicesMask),
                        IsStorageEnabled = _configured.HasFlag(SystemDevicesMask),
                        IsControllerEnabled = _configured.HasFlag(SystemDevicesMask)
                    };

                    // Open on this pool thread under the group's gate: it
                    // probes drivers, takes 1-2 s on healthy machines, and can
                    // be held by a filter driver on guarded ones - the gate
                    // absorbs all of it, and the split keeps it out of the
                    // other group's way. The stage breadcrumb names the
                    // enabled set on a slow open, so the field "metrics filled
                    // late" report says WHICH group's touch the guard holds
                    // (measured: 64619 ms on a fresh pack's first launch,
                    // 22256 ms the next, 1458 ms for the same set in a
                    // standalone process - the guard on the app process).
                    Stopwatch openSw = Stopwatch.StartNew();
                    computer.Open();
                    _computer = computer;
                    if (openSw.ElapsedMilliseconds >= SlowProbeStageMs)
                    {
                        LogSlow(
                            $"Computer.Open {openSw.ElapsedMilliseconds}ms " +
                            $"(cpu={_configured.HasFlag(HardwareMonitoringCategory.Cpu)}, " +
                            $"mem={_configured.HasFlag(HardwareMonitoringCategory.Memory)}, " +
                            $"gpu={_configured.HasFlag(HardwareMonitoringCategory.Gpu)}, " +
                            $"battery={_configured.HasFlag(HardwareMonitoringCategory.Battery)}, " +
                            $"systemDevices={_configured.HasFlag(SystemDevicesMask)})");
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
                    // Storage SMART exists only in the system-device group.
                    bool storage = _systemDevices && hardware.HardwareType == HardwareType.Storage;

                    // Storage SMART on its own cadence (see StorageUpdateInterval):
                    // the field breadcrumb measured the park - 21 s of SMART
                    // DeviceIoControl behind the AV storage filter. Inside this
                    // group's gate it serializes the group's own fields (DIMM
                    // temperatures, storage temperature: both quasi-static), and
                    // the split is what keeps it off the CPU/GPU fields.
                    if (storage && !StorageUpdateDue(DateTime.UtcNow, _lastStorageUpdateUtc))
                        continue;

                    Stopwatch devSw = Stopwatch.StartNew();
                    UpdateHardwareTree(hardware);

                    if (storage)
                        _lastStorageUpdateUtc = DateTime.UtcNow;

                    if (devSw.ElapsedMilliseconds >= SlowProbeStageMs)
                        LogSlow($"device update {hardware.HardwareType} \"{hardware.Name}\" {devSw.ElapsedMilliseconds}ms");
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
        }

        private static IHardware? FindHardware(Computer? computer, HardwareType type) =>
            computer?.Hardware.FirstOrDefault(hardware => hardware.HardwareType == type);

        /// <summary>
        /// Finds the best GPU that has valid sensors.
        /// Priority: NVIDIA → AMD → Intel. If a GPU is enumerated but returns
        /// zero values (e.g., dGPU disabled by MUX switch or PnP hotswap),
        /// falls through to the next candidate.
        /// </summary>
        private static (IHardware? hardware, ActiveGpuType type) FindPreferredGpuWithFallback(Computer? computer)
        {
            if (computer is null)
                return (null, ActiveGpuType.None);

            // Try NVIDIA first
            IHardware? nvidia = computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.GpuNvidia);
            if (nvidia is not null && HasValidSensors(nvidia))
                return (nvidia, ActiveGpuType.Nvidia);

            // Try AMD second (covers both dGPU Radeon RX and iGPU Radeon 610M)
            IHardware? amd = computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.GpuAmd);
            if (amd is not null && HasValidSensors(amd))
                return (amd, ActiveGpuType.Amd);

            // Try Intel third
            IHardware? intel = computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.GpuIntel);
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

        /// <summary>
        /// Average CPU core clock, MHz, 0 = unavailable. The engine probe's
        /// field-pinned rules (LhmSensorProbe.Extract), applied to the app's
        /// own tree: AMD's "Cores (Average)" aggregate first, else the mean of
        /// the per-core family - "Core #N" (AMD), "P-Core #N"/"E-Core #N"
        /// (Intel hybrid, field 2026-11-01 Ultra 9 275HX). The old
        /// ReadAverage(cpu, Clock, "Core") Contains-matched the "(Effective)"
        /// utilisation-weighted pseudo-clocks AND the iGPU sub-hardware's
        /// "GPU Core" clock: the dev box displayed 2929 MHz while the true
        /// aggregate was 5300 (and Intel's idle iGPU clock diluted every
        /// reading there too). Direct node sensors only, and the
        /// engine's fill-zeros overlay is untouched: when this answers 0
        /// (no Ring0), the engine's value wins as before.
        /// </summary>
        internal static int CoreClockMhzFrom(IHardware? cpu) =>
            cpu is null
                ? 0
                : CoreClockMhzFrom(cpu.Sensors.Select(s => (s.SensorType, s.Name, s.Value)).ToList());

        internal static int CoreClockMhzFrom(IReadOnlyList<(SensorType Type, string Name, float? Value)> sensors)
        {
            double aggregate = sensors
                .Where(s => s.Type == SensorType.Clock && s.Value.HasValue
                    && s.Name.Equals("Cores (Average)", StringComparison.OrdinalIgnoreCase))
                .Select(s => (double)s.Value!.Value)
                .FirstOrDefault(v => v > 0);

            if (aggregate > 0)
                return Round(aggregate);

            float[] cores = sensors
                .Where(s => s.Type == SensorType.Clock && s.Value.HasValue && s.Value.Value > 0
                    && IsCoreClockName(s.Name)
                    && !s.Name.Contains("(Effective)", StringComparison.OrdinalIgnoreCase))
                .Select(s => s.Value!.Value)
                .ToArray();

            return cores.Length == 0 ? 0 : Round(cores.Average());
        }

        private static bool IsCoreClockName(string name) =>
            name.StartsWith("Core #", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("P-Core #", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("E-Core #", StringComparison.OrdinalIgnoreCase);

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

            _fastCache.Dispose();
            _slowCache.Dispose();

            // Close is LHM driver I/O and a group's gate owner may be parked
            // in a filter driver forever; app exit must not wait on it.
            // Bounded per group: process exit releases the handles regardless.
            _fast.Close(ShutdownGateBudget);
            _slow.Close(ShutdownGateBudget);
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

        private static (int temp0, int temp1) ReadMemoryTemperatures(Computer? computer)
        {
            if (computer is null)
                return (0, 0);

            // DIMMs appear as top-level hardware items with HardwareType.Memory.
            // Each DIMM has a temperature sensor named "DIMM #N" if it's DDR4/DDR5 with a thermal sensor.
            // On some laptops (e.g., PCSpecialist Recoil 3 16), DIMMs may be at slots #0 and #2
            // instead of #0 and #1. So we collect all DIMM temperatures and assign by order.
            int temp0 = 0;
            int temp1 = 0;
            int index = 0;

            foreach (IHardware hardware in computer.Hardware)
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

        private static int ReadStorageTemperature(Computer? computer)
        {
            if (computer is null)
                return 0;

            // Storage hardware contains individual drives (HardwareType.Storage).
            // Each drive may have a "Temperature" sensor. We return the maximum across all drives.
            int maxTemp = 0;
            foreach (IHardware storage in computer.Hardware)
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
