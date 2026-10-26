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
}
