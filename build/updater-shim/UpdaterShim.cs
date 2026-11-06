// UCU Updater.exe shim — compiled by build\pack.ps1 (csc.exe) into the
// install layout, next to the app and Uninstall.exe. It performs the
// per-machine update apply that the unelevated app cannot:
//   1. stops the UCUEngine service (a running service locks the Service\
//      payload the update replaces),
//   2. runs Velopack's own self-updater (Uninstall.exe -a --from-update —
//      apply, then relaunch the app with the from-update marker),
//   3. starts the engine service again.
// Stamped requireAdministrator (the same admin.manifest as Uninstall.exe):
// writing the per-machine layout needs elevation, and this shim is the only
// process in the update flow that needs it — the app itself stays unelevated.
// The app routes ApplyAndRestart/TryApplyOnExit through this file when it
// exists (Services\VelopackUpdateService.cs).
// Compiled against .NET Framework 4 with the framework csc (C# 5 syntax
// only) — no dependencies beyond the GAC.
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

internal static class UpdaterShim
{
    private const int ErrorServiceAlreadyStopped = 1062;
    private const int ErrorServiceDoesNotExist = 1060;
    private const int ErrorServiceAlreadyRunning = 1056;
    private const int ErrorServiceNotStarted = 1052;

    private static int Main(string[] args)
    {
        string root = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
        string uninstaller = Path.Combine(root, "Uninstall.exe");

        // The service name is an argument so the shim survives a rename; the
        // default is what pack.ps1 ships.
        string serviceName = args.Length > 0 ? args[0] : "UCUEngine";

        if (!File.Exists(uninstaller))
            return 2; // not an install layout — nothing staged, nothing to apply

        // 1. Stop the engine. 1062 (already stopped) and 1060 (a pre-engine
        //    install) are fine; anything else proceeds anyway — the apply's
        //    file operations will surface a real problem.
        RunSc("stop", serviceName);
        Thread.Sleep(2000); // let the service's file handles close

        // 2. The Velopack apply itself, synchronously: Uninstall.exe -a
        //    replaces the layout, then relaunches the app with --from-update.
        //    WorkingDirectory is the install root (Uninstall.exe resolves the
        //    package ids relative to its own directory).
        int rc = Run(uninstaller, "-a --from-update", root);

        // 3. Start the engine on the new payload.
        RunSc("start", serviceName);
        return rc;
    }

    // sc.exe, hidden, bounded: a wedged service must not hang the update.
    private static int RunSc(string verb, string serviceName)
    {
        var psi = new ProcessStartInfo("sc.exe", verb + " " + serviceName);
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.WorkingDirectory = Environment.SystemDirectory;

        try
        {
            using (Process proc = Process.Start(psi))
            {
                if (proc == null)
                    return -1;
                if (!proc.WaitForExit(15000))
                {
                    try { proc.Kill(); }
                    catch { }
                    return -1;
                }
                return proc.ExitCode;
            }
        }
        catch
        {
            return -1;
        }
    }

    // Runs a process to completion, hidden, returning its exit code (-1 when
    // it could not start).
    private static int Run(string fileName, string arguments, string workingDirectory)
    {
        var psi = new ProcessStartInfo(fileName, arguments);
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.WorkingDirectory = workingDirectory;

        try
        {
            using (Process proc = Process.Start(psi))
            {
                if (proc == null)
                    return -1;
                proc.WaitForExit();
                return proc.ExitCode;
            }
        }
        catch
        {
            return -1;
        }
    }
}
