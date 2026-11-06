namespace UCU.Engine
{
    /// <summary>
    /// The app's graceful-exit signal, carried by the RPC protocol.
    ///
    /// The supervisor must tell "the user closed the app" from "the app died",
    /// and only the app knows which. The answer used to be a flag file in the
    /// install root's logs folder - a path two processes with different
    /// privileges have to agree on, and in a per-machine install the unelevated
    /// app cannot write there at all. The engine's pipe is the product's
    /// designed cross-process channel, so the app calls
    /// <c>engine.notify_app_exit</c> on its way out and the timestamp lands here.
    ///
    /// Written on a pipe worker thread, read by the supervisor's poll: the
    /// timestamp travels as ticks under <see cref="Interlocked"/>, so no torn
    /// value is observable. Keeping it in memory is deliberate - a graceful
    /// exit the engine did not witness is not a graceful exit, and a signal
    /// cannot outlive the process that produced it (the flag file's stale-entry
    /// problem disappears with it).
    /// </summary>
    public sealed class AppExitSignal
    {
        /// <summary>0 = the app has not exited gracefully since the engine started.</summary>
        private long _gracefulExitTicks;

        /// <summary>Records an intentional app shutdown (the verb handler's job).</summary>
        public void SignalGracefulExit(DateTime utc) =>
            Interlocked.Exchange(ref _gracefulExitTicks, utc.Ticks);

        public DateTime? LastGracefulExitUtc
        {
            get
            {
                long ticks = Interlocked.Read(ref _gracefulExitTicks);
                return ticks == 0 ? null : new DateTime(ticks, DateTimeKind.Utc);
            }
        }

        /// <summary>
        /// True when the recorded graceful exit belongs to the run that just
        /// ended. Two filters, both carried over from the flag file: the signal
        /// must be within <paramref name="window"/> of now, so a signal from an
        /// app that died long ago cannot suppress a relaunch; and it must be at
        /// or after the run's start (5 s tolerance for start-time granularity),
        /// so the previous run's graceful exit is not credited to this one.
        /// </summary>
        public bool IsFreshGracefulExit(DateTime? runStartedUtc, DateTime nowUtc, TimeSpan window)
        {
            DateTime? last = LastGracefulExitUtc;
            if (last is null)
                return false;

            if (nowUtc - last.Value >= window)
                return false;

            return runStartedUtc is null || last.Value >= runStartedUtc.Value.AddSeconds(-5);
        }
    }
}
