using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
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

        /// <summary>Verifies the file's signature chain AND reads the signer
        /// subject in one WinTrust pass. <see cref="IsSignatureValid"/> is the
        /// same verdict without the publisher read. The chain check is
        /// offline (the field laptops run disconnected as a norm, and the
        /// reference's own restore scripts do not do revocation either);
        /// unsigned, tampered, chain-broken: false, and the takeover gate
        /// refuses the target.</summary>
        public static bool VerifySignedFile(string filePath, out string? subject)
        {
            subject = null;

            var fileInfo = new WINTRUST_FILE_INFO
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(),
                pcwszFilePath = filePath,
                hFile = IntPtr.Zero,
                pgKnownSubject = IntPtr.Zero,
            };

            IntPtr fileInfoPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
            var data = new WINTRUST_DATA
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                dwUIChoice = 2,          // WTD_UI_NONE (a service must never pop UI)
                fdwRevocationChecks = 0, // WTD_REVOKE_NONE (offline chain: field machines)
                dwUnionChoice = 1,       // WTD_CHOICE_FILE
                dwStateAction = WTD_STATEACTION_VERIFY,
            };

            uint status;
            try
            {
                Marshal.StructureToPtr(fileInfo, fileInfoPtr, false);
                data.pFileData = fileInfoPtr;

                status = WinVerifyTrust(IntPtr.Zero, in WinTrustActionGenericVerifyV2, ref data);

                if (status == 0 && data.hWVTStateData != IntPtr.Zero)
                    subject = ReadSignerSubject(data.hWVTStateData);
            }
            finally
            {
                // VERIFY owns provider state; CLOSE releases it (skipping this
                // leaks the chain in a long-running service).
                if (data.hWVTStateData != IntPtr.Zero)
                {
                    WINTRUST_DATA close = data;
                    close.dwStateAction = WTD_STATEACTION_CLOSE;
                    WinVerifyTrust(IntPtr.Zero, in WinTrustActionGenericVerifyV2, ref close);
                }

                Marshal.DestroyStructure<WINTRUST_FILE_INFO>(fileInfoPtr);
                Marshal.FreeHGlobal(fileInfoPtr);
            }

            return status == 0; // TRUST_S_SUCCESS
        }

        /// <summary>True when the file carries a signature whose chain the
        /// platform trusts. Unsigned, tampered, chain-broken: false.</summary>
        public static bool IsSignatureValid(string filePath) => VerifySignedFile(filePath, out _);

        // ---- Authenticode signer (the publisher evidence) -------------------

        const uint WTD_STATEACTION_VERIFY = 1;
        const uint WTD_STATEACTION_CLOSE = 2;
        const uint X509_ASN_ENCODING = 0x00000001;
        const uint PKCS_7_ASN_ENCODING = 0x00010000;
        const uint CERT_SIMPLE_NAME_STR = 1;

        [StructLayout(LayoutKind.Sequential)]
        struct CRYPT_INTEGER_BLOB
        {
            public uint cbData;
            public IntPtr pbData;
        }

        /// <summary>wincrypt's FILETIME: two DWORDs, 4-byte aligned - NOT a
        /// CLR long (whose 8-byte alignment would shift every following
        /// field; the first probe read past the wrong offsets and lost the
        /// chain pointer).</summary>
        [StructLayout(LayoutKind.Sequential)]
        struct FILETIME
        {
            public uint dwLowDateTime;
            public uint dwHighDateTime;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct CRYPT_ALGORITHM_IDENTIFIER
        {
            public IntPtr pszObjId;
            public CRYPT_INTEGER_BLOB Parameters;
        }

        /// <summary>The CERT_INFO prefix the subject walk needs (dwVersion ..
        /// Subject; the layout matches wincrypt's CERT_INFO through Subject).</summary>
        [StructLayout(LayoutKind.Sequential)]
        struct CERT_INFO_PREFIX
        {
            public uint dwVersion;
            public CRYPT_INTEGER_BLOB SerialNumber;
            public CRYPT_ALGORITHM_IDENTIFIER SignatureAlgorithm;
            public CRYPT_INTEGER_BLOB Issuer;
            public FILETIME NotBefore;
            public FILETIME NotAfter;
            public CRYPT_INTEGER_BLOB Subject;
        }

        /// <summary>CERT_CONTEXT prefix (encoding type, encoded bytes, info).</summary>
        [StructLayout(LayoutKind.Sequential)]
        struct CERT_CONTEXT_PREFIX
        {
            public uint dwCertEncodingType;
            public IntPtr pbCertEncoded;
            public uint cbCertEncoded;
            public IntPtr pCertInfo;
        }

        /// <summary>CRYPT_PROVIDER_CERT prefix: the chain element's certificate.</summary>
        [StructLayout(LayoutKind.Sequential)]
        struct CRYPT_PROVIDER_CERT_PREFIX
        {
            public uint cbStruct;
            public IntPtr pCert;
        }

        /// <summary>CRYPT_PROVIDER_SGNR prefix: the signer's certificate chain.</summary>
        [StructLayout(LayoutKind.Sequential)]
        struct CRYPT_PROVIDER_SGNR_PREFIX
        {
            public uint cbStruct;
            public FILETIME sftVerifyAsOf;
            public uint csCertChain;
            public IntPtr pasCertChain;
        }

        [DllImport("wintrust.dll", SetLastError = true)]
        static extern IntPtr WTHelperProvDataFromStateData(IntPtr hStateData);

        [DllImport("wintrust.dll", SetLastError = true)]
        static extern IntPtr WTHelperGetProvSignerFromChain(
            IntPtr pProvData, uint dwIndex, [MarshalAs(UnmanagedType.Bool)] bool fCounterSigner, uint dwIndexCounterSigner);

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern int CertNameToStrW(
            uint dwCertEncodingType, ref CRYPT_INTEGER_BLOB pName, uint dwStrType, StringBuilder? psz, int csz);

        /// <summary>The Authenticode signer's subject DN ("CN=..., O=...,
        /// C=...") read from a VERIFY state handle. The publisher evidence
        /// the allowlist compares; nothing managed reads it (a
        /// certificate-FILE loader answers nothing for a PE with an embedded
        /// signature - field-proven on the tester laptop, pack #23 round:
        /// every signed OEM binary read with an empty subject and the whole
        /// stack was refused as the wrong publisher - and
        /// X509Certificate.CreateFromSignedFile is SYSLIB0057-obsolete).
        /// Null = no readable signer.</summary>
        static string? ReadSignerSubject(IntPtr hStateData)
        {
            try
            {
                IntPtr providerData = WTHelperProvDataFromStateData(hStateData);
                if (providerData == IntPtr.Zero)
                    return null;

                IntPtr signer = WTHelperGetProvSignerFromChain(providerData, 0, false, 0);
                if (signer == IntPtr.Zero)
                    return null;

                CRYPT_PROVIDER_SGNR_PREFIX sgnr = Marshal.PtrToStructure<CRYPT_PROVIDER_SGNR_PREFIX>(signer);
                if (sgnr.csCertChain == 0 || sgnr.pasCertChain == IntPtr.Zero)
                    return null;

                // Chain element 0 is the signer leaf.
                IntPtr certificate = Marshal.PtrToStructure<CRYPT_PROVIDER_CERT_PREFIX>(sgnr.pasCertChain).pCert;
                if (certificate == IntPtr.Zero)
                    return null;

                CERT_CONTEXT_PREFIX context = Marshal.PtrToStructure<CERT_CONTEXT_PREFIX>(certificate);
                if (context.pCertInfo == IntPtr.Zero)
                    return null;

                CERT_INFO_PREFIX info = Marshal.PtrToStructure<CERT_INFO_PREFIX>(context.pCertInfo);
                if (info.Subject.pbData == IntPtr.Zero || info.Subject.cbData == 0)
                    return null;

                int length = CertNameToStrW(
                    X509_ASN_ENCODING | PKCS_7_ASN_ENCODING, ref info.Subject, CERT_SIMPLE_NAME_STR, null, 0);
                if (length <= 1)
                    return null;

                var text = new StringBuilder(length);
                length = CertNameToStrW(
                    X509_ASN_ENCODING | PKCS_7_ASN_ENCODING, ref info.Subject, CERT_SIMPLE_NAME_STR, text, text.Capacity);
                return length > 1 ? text.ToString() : null;
            }
            catch
            {
                return null;
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
