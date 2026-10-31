using UCU.Core.Ec;
using UCU.Hardware.Ec;

namespace UCU.Hardware.Tests;

/// <summary>
/// The U-108 device switches at the register level, against the semantics
/// the lab-box probe established (docs\acpi\SwitchDiagnostic, 2026-10-31):
///
///  - Fn-lock and AC-recovery are plain bit-scoped RMWs, and the golden
///    writes pin the coexistence rule: the AC-recovery write to 0x0726
///    leaves the handshake's power-management bit 7 (and every other
///    multiplexed bit) untouched — the same byte the activation sequence
///    writes at bit 7, one writer discipline per bit.
///  - Win-lock has NO "write the state" primitive: 0x0768 is a STATUS byte
///    and the act fires the trigger byte 0x0767, which TOGGLES the lock and
///    self-consumes. The fake's OnWrite hook simulates that firmware
///    behaviour, so the engine's contract is testable: fire ONLY on truth
///    mismatch (re-firing would toggle away from the target), exactly once,
///    verify by re-read, report the OBSERVED state.
/// </summary>
public sealed class EcEngineSwitchTests
{
    private static (EcEngine Engine, FakeEcTransport Transport) NewEngine()
    {
        var transport = new FakeEcTransport();
        var engine = new EcEngine(transport, EcPlatform.Amd);
        engine.Initialize();
        transport.ClearLog();
        return (engine, transport);
    }

    // --- Fn lock (0x074E bit 4) -------------------------------------------

    [Fact]
    public void SetFnLock_GoldenWrite_MovesOnlyBit4FromTheProbedUnlockedByte()
    {
        var (engine, transport) = NewEngine();
        transport.Preset(EcEngine.REG_FN_LOCK, 0x01); // lab-box idle byte

        Assert.True(engine.SetFnLockLocked(true));

        Assert.Equal(new byte[] { 0x11 }, transport.WritesAt(EcEngine.REG_FN_LOCK));
    }

    [Fact]
    public void SetFnLock_Unlock_PreservesEveryOtherBit()
    {
        var (engine, transport) = NewEngine();
        transport.Preset(EcEngine.REG_FN_LOCK, 0x31); // bit4 set, bits 5 and 0 also set

        // The verb answers the OBSERVED STATE, not success: a successful
        // unlock answers false (unlocked). Success==state is only equal for
        // ON commands - the field defect that made every row snap back to
        // On after a working OFF write.
        Assert.False(engine.SetFnLockLocked(false));

        Assert.Equal(new byte[] { 0x21 }, transport.WritesAt(EcEngine.REG_FN_LOCK));
    }

    [Fact]
    public void SetFnLock_AlreadyMatches_WritesNothing()
    {
        var (engine, transport) = NewEngine();
        transport.Preset(EcEngine.REG_FN_LOCK, 0x11);

        Assert.True(engine.SetFnLockLocked(true));

        Assert.Empty(transport.Writes);
    }

    [Fact]
    public void GetFnLock_DecodesTheStatusBit_WritesNothing()
    {
        var (engine, transport) = NewEngine();
        transport.Preset(EcEngine.REG_FN_LOCK, 0x11);

        Assert.True(engine.GetFnLockLocked());
        transport.Preset(EcEngine.REG_FN_LOCK, 0x01);
        Assert.False(engine.GetFnLockLocked());
        Assert.Empty(transport.Writes);
    }

    [Fact]
    public void SetFnLock_FirmwareEatsTheWrite_ReportsObservedFalse()
    {
        var (engine, transport) = NewEngine();
        transport.Preset(EcEngine.REG_FN_LOCK, 0x01);

        // A read-back that ignores the write: the firmware owns the outcome,
        // the engine reports the EC's word, not the request.
        transport.OnWrite = (address, value) =>
        {
            if (address == EcEngine.REG_FN_LOCK)
                transport.Preset(EcEngine.REG_FN_LOCK, 0x01);
        };

        Assert.False(engine.SetFnLockLocked(true));
    }

    // --- AC recovery (0x0726 bit 3, shared register) ------------------------

    [Fact]
    public void SetAcRecovery_GoldenWrite_PreservesTheHandshakeGateBit7()
    {
        var (engine, transport) = NewEngine();
        transport.Preset(EcEngine.REG_POWER_MGMT_726, 0x80); // gate set, recovery off (probed idle)

        Assert.True(engine.SetAcRecoveryEnabled(true));

        // The probed post-write byte: 0x80 -> 0x88. Bit 7 SURVIVED — this is
        // acceptance (b): the activation handshake and the recovery toggle
        // coexist on one byte.
        Assert.Equal(new byte[] { 0x88 }, transport.WritesAt(EcEngine.REG_POWER_MGMT_726));
    }

    [Fact]
    public void SetAcRecovery_BusyByte_PreservesEveryUnclaimedBit()
    {
        var (engine, transport) = NewEngine();
        transport.Preset(EcEngine.REG_POWER_MGMT_726, 0x91); // gate + bits 4,0 set

        Assert.True(engine.SetAcRecoveryEnabled(true));

        Assert.Equal(new byte[] { 0x99 }, transport.WritesAt(EcEngine.REG_POWER_MGMT_726));
    }

    [Fact]
    public void SetAcRecovery_Disable_ClearsOnlyBit3()
    {
        var (engine, transport) = NewEngine();
        transport.Preset(EcEngine.REG_POWER_MGMT_726, 0x99);

        // State, not success: a successful disable answers false (see the
        // Fn-unlock pin).
        Assert.False(engine.SetAcRecoveryEnabled(false));

        Assert.Equal(new byte[] { 0x91 }, transport.WritesAt(EcEngine.REG_POWER_MGMT_726));
    }

    [Fact]
    public void SetAcRecovery_AlreadyMatches_WritesNothing()
    {
        var (engine, transport) = NewEngine();
        transport.Preset(EcEngine.REG_POWER_MGMT_726, 0x88);

        Assert.True(engine.SetAcRecoveryEnabled(true));

        Assert.Empty(transport.Writes);
    }

    [Fact]
    public void GetAcRecovery_DecodesTheBit_WritesNothing()
    {
        var (engine, transport) = NewEngine();
        transport.Preset(EcEngine.REG_POWER_MGMT_726, 0x88);

        Assert.True(engine.GetAcRecoveryEnabled());
        transport.Preset(EcEngine.REG_POWER_MGMT_726, 0x80);
        Assert.False(engine.GetAcRecoveryEnabled());
        Assert.Empty(transport.Writes);
    }

    [Fact]
    public void SetAcRecovery_LeavesTheHandshakeWriterTheSameByteItExpects()
    {
        // Coexistence stated from the other side: after the recovery toggle,
        // EnsurePowerManagementActive's bit-7 RMW sees the recovery bit
        // intact — the handshake must not stomp it either.
        var (engine, transport) = NewEngine();
        transport.Preset(EcEngine.REG_POWER_MGMT_726, 0x08); // recovery on, gate off

        engine.SetAcRecoveryEnabled(true); // no-op, byte stays 0x08

        engine.EnsurePowerManagementActive();

        IReadOnlyList<byte> writes726 = transport.WritesAt(EcEngine.REG_POWER_MGMT_726);
        Assert.Contains((byte)0x88, writes726); // gate armed WITHOUT clearing bit 3
    }

    // --- Win-lock (status 0x0768 bit 0, toggle trigger 0x0767 bit 0) -------

    /// <summary>
    /// The lab-box firmware behaviour as a hook: arming 0x0767 bit 0 flips
    /// 0x0768 bit 0 within one poll and self-consumes the trigger bit.
    /// </summary>
    private static void SimulateWinLockFirmware(FakeEcTransport transport)
    {
        transport.OnWrite = (address, value) =>
        {
            if (address != EcEngine.REG_TRIGGER)
                return;

            byte status = transport.Read(EcEngine.REG_WINLOCK_STATUS);
            bool fired = (value & EcSwitchCodec.WinLockTriggerBit) != 0;

            transport.Preset(EcEngine.REG_TRIGGER, (byte)(value & ~EcSwitchCodec.WinLockTriggerBit));

            if (fired)
            {
                transport.Preset(EcEngine.REG_WINLOCK_STATUS, (byte)(status ^ EcSwitchCodec.WinLockStatusBit));
            }
        };
    }

    [Fact]
    public void SetWinLock_FiresTheToggleOnMismatch_VerifiesByReRead()
    {
        var (engine, transport) = NewEngine();
        transport.Preset(EcEngine.REG_WINLOCK_STATUS, 0x00); // probed idle: unlocked
        SimulateWinLockFirmware(transport);

        Assert.True(engine.SetWinLockLocked(true));

        // Exactly ONE fire, bit-armed on top of the trigger byte, and the
        // firmware consumed the bit (self-consuming trigger).
        IReadOnlyList<byte> fires = transport.WritesAt(EcEngine.REG_TRIGGER);
        Assert.Single(fires);
        Assert.NotEqual(0, fires[0] & EcSwitchCodec.WinLockTriggerBit);
        Assert.Equal(0x01, transport.Read(EcEngine.REG_WINLOCK_STATUS));
        Assert.Equal(0x00, transport.Read(EcEngine.REG_TRIGGER));
    }

    [Fact]
    public void SetWinLock_TruthAlreadyMatches_FiresNothing()
    {
        var (engine, transport) = NewEngine();
        transport.Preset(EcEngine.REG_WINLOCK_STATUS, 0x01); // already locked
        SimulateWinLockFirmware(transport);

        Assert.True(engine.SetWinLockLocked(true));

        // The trigger is a TOGGLE: firing on match flips AWAY from the
        // desired state. Zero-fire on match is the whole point.
        Assert.Empty(transport.WritesAt(EcEngine.REG_TRIGGER));
        Assert.Equal(0x01, transport.Read(EcEngine.REG_WINLOCK_STATUS));
    }

    [Fact]
    public void SetWinLock_Unlocks_TheSameFireShapeTogglesBack()
    {
        var (engine, transport) = NewEngine();
        transport.Preset(EcEngine.REG_WINLOCK_STATUS, 0x01);
        SimulateWinLockFirmware(transport);

        // State, not success: a successful unlock answers false (see the
        // Fn-unlock pin).
        Assert.False(engine.SetWinLockLocked(false));

        Assert.Single(transport.WritesAt(EcEngine.REG_TRIGGER));
        Assert.Equal(0x00, transport.Read(EcEngine.REG_WINLOCK_STATUS));
    }

    [Fact]
    public void SetWinLock_ArmsOnTopOfExistingTriggerBits()
    {
        var (engine, transport) = NewEngine();
        transport.Preset(EcEngine.REG_TRIGGER, 0x60); // lighting-effect bits armed
        transport.Preset(EcEngine.REG_WINLOCK_STATUS, 0x00);
        SimulateWinLockFirmware(transport);

        Assert.True(engine.SetWinLockLocked(true));

        byte fire = transport.FirstWriteAt(EcEngine.REG_TRIGGER);
        Assert.Equal(0x61, fire); // bit0 armed, effect bits untouched
    }

    [Fact]
    public void SetWinLock_FirmwareIgnoresTheFire_ReportsObservedFalse()
    {
        var (engine, transport) = NewEngine();
        transport.Preset(EcEngine.REG_WINLOCK_STATUS, 0x00);
        // No firmware simulation: the status byte never moves. The engine
        // polls (2 s budget) and reports the EC's word, never the request.

        Assert.False(engine.SetWinLockLocked(true));

        Assert.Single(transport.WritesAt(EcEngine.REG_TRIGGER)); // fired once, never re-fired
    }

    [Fact]
    public void GetWinLock_DecodesTheStatusBit_WritesNothing()
    {
        var (engine, transport) = NewEngine();
        transport.Preset(EcEngine.REG_WINLOCK_STATUS, 0x01);

        Assert.True(engine.GetWinLockLocked());
        transport.Preset(EcEngine.REG_WINLOCK_STATUS, 0x02); // other status bits are not win-lock
        Assert.False(engine.GetWinLockLocked());
        Assert.Empty(transport.Writes);
    }
}
