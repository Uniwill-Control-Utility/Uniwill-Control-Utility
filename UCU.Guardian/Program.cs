using Serilog;
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Uniwill_Control_Utility.Services;

// Crash guardian (GCUService-style), shipped as "Uniwill Control Utility
// Service.exe" in the Service\ subfolder (out of the install root, where its
// similar name and icon would confuse users) so Task Manager shows a distinct
// name. Polls the main app's single-instance
// mutex and relaunches the app after a crash — never after an intentional exit
// (the app writes a graceful-exit flag file in OnExit; the file outlives the
// process, unlike a named event, which dies with the app before our poll runs).
// Relaunches carry --from-guardian: the app then skips the startup overlay and
// opens minimized to the tray.
//
// Headless: no window, no tray icon, no EC access. Registered via the
// "UCU Guardian" scheduled task (highest privileges) so it can relaunch the
// requireAdministrator app without a UAC prompt.

using var guardianMutex = new Mutex(true, GuardianConstants.GuardianMutexName, out bool createdNew);
if (!createdNew)
    return; // a guardian is already watching

// We run from the Service\ subfolder; the app, its logs, and the graceful-exit
// flag live one level up in the install root (the same logs folder the app
// writes to and the About page opens).
string rootDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ".."));
string logsFolder = Path.Combine(rootDir, "logs");
Directory.CreateDirectory(logsFolder);
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.File(Path.Combine(logsFolder, "ucu_guardian_log.txt"),
        fileSizeLimitBytes: 2 * 1024 * 1024,
        rollingInterval: RollingInterval.Day)
    .CreateLogger();

Log.Information("[Guardian] Started, watching {Mutex}", GuardianConstants.AppMutexName);

string mainExe = Path.Combine(rootDir, "Uniwill Control Utility.exe");
string gracefulFlag = Path.Combine(logsFolder, "graceful-exit.flag");

const int MaxFastCrashAttempts = 3;
var pollInterval = TimeSpan.FromSeconds(5);
var baseDelay = TimeSpan.FromSeconds(10);
var maxDelay = TimeSpan.FromMinutes(10);
var fastExitThreshold = TimeSpan.FromMinutes(2);

int consecutiveFailures = 0;
bool wasRunning = false;
DateTime? launchedAt = null;
DateTime? lastSeenRunning = null;

try
{
    while (true)
    {
        bool running = IsMutexAlive(GuardianConstants.AppMutexName);

        if (running)
        {
            if (!wasRunning)
            {
                wasRunning = true;
                launchedAt ??= DateTime.Now;
                Log.Information("[Guardian] App is running");
            }
            lastSeenRunning = DateTime.Now;
        }
        else
        {
            if (wasRunning)
            {
                // Running → stopped transition: apply the policy.
                wasRunning = false;
                var runDuration = (lastSeenRunning ?? DateTime.Now) - (launchedAt ?? DateTime.Now);
                bool graceful = GracefulExitSignaled(gracefulFlag);

                var decision = GuardianPolicy.Evaluate(
                    graceful, runDuration, consecutiveFailures,
                    MaxFastCrashAttempts, baseDelay, maxDelay, fastExitThreshold);

                Log.Information("[Guardian] App stopped after {Run}, graceful={Graceful} → {Action} (delay {Delay}, failures {Failures})",
                    runDuration, graceful, decision.Action, decision.Delay, consecutiveFailures);

                if (decision.Action == GuardianAction.Stop)
                    return;

                if (decision.Action == GuardianAction.RelaunchAfter)
                {
                    consecutiveFailures++;
                    Thread.Sleep(decision.Delay);
                }
                else
                {
                    consecutiveFailures = 0;
                }
            }

            LaunchApp(mainExe);
            launchedAt = DateTime.Now;
            lastSeenRunning = null;
            wasRunning = true;
        }

        Thread.Sleep(pollInterval);
    }
}
catch (Exception ex)
{
    Log.Fatal(ex, "[Guardian] Crashed");
}
finally
{
    Log.Information("[Guardian] Stopping");
    Log.CloseAndFlush();
}

static void LaunchApp(string mainExe)
{
    try
    {
        if (!File.Exists(mainExe))
        {
            Log.Error("[Guardian] Main executable not found: {Exe}", mainExe);
            return;
        }

        // --from-guardian: the app suppresses the startup overlay and opens
        // minimized to the tray — a crash recovery must not interrupt the user.
        Process.Start(new ProcessStartInfo(mainExe, "--from-guardian") { UseShellExecute = true });
        Log.Information("[Guardian] Launched the app");
    }
    catch (Exception ex)
    {
        Log.Error(ex, "[Guardian] Failed to launch the app");
    }
}

static bool IsMutexAlive(string name)
{
    try
    {
        using var probe = new Mutex(false, name, out bool createdNew);
        return !createdNew;
    }
    catch (AbandonedMutexException)
    {
        // The owning process died without releasing — treat as running
        // and let the next poll settle the transition.
        return true;
    }
}

// The app writes logs\graceful-exit.flag (UTC timestamp) at the top of OnExit.
// Fresh flag = the user closed the app; stale/missing = crash.
static bool GracefulExitSignaled(string flagPath)
{
    try
    {
        if (!File.Exists(flagPath))
            return false;

        var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(flagPath);
        return age < TimeSpan.FromMinutes(2);
    }
    catch (Exception ex)
    {
        Log.Warning(ex, "[Guardian] Graceful-exit flag unavailable");
        return false;
    }
}

/// <summary>
/// Names shared with the main app (must match App.xaml.cs exactly).
/// </summary>
internal static class GuardianConstants
{
    internal const string GuardianMutexName = "UniwillControlUtility.Guardian";
    internal const string AppMutexName = "UniwillControlUtility";
}
