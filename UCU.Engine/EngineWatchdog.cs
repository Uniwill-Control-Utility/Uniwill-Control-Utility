using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace UCU.Engine
{
    /// <summary>
    /// Watches the hardware queues for a call that will never finish.
    ///
    /// The failure this guards is a wedged EC: the IOCTL has no timeout and no
    /// cancellation, so a call parked in the driver parks its domain queue forever,
    /// and every later request in that domain times out with no path to recovery.
    /// The only way to clear a stuck kernel handle is to close it, and the only way
    /// to close it from a service that cannot unplug the device is to exit; the
    /// service control manager's restart-on-failure then gives the machine a fresh
    /// engine. Without this, a wedged EC means permanently dead fan control until a
    /// reboot.
    /// </summary>
    public sealed class EngineWatchdog : BackgroundService
    {
        private readonly IReadOnlyDictionary<string, SerialExecutor> _queues;
        private readonly EngineRuntimeOptions _options;
        private readonly ILogger<EngineWatchdog> _log;
        private readonly Action<string> _terminate;

        public EngineWatchdog(
            EngineDomainQueues queues,
            EngineRuntimeOptions options,
            ILogger<EngineWatchdog> log)
            : this(queues.Queues, options, log, terminate: null)
        {
        }

        internal EngineWatchdog(
            IReadOnlyDictionary<string, SerialExecutor> queues,
            EngineRuntimeOptions options,
            ILogger<EngineWatchdog> log,
            Action<string>? terminate = null)
        {
            _queues = queues;
            _options = options;
            _log = log;
            _terminate = terminate ?? DefaultTerminate;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var interval = TimeSpan.FromSeconds(5);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(interval, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                Check(stoppingToken);
            }
        }

        internal void Check(CancellationToken stoppingToken)
        {
            foreach ((string domain, SerialExecutor queue) in _queues)
            {
                if (queue.CurrentCallAge < _options.StuckCallThreshold)
                    continue;

                string message =
                    $"{domain} queue parked on '{queue.CurrentCall}' for {queue.CurrentCallAge.TotalSeconds:0}s " +
                    $"(threshold {_options.StuckCallThreshold.TotalSeconds:0}s); terminating so the SCM restarts the engine";

                // Log first: a FailFast exit gives Serilog no flush, so write and
                // dispose the logger factory path by logging then terminating.
                _log.LogCritical(message);

                if (!stoppingToken.IsCancellationRequested)
                    _terminate(message);

                return;
            }
        }

        private static void DefaultTerminate(string message) => Environment.FailFast(message);
    }
}
