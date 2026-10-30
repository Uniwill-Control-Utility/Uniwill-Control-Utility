using System.Runtime.InteropServices;

namespace UCU.Hardware.Firmware
{
    /// <summary>
    /// The firmware-variable and token-privilege calls the NVRAM verbs need.
    /// Kept to the exact surface the service uses so it stays reviewable.
    /// The W-variants of the firmware-variable APIs are the ones that demand
    /// SeSystemEnvironmentPrivilege (the non-W variants are the legacy
    /// GetFirmwareEnvironmentVariable, which the OS denies to user processes);
    /// LocalSystem holds and can enable the privilege, which is the whole
    /// reason these verbs live in the engine.
    /// </summary>
    internal static class Native
    {
        public const int ERROR_PRIVILEGE_NOT_HELD = 1312;

        public const uint TOKEN_ADJUST_PRIVILEGES = 0x20;
        public const uint SE_PRIVILEGE_ENABLED = 0x00000002;

        [StructLayout(LayoutKind.Sequential)]
        public struct TOKEN_PRIVILEGES
        {
            // The one-privilege specialization of TOKEN_PRIVILEGES, laid out
            // flat for x64: Count is a ULONG followed by a single
            // LUID_AND_ATTRIBUTES (the Windows header's array is size 1 by
            // convention when Count is 1). Blittable, so the ref passes
            // straight through the marshaller.
            public uint Count;
            public Luid Luid;
            public uint Attributes;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct Luid
        {
            public uint LowPart;
            public int HighPart;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetFirmwareEnvironmentVariableW")]
        public static extern uint GetFirmwareEnvironmentVariable(
            string lpName, string lpGuid, [Out] byte[] lpBuffer, uint nSize);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "SetFirmwareEnvironmentVariableW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetFirmwareEnvironmentVariable(
            string lpName, string lpGuid, [In] byte[] lpValue, uint nSize);

        [DllImport("kernel32.dll")]
        public static extern IntPtr GetCurrentProcess();

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool OpenProcessToken(IntPtr processToken, uint desiredAccess, out IntPtr token);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool LookupPrivilegeValue(string? lpSystemName, string lpName, ref Luid lpLuid);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool AdjustTokenPrivileges(
            IntPtr token, [MarshalAs(UnmanagedType.Bool)] bool disableAllPrivileges,
            ref TOKEN_PRIVILEGES newState, uint bufferLength, IntPtr previousState, IntPtr returnLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseHandle(IntPtr handle);
    }
}
