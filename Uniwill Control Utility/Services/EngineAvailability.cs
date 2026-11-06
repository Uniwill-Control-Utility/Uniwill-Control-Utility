using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;

namespace Uniwill_Control_Utility.Services
{
    /// <summary>
    /// The engine service's Service Control Manager state, as this process can see
    /// it. The states are separated because they need different repairs:
    /// <see cref="Stopped"/> is started (the DACL grants the app SERVICE_START),
    /// <see cref="NotInstalled"/> is registered with one elevation, and
    /// <see cref="AccessDenied"/> - the registration exists but this process may not
    /// open it - means a DACL from a build that predates the SERVICE_START grant and
    /// is repaired by re-running the elevated install, which re-asserts the DACL.
    /// </summary>
    public enum EngineServiceState
    {
        Running,
        Stopped,
        NotInstalled,
        AccessDenied
    }

    /// <summary>
    /// The app's single answer to "is the engine service running", read from the SCM
    /// (an <see cref="ScmNative"/> status query: sub-millisecond, no pipe, no EC, and
    /// it opens for only the SERVICE_QUERY_STATUS right the install grants) and cached
    /// briefly so a burst of call sites shares one query.
    ///
    /// The gate exists because every RPC verb is a pipe connect, and a connect to a
    /// pipe that does not exist costs the whole connect timeout - measured 5.0 s on
    /// this machine, one connect per verb, no retry. The startup path issues up to a
    /// dozen EC verbs, so with the engine stopped an un-gated path pays 5 s each and
    /// the splash stalls for tens of seconds (the "Initializing stuck at 78 %" report).
    /// A sub-millisecond SCM answer, asked once, replaces all of it.
    ///
    /// The cache is a snapshot with a short TTL, never a subscription: the service
    /// can be started from services.msc, by the SCM at boot, or by this app, and the
    /// next read is authoritative within the TTL. <see cref="Started"/> publishes the
    /// transition so work that was skipped while the engine was down can be applied
    /// once when it comes up.
    ///
    /// The SCM is not always able to speak. On a machine whose security product
    /// guards service descriptors (field-proven: Emsisoft), every DACL write is
    /// reverted, the app's opens are permanently denied, and the state is
    /// <see cref="AccessDenied"/> forever while the engine answers RPC fine. For that
    /// state <see cref="IsRunning"/> consults an injected RPC liveness probe - the
    /// pipe is independent of the SCM descriptor - on a background timer, so the
    /// consumers that gate EC writes on liveness keep working. The probe never
    /// overrules a real SCM answer.
    /// </summary>
    public sealed class EngineAvailability
    {
        public const string ServiceName = "UCUEngine";

        // SCM Win32 codes that separate the repairs. ScmNative surfaces the failing
        // open's error code directly (a Win32Exception), so the states stay
        // diagnosable: 5 is a DACL this process cannot open, 1060 is no registration.
        internal const int ErrorAccessDenied = 5;
        internal const int ErrorServiceDoesNotExist = 1060;

        private static readonly TimeSpan StatusTtl = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan ProbeInterval = TimeSpan.FromSeconds(5);

        private readonly Func<EngineServiceState> _readState;
        private readonly Func<Task<bool>>? _livenessProbe;
        private readonly TimeSpan _ttl;
        private readonly object _sync = new();
        private EngineServiceState? _cached;
        private EngineServiceState? _lastState;
        private DateTime _cachedUtc;
        private System.Threading.Timer? _probeTimer;
        private int _probeAlive;
        private int _probeInFlight;

        public EngineAvailability()
            : this(ReadStateFromScm, null)
        {
        }

        /// <summary>Production composition: the SCM read plus the app's RPC ping.</summary>
        public EngineAvailability(Func<Task<bool>> livenessProbe)
            : this(ReadStateFromScm, livenessProbe)
        {
            ArgumentNullException.ThrowIfNull(livenessProbe);
        }

        /// <summary>
        /// Composition: the SCM read is a seam, so the caching, the transition event
        /// and the state mapping are all observable. The liveness probe is the RPC
        /// ping the app injects: on a machine whose security product guards service
        /// descriptors (the AU ace cannot be made to stick - field-proven), the SCM
        /// state is permanently AccessDenied while the engine answers RPC fine; the
        /// probe is then the only honest liveness, and consumers ask for liveness,
        /// not for SCM vocabulary.
        /// </summary>
        internal EngineAvailability(
            Func<EngineServiceState> readState,
            Func<Task<bool>>? livenessProbe = null,
            TimeSpan? ttl = null)
        {
            ArgumentNullException.ThrowIfNull(readState);
            _readState = readState;
            _livenessProbe = livenessProbe;
            _ttl = ttl ?? StatusTtl;
        }

        /// <summary>
        /// Raised when a read observes the service enter <see cref="Running"/> from a
        /// different state. The first read never raises it (a process that starts
        /// while the engine is already up has nothing that was deferred).
        /// </summary>
        public event Action? Started;

        public EngineServiceState Query()
        {
            lock (_sync)
            {
                if (_cached is not null && DateTime.UtcNow - _cachedUtc < _ttl)
                    return _cached.Value;
            }

            EngineServiceState state = _readState();

            bool started;
            lock (_sync)
            {
                // The transition is detected against the last observed state, which
                // Invalidate() keeps: dropping the cache must not erase the history
                // the deferred apply depends on.
                started = state == EngineServiceState.Running
                    && _lastState is not null
                    && _lastState.Value != EngineServiceState.Running;
                _lastState = state;
                _cached = state;
                _cachedUtc = DateTime.UtcNow;

                // Keep the liveness probe's lifetime tied to the state that needs it.
                if (state == EngineServiceState.AccessDenied && _livenessProbe is not null)
                    EnsureProbeTimer();
                else
                    _probeTimer?.Dispose();
                _probeTimer = null;
            }

            if (started)
                Started?.Invoke();

            return state;
        }

        /// <summary>
        /// Liveness, for the consumers that skip EC work while the engine is down
        /// (deferred fan curve, charge limit, dashboard telemetry). The SCM answer
        /// is the primary truth; the RPC probe is the override for the one state
        /// where the SCM cannot speak: <see cref="AccessDenied"/>. On a machine
        /// whose security product guards service descriptors (field-proven:
        /// Emsisoft), the app's opens are denied permanently while the engine
        /// answers RPC fine - treating that as "not running" skips the EC writes
        /// of a working engine, which is the defect the probe closes.
        /// </summary>
        public bool IsRunning
        {
            get
            {
                EngineServiceState state = Query();
                return state == EngineServiceState.Running
                    || (state == EngineServiceState.AccessDenied
                        && Volatile.Read(ref _probeAlive) == 1);
            }
        }

        /// <summary>
        /// Drops the cached answer, so the next read re-queries. Used after the app
        /// starts, repairs or stops the service itself: the SCM has certainly
        /// changed, and a 2 s-old "Stopped" would otherwise hide the start. The
        /// transition history survives, so the read that follows an app-initiated
        /// start still publishes <see cref="Started"/>.
        /// </summary>
        public void Invalidate()
        {
            lock (_sync)
            {
                _cached = null;
            }
        }

        /// <summary>
        /// Starts the probe timer on the first AccessDenied read. The first tick is
        /// immediate so the first read after startup pays one ping, not one interval;
        /// the interval is far above the ping cost and well below the interval a
        /// service start can take, so a late-starting engine is picked up promptly.
        /// Call under <see cref="_sync"/>.
        /// </summary>
        private void EnsureProbeTimer()
        {
            _probeTimer ??= new Timer(_ => ProbeNow(), null, TimeSpan.Zero, ProbeInterval);
        }

        private void ProbeNow()
        {
            Func<Task<bool>>? probe = _livenessProbe;
            if (probe is null)
                return;

            // One ping at a time: the interval keeps ticking while one is in flight.
            if (Interlocked.Exchange(ref _probeInFlight, 1) == 1)
                return;

            _ = ProbeCoreAsync(probe);
        }

        private async Task ProbeCoreAsync(Func<Task<bool>> probe)
        {
            bool alive;
            try
            {
                alive = await probe().ConfigureAwait(false);
            }
            catch
            {
                alive = false;
            }
            finally
            {
                Interlocked.Exchange(ref _probeInFlight, 0);
            }

            bool transitioned;
            lock (_sync)
            {
                // The probe only speaks for the state where the SCM cannot: a real
                // SCM answer (Running, Stopped, NotInstalled) outranks the pipe.
                int target = alive && _lastState == EngineServiceState.AccessDenied ? 1 : 0;
                int previous = _probeAlive;
                _probeAlive = target;
                transitioned = target == 1 && previous == 0;
            }

            // Same contract as the SCM transition: consumers deferred EC work
            // because the engine looked down, and this is the moment it stopped
            // being down.
            if (transitioned)
                Started?.Invoke();
        }

        private static EngineServiceState ReadStateFromScm()
        {
            try
            {
                // ScmNative, not ServiceController: .NET's read-open requests the
                // interrogate and enumerate-dependents rights the install DACL
                // deliberately withholds, so a ServiceController query of a healthy
                // running service answered Access Denied and made every launch take
                // the elevated "DACL repair" path.
                return ScmNative.QueryState(ServiceName) == ScmNative.ServiceRunning
                    ? EngineServiceState.Running
                    // Paused and the pending states do not answer RPC, so for every
                    // consumer's purpose they are "not running", and the repair is
                    // the same as for Stopped.
                    : EngineServiceState.Stopped;
            }
            catch (Win32Exception ex)
            {
                return ClassifyScmFailure(ex);
            }
        }

        /// <summary>
        /// Maps a failed SCM open onto the state that needs the matching repair.
        /// Access denied is the case the app must never treat as "missing": the
        /// elevated install re-asserts the DACL, which is the fix.
        /// </summary>
        internal static EngineServiceState ClassifyScmFailure(Exception ex)
        {
            int code = (ex as Win32Exception)?.NativeErrorCode
                ?? (ex.InnerException as Win32Exception)?.NativeErrorCode
                ?? 0;

            return code switch
            {
                ErrorAccessDenied => EngineServiceState.AccessDenied,
                ErrorServiceDoesNotExist => EngineServiceState.NotInstalled,
                _ => EngineServiceState.NotInstalled
            };
        }
    }

    /// <summary>
    /// The app's implementation of the client's transport gate: a verb is refused
    /// before it costs a pipe connect when the SCM says the engine service is
    /// installed and not running.
    ///
    /// Not installed is "available": a developer points the same proxies at a
    /// console-mode engine (<c>dotnet run --project UCU.Engine -- --console</c>), which
    /// has no service, so there the connect is the only probe. Access denied - the
    /// state on a machine whose security product guards service descriptors - is
    /// answered by the liveness probe: the SCM is silent there, and the probe is
    /// the same pipe the verb is about to connect, so a live probe means the verb
    /// lands and a dead one means the service is down or still inside its EC
    /// activation handshake (the connect would cost the full timeout).
    /// </summary>
    public sealed class EngineServiceGate : UCU.Contracts.IEngineServiceGate
    {
        private readonly EngineAvailability _availability;

        public EngineServiceGate(EngineAvailability availability)
        {
            ArgumentNullException.ThrowIfNull(availability);
            _availability = availability;
        }

        public bool IsServiceAvailable()
        {
            EngineServiceState state = _availability.Query();

            if (state == EngineServiceState.Stopped)
                return false;

            if (state == EngineServiceState.AccessDenied)
                return _availability.IsRunning;

            return true;
        }
    }
}
