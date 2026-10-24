// UCU Uninstall.exe shim — compiled by build\pack.ps1 (csc.exe) into the
// install layout, so the install directory gets a familiar uninstall entry
// point next to the app. It:
//   1. kills running app + guardian (Process.Kill in-proc — milliseconds,
//      taskkill cost ~9.5s per call), then deletes the "UCU" /
//      "UCU Guardian" tasks (all console children run hidden — no
//      flashing terminal windows),
//   2. launches a hidden detached helper that waits for this shim to exit,
//      runs the Apps & Features uninstaller synchronously (blocking
//      msiexec client, so it lasts exactly as long as the wizard needs —
//      no blind sleep, no racing the engine), then deletes the leftover
//      install root (logs, packages).
// Compiled against .NET Framework 4 with the framework csc (C# 5 syntax
// only) — no dependencies beyond the GAC.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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

        // The app's App Paths auto-elevation shim key (registered at startup).
        string shimKey = "\"HKLM\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\App Paths\\Uniwill Control Utility.exe\"";

        string productKey = FindMsiProductKey("Uniwill Control Utility");

        const string hkcuUninstall = "\"HKCU\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\Uniwill-Control-Utility\"";
        const string hklmUninstall = "\"HKLM\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\MSI:Uniwill-Control-Utility\"";

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
        cmds.Add("schtasks /delete /tn UCU /f");
        cmds.Add("schtasks /delete /tn \"UCU Guardian\" /f");
        cmds.Add("reg delete " + shimKey + " /f");
        cmds.Add("reg delete " + hkcuUninstall + " /f");
        cmds.Add("reg delete " + hklmUninstall + " /f");
        if (!string.IsNullOrEmpty(productKey))
            cmds.Add("reg delete \"HKLM\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\" + productKey + "\" /f");
        cmds.Add(Echo("stage4-cleanup-done"));
        cmds.Add("rd /s /q \"" + root + "\"");
        cmds.Add("ping -n 4 127.0.0.1 >nul");
        cmds.Add("rd /s /q \"" + root + "\"");
        cmds.Add(Echo("stage5-done"));

        // Kill running app + guardian directly from this elevated process.
        // taskkill was the ~30s stall: the stage log timed each
        // "taskkill /f /im" at ~9.5s (process enumeration as admin) — two
        // of them plus ping equaled the entire delay before the wizard.
        // Process.Kill is milliseconds. Guardian first: a force-killed app
        // is a "crash" the guardian would relaunch.
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
