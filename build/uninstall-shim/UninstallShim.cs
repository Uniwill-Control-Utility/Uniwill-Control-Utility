// UCU Uninstall.exe shim — compiled by build\pack.ps1 (csc.exe) into the
// install layout, so the install directory gets a familiar uninstall entry
// point next to the app. It:
//   1. stops the UCUEngine service (before killing the app: a force-killed
//      app is a crash the engine's supervisor would relaunch), kills running
//      app + legacy guardian (Process.Kill in-proc — milliseconds, taskkill
//      cost ~9.5s per call), then deletes the "UCU Autostart" task, the legacy
//      "UCU" / "UCU Guardian" tasks, and the UCUEngine service registration
//      (all console children run hidden — no flashing terminal windows),
//   2. launches a hidden detached helper that waits for this shim to exit,
//      runs the Apps & Features uninstaller synchronously (blocking
//      msiexec client, so it lasts exactly as long as the wizard needs —
//      no blind sleep, no racing the engine), then deletes the leftover
//      install root (logs, packages) and the engine's %ProgramData% log dir.
// Compiled against .NET Framework 4 with the framework csc (C# 5 syntax
// only) — no dependencies beyond the GAC.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

internal static class UninstallShim
{
    [STAThread]
    private static int Main()
    {
        string root = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');

        DialogResult answer = MessageBox.Show(
            "Remove Uniwill Control Utility and all of its program files?\n\n" +
            "Your settings are kept in your user profile.",
            "Uninstall Uniwill Control Utility",
            MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (answer != DialogResult.Yes)
            return 0;

        // The app's legacy App Paths auto-elevation shim key: registration was
        // retired with the requireAdministrator manifest (phase 4), this
        // deletes the key on machines that ran the old builds. Best-effort.
        string shimKey = "\"HKLM\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\App Paths\\Uniwill Control Utility.exe\"";

        string productKey = FindMsiProductCode("Uniwill Control Utility", root.TrimEnd('\\'));

        const string hkcuUninstall = "\"HKCU\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\Uniwill-Control-Utility\"";
        const string hklmUninstall = "\"HKLM\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\MSI:Uniwill-Control-Utility\"";
        const string hklmShimEntry = "\"HKLM\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\Uniwill-Control-Utility\"";

        // The hidden helper's command list, joined with " & " — a strict
        // sequence, one command per element (string concatenation of the
        // fragments twice glued one command into the text of the next).
        // Elevation is inherited from this elevated shim; the helper's CWD
        // is the system dir (Windows refuses to delete a process CWD).
        // Flow: msiexec /x (the client blocks until the wizard completes —
        // a start /wait wrapper returns as soon as the client hands off to
        // the Installer service and races the engine), then tasks/shim-key/
        // ARP deletions (the ARP entries survive the MSI's own removal:
        // Velopack writes HKCU\...\Uninstall\<packId> and removes it in a
        // deferred CA context that misses the user's HKCU; the MSI
        // product-key display entry can be orphaned), then rd sweeps of the
        // install root with a retry — the app exes are locked for a moment
        // after the force-kill (AV scanning the just-killed images), and a
        // single rd leaves them behind.
        // !date!/!time! breadcrumbs (delayed expansion, /V:ON — %time%
        // expands at cmd parse time, not per-command) land in
        // %TEMP%\ucu_uninstall_log.txt to time every stage.
        var cmds = new List<string>
        {
            Echo("stage1-helper-alive"),
            "ping -n 2 127.0.0.1 >nul",
            Echo("stage2-msiexec-start"),
        };
        if (!string.IsNullOrEmpty(productKey))
            cmds.Add("msiexec.exe /x " + productKey + " /qb");
        cmds.Add(Echo("stage3-msiexec-exit rc=!ERRORLEVEL!"));
        // The engine verb sweep, as SYSTEM via a one-shot scheduled task:
        // guarded machines label the installed layout System-IL, so this
        // elevated helper's rd cannot delete the files and only SYSTEM can
        // (the verb destroys the layout, the service, tasks, husks, and
        // ProgramData logs - it also deletes this task). On plain machines
        // the helper's own rd below is the faster path; both are idempotent.
        // The task command goes through a script file because schtasks /tr
        // values cannot carry \" escapes (cmd does not honor them), while a
        // script line parses its own quotes correctly.
        string verbExe = root + "\\Service\\Uniwill Control Utility Engine.exe";
        string verbScript = "%TEMP%\\ucu-verb-task.cmd";
        cmds.Add("echo @echo off> \"" + verbScript + "\"");
        cmds.Add("echo \"" + verbExe + "\" --uninstall-engine >> \"" + verbScript + "\"");
        cmds.Add("schtasks /create /tn \"UCU Uninstall Verb\" /tr \"cmd.exe /c " +
            verbScript + "\" /sc once /st 23:59 /ru SYSTEM /f");
        cmds.Add("schtasks /run /tn \"UCU Uninstall Verb\"");
        cmds.Add("schtasks /delete /tn UCU /f");
        cmds.Add("schtasks /delete /tn \"UCU Autostart\" /f");
        cmds.Add("schtasks /delete /tn \"UCU Guardian\" /f");
        cmds.Add("sc delete UCUEngine");
        cmds.Add("reg delete " + shimKey + " /f");
        cmds.Add("reg delete " + hkcuUninstall + " /f");
        cmds.Add("reg delete " + hklmUninstall + " /f");
        cmds.Add("reg delete " + hklmShimEntry + " /f");
        if (!string.IsNullOrEmpty(productKey))
            cmds.Add("reg delete \"HKLM\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\" + productKey + "\" /f");
        cmds.Add(Echo("stage4-cleanup-done"));
        cmds.Add("rd /s /q \"%ProgramData%\\UCU\"");
        cmds.Add("rd /s /q \"" + root + "\"");
        cmds.Add("ping -n 4 127.0.0.1 >nul");
        cmds.Add("rd /s /q \"" + root + "\"");
        cmds.Add(Echo("stage5-done"));

        // Stop the engine service first, synchronously: a force-killed app
        // looks like a crash to the engine, which relaunches it (supervision),
        // and the app's startup would re-register the service we are about to
        // delete. sc stop is the clean path (no SCM failure-recovery restart).
        // 1062/1060 (already stopped / absent, e.g. pre-engine installs) are
        // benign; the helper's "sc delete" removes the registration.
        RunHidden("sc.exe", "stop UCUEngine", 15000);
        Thread.Sleep(2000); // let the service's file handles close

        // Kill running app + legacy guardian directly from this elevated
        // process (the guardian shipped in older builds; killing it here
        // sweeps installs where its task outlived App.RetireGuardianTask).
        // taskkill was the ~30s stall: the stage log timed each
        // "taskkill /f /im" at ~9.5s (process enumeration as admin) — two
        // of them plus ping equaled the entire delay before the wizard.
        // Process.Kill is milliseconds.
        KillProcess("Uniwill Control Utility Service");
        KillProcess("Uniwill Control Utility");

        AppendLog("stage0-shim-spawning");
        var psi = new ProcessStartInfo("cmd.exe", "/V:ON /c " + string.Join(" & ", cmds.ToArray()));
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.WorkingDirectory = Environment.SystemDirectory;
        Process.Start(psi);
        return 0;
    }

    // Force-kill every process with this base name (no .exe); already-gone
    // (the normal case) completes without a trace.
    private static void KillProcess(string name)
    {
        try
        {
            foreach (Process process in Process.GetProcessesByName(name))
            {
                try
                {
                    process.Kill();
                }
                catch
                {
                }
            }
        }
        catch
        {
        }
    }

    // Run a console command hidden, waiting up to timeoutMs (a wedged
    // service must not hang the uninstall UI). Failures are silent: the
    // helper's later cleanup steps are the safety net.
    private static void RunHidden(string fileName, string arguments, int timeoutMs)
    {
        try
        {
            var psi = new ProcessStartInfo(fileName, arguments);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.WorkingDirectory = Environment.SystemDirectory;

            using (Process proc = Process.Start(psi))
            {
                if (proc == null)
                    return;
                if (!proc.WaitForExit(timeoutMs))
                {
                    try { proc.Kill(); }
                    catch { }
                }
            }
        }
        catch
        {
        }
    }

    // Timestamped cmd breadcrumb (diagnosing uninstall wall time per
    // stage). Must be one standalone command — embedding the echo in the
    // text of a neighbouring command makes it print as output, not run.
    // Uses delayed expansion (!time! under /V:ON): cmd expands %time% at
    // PARSE time of the whole /c line, so %time% breadcrumbs all show the
    // same instant.
    private static string Echo(string name)
    {
        return "echo " + name + " !date! !time! >> \"%TEMP%\\ucu_uninstall_log.txt\"";
    }

    private static void AppendLog(string name)
    {
        try
        {
            System.IO.File.AppendAllText(
                Path.Combine(Path.GetTempPath(), "ucu_uninstall_log.txt"),
                name + " " + DateTime.Now + Environment.NewLine);
        }
        catch
        {
        }
    }

    // The MSI product code (a GUID) for this product. Two sources, in order:
    // the ARP GUID key whose DisplayName matches (what shipped before), and
    // the MSI database itself via MsiEnumProductsW. The second source is why
    // this exists: the packed MSI suppresses its ARP display entry
    // (ARPSYSTEMCOMPONENT=1), so FindMsiProductKey alone finds nothing on a
    // fresh install and the shim would skip "msiexec /x" - leaving the
    // registration, the cached package and the service behind. The shim runs
    // elevated, so the MSI enumeration reads the unobstructed machine view
    // (the guarded-box field evidence: only unelevated processes get the
    // registry blinded). Match is by ProductName, and the install-location
    // attribute prefers the registration that owns THIS layout, so a stale
    // per-user ghost of the same name is never the one we /x.
    private static string FindMsiProductCode(string displayName, string root)
    {
        string fromArp = FindMsiProductKey(displayName);
        if (!string.IsNullOrEmpty(fromArp))
            return fromArp;

        string first = null;
        StringBuilder product = new StringBuilder(39);
        for (int index = 0; index < 4096; index++)
        {
            if (MsiEnumProductsW(index, product) != 0)
                break;

            string candidate = product.ToString();
            product.Clear();
            if (candidate.Length == 0)
                continue;

            StringBuilder name = new StringBuilder(256);
            uint nameLength = 256;
            if (MsiGetProductInfoW(candidate, "ProductName", name, ref nameLength) != 0 ||
                !string.Equals(name.ToString(), displayName, StringComparison.OrdinalIgnoreCase))
                continue;

            StringBuilder location = new StringBuilder(260);
            uint locationLength = 260;
            if (MsiGetProductInfoW(candidate, "InstalledLocation", location, ref locationLength) == 0 &&
                string.Equals(location.ToString().TrimEnd('\\'), root, StringComparison.OrdinalIgnoreCase))
                return candidate;

            if (first == null)
                first = candidate;
        }

        return first;
    }

    [System.Runtime.InteropServices.DllImport("msi.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int MsiEnumProductsW(int iProductIndex, StringBuilder lpProductBuf);

    [System.Runtime.InteropServices.DllImport("msi.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int MsiGetProductInfoW(
        string szProduct, string szAttribute, StringBuilder lpValueBuf, ref uint pcchValueBuf);

    // The MSI product registration key (a GUID) whose DisplayName matches.
    // We build "msiexec.exe /x {GUID}" from it and delete the key if the
    // MSI's own sequence leaves the display entry orphaned.
    private static string FindMsiProductKey(string displayName)
    {
        RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using (RegistryKey uninstall = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"))
        {
            if (uninstall == null)
                return null;

            foreach (string name in uninstall.GetSubKeyNames())
            {
                // MSI product keys are GUIDs: {XXXXXXXX-XXXX-...}
                if (name.Length != 38 || name[0] != '{' || name[37] != '}')
                    continue;

                using (RegistryKey entry = uninstall.OpenSubKey(name))
                {
                    if (entry == null)
                        continue;

                    string dn = entry.GetValue("DisplayName") as string;
                    if (dn == displayName)
                        return name;
                }
            }
        }

        return null;
    }
}
