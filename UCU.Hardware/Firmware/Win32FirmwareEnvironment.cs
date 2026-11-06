using System.Runtime.InteropServices;
using Serilog;

namespace UCU.Hardware.Firmware
{
    /// <summary>
    /// The production <see cref="IFirmwareEnvironment"/>: the W-variant
    /// firmware-variable calls, each preceded by enabling
    /// SeSystemEnvironmentPrivilege on the current process token. The
    /// privilege demands a privileged token — an unelevated process cannot
    /// enable it — which is the whole reason the NVRAM rows live behind the
    /// engine (LocalSystem enables it, no prompt exists to decline).
    /// </summary>
    public sealed class Win32FirmwareEnvironment : IFirmwareEnvironment
    {
        private const int MAX_VARIABLE_SIZE = 64 * 1024;

        public byte[]? Read(string name, string guid, out uint error)
        {
            EnableSystemEnvironmentPrivilege();

            byte[] buffer = new byte[MAX_VARIABLE_SIZE];
            uint len = Native.GetFirmwareEnvironmentVariable(name, guid, buffer, (uint)buffer.Length);

            if (len == 0)
            {
                error = (uint)Marshal.GetLastWin32Error();
                return null;
            }

            error = 0;
            byte[] result = new byte[len];
            Array.Copy(buffer, result, len);
            return result;
        }

        public bool Write(string name, string guid, byte[] data, out uint error)
        {
            EnableSystemEnvironmentPrivilege();

            if (!Native.SetFirmwareEnvironmentVariable(name, guid, data, (uint)data.Length))
            {
                error = (uint)Marshal.GetLastWin32Error();
                return false;
            }

            error = 0;
            return true;
        }

        /// <summary>
        /// Enables SeSystemEnvironmentPrivilege on the current process token.
        /// Cheap and idempotent; asserted before every access because the
        /// privilege is per-token and the engine never relies on a previous
        /// call having left it enabled.
        /// </summary>
        private static void EnableSystemEnvironmentPrivilege()
        {
            if (!Native.OpenProcessToken(Native.GetCurrentProcess(), Native.TOKEN_ADJUST_PRIVILEGES, out IntPtr token))
            {
                Log.Information("[FwNvram] OpenProcessToken failed: {Error}", Marshal.GetLastWin32Error());
                return;
            }

            try
            {
                Native.TOKEN_PRIVILEGES tp = new()
                {
                    Count = 1,
                    Luid = default,
                    Attributes = Native.SE_PRIVILEGE_ENABLED,
                };

                if (!Native.LookupPrivilegeValue(null, "SeSystemEnvironmentPrivilege", ref tp.Luid))
                {
                    Log.Information("[FwNvram] LookupPrivilegeValue failed: {Error}", Marshal.GetLastWin32Error());
                    return;
                }

                if (!Native.AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero))
                {
                    Log.Information("[FwNvram] AdjustTokenPrivileges failed: {Error}", Marshal.GetLastWin32Error());
                }
                else if (Marshal.GetLastWin32Error() == Native.ERROR_PRIVILEGE_NOT_HELD)
                {
                    // The token this process runs with does not hold the privilege
                    // at all (an app-side process): the read below answers null and
                    // the caller's fallback route decides what that means.
                    Log.Information("[FwNvram] token does not hold SeSystemEnvironmentPrivilege");
                }
            }
            finally
            {
                Native.CloseHandle(token);
            }
        }
    }
}
