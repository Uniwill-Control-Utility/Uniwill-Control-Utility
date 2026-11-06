using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace Uniwill_Control_Utility.Services
{
    /// <summary>
    /// The app's Service Control Manager access, narrowed to the exact rights the
    /// engine service's DACL grants Authenticated Users: SERVICE_QUERY_STATUS to
    /// read liveness and SERVICE_START to start it.
    ///
    /// .NET's <c>ServiceController</c> cannot be used against this DACL, and the
    /// product must not "fix" that by widening the DACL. Its read-open requests
    /// SERVICE_QUERY_ALL_ACCESS, which includes SERVICE_ENUMERATE_DEPENDENTS and
    /// SERVICE_INTERROGATE (SDDL LC, 0x0080). Interrogate on a LocalSystem service
    /// is the documented token-duplication elevation hole (InterrogateService lets
    /// the caller open the service process's token), so the install descriptor
    /// omits it - and every ServiceController status query then failed with Access
    /// Denied even on a healthy running service. The app read that as "the
    /// registration needs a DACL repair" and ran its elevated install pass on every
    /// launch (field report: a UAC prompt each start, install log showing
    /// "sc start FAILED 1056" - already running).
    ///
    /// Everything the app legitimately does with the SCM needs rights the install
    /// already grants, so this opens for exactly those and nothing more. A handle
    /// open that requests only granted rights cannot fail with Access Denied on a
    /// correctly installed service, which is what makes the AccessDenied state
    /// again mean what it is documented to mean: a registration from a build whose
    /// DACL predates the SERVICE_START grant.
    /// </summary>
    internal static class ScmNative
    {
        // Service access rights (winnt.h). Only the two the AU ace grants.
        internal const uint ServiceQueryStatus = 0x0004; // SDDL SW
        internal const uint ServiceStart = 0x0020;       // SDDL RP

        internal const uint ScManagerConnect = 0x0001;

        // SERVICE_STATUS.currentState (winnt.h).
        internal const int ServiceStopped = 1;
        internal const int ServiceStartPending = 2;
        internal const int ServiceStopPending = 3;
        internal const int ServiceRunning = 4;
        internal const int ServiceContinuePending = 5;
        internal const int ServicePausePending = 6;
        internal const int ServicePaused = 7;

        internal const int ErrorServiceAlreadyRunning = 1056;

        private const int ScStatusProcessInfo = 0;
        private const int StatusBufferSize = 36; // SERVICE_STATUS_PROCESS
        private const int CurrentStateOffset = 4; // second DWORD of SERVICE_STATUS
        private const int PollIntervalMs = 250;

        /// <summary>
        /// Reads SERVICE_STATUS_PROCESS.currentState. Throws Win32Exception with
        /// 1060 (not installed) or 5 (access denied) when the handle cannot be
        /// opened, which is the diagnosis EngineAvailability classifies.
        /// </summary>
        public static int QueryState(string serviceName)
        {
            using SafeScHandle scm = OpenScManager();
            using SafeScHandle service = OpenService(scm, serviceName, ServiceQueryStatus);
            return ReadState(service);
        }

        /// <summary>
        /// Starts an installed service and waits for Running. A service that is
        /// already running counts as success. Throws Win32Exception (5 = this
        /// process may not start it, 1060 = it stopped existing) or
        /// <see cref="TimeoutException"/> when the SCM's transition exceeds the
        /// budget. Blocking by design: the caller is the lifecycle repair on a
        /// background task, which cannot proceed before the service answers RPC.
        /// </summary>
        public static void StartAndWait(string serviceName, TimeSpan budget)
        {
            Stopwatch clock = Stopwatch.StartNew();

            using (SafeScHandle scm = OpenScManager())
            using (SafeScHandle service = OpenService(scm, serviceName, ServiceStart))
            {
                if (!StartService(service, 0, IntPtr.Zero))
                {
                    int error = Marshal.GetLastWin32Error();
                    if (error != ErrorServiceAlreadyRunning)
                        throw new Win32Exception(error);
                }
            }

            while (true)
            {
                // Re-open for the status right: the start handle was opened for
                // SERVICE_START only, matching exactly what the AU ace grants.
                if (QueryState(serviceName) == ServiceRunning)
                    return;

                if (clock.Elapsed >= budget)
                    throw new TimeoutException($"the '{serviceName}' service did not reach Running within {budget.TotalSeconds:F0}s");

                Thread.Sleep(PollIntervalMs);
            }
        }

        private static int ReadState(SafeScHandle service)
        {
            var buffer = new byte[StatusBufferSize];
            if (!QueryServiceStatusEx(service, ScStatusProcessInfo, buffer, buffer.Length, out _))
                throw new Win32Exception(Marshal.GetLastWin32Error());

            return BitConverter.ToInt32(buffer, CurrentStateOffset);
        }

        private static SafeScHandle OpenScManager()
        {
            SafeScHandle scm = OpenSCManagerW(null, null, ScManagerConnect);
            if (scm.IsInvalid)
            {
                scm.Dispose();
                // 5 here would mean this machine's SCM denies Authenticated Users
                // the connect every non-admin service query uses; the app's repair
                // for a state it cannot read is the elevated pass, which is the
                // AccessDenied classification.
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            return scm;
        }

        private static SafeScHandle OpenService(SafeScHandle scm, string serviceName, uint access)
        {
            SafeScHandle service = OpenServiceW(scm, serviceName, access);
            if (service.IsInvalid)
            {
                service.Dispose();
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            return service;
        }

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeScHandle OpenSCManagerW(string? machineName, string? servicesDatabase, uint desiredAccess);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeScHandle OpenServiceW(SafeScHandle scManagerHandle, string serviceName, uint desiredAccess);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool QueryServiceStatusEx(SafeScHandle serviceHandle, int infoLevel, byte[] buffer, int bufSize, out uint bytesNeeded);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool StartService(SafeScHandle serviceHandle, uint numServiceArgs, IntPtr serviceArgs);

        [DllImport("advapi32.dll")]
        private static extern bool CloseServiceHandle(IntPtr handle);

        /// <summary>ADVAPI32 service/SCM handle; the invalid value is NULL.</summary>
        private sealed class SafeScHandle : SafeHandle
        {
            public SafeScHandle() : base(IntPtr.Zero, ownsHandle: true)
            {
            }

            public override bool IsInvalid => handle == IntPtr.Zero;

            protected override bool ReleaseHandle() => CloseServiceHandle(handle);
        }
    }
}
