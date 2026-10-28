using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace UCU.Engine
{
    /// <summary>
    /// Asserts the service's DACL (the app contract, <see cref="EngineInstaller.ServiceSddl"/>)
    /// from inside the service itself: on every start and on a slow timer.
    ///
    /// Why the installer pass cannot be the only writer (field-proven): a security
    /// product with service protection (observed: Emsisoft Anti-Malware) restores a
    /// guarded service's descriptor - <c>sc sdset</c> answers SUCCESS, and
    /// <c>sc sdshow</c> reads the create template back immediately, repeatedly,
    /// under any ordering (install pass, steady state, running, stopped). On such a
    /// machine the app contract never commits from outside, the unelevated app
    /// cannot open the service, and its self-heal elevates on every launch.
    ///
    /// The service is the one writer that always has rights (LocalSystem, and the
    /// object owner), and its write lands in the Service Control Manager's live
    /// record, which is where the app's opens are authorized - so the contract is
    /// self-asserted, not installed-and-hoped. If a policy engine reverts the
    /// descriptor (service restart re-reads the registry, periodic policy
    /// refresh), the next assertion converges it back within the interval.
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal sealed class ServiceSelfSecurityService : BackgroundService
    {
        private static readonly TimeSpan ReassertInterval = TimeSpan.FromMinutes(5);

        private readonly ILogger<ServiceSelfSecurityService> _logger;
        private bool _lastAssertFailed;

        public ServiceSelfSecurityService(ILogger<ServiceSelfSecurityService> logger)
        {
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                Assert();

                try
                {
                    await Task.Delay(ReassertInterval, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }

        private void Assert()
        {
            try
            {
                if (Native.AssertServiceSddl(EngineHostFactory.ServiceName, EngineInstaller.ServiceSddl))
                {
                    if (_lastAssertFailed)
                        _logger.LogInformation("[SelfSecurity] Service DACL assertion succeeded again");
                    _lastAssertFailed = false;
                    return;
                }

                if (!_lastAssertFailed)
                    _logger.LogWarning("[SelfSecurity] SetServiceObjectSecurity failed - the unelevated app may not open this service");
                _lastAssertFailed = true;
            }
            catch (Exception ex)
            {
                if (!_lastAssertFailed)
                    _logger.LogWarning(ex, "[SelfSecurity] Service DACL assertion threw");
                _lastAssertFailed = true;
            }
        }

        private static class Native
        {
            private const uint ScManagerConnect = 0x0001;
            private const uint WriteDac = 0x00040000;
            private const uint DaclSecurityInformation = 0x0004;
            private const int SddlRevision = 1;

            public static bool AssertServiceSddl(string serviceName, string sddl)
            {
                if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(
                        sddl, SddlRevision, out IntPtr securityDescriptor, out _))
                    return false;

                try
                {
                    using SafeScHandle scm = OpenSCManagerW(null, null, ScManagerConnect);
                    if (scm.IsInvalid)
                        return false;

                    using SafeScHandle service = OpenServiceW(scm, serviceName, WriteDac);
                    if (service.IsInvalid)
                        return false;

                    return SetServiceObjectSecurity(service, DaclSecurityInformation, securityDescriptor);
                }
                finally
                {
                    LocalFree(securityDescriptor);
                }
            }

            [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(
                string stringSecurityDescriptor, int securityDescriptorRevision,
                out IntPtr securityDescriptor, out int securityDescriptorSize);

            [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            private static extern SafeScHandle OpenSCManagerW(string? machineName, string? servicesDatabase, uint desiredAccess);

            [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            private static extern SafeScHandle OpenServiceW(SafeScHandle scManagerHandle, string serviceName, uint desiredAccess);

            [DllImport("advapi32.dll", SetLastError = true)]
            private static extern bool SetServiceObjectSecurity(SafeScHandle serviceHandle, uint securityInformation, IntPtr securityDescriptor);

            [DllImport("advapi32.dll")]
            private static extern bool CloseServiceHandle(IntPtr handle);

            [DllImport("kernel32.dll")]
            private static extern IntPtr LocalFree(IntPtr handle);

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
}
