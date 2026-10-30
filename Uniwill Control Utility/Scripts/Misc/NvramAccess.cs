using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Serilog;

namespace Uniwill_Control_Utility.Scripts.Misc
{
    /// <summary>
    /// UEFI firmware variable access for the rows that use the OemMagicVariable
    /// NVRAM entry: the CPU Advanced Performance Menu (Settings page, offsets
    /// 111/51, support byte at 110) and the GPU mux's firmware-visible mirror
    /// (Display page, offset 98).
    ///
    /// Reading and writing firmware variables demands
    /// SeSystemEnvironmentPrivilege, which an unelevated process cannot enable —
    /// so on the field machines every app-side use of these was silently dead:
    /// the support probe found nothing, the writes failed with access denied,
    /// and no prompt ever appeared to explain (the direct API answers empty, it
    /// does not ask). The engine runs LocalSystem and enables the privilege, so
    /// the engine's device.nvram verbs are the FIRST route and the rows work as
    /// designed with no prompt anywhere.
    ///
    /// The direct P/Invoke route below is the FALLBACK for a machine whose
    /// engine is not answering — where it works, it works exactly as the old
    /// code did (the app is run elevated, e.g. the dev box). Every switch of
    /// routes logs the reason, so the field can tell which route answered.
    /// </summary>
    internal static class NvramAccess
    {
        /// <summary>
        /// The engine's device capability (NVRAM verbs). Set from DI at startup
        /// (App phase 1) and by the pages, the same static-singleton idiom as
        /// HardwareManagement.Devices.
        /// </summary>
        internal static UCU.Contracts.Capabilities.IDeviceCapability? Devices { get; set; }

        // The OEM entry and the offsets the rows patch. The engine's service
        // carries the same AP-menu offsets for its atomic pair-write verb; the
        // parity is pinned in tests so the routes cannot drift.
        public const string OemMagicName = "OemMagicVariable";
        public const string OemMagicGuid = "{9F33F85C-13CA-4FD1-9C4A-96217722C593}";

        public const int OffsetCpuApMenuSupport = 110;
        public const int OffsetCpuApMenuSwitch = 111;
        public const int OffsetMemOcSwitch = 51;

        /// <summary>
        /// The OemMagicVariable bytes, or null when unavailable (the firmware
        /// has no such entry, the engine is not answering and the local token
        /// cannot read). Callers treat null as "NVRAM unavailable", exactly as
        /// the pages' own probes did.
        /// </summary>
        public static byte[]? ReadOemMagic()
        {
            if (Devices != null)
            {
                try
                {
                    byte[]? data = Devices.GetNvramValueAsync(OemMagicName, OemMagicGuid)
                        .GetAwaiter().GetResult();
                    if (data != null)
                        return data;

                    // The engine answered and the entry is genuinely absent:
                    // that is the truth, not a route failure. No fallback.
                    Log.Information("[Nvram] engine reports {Name} absent", OemMagicName);
                    return null;
                }
                catch (Exception ex)
                {
                    Log.Information("[Nvram] engine read unavailable ({Message}) - using the local route", ex.Message);
                }
            }

            return LocalRead(OemMagicName, OemMagicGuid);
        }

        /// <summary>
        /// Whole-buffer write of the OEM entry. True when the write landed.
        /// </summary>
        public static bool WriteOemMagic(byte[] data)
        {
            if (Devices != null)
            {
                try
                {
                    bool ok = Devices.SetNvramValueAsync(OemMagicName, OemMagicGuid, data)
                        .GetAwaiter().GetResult();
                    Log.Information("[Nvram] {Name} write via engine: ok={Ok}", OemMagicName, ok);
                    return ok;
                }
                catch (Exception ex)
                {
                    Log.Information("[Nvram] engine write unavailable ({Message}) - using the local route", ex.Message);
                }
            }

            return LocalWrite(OemMagicName, OemMagicGuid, data);
        }

        /// <summary>
        /// Firmware-reported support for the CPU Advanced Performance Menu
        /// (support byte at offset 110, non-zero on Intel NB02, zero on AMD).
        /// Gates the Settings row; the probe needs the same privilege as the
        /// write, so it rides the engine verb first.
        /// </summary>
        public static bool SupportsCpuApMenu()
        {
            if (Devices != null)
            {
                try
                {
                    bool supported = Devices.GetSupportsCpuApMenuAsync().GetAwaiter().GetResult();
                    Log.Information("[Nvram] AP menu support via engine: {Supported}", supported);
                    return supported;
                }
                catch (Exception ex)
                {
                    Log.Information("[Nvram] engine support probe unavailable ({Message}) - using the local route", ex.Message);
                }
            }

            byte[]? nvram = LocalRead(OemMagicName, OemMagicGuid);
            return nvram != null && nvram.Length > OffsetCpuApMenuSupport && nvram[OffsetCpuApMenuSupport] != 0;
        }

        /// <summary>
        /// Unlocks/locks the BIOS CPU Advanced Performance Menu: offsets 111
        /// (CPU menu switch) and 51 (memory OC switch) in lockstep,
        /// tester-confirmed on Intel NB02. Through the engine this is one
        /// atomic read-patch-write verb on the device queue, verified by
        /// re-read; the local fallback performs the same pair locally.
        /// </summary>
        public static bool SetCpuApMenuUnlock(bool enabled)
        {
            if (Devices != null)
            {
                try
                {
                    bool verified = Devices.SetCpuApMenuUnlockAsync(enabled).GetAwaiter().GetResult();
                    Log.Information("[Nvram] AP menu unlock {State} via engine: verified={Verified}",
                        enabled ? "on" : "off", verified);
                    return verified;
                }
                catch (Exception ex)
                {
                    Log.Information("[Nvram] engine AP menu write unavailable ({Message}) - using the local route", ex.Message);
                }
            }

            byte[]? nvram = LocalRead(OemMagicName, OemMagicGuid);
            if (nvram == null || nvram.Length <= OffsetCpuApMenuSwitch)
            {
                Debug.WriteLine($"[Nvram] {OemMagicName} unavailable for AP menu write");
                return false;
            }

            byte[] modified = (byte[])nvram.Clone();
            modified[OffsetCpuApMenuSwitch] = (byte)(enabled ? 1 : 0);
            modified[OffsetMemOcSwitch] = (byte)(enabled ? 1 : 0);

            bool ok = LocalWrite(OemMagicName, OemMagicGuid, modified);
            Debug.WriteLine($"[Nvram] AP menu local write: ok={ok}, offsets {OffsetCpuApMenuSwitch}/{OffsetMemOcSwitch} set to {(enabled ? 1 : 0)}");
            return ok;
        }

        #region Local route (fallback: engine not answering; works when the app runs elevated)

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetFirmwareEnvironmentVariableW")]
        private static extern uint GetFirmwareEnvironmentVariable(
            string lpName, string lpGuid, [Out] byte[] lpBuffer, uint nSize);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "SetFirmwareEnvironmentVariableW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetFirmwareEnvironmentVariable(
            string lpName, string lpGuid, [In] byte[] lpValue, uint nSize);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(IntPtr h, uint d, out IntPtr t);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool LookupPrivilegeValue(string? lpSystemName, string lpName, ref long lpLuid);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool AdjustTokenPrivileges(IntPtr t, bool d, ref TP tp, uint r1, IntPtr r2, IntPtr r3);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr h);

        [StructLayout(LayoutKind.Sequential)]
        private struct TP
        {
            public uint Count;
            public long Luid;
            public uint Attr;
        }

        private const uint TOKEN_ADJUST_PRIVILEGES = 0x20;
        private const uint SE_PRIVILEGE_ENABLED = 0x00000002;

        /// <summary>
        /// Enables SeSystemEnvironmentPrivilege on this process token, the
        /// elevation the local route needs. An asInvoker token never holds it —
        /// that is the silent-death condition this class exists to route around;
        /// the privilege adjust on such a token is a no-op, not a prompt.
        /// </summary>
        private static void EnableLocalNvramPrivilege()
        {
            if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES, out IntPtr token))
            {
                Debug.WriteLine($"[Nvram] OpenProcessToken failed: {Marshal.GetLastWin32Error()}");
                return;
            }

            try
            {
                var tp = new TP { Count = 1, Attr = SE_PRIVILEGE_ENABLED };
                if (!LookupPrivilegeValue(null, "SeSystemEnvironmentPrivilege", ref tp.Luid))
                {
                    Debug.WriteLine($"[Nvram] LookupPrivilegeValue failed: {Marshal.GetLastWin32Error()}");
                    return;
                }

                AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
            }
            finally
            {
                CloseHandle(token);
            }
        }

        private static byte[]? LocalRead(string name, string guid)
        {
            EnableLocalNvramPrivilege();

            byte[] buf = new byte[4096];
            uint len = GetFirmwareEnvironmentVariable(name, guid, buf, (uint)buf.Length);
            if (len == 0 || len > buf.Length)
                return null;

            byte[] result = new byte[len];
            Array.Copy(buf, result, len);
            return result;
        }

        private static bool LocalWrite(string name, string guid, byte[] data)
        {
            EnableLocalNvramPrivilege();

            if (!SetFirmwareEnvironmentVariable(name, guid, data, (uint)data.Length))
            {
                Debug.WriteLine($"[Nvram] local write failed: {Marshal.GetLastWin32Error()}");
                return false;
            }

            return true;
        }

        #endregion
    }
}
