using System;

namespace UCU.Core.Ec
{
    /// <summary>
    /// Which EC register domains a RAW write verb (fan.write_register_blocks,
    /// telemetry.write_register) may touch. The engine's semantic verbs carry their
    /// own validation (the codec Validate* seam); the raw verbs carry an address the
    /// caller chose, and the pipe grants every interactive user a connection — so
    /// the raw surface is gated to the domains whose writes are recoverable by the
    /// app's own restore paths, and the domains that brick the machine are not
    /// reachable at all:
    ///
    ///   allowed:  the fan-table block (temp/duty tables, 0x0F00-0x0F5F), the
    ///             writeable PWM registers (0x1804/0x1809 families), the keyboard
    ///             RGB/brightness set (0x0767-0x076B, 0x078C), the battery charge
    ///             mode register (0x07A6).
    ///   denied:   the power limits (0x0783-0x0788), the performance-mode index
    ///             (0x07AB), the power-management / custom-profile magic
    ///             (0x0726/0x0727), the OEM mode flag (0x045B), the mux state
    ///             (0x072A), the fan-control registers (0x0751/0x07C5/0x07C6) —
    ///             every one of those is writable only through its semantic verb,
    ///             which validates the value first.
    ///
    /// Pure policy (the GuardianPolicy/PresetArbiterPolicy pattern): the engine
    /// enforces, this decides, the unit tests pin. Ranges mirror the EcEngine
    /// REG_* constants; the addresses live here because the domain assembly is the
    /// only place both the engine and the tests may read them from.
    /// </summary>
    public static class EcWriteGatePolicy
    {
        /// <summary>
        /// The OEM firmware variable family the NVRAM verbs may write. Both NVRAM
        /// rows (the CPU Advanced Performance Menu and the GPU mux's
        /// firmware-visible mirror) live in this one entry; a write to any other
        /// GUID is a firmware-variable brick and is refused.
        /// </summary>
        public const string OemMagicVariableGuid = "{9F33F85C-13CA-4FD1-9C4A-96217722C593}";

        /// <summary>Inclusive [start, end] register ranges a raw write may touch.</summary>
        private static readonly (ushort Start, ushort End)[] AllowedRanges =
        {
            // Six 16-byte fan tables: CPU temp-up/temp-down/duty, GPU temp-up/temp-down/duty.
            (0x0F00, 0x0F5F),
            // The writeable PWM register blocks (five bytes per fan).
            (0x1804, 0x1808),
            (0x1809, 0x180D),
            // Keyboard: trigger (0x0767) + RGB channels (0x0769-0x076B); brightness status at 0x078C.
            (0x0767, 0x076B),
            (0x078C, 0x078C),
            // Battery charge mode.
            (0x07A6, 0x07A6),
        };

        /// <summary>
        /// True when a raw write of <paramref name="length"/> bytes starting at
        /// <paramref name="address"/> lies entirely inside ONE allowed range. A
        /// block straddling an allow boundary is refused as a whole: the caller
        /// cannot smuggle a power byte into a fan-table write by length arithmetic.
        /// </summary>
        public static bool IsRawWriteAllowed(ushort address, int length)
        {
            if (length <= 0)
                return false;

            // The last byte of the block, checked against each range end.
            int end = address + length - 1;
            if (end > ushort.MaxValue)
                return false;

            foreach ((ushort start, ushort stop) in AllowedRanges)
            {
                if (address >= start && end <= stop)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// True for the NVRAM GUIDs the raw get/set verbs may write. Comparison
        /// normalizes the brace/hyphen/case variants callers carry (the app writes
        /// braced lowercase, Windows reports bare) so the allowlist cannot be
        /// bypassed by formatting.
        /// </summary>
        public static bool IsNvramWriteAllowed(string guid)
        {
            return string.Equals(NormalizeGuid(guid), NormalizeGuid(OemMagicVariableGuid),
                StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeGuid(string guid)
        {
            if (string.IsNullOrWhiteSpace(guid))
                return string.Empty;

            return guid.Replace("{", "").Replace("}", "").Replace("-", "").Trim();
        }
    }
}
