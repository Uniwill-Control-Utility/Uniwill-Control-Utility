using UCU.Hardware.Firmware;

namespace UCU.Hardware.Tests;

/// <summary>
/// The 50-series gatekeeper handshake (U-106): every write of the OEM entry
/// (OemMagicVariable under the UniWill GUID) unlocks OemMagicDoor (one byte,
/// 0x01) FIRST — the reference (open-revo <c>uefi_nvram.rs</c>) fires the
/// handshake on the variable name alone, and on this chassis generation an
/// entry write without it is the "did not stick across reboot" class. The
/// rules pinned here: golden call order (door, then entry); a failed door
/// write refuses the entry write (no blind write), with one retry; a
/// firmware WITHOUT the door (read = NOT_FOUND) gets a direct entry write
/// and the door is never created; reads and non-entry writes never touch
/// the door. All of it runs on the scripted IFirmwareEnvironment seam —
/// no test writes a real machine's SPI flash.
/// </summary>
public sealed class FwNvramServiceMagicDoorTests
{
    private static readonly byte[] Entry = EntryBytes();

    private static byte[] EntryBytes()
    {
        byte[] buffer = new byte[120];
        buffer[FwNvramService.OffsetCpuApMenuSupport] = 1;
        return buffer;
    }

    [Fact]
    public void Write_OemMagicEntry_UnlocksTheDoorBeforeWriting_TheGoldenOrder()
    {
        var env = new FakeFirmwareEnvironment().WithDoor();
        var service = new FwNvramService(env);

        Assert.True(service.Write(FwNvramService.OemMagicVariable, FwNvramService.OemMagicVariableGuid, Entry));

        Assert.Equal(
            new[] { FwNvramService.OemMagicDoor, FwNvramService.OemMagicVariable },
            env.Writes.Select(w => w.Name).ToArray());

        // The key: the door variable, one byte 0x01, under the UniWill GUID.
        (string doorName, string doorGuid, byte[] doorData) = env.Writes[0];
        Assert.Equal(FwNvramService.OemMagicDoor, doorName);
        Assert.Equal(FwNvramService.OemMagicVariableGuid, doorGuid);
        Assert.Equal(new byte[] { FwNvramService.OemMagicDoorKey }, doorData);
    }

    [Fact]
    public void Write_OemMagicEntry_DoorWriteRefusedAfterRetry_WritesNothing()
    {
        var env = new FakeFirmwareEnvironment().WithDoor().WithDoorWriteOutcomes(false, false);
        var service = new FwNvramService(env);

        Assert.False(service.Write(FwNvramService.OemMagicVariable, FwNvramService.OemMagicVariableGuid, Entry));

        // Both unlock attempts burned, and the entry was never written blind.
        Assert.Equal(2, env.Writes.Count);
        Assert.All(env.Writes, w => Assert.Equal(FwNvramService.OemMagicDoor, w.Name));
        Assert.False(env.HasEntry());
    }

    [Fact]
    public void Write_OemMagicEntry_DoorUnlockSucceedsOnRetry_EntryWriteProceeds()
    {
        var env = new FakeFirmwareEnvironment().WithDoor().WithDoorWriteOutcomes(false, true);
        var service = new FwNvramService(env);

        Assert.True(service.Write(FwNvramService.OemMagicVariable, FwNvramService.OemMagicVariableGuid, Entry));

        Assert.Equal(
            new[] { FwNvramService.OemMagicDoor, FwNvramService.OemMagicDoor, FwNvramService.OemMagicVariable },
            env.Writes.Select(w => w.Name).ToArray());
        Assert.True(env.HasEntry());
    }

    [Fact]
    public void Write_OemMagicEntry_FirmwareWithoutTheDoor_WritesTheEntryDirectly_NeverCreatesTheDoor()
    {
        var env = new FakeFirmwareEnvironment(); // door read answers NOT_FOUND
        var service = new FwNvramService(env);

        Assert.True(service.Write(FwNvramService.OemMagicVariable, FwNvramService.OemMagicVariableGuid, Entry));

        Assert.Equal(new[] { FwNvramService.OemMagicVariable }, env.Writes.Select(w => w.Name).ToArray());
    }

    [Fact]
    public void Write_OemMagicEntry_DoorProbeFailsForANonAbsenceReason_TheUnlockIsAttemptedAnyway()
    {
        // A privilege/firmware read failure is not "absent": knocking on the
        // door is attempted, and only a FAILED DOOR WRITE refuses the entry.
        var env = new FakeFirmwareEnvironment()
            .WithReadError(FwNvramService.OemMagicDoor, 5); // ERROR_ACCESS_DENIED
        var service = new FwNvramService(env);

        Assert.True(service.Write(FwNvramService.OemMagicVariable, FwNvramService.OemMagicVariableGuid, Entry));

        Assert.Equal(
            new[] { FwNvramService.OemMagicDoor, FwNvramService.OemMagicVariable },
            env.Writes.Select(w => w.Name).ToArray());
    }

    [Fact]
    public void Write_OtherNameUnderTheGuid_MovesNoDoor()
    {
        var env = new FakeFirmwareEnvironment().WithDoor();
        var service = new FwNvramService(env);

        Assert.True(service.Write("SomeOtherEntry", FwNvramService.OemMagicVariableGuid, Entry));

        Assert.Equal(new[] { "SomeOtherEntry" }, env.Writes.Select(w => w.Name).ToArray());
    }

    [Fact]
    public void SetCpuApMenuUnlock_PairWriteRidesTheHandshake_VerifiedByReRead()
    {
        var env = new FakeFirmwareEnvironment()
            .WithDoor()
            .WithVariable(FwNvramService.OemMagicVariable, EntryBytes());
        var service = new FwNvramService(env);

        Assert.True(service.SetCpuApMenuUnlock(enabled: true));

        // Door first, then the pair-write; the offsets carry the unlock.
        Assert.Equal(
            new[] { FwNvramService.OemMagicDoor, FwNvramService.OemMagicVariable },
            env.Writes.Select(w => w.Name).ToArray());
        byte[] stored = env.EntryBytes()!;
        Assert.Equal(1, stored[FwNvramService.OffsetCpuApMenuSwitch]);
        Assert.Equal(1, stored[FwNvramService.OffsetMemOcSwitch]);
    }

    [Fact]
    public void Reads_NeverTouchTheDoor()
    {
        var env = new FakeFirmwareEnvironment()
            .WithDoor()
            .WithVariable(FwNvramService.OemMagicVariable, EntryBytes());
        var service = new FwNvramService(env);

        Assert.True(service.SupportsCpuApMenu());
        Assert.NotNull(service.Read(FwNvramService.OemMagicVariable, FwNvramService.OemMagicVariableGuid));

        Assert.Empty(env.Writes);
    }

    [Fact]
    public void Write_LandedButReadbackFails_TheWriteCallVerdictStands()
    {
        // The readback is evidence in the log, not a second gate: a firmware
        // that answered the write and then refuses the re-read has landed it.
        var env = new FakeFirmwareEnvironment().WithDoor();
        env.WithReadError(FwNvramService.OemMagicVariable, 5);
        var service = new FwNvramService(env);

        Assert.True(service.Write(FwNvramService.OemMagicVariable, FwNvramService.OemMagicVariableGuid, Entry));
    }

    /// <summary>
    /// The scripted firmware variable store: reads answer from the store (with
    /// per-name scripted errors), writes record their (name, guid, bytes) in
    /// call order and land in the store. Door write outcomes are scripted
    /// attempt by attempt.
    /// </summary>
    private sealed class FakeFirmwareEnvironment : IFirmwareEnvironment
    {
        private readonly Dictionary<string, byte[]> _variables = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, uint> _readErrors = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<bool> _doorWriteOutcomes = new();

        public List<(string Name, string Guid, byte[] Data)> Writes { get; } = new();

        public FakeFirmwareEnvironment WithDoor(byte value = FwNvramService.OemMagicDoorKey) =>
            WithVariable(FwNvramService.OemMagicDoor, new[] { value });

        public FakeFirmwareEnvironment WithVariable(string name, byte[] data)
        {
            _variables[name] = (byte[])data.Clone();
            return this;
        }

        public FakeFirmwareEnvironment WithReadError(string name, uint error)
        {
            _readErrors[name] = error;
            return this;
        }

        public FakeFirmwareEnvironment WithDoorWriteOutcomes(params bool[] outcomes)
        {
            _doorWriteOutcomes.AddRange(outcomes);
            return this;
        }

        public byte[]? EntryBytes() =>
            _variables.TryGetValue(FwNvramService.OemMagicVariable, out byte[]? data) ? data : null;

        public bool HasEntry() => EntryBytes() is not null;

        public byte[]? Read(string name, string guid, out uint error)
        {
            if (_readErrors.TryGetValue(name, out uint scripted))
            {
                error = scripted;
                return null;
            }

            if (_variables.TryGetValue(name, out byte[]? data))
            {
                error = 0;
                return (byte[])data.Clone();
            }

            error = FwNvramService.ERROR_ENVVAR_NOT_FOUND;
            return null;
        }

        public bool Write(string name, string guid, byte[] data, out uint error)
        {
            Writes.Add((name, guid, (byte[])data.Clone()));

            if (name.Equals(FwNvramService.OemMagicDoor, StringComparison.OrdinalIgnoreCase)
                && _doorWriteOutcomes.Count > 0)
            {
                bool outcome = _doorWriteOutcomes[0];
                _doorWriteOutcomes.RemoveAt(0);
                error = outcome ? 0u : 5u; // ERROR_ACCESS_DENIED
                if (outcome)
                    _variables[name] = (byte[])data.Clone();
                return outcome;
            }

            error = 0;
            _variables[name] = (byte[])data.Clone();
            return true;
        }
    }
}
