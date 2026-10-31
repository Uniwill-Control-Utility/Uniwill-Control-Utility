using Microsoft.Win32;
using System;
using System.Diagnostics;
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
