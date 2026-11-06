using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using System;
using System.Runtime.InteropServices;
using UCU.Core.Supervision;

namespace Uniwill_Control_Utility.Services
{
    public static class PawnIoDetectionService
    {
        private static readonly string OldDevicePath = @"\\.\PawnIO";
        private static readonly string DevicePath = @"\\?\GLOBALROOT\Device\PawnIO";

        // HVCI truth (U-110): the DeviceGuard scenario key Memory Integrity
        // writes; Enabled = 1 means the policy is ON. Standard users can read
        // it — the diagnostic never needs elevation.
        private const string DeviceGuardKeyPath =
            @"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity";

        // PawnIO's own uninstall entry — the "driver is installed" witness
        // (the same key the AMD/Intel PawnIO helpers read for the version).
        private const string PawnIoUninstallKeyPath =
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO";

        private const uint GENERIC_READ = 0x80000000;
        private const uint FILE_SHARE_READ = 0x00000001;
        private const uint FILE_SHARE_WRITE = 0x00000002;
        private const uint OPEN_EXISTING = 3;
        private const uint FILE_ATTRIBUTE_NORMAL = 0x00000080;

        private const int ERROR_FILE_NOT_FOUND = 2;
        private const int ERROR_PATH_NOT_FOUND = 3;
        private const int ERROR_ACCESS_DENIED = 5;

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern SafeFileHandle CreateFile(
            string lpFileName,
            uint dwDesiredAccess,
            uint dwShareMode,
            IntPtr lpSecurityAttributes,
            uint dwCreationDisposition,
            uint dwFlagsAndAttributes,
            IntPtr hTemplateFile);

        public static bool IsInstalled()
        {
            PawnIoDetectionResult result = GetStatus();

            return result == PawnIoDetectionResult.Available ||
                   result == PawnIoDetectionResult.AccessDenied;
        }

        public static PawnIoDetectionResult GetStatus()
        {
            PawnIoDetectionResult newPathResult = CheckDevicePath(DevicePath);

            if (newPathResult == PawnIoDetectionResult.Available ||
                newPathResult == PawnIoDetectionResult.AccessDenied)
            {
                return newPathResult;
            }

            PawnIoDetectionResult oldPathResult = CheckDevicePath(OldDevicePath);

            if (oldPathResult == PawnIoDetectionResult.Available ||
                oldPathResult == PawnIoDetectionResult.AccessDenied)
            {
                return oldPathResult;
            }

            if (newPathResult == PawnIoDetectionResult.UnknownError)
                return newPathResult;

            if (oldPathResult == PawnIoDetectionResult.UnknownError)
                return oldPathResult;

            return PawnIoDetectionResult.NotFound;
        }

        public static bool ShouldOpenInstaller()
        {
            return !IsInstalled();
        }

        /// <summary>
        /// The U-110 diagnostic: why the device is absent. A device that
        /// EXISTS (openable, or access-denied to this token) is not blocked —
        /// only a missing device is explained. UCU installs through the
        /// vendor installer, so there is usually no StartService code to
        /// read: the installed-driver + Memory-Integrity-ON + device-absent
        /// inference is the honest shape (policy:
        /// <see cref="PawnIoDriverBlockPolicy"/>; OpenRevo's driver manager
        /// names the hard 1275 the same way).
        /// </summary>
        public static PawnIoBlockReason GetBlockReason()
        {
            PawnIoDetectionResult status = GetStatus();

            if (status == PawnIoDetectionResult.Available ||
                status == PawnIoDetectionResult.AccessDenied)
            {
                return PawnIoBlockReason.None;
            }

            return PawnIoDriverBlockPolicy.Diagnose(
                startWin32Code: null,
                memoryIntegrityOn: MemoryIntegrityEnabled(),
                driverEntryInstalled: DriverEntryInstalled());
        }

        /// <summary>True when Windows Core Isolation / Memory Integrity is ON (DeviceGuard scenario Enabled = 1).</summary>
        public static bool MemoryIntegrityEnabled()
        {
            try
            {
                using RegistryKey? key = RegistryKey
                    .OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                    .OpenSubKey(DeviceGuardKeyPath);

                return key?.GetValue("Enabled") is int enabled && enabled == 1;
            }
            catch (Exception)
            {
                // A diagnostic must never outlive its registry read.
                return false;
            }
        }

        /// <summary>True when PawnIO's uninstall entry exists (the driver is installed, running is a separate question).</summary>
        public static bool DriverEntryInstalled()
        {
            try
            {
                using RegistryKey? key = RegistryKey
                    .OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                    .OpenSubKey(PawnIoUninstallKeyPath);

                return key != null;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static PawnIoDetectionResult CheckDevicePath(string path)
        {
            using SafeFileHandle handle = CreateFile(
                path,
                GENERIC_READ,
                FILE_SHARE_READ | FILE_SHARE_WRITE,
                IntPtr.Zero,
                OPEN_EXISTING,
                FILE_ATTRIBUTE_NORMAL,
                IntPtr.Zero);

            if (!handle.IsInvalid)
                return PawnIoDetectionResult.Available;

            int errorCode = Marshal.GetLastWin32Error();

            return errorCode switch
            {
                ERROR_FILE_NOT_FOUND => PawnIoDetectionResult.NotFound,
                ERROR_PATH_NOT_FOUND => PawnIoDetectionResult.NotFound,

                ERROR_ACCESS_DENIED => PawnIoDetectionResult.AccessDenied,

                _ => PawnIoDetectionResult.UnknownError
            };
        }
    }

    public enum PawnIoDetectionResult
    {
        Available,
        AccessDenied,
        NotFound,
        UnknownError
    }
}