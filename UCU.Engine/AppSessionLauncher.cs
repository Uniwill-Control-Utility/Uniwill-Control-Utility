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
        private const int TokenLinkedToken = 19;
        private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
        private const uint WTSConsoleSession = 0xFFFFFFFF;

        /// <summary>
        /// Starts <paramref name="exePath"/> in the active console user's
        /// session at the session user's filtered (normal, unelevated) token.
        /// </summary>
        public static bool Launch(string exePath, string arguments, ILogger? logger)
        {
            return Launch(exePath, arguments, elevate: false, logger);
        }

        /// <summary>
        /// Starts <paramref name="exePath"/> in the active console user's
        /// session. False when there is no console session (RDP-less machine at
        /// the login screen), the token cannot be duplicated, or the process
        /// cannot start — every failure is logged, the caller (the supervisor)
        /// keeps its crash budget.
        ///
        /// <paramref name="elevate"/> selects the user's LINKED token (the full
        /// administrator token behind a UAC-filtered one). It is required for
        /// an image whose launch flags demand elevation: a "Run as
        /// administrator" compat flag against a filtered token fails the
        /// CreateProcess with ERROR_ELEVATION_REQUIRED — no process, and no
        /// consent dialog for a service to answer. SYSTEM reading the linked
        /// token is the documented elevation-the-user-already-consented-to
        /// route; a standard user has no linked token, so the filtered token
        /// is the fallback.
        /// </summary>
        public static bool Launch(string exePath, string arguments, bool elevate, ILogger? logger)
        {
            uint sessionId = WTSGetActiveConsoleSessionId();
            if (sessionId == WTSConsoleSession)
            {
                logger?.LogInformation("[Supervision] No active console session — nothing to launch into.");
                return false;
            }

            if (!WTSQueryUserToken(sessionId, out IntPtr userToken) || userToken == IntPtr.Zero)
            {
                logger?.LogWarning("[Supervision] WTSQueryUserToken(session {Session}) failed: {Error}",
                    sessionId, Marshal.GetLastWin32Error());
                return false;
            }

            IntPtr linked = IntPtr.Zero;
            IntPtr launchToken = userToken;

            if (elevate)
            {
                linked = LinkedTokenOf(userToken);
                if (linked != IntPtr.Zero)
                    launchToken = linked;
                else
                    logger?.LogInformation("[Supervision] No linked (elevated) token for session {Session} - launching filtered.", sessionId);
            }

            IntPtr primary = IntPtr.Zero;
            IntPtr environment = IntPtr.Zero;
            try
            {
                if (!DuplicateTokenEx(launchToken, AllAccess, IntPtr.Zero,
                        SecurityImpersonation, TokenPrimary, out primary))
                {
                    logger?.LogWarning("[Supervision] DuplicateTokenEx failed: {Error}", Marshal.GetLastWin32Error());
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
                    // FIELD-PROVEN 2026-11-01: pass ONLY CreateProcess-valid
                    // flags. The profile flag of CreateProcessWithTokenW/
                    // WithLogonW (value 0x1) is not a CreateProcessAsUser
                    // flag: in the CreateProcess* flag space 0x1 is
                    // DEBUG_PROCESS, so including it created every process
                    // attached to a nonexistent debugger: the process object
                    // appeared (1 thread, ~3 MB, frozen at the ntdll
                    // image-load debug event) and NEVER ran - the RTSS
                    // "Start" button produced a helper-less RTSS.exe (no Hooks
                    // Loader => no injection => no OSD), auto-start "succeeded"
                    // against a husk, and a supervised app relaunch never
                    // painted a window. CreateProcessAsUser loads the token
                    // user's profile on its own; no flag is needed for it.
                    CREATE_UNICODE_ENVIRONMENT,
                    environment, workingDirectory, ref startupInfo, out PROCESS_INFORMATION pi);

                if (!started)
                {
                    logger?.LogWarning("[Supervision] CreateProcessAsUser('{Exe}') failed: {Error}",
                        exePath, Marshal.GetLastWin32Error());
                    return false;
                }

                CloseHandle(pi.hProcess);
                CloseHandle(pi.hThread);
                logger?.LogInformation("[Supervision] Launched {Exe} in session {Session}{Elevation}",
                    exePath, sessionId, elevate ? " (elevated)" : string.Empty);
                return true;
            }
            finally
            {
                if (environment != IntPtr.Zero)
                    DestroyEnvironmentBlock(environment);
                if (primary != IntPtr.Zero)
                    CloseHandle(primary);
                if (linked != IntPtr.Zero)
                    CloseHandle(linked);
                CloseHandle(userToken);
            }
        }

        /// <summary>
        /// The full (unfiltered) token behind a UAC-filtered one, or
        /// IntPtr.Zero when there is none (standard user, UAC off). The
        /// information buffer for TokenLinkedToken IS a token HANDLE, so it is
        /// sized as a pointer and closed with CloseHandle, not LocalFree.
        /// </summary>
        private static IntPtr LinkedTokenOf(IntPtr token)
        {
            IntPtr buffer = Marshal.AllocHGlobal(IntPtr.Size);

            try
            {
                if (!GetTokenInformation(token, TokenLinkedToken, buffer, (uint)IntPtr.Size, out _)
                    || Marshal.ReadIntPtr(buffer) == IntPtr.Zero)
                {
                    return IntPtr.Zero;
                }

                return Marshal.ReadIntPtr(buffer);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        #region Win32

        // WTSGetActiveConsoleSessionId is exported from kernel32, not wtsapi32
        // (a wtsapi32 import throws EntryPointNotFoundException at the first
        // supervised relaunch, and unhandled it took the whole engine host down).
        [DllImport("kernel32.dll")]
        private static extern uint WTSGetActiveConsoleSessionId();

        [DllImport("wtsapi32.dll", SetLastError = true)]
        private static extern bool WTSQueryUserToken(uint sessionId, out IntPtr token);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool DuplicateTokenEx(
            IntPtr existingToken, uint desiredAccess, IntPtr tokenAttributes,
            int impersonationLevel, int tokenType, out IntPtr primaryToken);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool GetTokenInformation(
            IntPtr token, int tokenInformationClass, IntPtr tokenInformation,
            uint tokenInformationLength, out uint returnLength);

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
