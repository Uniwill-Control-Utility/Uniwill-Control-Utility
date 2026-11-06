using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using UCU.Hardware.Metrics;

namespace UCU.Engine
{
    /// <summary>
    /// Runs the kernel-side sensor probe (CPU Ring0 power/clocks, DIMM SPD) on the
    /// LocalSystem engine, on its own loop, and publishes every cycle to
    /// <see cref="EngineMetricsSnapshotSource"/>.
    ///
    /// Why it is a loop and not the verb: the field machine (resident AV filter
    /// drivers) parks sensor probes at startup - the app's own LHM probe parks
    /// every session, and a verb that probed live would put that park inside the
    /// engine's telemetry queue, where a wedged probe delays every telemetry verb
    /// and trips the domain watchdog. A dedicated loop parks alone; the verb
    /// serves the last cache; the EC queues never touch this code.
    ///
    /// The loop is single-threaded by design (one in-flight probe, sequential) and
    /// never throws: a failed open retries on a backoff, a hung open blocks only
    /// this loop (a pool thread the process exit reclaims), and the published
    /// cache simply goes stale - the app's merge treats stale-zero as "no
    /// reading", identical to the probe never existing.
    /// </summary>
    internal sealed class MetricsProbeService : BackgroundService
    {
        /// <summary>Refresh cadence for a healthy probe.</summary>
        internal static readonly TimeSpan CycleCadence = TimeSpan.FromSeconds(5);

        /// <summary>Retry delay after a failed probe open, and the delay after
        /// which a wedged open is retried (Reset + fresh Computer).</summary>
        internal static readonly TimeSpan OpenRetryBackoff = TimeSpan.FromSeconds(60);

        /// <summary>A cycle that ran longer than this is logged: on a guarded
        /// machine the first sign is that the sensor stack answers slowly.</summary>
        internal static readonly TimeSpan SlowCycleLogThreshold = TimeSpan.FromSeconds(30);

        private readonly EngineMetricsSnapshotSource _source;
        private readonly UCU.Hardware.Metrics.ISensorProbe _probe;
        private readonly ILogger<MetricsProbeService> _logger;

        public MetricsProbeService(
            EngineMetricsSnapshotSource source, UCU.Hardware.Metrics.ISensorProbe probe, ILogger<MetricsProbeService> logger)
        {
            _source = source;
            _probe = probe;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var cycle = new Stopwatch();

            while (!stoppingToken.IsCancellationRequested)
            {
                cycle.Restart();

                try
                {
                    SensorProbeReading reading = _probe.Read();
                    _source.Publish(reading);

                    if (!reading.ProbeOpen)
                    {
                        _logger.LogInformation(
                            "sensor probe not open; retrying in {Backoff}", OpenRetryBackoff);
                        _probe.Reset();
                        await Task.Delay(OpenRetryBackoff, stoppingToken).ConfigureAwait(false);
                        continue;
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // One cycle's failure must not end the loop: the app polls the
                    // verb on its own cadence, and the last good reading stays
                    // published while this cycle recovers.
                    _logger.LogWarning(ex, "sensor probe cycle failed; last reading stays published");
                }

                if (cycle.Elapsed > SlowCycleLogThreshold)
                    _logger.LogInformation(
                        "sensor probe cycle took {Elapsed:g}; the sensor stack is answering slowly", cycle.Elapsed);

                await Task.Delay(CycleCadence, stoppingToken).ConfigureAwait(false);
            }
        }
    }
}
