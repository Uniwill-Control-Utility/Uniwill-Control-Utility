using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace UCU.Engine
{
    /// <summary>
    /// Launches a process inside the interactive user's session.
    ///
    /// This is the piece the design cannot avoid: the engine runs as
    /// LocalSystem in session 0, and a UI process started from there is
    /// invisible (session isolation). The documented way for a service to put a
    /// windowed process on the user's desktop is the token route: duplicate the
    /// active console session's user token (WTSQueryUserToken, which needs
    /// SE_TCB_NAME — LocalSystem holds it), and start the process as that
    /// token with the session's environment block and window station.
    /// </summary>
    internal static class AppSessionLauncher
    {
        private const uint AllAccess = 0x000F003F; // STANDARD_RIGHTS_REQUIRED | SYNCHRONIZE | 0x3F (token maximum access)
        private const int SecurityImpersonation = 2;
        private const int TokenPrimary = 1;
        private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
        private const uint LOGON_WITH_PROFILE = 0x00000001;
        private const uint WTSConsoleSession = 0xFFFFFFFF;

        /// <summary>
        /// Starts <paramref name="exePath"/> in the active console user's
        /// session. False when there is no console session (RDP-less machine at
        /// the login screen), the token cannot be duplicated, or the process
        /// cannot start — every failure is logged, the caller (the supervisor)
        /// keeps its crash budget.
        /// </summary>
        public static bool Launch(string exePath, string arguments, ILogger logger)
        {
            uint sessionId = WTSGetActiveConsoleSessionId();
            if (sessionId == WTSConsoleSession)
            {
                logger.LogInformation("[Supervision] No active console session — nothing to launch into.");
                return false;
            }

            if (!WTSQueryUserToken(sessionId, out IntPtr userToken) || userToken == IntPtr.Zero)
            {
                logger.LogWarning("[Supervision] WTSQueryUserToken(session {Session}) failed: {Error}",
                    sessionId, Marshal.GetLastWin32Error());
                return false;
            }

            IntPtr primary = IntPtr.Zero;
            IntPtr environment = IntPtr.Zero;
            try
            {
                if (!DuplicateTokenEx(userToken, AllAccess, IntPtr.Zero,
                        SecurityImpersonation, TokenPrimary, out primary))
                {
                    logger.LogWarning("[Supervision] DuplicateTokenEx failed: {Error}", Marshal.GetLastWin32Error());
                    return false;
                }

                // A missing environment block only costs the app its USERPROFILE
                // settings, so it is best-effort, not a gate.
                CreateEnvironmentBlock(out environment, primary, false);

                var startupInfo = new STARTUPINFO
                {
                    cb = Marshal.SizeOf<STARTUPINFO>(),
                    lpDesktop = "winsta0\\default", // the session's interactive window station
                };

                string workingDirectory = Path.GetDirectoryName(exePath)
                    ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ".."));
                string commandLine = $"\"{exePath}\" {arguments}";

                bool started = CreateProcessAsUser(primary, null, commandLine,
                    IntPtr.Zero, IntPtr.Zero, false,
                    CREATE_UNICODE_ENVIRONMENT | LOGON_WITH_PROFILE,
                    environment, workingDirectory, ref startupInfo, out PROCESS_INFORMATION pi);

                if (!started)
                {
                    logger.LogWarning("[Supervision] CreateProcessAsUser('{Exe}') failed: {Error}",
                        exePath, Marshal.GetLastWin32Error());
                    return false;
                }

                CloseHandle(pi.hProcess);
                CloseHandle(pi.hThread);
                logger.LogInformation("[Supervision] Launched the app in session {Session}: {Exe}", sessionId, exePath);
                return true;
            }
            finally
            {
                if (environment != IntPtr.Zero)
                    DestroyEnvironmentBlock(environment);
                if (primary != IntPtr.Zero)
                    CloseHandle(primary);
                CloseHandle(userToken);
            }
        }

        #region Win32

        [DllImport("wtsapi32.dll")]
        private static extern uint WTSGetActiveConsoleSessionId();

        [DllImport("wtsapi32.dll", SetLastError = true)]
        private static extern bool WTSQueryUserToken(uint sessionId, out IntPtr token);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool DuplicateTokenEx(
            IntPtr existingToken, uint desiredAccess, IntPtr tokenAttributes,
            int impersonationLevel, int tokenType, out IntPtr primaryToken);

        [DllImport("userenv.dll", SetLastError = true)]
        private static extern bool CreateEnvironmentBlock(out IntPtr environment, IntPtr token, bool inherit);

        [DllImport("userenv.dll", SetLastError = true)]
        private static extern bool DestroyEnvironmentBlock(IntPtr environment);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CreateProcessAsUser(
            IntPtr token, string? applicationName, string commandLine,
            IntPtr processAttributes, IntPtr threadAttributes, bool inheritHandles,
            uint creationFlags, IntPtr environment, string? workingDirectory,
            ref STARTUPINFO startupInfo, out PROCESS_INFORMATION processInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct STARTUPINFO
        {
            public int cb;
            public string? lpReserved;
            public string? lpDesktop;
            public string? lpTitle;
            public int dwX;
            public int dwY;
            public int dwXSize;
            public int dwYSize;
            public int dwXCountChars;
            public int dwYCountChars;
            public int dwFillAttribute;
            public int dwFlags;
            public short wShowWindow;
            public short cbReserved2;
            public IntPtr lpReserved2;
            public IntPtr hStdInput;
            public IntPtr hStdOutput;
            public IntPtr hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_INFORMATION
        {
            public IntPtr hProcess;
            public IntPtr hThread;
            public int dwProcessId;
            public int dwThreadId;
        }

        #endregion
    }
}
