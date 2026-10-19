using System;
using System.Threading;
using System.Threading.Tasks;

namespace Uniwill_Control_Utility.Services
{
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

    /// <summary>
    /// Shares one demand-driven EC telemetry sample between UI consumers. Reads
    /// return immediately while stale data is refreshed on a worker thread.
    ///
    /// If EC reads fail (driver handle lost, EC unresponsive), the service attempts
    /// to reconnect automatically and logs the failure. Consecutive failures are
    /// tracked so the UI can detect a persistent loss of EC access.
    /// </summary>
    public sealed class EcTelemetryService : IDisposable
    {
        private static readonly TimeSpan DefaultMaxAge = TimeSpan.FromSeconds(1);
        private readonly StaleWhileRevalidateCache<EcFanDutySnapshot> _fanDutyCache;
        private readonly StaleWhileRevalidateCache<int> _mainFanRpmCache;

        // Consecutive failure counter — reset on success, incremented on failure.
        // Exposed so callers can detect persistent EC access loss.
        private int _consecutiveFailures;

        /// <summary>
        /// Number of consecutive EC read failures. Zero means the last read succeeded.
        /// High values indicate the EC driver handle may be stale or the EC unresponsive.
        /// </summary>
        public int ConsecutiveFailures => Volatile.Read(ref _consecutiveFailures);

        public EcTelemetryService(UniwillECService ecService)
            : this(CreateCaptures(ecService), DefaultMaxAge)
        {
        }

        internal EcTelemetryService(
            Func<EcFanDutySnapshot> fanDutyCapture,
            Func<int> mainFanRpmCapture,
            TimeSpan maxAge,
            TimeProvider? timeProvider = null)
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
        }

        private EcTelemetryService(
            (Func<EcFanDutySnapshot> fanDuty, Func<int> mainFanRpm) captures,
            TimeSpan maxAge)
            : this(captures.fanDuty, captures.mainFanRpm, maxAge)
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

        public void Dispose()
        {
            _fanDutyCache.Dispose();
            _mainFanRpmCache.Dispose();
        }

        private static StaleWhileRevalidateCache<T> CreateCache<T>(
            T initialValue,
            Func<T> capture,
            TimeSpan maxAge,
            TimeProvider? timeProvider,
            Action<Exception>? onRefreshError = null,
            Action? onRefreshSuccess = null)
        {
            return new StaleWhileRevalidateCache<T>(
                initialValue,
                maxAge,
                _ => Task.FromResult(capture()),
                timeProvider,
                onRefreshError,
                onRefreshSuccess);
        }

        private static (Func<EcFanDutySnapshot> fanDuty, Func<int> mainFanRpm)
            CreateCaptures(UniwillECService ecService)
        {
            ArgumentNullException.ThrowIfNull(ecService);
            var available = new Lazy<bool>(
                ecService.Initialize,
                System.Threading.LazyThreadSafetyMode.ExecutionAndPublication);

            return (
                () =>
                {
                    if (!available.Value)
                        return EcFanDutySnapshot.Empty;

                    try
                    {
                        return ecService.ReadFanDutyTelemetry();
                    }
                    catch (Exception ex)
                    {
                        // Log and attempt reconnect on the next call. Re-throw so the
                        // cache error handler counts this as a failure.
                        try { Serilog.Log.Debug(ex, "[EC] Fan duty telemetry read threw"); }
                        catch { /* Serilog may not be ready */ }
                        throw;
                    }
                },
                () =>
                {
                    if (!available.Value)
                        return 0;

                    try
                    {
                        return ecService.GetMainFanRpm();
                    }
                    catch (Exception ex)
                    {
                        try { Serilog.Log.Debug(ex, "[EC] Main fan RPM read threw"); }
                        catch { /* Serilog may not be ready */ }
                        throw;
                    }
                });
        }
    }
}
