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
        /// The RTSS executable, for starting it. The uninstall entry's
        /// DisplayIcon ("path,0" icon form stripped) and InstallLocation are
        /// read in both registry views - the RTSS installer is 32-bit, so the
        /// 64-bit view alone would miss real installs - and the install
        /// default is the last witness. Only a file that exists is returned:
        /// a located path is safe to launch.
        /// </summary>
        public static string? LocateExe()
        {
            foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using RegistryKey? key = RegistryKey
                        .OpenBaseKey(RegistryHive.LocalMachine, view)
                        .OpenSubKey(UninstallKeyPath);

                    if (key == null)
                        continue;

                    string? icon = key.GetValue("DisplayIcon") as string;
                    if (!string.IsNullOrWhiteSpace(icon))
                    {
                        // "C:\...\RTSS.exe,0" - the icon-index suffix is not path.
                        int comma = icon.LastIndexOf(',');
                        string path = comma > 1 ? icon.Substring(0, comma) : icon;

                        if (File.Exists(path.Trim().Trim('"')))
                            return Path.GetFullPath(path.Trim().Trim('"'));
                    }

                    string? location = key.GetValue("InstallLocation") as string;
                    if (!string.IsNullOrWhiteSpace(location))
                    {
                        string candidate = Path.Combine(
                            location.Trim().Trim('"'), "RTSS.exe");

                        if (File.Exists(candidate))
                            return Path.GetFullPath(candidate);
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
