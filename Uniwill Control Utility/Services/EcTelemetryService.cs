using System;
using System.Threading;
using System.Threading.Tasks;
using UCU.Contracts.Capabilities;
using Uniwill_Control_Utility.Models;

namespace Uniwill_Control_Utility.Services
{
    /// <summary>
    /// The embedded controller's CPU temperature, as the app is allowed to read it: a
    /// cached, non-blocking read of the engine's <c>telemetry.get_cpu_temperature</c>
    /// verb (EC register 0x043E - the same sensor the EC's own fan curve runs on).
    ///
    /// This is the fallback sensor source for the app's CPU temperature. The primary
    /// source is LibreHardwareMonitor reading the CPU's MSR-derived sensors, which needs
    /// the PawnIO kernel driver, and PawnIO's device ACL is System + Administrators
    /// (its INF ships D:P(A;;GA;;;SY)(A;;GA;;;BA)), so the asInvoker app installed
    /// per-machine gets access denied and no CPU sensor at all. The EC reading needs no
    /// privilege: the LocalSystem engine opens the EC driver and the pipe DACL admits the
    /// interactive user.
    /// </summary>
    public interface IEcCpuTemperatureSource
    {
        /// <summary>EC CPU temperature in degrees Celsius; 0 when unavailable.</summary>
        int ReadCpuTemperature();
    }

    /// <summary>
    /// Shares one demand-driven EC telemetry sample between UI consumers. Reads
    /// return immediately while stale data is refreshed on a worker thread.
    ///
    /// If EC reads fail (engine not running, driver handle lost, EC unresponsive), the
    /// service attempts to recover on the next refresh and logs the failure.
    /// Consecutive failures are tracked so the UI can detect a persistent loss of EC
    /// access. Availability is re-checked on every refresh, never cached: the shipped
    /// app starts alongside the engine service, and a startup probe that raced the
    /// service starting must not latch the UI into a permanently degraded state.
    /// </summary>
    public sealed class EcTelemetryService : IDisposable, IEcCpuTemperatureSource
    {
        private static readonly TimeSpan DefaultMaxAge = TimeSpan.FromSeconds(1);
        private readonly StaleWhileRevalidateCache<EcFanDutySnapshot> _fanDutyCache;
        private readonly StaleWhileRevalidateCache<int> _mainFanRpmCache;
        private readonly StaleWhileRevalidateCache<int> _cpuTemperatureCache;

        // Consecutive failure counter — reset on success, incremented on failure.
        // Exposed so callers can detect persistent EC access loss.
        private int _consecutiveFailures;

        /// <summary>
        /// Number of consecutive EC read failures. Zero means the last read succeeded.
        /// High values indicate the EC driver handle may be stale or the EC unresponsive.
        /// </summary>
        public int ConsecutiveFailures => Volatile.Read(ref _consecutiveFailures);

        public EcTelemetryService(IEcTelemetryCapability telemetry, IEngineCapability engine)
            : this(CreateCaptures(telemetry, engine), DefaultMaxAge)
        {
        }

        internal EcTelemetryService(
            Func<Task<EcFanDutySnapshot>> fanDutyCapture,
            Func<Task<int>> mainFanRpmCapture,
            TimeSpan maxAge,
            TimeProvider? timeProvider = null,
            Func<Task<int>>? cpuTemperatureCapture = null)
        {
            ArgumentNullException.ThrowIfNull(fanDutyCapture);
            ArgumentNullException.ThrowIfNull(mainFanRpmCapture);

            _fanDutyCache = CreateCache(
                EcFanDutySnapshot.Empty,
                fanDutyCapture,
                maxAge,
                timeProvider,
                onRefreshError: ex => OnRefreshFailed(ex, "Fan duty"),
                onRefreshSuccess: () => OnRefreshSucceeded());
            _mainFanRpmCache = CreateCache(0, mainFanRpmCapture, maxAge, timeProvider,
                onRefreshError: ex => OnRefreshFailed(ex, "Main fan RPM"),
                onRefreshSuccess: () => OnRefreshSucceeded());
            // A caller with no EC source passes null: the read then answers 0, which is
            // the "unavailable" value every consumer already understands.
            _cpuTemperatureCache = CreateCache(0, cpuTemperatureCapture ?? ZeroCapture, maxAge, timeProvider,
                onRefreshError: ex => OnRefreshFailed(ex, "CPU temperature (EC)"),
                onRefreshSuccess: () => OnRefreshSucceeded());
        }

        private static Task<int> ZeroCapture() => Task.FromResult(0);

        private EcTelemetryService(
            (Func<Task<EcFanDutySnapshot>> fanDuty, Func<Task<int>> mainFanRpm, Func<Task<int>> cpuTemperature) captures,
            TimeSpan maxAge)
            : this(captures.fanDuty, captures.mainFanRpm, maxAge, cpuTemperatureCapture: captures.cpuTemperature)
        {
        }

        private void OnRefreshFailed(Exception ex, string metricName)
        {
            int failures = Interlocked.Increment(ref _consecutiveFailures);
            try
            {
                Serilog.Log.Information(ex,
                    "[EC] {MetricName} telemetry refresh failed (consecutive failures: {Failures})",
                    metricName, failures);
            }
            catch { /* Serilog may not be ready */ }
        }

        private void OnRefreshSucceeded()
        {
            Interlocked.Exchange(ref _consecutiveFailures, 0);
        }

        public EcFanDutySnapshot ReadFanDuty() => _fanDutyCache.Read();

        public async Task<int> RefreshMainFanRpmAsync()
        {
            _mainFanRpmCache.Invalidate();
            await _mainFanRpmCache.RefreshAsync().ConfigureAwait(false);
            // If invalidation joined an older in-flight refresh, that generation is
            // discarded. A second call then captures the requested generation.
            await _mainFanRpmCache.RefreshAsync().ConfigureAwait(false);
            return _mainFanRpmCache.Read();
        }

        internal int ReadMainFanRpm() => _mainFanRpmCache.Read();

        /// <summary>
        /// The EC's CPU temperature (degrees Celsius), 0 when the EC cannot answer. Cached
        /// and non-blocking like the other reads; it is the app's CPU-temperature fallback
        /// when the in-process MSR sensor path is unavailable (see
        /// <see cref="IEcCpuTemperatureSource"/>).
        /// </summary>
        public int ReadCpuTemperature() => _cpuTemperatureCache.Read();

        public void Dispose()
        {
            _fanDutyCache.Dispose();
            _mainFanRpmCache.Dispose();
            _cpuTemperatureCache.Dispose();
        }

        private static StaleWhileRevalidateCache<T> CreateCache<T>(
            T initialValue,
            Func<Task<T>> capture,
            TimeSpan maxAge,
            TimeProvider? timeProvider,
            Action<Exception>? onRefreshError = null,
            Action? onRefreshSuccess = null)
        {
            return new StaleWhileRevalidateCache<T>(
                initialValue,
                maxAge,
                _ => capture(),
                timeProvider,
                onRefreshError,
                onRefreshSuccess);
        }

        private static (Func<Task<EcFanDutySnapshot>> fanDuty, Func<Task<int>> mainFanRpm,
            Func<Task<int>> cpuTemperature) CreateCaptures(IEcTelemetryCapability telemetry, IEngineCapability engine)
        {
            ArgumentNullException.ThrowIfNull(telemetry);
            ArgumentNullException.ThrowIfNull(engine);

            return (
                async () =>
                {
                    if (!await engine.IsHardwareAvailableAsync().ConfigureAwait(false))
                        return EcFanDutySnapshot.Empty;

                    try
                    {
                        UCU.Contracts.FanDutySnapshotDto dto = await telemetry.GetFanDutyAsync().ConfigureAwait(false);
                        return new EcFanDutySnapshot(dto.CpuFanDuty, dto.GpuFanDuty);
                    }
                    catch (Exception ex)
                    {
                        // Log and attempt recovery on the next refresh. Re-throw so the
                        // cache error handler counts this as a failure.
                        try { Serilog.Log.Debug(ex, "[EC] Fan duty telemetry read threw"); }
                        catch { /* Serilog may not be ready */ }
                        throw;
                    }
                },
                async () =>
                {
                    if (!await engine.IsHardwareAvailableAsync().ConfigureAwait(false))
                        return 0;

                    try
                    {
                        return await telemetry.GetMainFanRpmAsync().ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        try { Serilog.Log.Debug(ex, "[EC] Main fan RPM read threw"); }
                        catch { /* Serilog may not be ready */ }
                        throw;
                    }
                },
                async () =>
                {
                    if (!await engine.IsHardwareAvailableAsync().ConfigureAwait(false))
                        return 0;

                    try
                    {
                        return await telemetry.GetCpuTemperatureAsync().ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        try { Serilog.Log.Debug(ex, "[EC] CPU temperature read threw"); }
                        catch { /* Serilog may not be ready */ }
                        throw;
                    }
                });
        }
    }
}
