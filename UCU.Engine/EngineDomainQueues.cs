using UCU.Contracts;

namespace UCU.Engine
{
    /// <summary>
    /// The per-domain serial queues. The domain is the verb's prefix (<c>fan.*</c> runs
    /// on the fan queue), so adding a verb needs no registration here and writes that
    /// share registers stay ordered by construction.
    ///
    /// Engine-identity verbs (<c>engine.ping</c>, <c>engine.get_info</c>) run inline:
    /// they touch no hardware, and a client must be able to ask "is the EC wedged?"
    /// when every hardware queue is parked on a stuck call.
    /// </summary>
    public sealed class EngineDomainQueues : IAsyncDisposable
    {
        public const string FanDomain = "fan";
        public const string PowerDomain = "power";
        public const string MuxDomain = "mux";
        public const string KeyboardDomain = "keyboard";
        public const string BatteryDomain = "battery";
        public const string DeviceDomain = "device";
        public const string CpuDomain = "cpu";
        public const string TelemetryDomain = "telemetry";
        public const string EngineDomain = "engine";

        /// <summary>
        /// RTSS verbs run inline, like the engine domain: starting a
        /// user-session process touches no EC hardware, and the WTS/token/
        /// CreateProcess round trip has no business queueing behind a parked
        /// fan-curve upload.
        /// </summary>
        public const string RtssDomain = "rtss";

        private readonly Dictionary<string, SerialExecutor> _queues;

        public EngineDomainQueues(EngineRuntimeOptions options)
        {
            _queues = new Dictionary<string, SerialExecutor>(StringComparer.Ordinal)
            {
                [FanDomain] = new SerialExecutor("fan", options),
                [PowerDomain] = new SerialExecutor("power", options),
                [MuxDomain] = new SerialExecutor("mux", options),
                [KeyboardDomain] = new SerialExecutor("keyboard", options),
                [BatteryDomain] = new SerialExecutor("battery", options),
                [DeviceDomain] = new SerialExecutor("device", options),
                [CpuDomain] = new SerialExecutor("cpu", options),
                [TelemetryDomain] = new SerialExecutor("telemetry", options),
            };
        }

        public IReadOnlyDictionary<string, SerialExecutor> Queues => _queues;

        /// <summary>
        /// The queue a verb runs on, or null to run inline. Unknown prefixes go to the
        /// telemetry queue rather than being dropped: an unregistered verb is already
        /// rejected by the dispatcher's verb table, so reaching here with a new prefix
        /// means a read, which is the safe guess.
        ///
        /// The cached sensor snapshot joins the inline set: it touches no hardware,
        /// and a client must be able to read the temperature/power history when a
        /// hardware queue is parked on a stuck call - which is the whole point of
        /// serving it from a cache on a machine whose sensor probes park.
        /// </summary>
        public SerialExecutor? QueueFor(string verb)
        {
            int dot = verb.IndexOf('.');
            string domain = dot > 0 ? verb[..dot] : verb;

            // The engine domain (identity/health) and the cached sensor snapshot
            // run inline: they touch no hardware, and the app must be able to
            // read them when every hardware queue is parked on a stuck call -
            // which is exactly when the sensor readings matter most.
            // The engine domain (identity/health), the RTSS domain (a
            // user-session process launch, no hardware), and the cached sensor
            // snapshot run inline: they touch no hardware, and the app must be
            // able to read them when every hardware queue is parked on a stuck
            // call - which is exactly when the sensor readings matter most.
            if (domain == EngineDomain || domain == RtssDomain || verb == EngineVerbs.Telemetry.GetMetricsSnapshot)
                return null;

            // The documented fallback, as a lookup that cannot throw: the
            // dispatcher's verb table has already rejected unknown verbs, so a
            // prefix with no queue of its own is a read, and telemetry is the
            // safe guess. A bare indexer here threw KeyNotFoundException, which
            // the client saw as "unexpected engine fault", for the first verb
            // that added a new domain.
            return _queues.TryGetValue(domain, out SerialExecutor? queue)
                ? queue
                : _queues[TelemetryDomain];
        }

        public async ValueTask DisposeAsync()
        {
            foreach (SerialExecutor queue in _queues.Values)
                await queue.DisposeAsync().ConfigureAwait(false);
        }
    }
}
