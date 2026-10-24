// UCU Uninstall.exe shim — compiled by build\pack.ps1 (csc.exe) into the
// install layout, so the install directory gets a familiar uninstall entry
// point next to the app. It:
//   1. kills running app + guardian, deletes the "UCU" / "UCU Guardian" tasks,
//   2. looks up the MSI product in Apps & Features and runs its uninstall,
//   3. deletes the leftover install root (logs, packages) from a detached
//      cmd that starts after this shim has exited.
// Compiled against .NET Framework 4 with the framework csc (C# 5 syntax
// only) — no dependencies beyond the GAC.
using System;
using System.Diagnostics;
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

        // Stop the app and guardian so the MSI doesn't hit files-in-use,
        // and drop the logon tasks they registered.
        Run("taskkill.exe", "/f /im \"Uniwill Control Utility.exe\"");
        Run("taskkill.exe", "/f /im \"Uniwill Control Utility Service.exe\"");
        Run("schtasks.exe", "/delete /tn UCU /f");
        Run("schtasks.exe", "/delete /tn \"UCU Guardian\" /f");

        string uninstall = FindMsiUninstallString("Uniwill Control Utility");

        // Detached chain: let this shim exit (it lives in the tree we delete),
        // run the MSI uninstall quietly, wait it out, then remove leftovers.
        string msi = string.IsNullOrEmpty(uninstall)
            ? "echo no-registered-installer"
            : uninstall + " /qb";
        string cmd = string.Format(
            "ping -n 4 127.0.0.1 >nul & {0} & ping -n 60 127.0.0.1 >nul & rd /s /q \"{1}\"",
            msi, root);
        var psi = new ProcessStartInfo("cmd.exe", "/c \"" + cmd + "\"");
        psi.UseShellExecute = true;
        psi.CreateNoWindow = true;
        Process.Start(psi);
        return 0;
    }

    private static void Run(string file, string args)
    {
        try
        {
            var psi = new ProcessStartInfo(file, args);
            psi.UseShellExecute = true;
            psi.CreateNoWindow = true;
            Process p = Process.Start(psi);
            if (p != null)
                p.WaitForExit(30000);
        }
        catch
        {
            // Missing task/process is exactly the success case.
        }
    }

    private static string FindMsiUninstallString(string displayName)
    {
        RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using (RegistryKey uninstall = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"))
        {
            if (uninstall == null)
                return null;

            foreach (string name in uninstall.GetSubKeyNames())
            {
                using (RegistryKey entry = uninstall.OpenSubKey(name))
                {
                    if (entry == null)
                        continue;

                    string dn = entry.GetValue("DisplayName") as string;
                    if (dn == displayName)
                        return entry.GetValue("UninstallString") as string;
                }
            }
        }

        return null;
    }
}
