using System.ComponentModel;
using System.Runtime.InteropServices;
using Serilog;

namespace UCU.Hardware.Firmware
{
    /// <summary>
    /// The seam the NVRAM verbs run on: the production implementation reads and
    /// writes UEFI firmware environment variables with SeSystemEnvironmentPrivilege
    /// enabled on the process token (which only a privileged token can do); the
    /// RPC tests inject a scripted variable store, because no test may write a
    /// real machine's firmware.
    /// </summary>
    public interface IFwNvramService
    {
        /// <summary>The variable's bytes, or null when the variable does not
        /// exist (the same shape the app's direct reads used).</summary>
        byte[]? Read(string name, string guid);

        bool Write(string name, string guid, byte[] data);

        /// <summary>Firmware-reported support byte for the CPU Advanced
        /// Performance Menu (OemMagicVariable offset 110, non-zero = the BIOS
        /// exposes the menu; Intel NB02 = 1, AMD = 0).</summary>
        bool SupportsCpuApMenu();

        /// <summary>Unlocks/locks the BIOS CPU Advanced Performance Menu by
        /// patching offsets 111 (CPU menu switch) and 51 (memory OC switch) in
        /// lockstep. Read-patch-write of the whole entry on one call: the pair
        /// can never half-update. True when the write landed, verified by
        /// re-reading the offsets.</summary>
        bool SetCpuApMenuUnlock(bool enabled);
    }

    /// <summary>
    /// UEFI firmware environment variables through the Win32 firmware-variable
    /// APIs. These demand SeSystemEnvironmentPrivilege, which an unelevated
    /// process cannot enable — the reason every app-side use of them (the CPU
    /// Advanced Performance Menu row, the GPU mux's firmware-visible mirror)
    /// was silently dead on the field machines: the reads returned nothing and
    /// the writes failed with access denied, with no prompt to explain. The
    /// engine runs LocalSystem, enables the privilege on its own token, and
    /// answers the same reads and writes as designed.
    ///
    /// The OEM entry's layout (which offset is which feature) is machine data
    /// the app owns; this service keeps only the two offsets whose pair-write
    /// must be atomic (the AP-menu unlock) and the support byte that gates it.
    /// </summary>
    public sealed class FwNvramService : IFwNvramService
    {
        public const string OemMagicVariable = "OemMagicVariable";
        public const string OemMagicVariableGuid = "{9F33F85C-13CA-4FD1-9C4A-96217722C593}";

        public const int OffsetCpuApMenuSupport = 110;
        public const int OffsetCpuApMenuSwitch = 111;
        public const int OffsetMemOcSwitch = 51;

        private const uint ERROR_SUCCESS = 0;
        private const uint ERROR_ENVVAR_NOT_FOUND = 203;
        private const int MAX_VARIABLE_SIZE = 64 * 1024;

        public byte[]? Read(string name, string guid)
        {
            EnableSystemEnvironmentPrivilege();

            byte[] buffer = new byte[MAX_VARIABLE_SIZE];
            uint len = Native.GetFirmwareEnvironmentVariable(name, guid, buffer, (uint)buffer.Length);

            if (len == 0)
            {
                uint error = (uint)Marshal.GetLastWin32Error();
                if (error != ERROR_ENVVAR_NOT_FOUND)
                {
                    // The honest signal, which the app-side reads never carried:
                    // "no bytes" has reasons (privilege, missing variable, bad
                    // buffer), and the field's question is always which one.
                    Log.Information("[FwNvram] read {Name} failed: win32 error {Error}", name, error);
                }
                return null;
            }

            byte[] result = new byte[len];
            Array.Copy(buffer, result, len);
            return result;
        }

        public bool Write(string name, string guid, byte[] data)
        {
            EnableSystemEnvironmentPrivilege();

            if (!Native.SetFirmwareEnvironmentVariable(name, guid, data, (uint)data.Length))
            {
                Log.Information("[FwNvram] write {Name} ({Bytes} bytes) failed: win32 error {Error}",
                    name, data.Length, Marshal.GetLastWin32Error());
                return false;
            }

            Log.Information("[FwNvram] write {Name} ok ({Bytes} bytes)", name, data.Length);
            return true;
        }

        public bool SupportsCpuApMenu()
        {
            byte[]? nvram = Read(OemMagicVariable, OemMagicVariableGuid);
            return nvram is not null
                && nvram.Length > OffsetCpuApMenuSupport
                && nvram[OffsetCpuApMenuSupport] != 0;
        }

        public bool SetCpuApMenuUnlock(bool enabled)
        {
            byte[]? nvram = Read(OemMagicVariable, OemMagicVariableGuid);
            if (nvram is null || nvram.Length <= OffsetCpuApMenuSwitch)
            {
                Log.Information("[FwNvram] OemMagicVariable unavailable for AP menu write");
                return false;
            }

            byte[] modified = (byte[])nvram.Clone();
            modified[OffsetCpuApMenuSwitch] = (byte)(enabled ? 1 : 0);
            modified[OffsetMemOcSwitch] = (byte)(enabled ? 1 : 0);

            if (!Write(OemMagicVariable, OemMagicVariableGuid, modified))
                return false;

            // Verify by re-read: the entry survives a firmware write only if the
            // offsets say so afterwards, and the row's toggle should flip on the
            // firmware's answer, not on the write call's.
            byte[]? check = Read(OemMagicVariable, OemMagicVariableGuid);
            bool verified = check is not null
                && check.Length > OffsetCpuApMenuSwitch
                && check[OffsetCpuApMenuSwitch] == (byte)(enabled ? 1 : 0)
                && check[OffsetMemOcSwitch] == (byte)(enabled ? 1 : 0);

            Log.Information("[FwNvram] AP menu unlock {State}: verified={Verified}",
                enabled ? "on" : "off", verified);
            return verified;
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
