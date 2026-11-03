using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using UCU.Core.Supervision;

namespace UCU.Engine
{
    /// <summary>
    /// The engine's app supervisor: the XMG Control Center's "relaunch the
    /// shell when it dies" behaviour, now owned by the service (phase 5).
    ///
    /// The policy (GuardianPolicy, UCU.Core) was written for the file-flow
    /// guardian and is reused verbatim; the supervisor changes who observes:
    /// the engine sees every process on the machine (LocalSystem), needs no
    /// mutex trick, and launches into the user's session through
    /// <see cref="AppSessionLauncher"/>.
    ///
    /// The graceful-vs-crash input also changed: the app reports an intentional
    /// exit through the RPC verb engine.notify_app_exit (see
    /// <see cref="AppExitSignal"/>) instead of writing a flag file into the
    /// install root, which an unelevated app cannot write in a per-machine
    /// install and which no privilege boundary should require it to.
    ///
    /// Arming rule: the supervisor watches only once it has seen the app
    /// running Ã¢â‚¬â€ it is a crash/kill respawn, not the logon autostart (the
    /// "UCU Autostart" scheduled task owns that). After the policy stops
    /// (graceful exit or crash-loop budget), the supervisor disarms and waits
    /// for the app to run again before re-arming.
    /// </summary>
    internal sealed class AppSupervision : BackgroundService
    {
        internal const string AppProcessName = "Uniwill Control Utility";
        internal const string AppExeFileName = "Uniwill Control Utility.exe";

        /// <summary>Relaunch marker the app recognizes: no startup overlay, open minimized.</summary>
        internal const string RelaunchArguments = "--from-guardian";

        internal static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
        internal const int MaxFastCrashAttempts = 3;
        internal static readonly TimeSpan BaseDelay = TimeSpan.FromSeconds(10);
        internal static readonly TimeSpan MaxDelay = TimeSpan.FromMinutes(10);
        internal static readonly TimeSpan FastExitThreshold = TimeSpan.FromMinutes(2);
        internal static readonly TimeSpan GracefulExitWindow = TimeSpan.FromMinutes(2);

        /// <summary>Retries for a launch attempt that never produced an app process
        /// (launcher refused, session not ready), on top of the first attempt.</summary>
        internal const int MaxLaunchRetries = 2;

        private readonly ILogger<AppSupervision> _logger;
        private readonly Func<DateTime?> _probeAppStartUtc;
        private readonly Func<DateTime?, bool> _gracefulExitFresh;
        private readonly Func<bool> _launchApp;
        private readonly Func<DateTime> _clock;

        private bool _watching;
        private DateTime? _runningSince;
        private int _consecutiveFastFailures;
        private int _failedLaunches;
        private DateTime? _relaunchDueUtc;

        /// <summary>
        /// Production composition: real process probe, the verb-carried graceful
        /// exit signal, real launcher.
        /// </summary>
        public AppSupervision(EngineRuntimeOptions options, AppExitSignal appExit, ILogger<AppSupervision> logger)
            : this(options, logger,
                ProbeAppProcess,
                runStartedUtc => appExit.IsFreshGracefulExit(runStartedUtc, DateTime.UtcNow, GracefulExitWindow),
                () => LaunchApp(logger),
                () => DateTime.UtcNow)
        {
        }

        /// <summary>
        /// Test seam: probe / flag / launch / clock are injectable so the state
        /// machine runs in-process against fakes (the same seam style as
        /// <c>EngineHostFactory</c>'s transport override).
        /// </summary>
        internal AppSupervision(
            EngineRuntimeOptions options,
            ILogger<AppSupervision> logger,
            Func<DateTime?> probeAppStartUtc,
            Func<DateTime?, bool> gracefulExitFresh,
            Func<bool> launchApp,
            Func<DateTime> clock)
        {
            _logger = logger;
            _probeAppStartUtc = probeAppStartUtc;
            _gracefulExitFresh = gracefulExitFresh;
            _launchApp = launchApp;
            _clock = clock;
            Options = options;
        }

        internal EngineRuntimeOptions Options { get; }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (!Options.SupervisionEnabled)
            {
                _logger.LogInformation("[Supervision] Disabled by options.");
                return;
            }

            if (FindAppExe() is null)
            {
                // Dev checkout: the app exe is not staged next to the engine.
                _logger.LogInformation("[Supervision] No app exe beside the engine Ã¢â‚¬â€ supervision off (dev layout).");
                return;
            }

            using var timer = new PeriodicTimer(PollInterval);
            try
            {
                while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
                {
                    // A supervision tick is never worth the engine. The field
                    // proof: a broken P/Invoke in the session launcher threw on
                    // the relaunch path, BackgroundService surfaced it as
                    // unhandled, and the default BackgroundServiceException
                    // Behavior stopped the whole host - RPC, watchdog and all.
                    // The relaunch lost for this tick is retried by the policy
                    // on the next one.
                    try
                    {
                        Tick();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "[Supervision] Tick failed; supervision survives and retries on the next poll.");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Host shutting down.
            }
        }

        /// <summary>One poll pass; internal so the tests drive the state machine directly.</summary>
        internal void Tick()
        {
            DateTime now = _clock();
            DateTime? appStart = _probeAppStartUtc();

            if (appStart is not null)
            {
                if (!_watching)
                {
                    // Re-arm. The fast-crash budget is deliberately NOT reset
                    // here: a crash-loop relaunch must keep counting. Only a
                    // real run (the Relaunch decision) and a disarm (Stop)
                    // clear it.
                    _watching = true;
                    _runningSince = appStart;
                    _relaunchDueUtc = null;
                    _failedLaunches = 0; // a launch that worked ends the retry budget
                    _logger.LogInformation("[Supervision] App running Ã¢â‚¬â€ watching for crash/kill.");
                }
                return;
            }

            // The app is not running. A backoff-delayed relaunch comes due here.
            if (_relaunchDueUtc is DateTime due && now >= due)
            {
                _relaunchDueUtc = null;
                LaunchWithRetryBudget(now);
            }

            if (!_watching)
                return;

            // Running Ã¢â€ â€™ stopped transition: the policy decides.
            _watching = false;
            TimeSpan runDuration = now - (_runningSince ?? now);
            bool graceful = _gracefulExitFresh(_runningSince);

            GuardianDecision decision = GuardianPolicy.Evaluate(
                graceful, runDuration, _consecutiveFastFailures,
                MaxFastCrashAttempts, BaseDelay, MaxDelay, FastExitThreshold);

            _logger.LogInformation(
                "[Supervision] App stopped after {Run}, graceful={Graceful} Ã¢â€ â€™ {Action} (delay {Delay}, failures {Failures})",
                runDuration, graceful, decision.Action, decision.Delay, _consecutiveFastFailures);

            switch (decision.Action)
            {
                case GuardianAction.Stop:
                    // Intentional exit (user closed the app) or crash-loop
                    // budget spent: disarm until the app runs again.
                    _consecutiveFastFailures = 0;
                    _runningSince = null;
                    break;

                case GuardianAction.RelaunchAfter:
                    _consecutiveFastFailures++;
                    _relaunchDueUtc = now + decision.Delay;
                    break;

                case GuardianAction.Relaunch:
                    _consecutiveFastFailures = 0;
                    LaunchWithRetryBudget(now);
                    break;
            }
        }

        /// <summary>
        /// Launches the app, and retries a *failed attempt* a couple of times before
        /// giving up. A launch that returns false produced no app process at all
        /// (session launcher refused, session not interactive yet, exe momentarily
        /// locked), which used to leave supervision disarmed with nothing scheduled -
        /// the app stayed dead until the user started it (gap found 2026-11-03).
        ///
        /// Bounded on purpose, same philosophy as the fast-crash budget: a machine
        /// where the launcher cannot work at all must not retry forever. A launch
        /// that succeeds but whose app dies immediately is the crash-loop budget's
        /// job, not this one's.
        /// </summary>
        private void LaunchWithRetryBudget(DateTime now)
        {
            if (_launchApp())
            {
                _failedLaunches = 0;
                return;
            }

            if (_failedLaunches >= MaxLaunchRetries)
            {
                _logger.LogWarning(
                    "[Supervision] App launch failed {Attempts} times - giving up until the app runs again.",
                    _failedLaunches + 1);
                _runningSince = null;
                return;
            }

            _failedLaunches++;
            long ticks = BaseDelay.Ticks << Math.Min(_failedLaunches - 1, 20);
            var delay = TimeSpan.FromTicks(Math.Min(ticks, MaxDelay.Ticks));
            _relaunchDueUtc = now + delay;
            _logger.LogWarning(
                "[Supervision] App launch failed - retry {Attempt}/{Max} in {Delay}",
                _failedLaunches, MaxLaunchRetries, delay);
        }
        /// <summary>The app exe staged beside the engine's Service\ folder, or null (dev layout).</summary>
        internal static string? FindAppExe()
        {
            string path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", AppExeFileName));
            return File.Exists(path) ? path : null;
        }

        private static bool LaunchApp(ILogger logger)
        {
            string? exe = FindAppExe();
            if (exe is null)
                return false;

            return AppSessionLauncher.Launch(exe, RelaunchArguments, logger);
        }

        /// <summary>
        /// The app's start time (UTC) while it runs, null when it does not.
        /// LocalSystem sees every session's processes; a protected/access-denied
        /// probe reads StartTime throws on Ã¢â‚¬â€ StartTime is read inside the guard.
        /// </summary>
        private static DateTime? ProbeAppProcess()
        {
            try
            {
                foreach (Process process in Process.GetProcessesByName(AppProcessName))
                {
                    try
                    {
                        // The app is single-instance (global mutex); first hit is it.
                        return process.StartTime.ToUniversalTime();
                    }
                    catch (Exception)
                    {
                        // Process vanished mid-enumeration, or StartTime denied:
                        // running is what matters, so report a plausible start.
                        return DateTime.UtcNow;
                    }
                    finally
                    {
                        process.Dispose();
                    }
                }

                return null;
            }
            catch (Exception)
            {
                // Process enumeration is not the critical path; treat as not running.
                return null;
            }
        }
    }
}