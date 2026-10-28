namespace UCU.Contracts
{
    /// <summary>
    /// Protocol identity and framing limits for the engine pipe.
    ///
    /// The pipe name carries the protocol major version: an installed app and an
    /// installed engine are shipped together, but the version in the name means a
    /// mismatched pair fails at connect time with a clear "engine version" error
    /// instead of deserializing a request the other side will misinterpret.
    /// </summary>
    public static class EngineProtocol
    {
        /// <summary>Pipe name, appended to <see cref="PipePathPrefix"/>.</summary>
        public const string PipeName = "UCUEngine.v1";

        public const string PipePathPrefix = @"\\.\pipe\";

        public static string FullPipeName(string pipeName = PipeName) => PipePathPrefix + pipeName;

        /// <summary>Major protocol version, checked in every request envelope.</summary>
        public const int Version = 1;

        /// <summary>Size of the length prefix that precedes each JSON frame.</summary>
        public const int LengthPrefixBytes = 4;

        /// <summary>
        /// Largest request the engine accepts. A fan curve is well under 1 KiB; the cap
        /// is a bound on memory per connection, not a feature limit.
        /// </summary>
        public const int MaxRequestBytes = 256 * 1024;

        /// <summary>Largest response the client accepts (raw register dumps are small).</summary>
        public const int MaxResponseBytes = 1024 * 1024;
    }

    /// <summary>
    /// Client-side transport gate: asks whether calling the engine is worth a pipe
    /// connect at all, cheaply and without touching the pipe.
    ///
    /// The name of the game is the cost of a negative. A connect to the missing pipe
    /// of a stopped service does not fail fast: it polls for the whole connect timeout
    /// (measured 5.0 s), one per verb, and a single call site that used to be a
    /// nicety (a page constructor, a tray tick, a telemetry cache refresh, a preset's
    /// <c>ec-*</c> chain) turns into a frozen UI thread or a splash that stalls for
    /// tens of seconds. The app answers this from the Service Control Manager - a
    /// sub-millisecond status query, no privilege - and skips the connect.
    ///
    /// Deliberately a delegate over the SCM, not the SCM itself: the client library
    /// stays free of Windows service APIs, and a composition without a gate (tests,
    /// a developer pointing the proxies at a console-mode engine) keeps the connect
    /// behaviour.
    /// </summary>
    public interface IEngineServiceGate
    {
        /// <summary>
        /// True when a connect is worth attempting: the service is running, or the
        /// state is unknown/not installed (a console-mode engine has no service, so
        /// the connect is the only probe left). False only when the service is
        /// installed and known not to be running.
        /// </summary>
        bool IsServiceAvailable();
    }
}
