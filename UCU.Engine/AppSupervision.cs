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
    /// Arming rule: the supervisor watches only once it has seen the app
    /// running — it is a crash/kill respawn, not the logon autostart (the
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
        internal static readonly TimeSpan GracefulFlagWindow = TimeSpan.FromMinutes(2);

        private readonly ILogger<AppSupervision> _logger;
        private readonly Func<DateTime?> _probeAppStartUtc;
        private readonly Func<DateTime?, bool> _gracefulFlagFresh;
        private readonly Func<bool> _launchApp;
        private readonly Func<DateTime> _clock;

        private bool _watching;
        private DateTime? _runningSince;
        private int _consecutiveFastFailures;
        private DateTime? _relaunchDueUtc;

        /// <summary>Production composition: real process probe, real flag, real launcher.</summary>
        public AppSupervision(EngineRuntimeOptions options, ILogger<AppSupervision> logger)
            : this(options, logger,
                ProbeAppProcess,
                runStartedUtc => GracefulExitSignaled(EnginePaths.GracefulExitFlagPath, runStartedUtc, DateTime.UtcNow),
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
            Func<DateTime?, bool> gracefulFlagFresh,
            Func<bool> launchApp,
            Func<DateTime> clock)
        {
            _logger = logger;
            _probeAppStartUtc = probeAppStartUtc;
            _gracefulFlagFresh = gracefulFlagFresh;
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
                _logger.LogInformation("[Supervision] No app exe beside the engine — supervision off (dev layout).");
                return;
            }

            using var timer = new PeriodicTimer(PollInterval);
            try
            {
                while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
                    Tick();
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
                    _logger.LogInformation("[Supervision] App running — watching for crash/kill.");
                }
                return;
            }

            // The app is not running. A backoff-delayed relaunch comes due here.
            if (_relaunchDueUtc is DateTime due && now >= due)
            {
                _relaunchDueUtc = null;
                _launchApp();
            }

            if (!_watching)
                return;

            // Running → stopped transition: the policy decides.
            _watching = false;
            TimeSpan runDuration = now - (_runningSince ?? now);
            bool graceful = _gracefulFlagFresh(_runningSince);

            GuardianDecision decision = GuardianPolicy.Evaluate(
                graceful, runDuration, _consecutiveFastFailures,
                MaxFastCrashAttempts, BaseDelay, MaxDelay, FastExitThreshold);

            _logger.LogInformation(
                "[Supervision] App stopped after {Run}, graceful={Graceful} → {Action} (delay {Delay}, failures {Failures})",
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
                    _launchApp();
                    break;
            }
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
        /// probe reads StartTime throws on — StartTime is read inside the guard.
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

        /// <summary>
        /// Fresh graceful-exit flag = the user closed the app; stale or missing =
        /// crash/kill. The flag must belong to the run that ended: the file
        /// outlives the process, so a previous clean exit's flag is filtered by
        /// the run's start time (5 s tolerance for clock granularity).
        /// </summary>
        internal static bool GracefulExitSignaled(string flagPath, DateTime? runStartedUtc, DateTime nowUtc)
        {
            try
            {
                if (!File.Exists(flagPath))
                    return false;

                DateTime lastWrite = File.GetLastWriteTimeUtc(flagPath);
                if (nowUtc - lastWrite >= GracefulFlagWindow)
                    return false;

                return runStartedUtc is null || lastWrite >= runStartedUtc.Value.AddSeconds(-5);
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
