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
        public const string TelemetryDomain = "telemetry";
        public const string EngineDomain = "engine";

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
                [TelemetryDomain] = new SerialExecutor("telemetry", options),
            };
        }

        public IReadOnlyDictionary<string, SerialExecutor> Queues => _queues;

        /// <summary>
        /// The queue a verb runs on, or null to run inline. Unknown prefixes go to the
        /// telemetry queue rather than being dropped: an unregistered verb is already
        /// rejected by the dispatcher's verb table, so reaching here with a new prefix
        /// means a read, which is the safe guess.
        /// </summary>
        public SerialExecutor? QueueFor(string verb)
        {
            int dot = verb.IndexOf('.');
            string domain = dot > 0 ? verb[..dot] : verb;

            return domain == EngineDomain ? null : _queues[domain];
        }

        public async ValueTask DisposeAsync()
        {
            foreach (SerialExecutor queue in _queues.Values)
                await queue.DisposeAsync().ConfigureAwait(false);
        }
    }
}
