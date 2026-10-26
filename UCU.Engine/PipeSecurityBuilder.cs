using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;

namespace UCU.Engine
{
    /// <summary>
    /// The pipe's DACL, built from a SDDL string.
    ///
    /// This is the security boundary of the whole design: the engine runs as
    /// LocalSystem and can write EC registers that change fan curves, power limits and
    /// the GPU MUX, and any process on the machine can connect to a named pipe by
    /// name. Without this DACL, "the app runs unelevated" means "malware runs the EC
    /// unelevated".
    ///
    /// The SDDL grants:
    ///  - SY (LocalSystem) and BA (Administrators): full control, so the service
    ///    itself and an elevated admin diagnostic can manage the pipe.
    ///  - IU (the interactive user of this console session) read/write only, so the
    ///    user's own app can call the hardware without a UAC prompt, but a service
    ///    running as another account, a remote session's user, or a low-integrity
    ///    sandboxed process cannot.
    ///  - Nobody else: the DACL is explicit (P), with no inherited access.
    ///
    /// Note what this does *not* do: it authenticates the caller only by account, not
    /// by image. Any process running as the console user may call the EC. That is the
    /// same trust level the user already has, so it is the intended boundary; the
    /// client certificate pinning a design doc might ask for would buy nothing a
    /// LocalSystem service cannot already be asked for by the same user.
    /// </summary>
    public static class PipeSecurityBuilder
    {
        /// <summary>
        /// D:P - explicit DACL, no inheritance; (A;;FA;;;SY) system full;
        /// (A;;FA;;;BA) administrators full; (A;;GRGW;;;IU) interactive user read+write.
        /// </summary>
        public const string RestrictedSddl =
            "D:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;GRGW;;;IU)";

        public static PipeSecurity BuildRestricted()
        {
            var security = new PipeSecurity();
            security.SetSecurityDescriptorSddlForm(
                RestrictedSddl,
                AccessControlSections.Access);
            return security;
        }

        /// <summary>
        /// A pipe the current user can connect to from an unelevated test process, by
        /// granting FullControl to this user's SID. Used only when
        /// <see cref="EngineRuntimeOptions.RestrictPipeAccess"/> is false, which the
        /// service never sets.
        /// </summary>
        public static PipeSecurity BuildForLocalTesting()
        {
            string userSid = WindowsIdentity.GetCurrent().User!.Value;
            var security = new PipeSecurity();
            security.SetSecurityDescriptorSddlForm(
                $"D:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;FA;;;{userSid})",
                AccessControlSections.Access);
            return security;
        }

        public static PipeSecurity Build(EngineRuntimeOptions options) =>
            options.RestrictPipeAccess ? BuildRestricted() : BuildForLocalTesting();
    }
}
