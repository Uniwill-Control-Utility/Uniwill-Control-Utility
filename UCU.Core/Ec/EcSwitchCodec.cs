using UCU.Core.Ec.Generated;

namespace UCU.Core.Ec
{
    /// <summary>
    /// Pure bit encoding for the U-108 device switches, field-probed on the
    /// lab box (2026-10-31, docs\acpi\SwitchDiagnostic):
    ///
    ///  - Fn-key lock: 0x074E bit 4 — 0x11 locked / 0x01 unlocked (the
    ///    open-revo encoding, confirmed writable; every other bit of the
    ///    byte survived the toggle + restore).
    ///  - AC power recovery ("turn on when AC is restored"): 0x0726 bit 3 —
    ///    the SAME register the engine's power-management handshake RMWs at
    ///    bit 7. Both writers are bit-scoped: each read-modify-write touches
    ///    exactly its own bit and returns every other bit unchanged (the
    ///    probe confirmed bit 7 survived a bit-3 write, and the EC's single
    ///    serialized domain queue orders the two writers).
    ///  - Win-key lock: TRUTH is 0x0768 bit 0 (the status byte); the SET is
    ///    NOT a write of the state — it fires 0x0767 (trigger byte, bit 0),
    ///    which TOGGLES the lock and self-consumes. The probe watched
    ///    fire→flip within 0.1 s, and a fire inside the firmware's debounce
    ///    window swallowed: fire-only-on-mismatch with verify-by-re-read is
    ///    the honest shape. Lock state lives in the EC, so it survives app
    ///    restarts untouched.
    ///
    /// Every byte builder here is bit-scoped by construction, and the
    /// preserve-unknown-bits property is pinned in EcSwitchCodecTests —
    /// a future edit that returns `value` instead of `value | bit` would
    /// stomp the handshake's bit 7 and the firmware's other multiplexed
    /// bits (0x0726 also carries the Q-key LED white bit 0x80 per the
    /// open-revo registry — the EC multiplexes, so unclaimed bits stay
    /// owned by whoever set them).
    /// </summary>
    public static class EcSwitchCodec
    {
        /// <summary>0x074E bit 4: F-key lock.</summary>
        public const byte FnLockBit = EcVault.FN_LOCK_BIT;

        /// <summary>0x0726 bit 3: power on when AC is restored.</summary>
        public const byte AcRecoveryBit = EcVault.AC_RECOVERY_BIT;

        /// <summary>0x0768 bit 0: Win-key lock status.</summary>
        public const byte WinLockStatusBit = EcVault.WINLOCK_STATUS_BIT;

        /// <summary>0x0767 bit 0: Win-key lock TOGGLE trigger (self-consuming).</summary>
        public const byte WinLockTriggerBit = EcVault.WINLOCK_TRIGGER_BIT;

        public static bool IsFnLockLocked(byte registerByte) => (registerByte & FnLockBit) != 0;

        /// <summary>The byte to write to move the Fn-lock bit to <paramref name="locked"/>,
        /// returning every other bit of <paramref name="current"/> unchanged.</summary>
        public static byte FnLockByte(byte current, bool locked) =>
            locked ? (byte)(current | FnLockBit) : (byte)(current & ~FnLockBit);

        public static bool IsAcRecoveryEnabled(byte registerByte) => (registerByte & AcRecoveryBit) != 0;

        /// <summary>The byte to write to move the AC-recovery bit, preserving
        /// every other bit of the shared 0x0726 (the handshake's bit 7 above
        /// all — acceptance b of U-108).</summary>
        public static byte AcRecoveryByte(byte current, bool enabled) =>
            enabled ? (byte)(current | AcRecoveryBit) : (byte)(current & ~AcRecoveryBit);

        public static bool IsWinLockLocked(byte statusByte) => (statusByte & WinLockStatusBit) != 0;

        /// <summary>The trigger-byte value that fires the Win-lock TOGGLE:
        /// sets the trigger bit on top of <paramref name="currentTrigger"/>
        /// (the firmware consumes it; other trigger bits ride untouched —
        /// the open-revo trigger_ec_action shape, confirmed on this
        /// chassis).</summary>
        public static byte WinLockTriggerByte(byte currentTrigger) =>
            (byte)(currentTrigger | WinLockTriggerBit);
    }
}
