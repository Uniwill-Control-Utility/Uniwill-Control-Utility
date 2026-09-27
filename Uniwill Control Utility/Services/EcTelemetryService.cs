using System;
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
    /// </summary>
    public sealed class EcTelemetryService : IDisposable
    {
        private static readonly TimeSpan DefaultMaxAge = TimeSpan.FromSeconds(1);
        private readonly StaleWhileRevalidateCache<EcFanDutySnapshot> _fanDutyCache;
        private readonly StaleWhileRevalidateCache<int> _mainFanRpmCache;

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
                timeProvider);
            _mainFanRpmCache = CreateCache(0, mainFanRpmCapture, maxAge, timeProvider);
        }

        private EcTelemetryService(
            (Func<EcFanDutySnapshot> fanDuty, Func<int> mainFanRpm) captures,
            TimeSpan maxAge)
            : this(captures.fanDuty, captures.mainFanRpm, maxAge)
        {
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
            TimeProvider? timeProvider)
        {
            return new StaleWhileRevalidateCache<T>(
                initialValue,
                maxAge,
                _ => Task.FromResult(capture()),
                timeProvider,
                ex =>
                {
                    try { Serilog.Log.Debug(ex, "[EC] Background telemetry refresh failed"); }
                    catch { /* Serilog may not be ready */ }
                });
        }

        private static (Func<EcFanDutySnapshot> fanDuty, Func<int> mainFanRpm)
            CreateCaptures(UniwillECService ecService)
        {
            ArgumentNullException.ThrowIfNull(ecService);
            var available = new Lazy<bool>(
                ecService.Initialize,
                System.Threading.LazyThreadSafetyMode.ExecutionAndPublication);

            return (
                () => available.Value
                    ? ecService.ReadFanDutyTelemetry()
                    : EcFanDutySnapshot.Empty,
                () => available.Value ? ecService.GetMainFanRpm() : 0);
        }
    }
}
