using Serilog;

namespace UCU.Hardware.Firmware
{
    /// <summary>
    /// The seam the NVRAM verbs run on: the production implementation reads and
    /// writes UEFI firmware environment variables with SeSystemEnvironmentPrivilege
    /// enabled on the process token (which only a privileged token can do); the
    /// RPC tests inject a scripted variable store, because no test may write a
    /// real machine's firmware.
    /// </summary>
    public interface IFwNvramService
    {
        /// <summary>The variable's bytes, or null when the variable does not
        /// exist (the same shape the app's direct reads used).</summary>
        byte[]? Read(string name, string guid);

        bool Write(string name, string guid, byte[] data);

        /// <summary>Firmware-reported support byte for the CPU Advanced
        /// Performance Menu (OemMagicVariable offset 110, non-zero = the BIOS
        /// exposes the menu; Intel NB02 = 1, AMD = 0).</summary>
        bool SupportsCpuApMenu();

        /// <summary>Unlocks/locks the BIOS CPU Advanced Performance Menu by
        /// patching offsets 111 (CPU menu switch) and 51 (memory OC switch) in
        /// lockstep. Read-patch-write of the whole entry on one call: the pair
        /// can never half-update. True when the write landed, verified by
        /// re-reading the offsets.</summary>
        bool SetCpuApMenuUnlock(bool enabled);
    }

    /// <summary>
    /// UEFI firmware environment variables through the Win32 firmware-variable
    /// APIs. These demand SeSystemEnvironmentPrivilege, which an unelevated
    /// process cannot enable — the reason every app-side use of them (the CPU
    /// Advanced Performance Menu row, the GPU mux's firmware-visible mirror)
    /// was silently dead on the field machines: the reads returned nothing and
    /// the writes failed with access denied, with no prompt to explain. The
    /// engine runs LocalSystem, enables the privilege on its own token, and
    /// answers the same reads and writes as designed.
    ///
    /// The OEM entry's layout (which offset is which feature) is machine data
    /// the app owns; this service keeps only the two offsets whose pair-write
    /// must be atomic (the AP-menu unlock) and the support byte that gates it.
    ///
    /// The 50-series gatekeeper: on this chassis generation a firmware write
    /// of the OEM entry lands (and sticks across reboot) only when the
    /// OemMagicDoor variable is unlocked first — written 0x01 immediately
    /// before the entry write, the handshake the reference implementation
    /// performs on the variable name alone (open-revo
    /// <c>src-tauri\src\driver\uefi_nvram.rs</c>). Every write of the entry
    /// funnels through <see cref="Write"/>, so the handshake lives there:
    /// the raw verb, the AP-menu pair-write and the app-side rows all get
    /// it, and none can write the entry blind.
    /// </summary>
    public sealed class FwNvramService : IFwNvramService
    {
        public const string OemMagicVariable = "OemMagicVariable";
        public const string OemMagicVariableGuid = "{9F33F85C-13CA-4FD1-9C4A-96217722C593}";

        /// <summary>The 50-series gatekeeper variable: written
        /// <see cref="OemMagicDoorKey"/> immediately before any write of the
        /// OEM entry. The app's local fallback carries the same name, pinned
        /// in tests.</summary>
        public const string OemMagicDoor = "OemMagicDoor";

        /// <summary>The unlock key: the door variable set to one byte, 0x01.</summary>
        public const byte OemMagicDoorKey = 0x01;

        /// <summary>Win32 "the variable genuinely does not exist" — the
        /// difference between a firmware without the gatekeeper and a failed
        /// probe.</summary>
        public const uint ERROR_ENVVAR_NOT_FOUND = 203;

        public const int OffsetCpuApMenuSupport = 110;
        public const int OffsetCpuApMenuSwitch = 111;
        public const int OffsetMemOcSwitch = 51;

        private const int DoorUnlockAttempts = 2;

        private readonly IFirmwareEnvironment _env;

        public FwNvramService() : this(new Win32FirmwareEnvironment())
        {
        }

        /// <summary>Test seam: a scripted <see cref="IFirmwareEnvironment"/>
        /// (no test writes a real machine's firmware).</summary>
        public FwNvramService(IFirmwareEnvironment env)
        {
            _env = env;
        }

        public byte[]? Read(string name, string guid)
        {
            // The engine holds SeSystemEnvironmentPrivilege, so an ungated read
            // hands any interactive user firmware variables they cannot read
            // themselves (Secure Boot dbx, vendor provisioning data). This service
            // only ever serves the OEM entry and its door.
            if (!IsUnderOemGuid(guid) || !IsOemVariableName(name))
            {
                Log.Warning("[FwNvram] read {Name} refused: only the OEM entry and its door are served", name);
                return null;
            }

            byte[]? data = _env.Read(name, guid, out uint error);

            if (data is null && error != ERROR_ENVVAR_NOT_FOUND)
            {
                // The honest signal, which the app-side reads never carried:
                // "no bytes" has reasons (privilege, missing variable, bad
                // buffer), and the field's question is always which one.
                Log.Information("[FwNvram] read {Name} failed: win32 error {Error}", name, error);
            }

            return data;
        }

        public bool Write(string name, string guid, byte[] data)
        {
            bool magicEntry = IsOemMagicEntry(name, guid);

            // The name and the length are part of the address, and both arrive from
            // a pipe verb any interactive user can call. Under the OEM GUID this app
            // owns exactly one variable, and its length is the firmware's layout:
            // a one-byte write of a name nobody reads (or a short write of the entry)
            // destroys offsets the rows cannot repair - the mem-OC switch, the GPU
            // mux mirror and the AP-menu pair. So only the entry, and only a
            // same-length (read-modify-write) replacement, is written at all.
            if (IsUnderOemGuid(guid) && !magicEntry)
            {
                Log.Warning("[FwNvram] write {Name} refused: only {Entry} is written under the OEM GUID",
                    name, OemMagicVariable);
                return false;
            }

            if (magicEntry)
            {
                byte[]? existing = _env.Read(name, guid, out _);
                if (existing is not null && existing.Length != data.Length)
                {
                    Log.Warning(
                        "[FwNvram] write {Name} refused: {Bytes} bytes against the entry's {Existing} (the length is the firmware layout)",
                        name, data.Length, existing.Length);
                    return false;
                }
            }

            // The gatekeeper comes first: on a 50-series firmware the entry
            // write below is refused (returns false, nothing written) when
            // the door cannot be unlocked — no blind write.
            if (magicEntry && !UnlockMagicDoor())
                return false;

            if (!_env.Write(name, guid, data, out uint error))
            {
                Log.Information("[FwNvram] write {Name} ({Bytes} bytes) failed: win32 error {Error}",
                    name, data.Length, error);
                return false;
            }

            Log.Information("[FwNvram] write {Name} ok ({Bytes} bytes)", name, data.Length);

            if (magicEntry)
                VerifyEntryWrite(name, guid, data);

            return true;
        }

        public bool SupportsCpuApMenu()
        {
            byte[]? nvram = Read(OemMagicVariable, OemMagicVariableGuid);
            return nvram is not null
                && nvram.Length > OffsetCpuApMenuSupport
                && nvram[OffsetCpuApMenuSupport] != 0;
        }

        public bool SetCpuApMenuUnlock(bool enabled)
        {
            byte[]? nvram = Read(OemMagicVariable, OemMagicVariableGuid);
            if (nvram is null || nvram.Length <= OffsetCpuApMenuSwitch)
            {
                Log.Information("[FwNvram] OemMagicVariable unavailable for AP menu write");
                return false;
            }

            byte[] modified = (byte[])nvram.Clone();
            modified[OffsetCpuApMenuSwitch] = (byte)(enabled ? 1 : 0);
            modified[OffsetMemOcSwitch] = (byte)(enabled ? 1 : 0);

            // The door handshake rides Write (the entry's chokepoint), so the
            // pair-write lands unlocked like every other entry write.
            if (!Write(OemMagicVariable, OemMagicVariableGuid, modified))
                return false;

            // Verify by re-read: the entry survives a firmware write only if the
            // offsets say so afterwards, and the row's toggle should flip on the
            // firmware's answer, not on the write call's.
            byte[]? check = Read(OemMagicVariable, OemMagicVariableGuid);
            bool verified = check is not null
                && check.Length > OffsetCpuApMenuSwitch
                && check[OffsetCpuApMenuSwitch] == (byte)(enabled ? 1 : 0)
                && check[OffsetMemOcSwitch] == (byte)(enabled ? 1 : 0);

            Log.Information("[FwNvram] AP menu unlock {State}: verified={Verified}",
                enabled ? "on" : "off", verified);
            return verified;
        }

        /// <summary>
        /// The 50-series handshake: unlock OemMagicDoor (one byte, 0x01) so
        /// the following OEM-entry write lands. The door is probed first, and
        /// a firmware WITHOUT the door variable — the read says NOT_FOUND —
        /// is not a gatekeeper board: the entry is written directly and the
        /// door is never CREATED (fabricating firmware variables is a
        /// board-bricking act). A door write that fails after the retry
        /// refuses the entry write: the entry is never written blind.
        /// </summary>
        private bool UnlockMagicDoor()
        {
            byte[]? door = _env.Read(OemMagicDoor, OemMagicVariableGuid, out uint readError);

            if (door is null && readError == ERROR_ENVVAR_NOT_FOUND)
            {
                Log.Information("[FwNvram] OemMagicDoor absent - no gatekeeper on this firmware; writing the entry directly");
                return true;
            }

            if (door is null)
            {
                // Probe failed for a non-absence reason (privilege, firmware):
                // attempt the unlock anyway; if the write then fails the
                // entry write is refused below.
                Log.Information("[FwNvram] OemMagicDoor probe failed: win32 error {Error} - attempting the unlock anyway", readError);
            }

            byte[] key = { OemMagicDoorKey };
            for (int attempt = 1; attempt <= DoorUnlockAttempts; attempt++)
            {
                if (_env.Write(OemMagicDoor, OemMagicVariableGuid, key, out uint writeError))
                {
                    // The readback is evidence, not a gate: firmware may
                    // consume the door as part of unlocking, so "gone" after
                    // a successful write is a normal answer.
                    byte[]? after = _env.Read(OemMagicDoor, OemMagicVariableGuid, out _);
                    Log.Information("[FwNvram] OemMagicDoor unlocked (attempt {Attempt}); readback: {Readback}",
                        attempt, after is null ? "gone (consumed)" : string.Concat(after.Select(b => b.ToString("X2"))));
                    return true;
                }

                Log.Information("[FwNvram] OemMagicDoor unlock attempt {Attempt} failed: win32 error {Error}",
                    attempt, writeError);
            }

            Log.Information("[FwNvram] OemMagicDoor unlock failed - refusing the OemMagicVariable write (no blind write)");
            return false;
        }

        /// <summary>
        /// Readback evidence for a landed OEM-entry write: the firmware's
        /// SPI-flash answer, logged next to the write's ok-line. The write's
        /// return value stays the write call's verdict (the AP-menu verb's
        /// offset-level verification is its own, stricter check).
        /// </summary>
        private void VerifyEntryWrite(string name, string guid, byte[] data)
        {
            byte[]? check = _env.Read(name, guid, out _);
            Log.Information("[FwNvram] write {Name} readback verification: {Verified}",
                name, check is not null && check.AsSpan().SequenceEqual(data));
        }

        private static bool IsOemMagicEntry(string name, string guid) =>
            string.Equals(name, OemMagicVariable, StringComparison.OrdinalIgnoreCase)
            && string.Equals(guid, OemMagicVariableGuid, StringComparison.OrdinalIgnoreCase);

        private static bool IsUnderOemGuid(string guid) =>
            string.Equals(guid, OemMagicVariableGuid, StringComparison.OrdinalIgnoreCase);

        private static bool IsOemVariableName(string name) =>
            string.Equals(name, OemMagicVariable, StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, OemMagicDoor, StringComparison.OrdinalIgnoreCase);
    }
}
