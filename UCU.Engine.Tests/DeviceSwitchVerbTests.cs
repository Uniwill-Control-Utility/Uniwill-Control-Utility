using UCU.Hardware.Ec;

namespace UCU.Engine.Tests;

/// <summary>
/// The U-108 device-switch verbs end to end: client proxy → pipe →
/// dispatcher → domain queue → EcEngine on a scripted transport. The locks
/// ride their effect domains on purpose — the win-lock fire is an RMW of
/// the keyboard trigger byte (ordered against the lighting-effect writes)
/// and the AC-recovery RMW of 0x0726 is ordered against the power-domain
/// handshake writers; one serialized queue per byte, no interleaved RMW.
/// The SET verbs answer with the OBSERVED EC state, not the request: the
/// firmware owns the outcome.
/// </summary>
public sealed class DeviceSwitchVerbTests
{
    // --- Fn lock (keyboard domain) -----------------------------------------

    [Fact]
    public async Task GetFnLock_RoundTripsTheStatusBit()
    {
        var transport = new FakeEcTransport();
        transport.Preset(EcEngine.REG_FN_LOCK, 0x11);

        await using var host = await EngineHostFixture.StartAsync(transport: transport);

        Assert.True(await host.Keyboard.GetFnLockAsync());
    }

    [Fact]
    public async Task SetFnLock_GoldenWrite_ReportsObservedTrue()
    {
        var transport = new FakeEcTransport();
        transport.Preset(EcEngine.REG_FN_LOCK, 0x01); // probed idle byte

        await using var host = await EngineHostFixture.StartAsync(transport: transport);

        Assert.True(await host.Keyboard.SetFnLockAsync(true));
        Assert.Equal(new byte[] { 0x11 }, host.Fake.WritesAt(EcEngine.REG_FN_LOCK));
    }

    // --- Win-lock (keyboard domain: trigger-byte RMW) -----------------------

    /// <summary>Script the lab-box firmware: arming 0x0767 bit0 flips
    /// 0x0768 bit0 and self-consumes.</summary>
    private static void SimulateWinLockFirmware(FakeEcTransport transport)
    {
        transport.OnWrite = (address, value) =>
        {
            if (address != EcEngine.REG_TRIGGER)
                return;

            byte status = transport.Read(EcEngine.REG_WINLOCK_STATUS);
            bool fired = (value & 0x01) != 0;
            transport.Preset(EcEngine.REG_TRIGGER, (byte)(value & ~0x01));

            if (fired)
                transport.Preset(EcEngine.REG_WINLOCK_STATUS, (byte)(status ^ 0x01));
        };
    }

    [Fact]
    public async Task GetWinLock_RoundTripsTheStatusByte()
    {
        var transport = new FakeEcTransport();
        transport.Preset(EcEngine.REG_WINLOCK_STATUS, 0x01);

        await using var host = await EngineHostFixture.StartAsync(transport: transport);

        Assert.True(await host.Keyboard.GetWinLockAsync());
    }

    [Fact]
    public async Task SetWinLock_FiresOnMismatch_VerifiesAndMovesNoEffectBits()
    {
        var transport = new FakeEcTransport();
        transport.Preset(EcEngine.REG_TRIGGER, 0x60); // effect bits armed
        transport.Preset(EcEngine.REG_WINLOCK_STATUS, 0x00);
        SimulateWinLockFirmware(transport);

        await using var host = await EngineHostFixture.StartAsync(transport: transport);

        Assert.True(await host.Keyboard.SetWinLockAsync(true));

        IReadOnlyList<byte> fires = host.Fake.WritesAt(EcEngine.REG_TRIGGER);
        Assert.Single(fires); // exactly one fire: the trigger is a toggle
        Assert.Equal(0x61, fires[0]); // bit0 armed on top, effect bits intact
    }

    [Fact]
    public async Task SetWinLock_TruthMatches_FiresNothing()
    {
        var transport = new FakeEcTransport();
        transport.Preset(EcEngine.REG_WINLOCK_STATUS, 0x01);
        SimulateWinLockFirmware(transport);

        await using var host = await EngineHostFixture.StartAsync(transport: transport);

        Assert.True(await host.Keyboard.SetWinLockAsync(true));

        Assert.DoesNotContain(host.Fake.Writes, w => w.Address == EcEngine.REG_TRIGGER);
    }

    [Fact]
    public async Task SetWinLock_FirmwareIgnoresTheFire_ReportsObservedFalse()
    {
        var transport = new FakeEcTransport();
        transport.Preset(EcEngine.REG_WINLOCK_STATUS, 0x00); // no simulation

        await using var host = await EngineHostFixture.StartAsync(transport: transport);

        Assert.False(await host.Keyboard.SetWinLockAsync(true));
    }

    // --- AC recovery (power domain: shared 0x0726) --------------------------

    [Fact]
    public async Task GetAcRecovery_RoundTripsTheBit()
    {
        var transport = new FakeEcTransport();
        transport.Preset(EcEngine.REG_POWER_MGMT_726, 0x88); // gate + recovery

        await using var host = await EngineHostFixture.StartAsync(transport: transport);

        Assert.True(await host.Power.GetAcRecoveryAsync());
    }

    [Fact]
    public async Task SetAcRecovery_GoldenWrite_PreservesTheGateBit_ReportsObservedTrue()
    {
        var transport = new FakeEcTransport();
        transport.Preset(EcEngine.REG_POWER_MGMT_726, 0x80); // probed idle byte

        await using var host = await EngineHostFixture.StartAsync(transport: transport);

        Assert.True(await host.Power.SetAcRecoveryAsync(true));

        // The probed golden byte: bit3 moves, the handshake's bit7 rides.
        // (The init handshake's own Step 3b gate write to the same byte —
        // 0x80 -> 0x80 — precedes it: two writers, one byte, each bit-scoped.)
        Assert.Equal((byte)0x88, host.Fake.LastWriteAt(EcEngine.REG_POWER_MGMT_726));
    }
}
