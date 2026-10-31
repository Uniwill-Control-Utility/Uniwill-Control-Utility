using Microsoft.Win32;
using System;
using System.Diagnostics;
using System.IO;
using Uniwill_Control_Utility.Services.Performance;

namespace Uniwill_Control_Utility.Services
{
    /// <summary>
    /// RTSS presence, separate from RTSS liveness. The prerequisite witness is
    /// RTSS's own uninstall entry: the RTSS installer is 32-bit, so on 64-bit
    /// Windows the key lives under WOW6432Node - both registry views are read
    /// (the running-only check this replaces told an installed RTSS that was
    /// not in the boot set "Required Software", on a machine that had it).
    /// A live process or the shared memory segment is self-evidencing, so
    /// either also counts as installed. Registry reads are swallowed to false
    /// - the detection-service rule the PawnIO detection follows.
    /// </summary>
    public static class RtssDetectionService
    {
        private const string UninstallKeyPath =
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\RTSS";

        /// <summary>True when RTSS is installed, running state irrelevant.</summary>
        public static bool IsInstalled()
        {
            return ExistsIn(RegistryView.Registry64)
                || ExistsIn(RegistryView.Registry32)
                || IsRunning();
        }

        /// <summary>True when RTSS is live: its process, or its shared-memory segment.</summary>
        public static bool IsRunning()
        {
            try
            {
                if (Process.GetProcessesByName("RTSS").Length > 0)
                    return true;
            }
            catch (Exception)
            {
                // A process snapshot that fails tells nothing; the shared memory decides.
            }

            return RtssReader.IsRunning();
        }

        /// <summary>
        /// The RTSS executable, for starting it. The witnesses, in order of
        /// trust: a running RTSS process (its own image path - the truth),
        /// then the uninstall entry's InstallLocation and DisplayIcon read in
        /// BOTH registry views (the RTSS installer is 32-bit, so the 64-bit
        /// view alone would miss real installs), then the install default.
        /// The field probe of a real 7.3.7 install is why the registry
        /// witnesses are secondary and guarded: InstallLocation is EMPTY and
        /// DisplayIcon names uninstall.exe, so an unguarded DisplayIcon would
        /// make the Start button launch RTSS's own uninstaller. Only a path
        /// that exists as a file is returned - a located path is launchable
        /// by contract.
        /// </summary>
        public static string? LocateExe()
        {
            string? running = RunningExePath();
            if (running != null)
                return running;

            foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using RegistryKey? key = RegistryKey
                        .OpenBaseKey(RegistryHive.LocalMachine, view)
                        .OpenSubKey(UninstallKeyPath);

                    if (key == null)
                        continue;

                    string? location = CleanPath(key.GetValue("InstallLocation") as string);
                    if (location != null)
                    {
                        string candidate = Path.Combine(location, "RTSS.exe");

                        if (File.Exists(candidate))
                            return Path.GetFullPath(candidate);
                    }

                    // DisplayIcon is the Add/Remove Programs icon: on real RTSS
                    // installs it names uninstall.exe. Accept it only when it
                    // names RTSS.exe - the Start button must never run an
                    // uninstaller.
                    string? icon = CleanIconPath(key.GetValue("DisplayIcon") as string);
                    if (icon != null
                        && string.Equals(Path.GetFileName(icon), "RTSS.exe", StringComparison.OrdinalIgnoreCase)
                        && File.Exists(icon))
                    {
                        return Path.GetFullPath(icon);
                    }
                }
                catch (Exception)
                {
                    // Location probing never outlives its read - next witness.
                }
            }

            const string Default =
                @"C:\Program Files (x86)\RivaTuner Statistics Server\RTSS.exe";

            return File.Exists(Default) ? Default : null;
        }

        /// <summary>The image path of a running RTSS, or null (field-proven: a 64-bit reader can name a 32-bit RTSS's image).</summary>
        private static string? RunningExePath()
        {
            try
            {
                foreach (Process process in Process.GetProcessesByName("RTSS"))
                {
                    using (process)
                    {
                        try
                        {
                            string? path = process.MainModule?.FileName;

                            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                                return Path.GetFullPath(path);
                        }
                        catch (Exception)
                        {
                            // A module read a security product denies: next process.
                        }
                    }
                }
            }
            catch (Exception)
            {
                // A process snapshot that fails tells nothing.
            }

            return null;
        }

        private static string? CleanPath(string? raw)
        {
            string? path = raw == null ? null : raw.Trim().Trim('"');
            return string.IsNullOrWhiteSpace(path) ? null : path;
        }

        /// <summary>"C:\...\RTSS.exe,0" - the icon-index suffix is not part of the path.</summary>
        private static string? CleanIconPath(string? raw)
        {
            string? path = CleanPath(raw);
            if (path == null)
                return null;

            int comma = path.LastIndexOf(',');
            if (comma > 1)
                path = path.Substring(0, comma).Trim();

            return string.IsNullOrWhiteSpace(path) ? null : path;
        }

        private static bool ExistsIn(RegistryView view)
        {
            try
            {
                using RegistryKey? key = RegistryKey
                    .OpenBaseKey(RegistryHive.LocalMachine, view)
                    .OpenSubKey(UninstallKeyPath);

                return key != null;
            }
            catch (Exception)
            {
                // Detection never outlives its read.
                return false;
            }
        }
    }
}
