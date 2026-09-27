using System;
using System.Threading.Tasks;

namespace Uniwill_Control_Utility.Services
{
    public readonly record struct EcTelemetrySnapshot(
        int CpuTemperatureC,
        int GpuTemperatureC,
        int MainFanRpm,
        int SecondFanRpm,
        int CpuFanDuty,
        int GpuFanDuty)
    {
        public static EcTelemetrySnapshot Empty { get; } = new();

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
        private readonly StaleWhileRevalidateCache<EcTelemetrySnapshot> _cache;

        public EcTelemetryService(UniwillECService ecService)
            : this(CreateCapture(ecService), DefaultMaxAge)
        {
        }

        internal EcTelemetryService(
            Func<EcTelemetrySnapshot> capture,
            TimeSpan maxAge,
            TimeProvider? timeProvider = null)
        {
            ArgumentNullException.ThrowIfNull(capture);

            _cache = new StaleWhileRevalidateCache<EcTelemetrySnapshot>(
                EcTelemetrySnapshot.Empty,
                maxAge,
                _ => System.Threading.Tasks.Task.FromResult(capture()),
                timeProvider,
                ex =>
                {
                    try { Serilog.Log.Debug(ex, "[EC] Background telemetry refresh failed"); }
                    catch { /* Serilog may not be ready */ }
                });
        }

        public EcTelemetrySnapshot Read() => _cache.Read();

        public async Task<EcTelemetrySnapshot> RefreshAsync()
        {
            _cache.Invalidate();
            await _cache.RefreshAsync().ConfigureAwait(false);
            // If invalidation joined an older in-flight refresh, that generation is
            // discarded. A second call then captures the requested generation.
            await _cache.RefreshAsync().ConfigureAwait(false);
            return _cache.Read();
        }

        public void Dispose() => _cache.Dispose();

        private static Func<EcTelemetrySnapshot> CreateCapture(UniwillECService ecService)
        {
            ArgumentNullException.ThrowIfNull(ecService);
            bool initializationAttempted = false;
            bool available = false;

            return () =>
            {
                if (!initializationAttempted)
                {
                    available = ecService.Initialize();
                    initializationAttempted = true;
                }

                return available
                    ? ecService.ReadFanTelemetry()
                    : EcTelemetrySnapshot.Empty;
            };
        }
    }
}
