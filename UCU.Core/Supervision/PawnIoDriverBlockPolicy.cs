namespace UCU.Core.Supervision
{
    /// <summary>
    /// Why the PawnIO kernel driver is not running, as a pure code mapping
    /// (the OpenRevo driver manager — `driver\bld_driver_mgr.rs` — recognizes
    /// Win32 1275, ERROR_DRIVER_BLOCKED_BY_CORE_ISOLATION, from StartService
    /// and names Core Isolation; this policy is UCU's re-implementation of
    /// that recognition, extended to the inference path).
    ///
    /// The codes: 1275 is the SCM's word that Memory Integrity blocked the
    /// driver; 577 (ERROR_INVALID_IMAGE_HASH) is the image-integrity reject
    /// (unsigned/tampered driver image) that Memory Integrity enforces as
    /// policy. The inference path has no StartService attempt to read — UCU
    /// installs through the vendor installer, so a running app often only
    /// observes "device absent": driver entry installed + Memory Integrity
    /// ON + device absent is the HVCI shape, and the UI copy says "is
    /// blocking", not "was refused with 1275".
    /// </summary>
    public enum PawnIoBlockReason
    {
        /// <summary>No blocking signal: not installed, running, access-denied, or an unrelated failure.</summary>
        None,

        /// <summary>Core Isolation (Memory Integrity) is blocking the driver — Win32 1275, or the installed-driver + HVCI-on inference.</summary>
        CoreIsolation,

        /// <summary>The driver image failed integrity-hash verification — Win32 577 (unsigned/tampered image), the same Memory Integrity surface.</summary>
        UnsignedImage,
    }

    public static class PawnIoDriverBlockPolicy
    {
        /// <summary>ERROR_DRIVER_BLOCKED_BY_CORE_ISOLATION — StartService refused the driver (HVCI).</summary>
        public const int ErrorDriverBlockedByCoreIsolation = 1275;

        /// <summary>ERROR_INVALID_IMAGE_HASH — the image's signature/hash check failed.</summary>
        public const int ErrorInvalidImageHash = 577;

        /// <summary>
        /// Decide the block reason. A hard code from the SCM wins over the
        /// inference (it IS the answer); the inference only fires when
        /// nothing observed the start failure (code null), Memory Integrity
        /// is ON, and the driver entry IS installed — driver absent + HVCI
        /// off says nothing, and driver-absent + HVCI on with no installed
        /// driver is just "not installed".
        /// </summary>
        public static PawnIoBlockReason Diagnose(
            int? startWin32Code,
            bool memoryIntegrityOn,
            bool driverEntryInstalled)
        {
            switch (startWin32Code)
            {
                case ErrorDriverBlockedByCoreIsolation:
                    return PawnIoBlockReason.CoreIsolation;
                case ErrorInvalidImageHash:
                    return PawnIoBlockReason.UnsignedImage;
            }

            if (startWin32Code is null && memoryIntegrityOn && driverEntryInstalled)
                return PawnIoBlockReason.CoreIsolation;

            return PawnIoBlockReason.None;
        }
    }
}
