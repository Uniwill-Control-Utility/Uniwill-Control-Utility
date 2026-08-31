using Microsoft.Win32.SafeHandles;
using System;
using System.Runtime.InteropServices;

namespace Uniwill_Control_Utility.Services
{
    public static class PawnIoDetectionService
    {
        private static readonly string OldDevicePath = @"\\.\PawnIO";
        private static readonly string DevicePath = @"\\?\GLOBALROOT\Device\PawnIO";

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