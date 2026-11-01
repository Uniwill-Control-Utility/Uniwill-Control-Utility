using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace UCU.Hardware.Takeover
{
    /// <summary>
    /// The Win32 surface the OEM takeover executor (story U-203) needs:
    /// the SCM calls (query config, change start type, stop, start), the
    /// Authenticode verdict (WinVerifyTrust) that gates every binary, and
    /// the image path of another process. P/Invoke lives here so the
    /// service class stays policy-readable.
    /// </summary>
    internal static class TakeoverNative
    {
        // ---- Service Control Manager (advapi32) --------------------------

        public const uint SC_MANAGER_CONNECT = 0x0001;

        /// <summary>The rights the takeover needs on one service: read its
        /// config, re-arm its start type, stop it, start it, watch it stop.</summary>
        public const uint SERVICE_TAKEOVER_ACCESS =
            0x0001 /* QUERY_CONFIG */ | 0x0002 /* CHANGE_CONFIG */ | 0x0004 /* QUERY_STATUS */ |
            0x0010 /* START */ | 0x0020 /* STOP */ | 0x0080 /* INTERROGATE */;

        public const uint SERVICE_CONTROL_STOP = 0x0001;

        public const uint SERVICE_STOPPED = 1;
        public const uint SERVICE_START_PENDING = 2;
        public const uint SERVICE_STOP_PENDING = 3;
        public const uint SERVICE_RUNNING = 4;

        public const uint SERVICE_NO_CHANGE = 0xFFFFFFFF;

        /// <summary>The Start-type values: 2 = automatic, 3 = manual
        /// (demand). Pause parks the stack at 3, the reference's
        /// "sc config demand" step.</summary>
        public const uint SERVICE_AUTO_START = 2;
        public const uint SERVICE_DEMAND_START = 3;

        public const uint ERROR_SERVICE_ALREADY_RUNNING = 1056;
        public const uint ERROR_SERVICE_ALREADY_STOPPED = 1062;
        public const uint ERROR_SERVICE_MARKED_FOR_DELETE = 1072;

        [StructLayout(LayoutKind.Sequential)]
        public struct SERVICE_STATUS
        {
            public uint dwServiceType;
            public uint dwCurrentState;
            public uint dwControlsAccepted;
            public uint dwWin32ExitCode;
            public uint dwServiceSpecificExitCode;
            public uint dwCheckPoint;
            public uint dwWaitHint;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct QUERY_SERVICE_CONFIG
        {
            public uint dwServiceType;
            public uint dwStartType;
            public uint dwErrorControl;
            public IntPtr lpBinaryPathName;
            public IntPtr lpLoadOrderGroup;
            public uint dwTagId;
            public IntPtr lpDependencies;
            public IntPtr lpServiceStartName;
            public IntPtr lpDisplayName;
        }

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "OpenSCManagerW")]
        public static extern IntPtr OpenSCManager(string? machineName, string? databaseName, uint access);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "OpenServiceW")]
        public static extern IntPtr OpenService(IntPtr hSCManager, string serviceName, uint access);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseServiceHandle(IntPtr hClose);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool QueryServiceStatus(IntPtr hService, ref SERVICE_STATUS lpServiceStatus);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "StartServiceW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool StartServiceW(IntPtr hService, uint numServiceArgs, IntPtr ppServiceArgVectors);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ControlService(IntPtr hService, uint dwControl, ref SERVICE_STATUS lpServiceStatus);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "QueryServiceConfigW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool QueryServiceConfigNative(IntPtr hService, IntPtr lpServiceConfig, uint cbBufSize, out uint pcbBytesNeeded);

        /// <summary>Reads the service config (Start type + ImagePath). The
        /// two-call buffer dance is the documented query (the first call
        /// reports the needed size via ERROR_INSUFFICIENT_BUFFER).</summary>
        public static QUERY_SERVICE_CONFIG? QueryServiceConfig(IntPtr hService)
        {
            QueryServiceConfigNative(hService, IntPtr.Zero, 0, out uint needed);
            if (needed == 0)
                return null;

            IntPtr buffer = Marshal.AllocHGlobal((int)needed);
            try
            {
                if (!QueryServiceConfigNative(hService, buffer, needed, out needed))
                    return null;

                QUERY_SERVICE_CONFIG config = Marshal.PtrToStructure<QUERY_SERVICE_CONFIG>(buffer);

                // Copy the strings out before the buffer is released; keep the
                // struct's pointers pointed at them so callers read one shape.
                config.lpBinaryPathName = Marshal.StringToHGlobalUni(Marshal.PtrToStringUni(config.lpBinaryPathName) ?? string.Empty);
                config.lpLoadOrderGroup = Marshal.StringToHGlobalUni(Marshal.PtrToStringUni(config.lpLoadOrderGroup) ?? string.Empty);
                config.lpDependencies = Marshal.StringToHGlobalUni(Marshal.PtrToStringUni(config.lpDependencies) ?? string.Empty);
                config.lpServiceStartName = Marshal.StringToHGlobalUni(Marshal.PtrToStringUni(config.lpServiceStartName) ?? string.Empty);
                config.lpDisplayName = Marshal.StringToHGlobalUni(Marshal.PtrToStringUni(config.lpDisplayName) ?? string.Empty);
                return config;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "ChangeServiceConfigW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ChangeServiceConfig(
            IntPtr hService, uint dwServiceType, uint dwStartType, uint dwErrorControl,
            string? lpBinaryPathName, string? lpLoadOrderGroup, IntPtr lpdwTagId,
            string? lpDependencies, string? lpServiceStartName, string? lpPassword, string? lpDisplayName);

        // ---- Authenticode (wintrust) --------------------------------------

        /// <summary>WINTRUST_ACTION_GENERIC_VERIFY_V2: the "is this file's
        /// signature trusted" action - the same verdict Win32 PE loading
        /// consults. The observed gate for every takeover mutation.</summary>
        public static readonly Guid WinTrustActionGenericVerifyV2 =
            new(0x00AAC56B, 0xCD44, 0x11D0, 0x8C, 0xC2, 0x00, 0xC0, 0x4F, 0xC2, 0x95, 0xEE);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct WINTRUST_FILE_INFO
        {
            public uint cbStruct;
            [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
            public IntPtr hFile;
            public IntPtr pgKnownSubject;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct WINTRUST_DATA
        {
            public uint cbStruct;
            public IntPtr pPolicyCallbackData;
            public IntPtr pSIPClientData;
            public uint dwUIChoice;          // 2 = WTD_UI_NONE (a service must never pop UI)
            public uint fdwRevocationChecks; // 0 = WTD_REVOKE_NONE (offline chain: field machines)
            public uint dwUnionChoice;       // 1 = WTD_CHOICE_FILE
            public IntPtr pFileData;
            public uint dwStateAction;       // 0 = WTD_STATEACTION_IGNORE (no state to free)
            public IntPtr hWVTStateData;
            public IntPtr pwszURLReference;
            public uint dwProvFlags;
            public uint dwUIContext;
            public IntPtr pSignatureSettings; // Win8+ trailing member; cbStruct covers it
        }

        [DllImport("wintrust.dll", SetLastError = true)]
        static extern uint WinVerifyTrust(IntPtr hWnd, in Guid pgActionID, ref WINTRUST_DATA pWVTData);

        /// <summary>True when the file carries a signature whose chain the
        /// platform trusts (offline chain check - the field laptops run
        /// disconnected as a norm, and the reference's own restore scripts
        /// do not do revocation either). Unsigned, tampered, chain-broken:
        /// false, and the takeover gate refuses the target.</summary>
        public static bool IsSignatureValid(string filePath)
        {
            var fileInfo = new WINTRUST_FILE_INFO
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(),
                pcwszFilePath = filePath,
                hFile = IntPtr.Zero,
                pgKnownSubject = IntPtr.Zero,
            };

            IntPtr fileInfoPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
            try
            {
                Marshal.StructureToPtr(fileInfo, fileInfoPtr, false);

                var data = new WINTRUST_DATA
                {
                    cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                    pPolicyCallbackData = IntPtr.Zero,
                    pSIPClientData = IntPtr.Zero,
                    dwUIChoice = 2,
                    fdwRevocationChecks = 0,
                    dwUnionChoice = 1,
                    pFileData = fileInfoPtr,
                    dwStateAction = 0,
                    hWVTStateData = IntPtr.Zero,
                    pwszURLReference = IntPtr.Zero,
                    dwProvFlags = 0,
                    dwUIContext = 0,
                    pSignatureSettings = IntPtr.Zero,
                };

                uint status = WinVerifyTrust(IntPtr.Zero, in WinTrustActionGenericVerifyV2, ref data);
                return status == 0; // TRUST_S_SUCCESS
            }
            finally
            {
                Marshal.DestroyStructure<WINTRUST_FILE_INFO>(fileInfoPtr);
                Marshal.FreeHGlobal(fileInfoPtr);
            }
        }

        // ---- Process image path (kernel32) ---------------------------------

        const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool QueryFullProcessImageNameW(IntPtr hProcess, int dwFlags, StringBuilder lpExeName, ref int lpdwSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool CloseHandle(IntPtr hObject);

        /// <summary>The full image path of a process, the gate's input for
        /// a kill. MainModule throws on protected/elevated processes (the
        /// field's AV product makes service opens throw, see the runbook's
        /// elevated-game detection note); QueryFullProcessImageNameW with
        /// LIMITED_INFORMATION is the path that survives. Null when the
        /// path cannot be read: no path, no identity, no kill.</summary>
        public static string? ProcessImagePath(int processId)
        {
            IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
            if (h == IntPtr.Zero)
                return null;

            try
            {
                var sb = new StringBuilder(1024);
                int size = sb.Capacity;
                return QueryFullProcessImageNameW(h, 0, sb, ref size) ? sb.ToString(0, size) : null;
            }
            catch
            {
                return null;
            }
            finally
            {
                CloseHandle(h);
            }
        }
    }
}
