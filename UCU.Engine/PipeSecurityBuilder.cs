namespace UCU.Engine
{
    /// <summary>
    /// The pipe's DACL, as SDDL.
    ///
    /// This is the security boundary of the whole design: the engine runs as
    /// LocalSystem and can write EC registers that change fan curves, power limits and
    /// the GPU MUX, and any process on the machine can connect to a named pipe by
    /// name. Without this DACL, "the app runs unelevated" means "malware runs the
    /// EC unelevated".
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
    /// The string is handed to CreateNamedPipeW through NativePipeFactory - the
    /// only way to secure a pipe on .NET 10 (the managed PipeSecurity surface on
    /// streams was removed; applying it after creation throws, which the first
    /// field run demonstrated the hard way).
    ///
    /// Note what this does *not* do: it authenticates the caller only by account,
    /// not by image. Any process running as the console user may call the EC. That
    /// is the same trust level the user already has, so it is the intended
    /// boundary.
    /// </summary>
    public static class PipeSecurityBuilder
    {
        /// <summary>
        /// D:P - explicit DACL, no inheritance; (A;;FA;;;SY) system full;
        /// (A;;FA;;;BA) administrators full; (A;;GRGW;;;IU) interactive user read+write.
        /// </summary>
        public const string RestrictedSddl =
            "D:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;GRGW;;;IU)";
    }
}
