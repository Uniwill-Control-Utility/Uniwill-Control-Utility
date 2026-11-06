using UCU.Core.Ec;

namespace UCU.Core.Tests.Ec;

/// <summary>
/// The U-108 device-switch bit encodings, field-probed on the lab box
/// (docs\acpi\SwitchDiagnostic). The load-bearing property is BIT-SCOPED
/// READ-MODIFY-WRITE: 0x0726 is shared with the power-management handshake
/// (bit 7) and the open-revo registry names bit 7's neighbourhood for the
/// Q-key LED white bit, so an AC-recovery write that stomps any other bit is
/// a hardware regression, not a style issue. Every builder is therefore
/// pinned over ALL 256 input bytes: the target bit moves, every other bit
/// rides through byte-identical.
/// </summary>
public sealed class EcSwitchCodecTests
{
    private static IEnumerable<byte> AllBytes()
    {
        for (int v = 0; v <= 0xFF; v++) yield return (byte)v;
    }

    // --- Fn lock (0x074E bit 4) -------------------------------------------

    [Theory]
    [InlineData(0x01, false)]
    [InlineData(0x11, true)]
    [InlineData(0x10, true)]
    [InlineData(0x00, false)]
    public void IsFnLockLocked_DecodesTheProbedEncoding(byte registerByte, bool expected)
    {
        Assert.Equal(expected, EcSwitchCodec.IsFnLockLocked(registerByte));
    }

    [Fact]
    public void FnLockByte_MovesOnlyBit4_AcrossEveryInputByte()
    {
        foreach (byte current in AllBytes())
        {
            foreach (bool locked in new[] { true, false })
            {
                byte result = EcSwitchCodec.FnLockByte(current, locked);

                // The target bit lands exactly where commanded...
                Assert.Equal(locked, EcSwitchCodec.IsFnLockLocked(result));

                // ...and every other bit is byte-identical to the input.
                Assert.Equal(current & ~EcSwitchCodec.FnLockBit, result & ~EcSwitchCodec.FnLockBit);
            }
        }
    }

    [Fact]
    public void FnLockByte_IsIdempotent_ReapplyingTheSameStateChangesNothing()
    {
        foreach (byte current in AllBytes())
        {
            foreach (bool locked in new[] { true, false })
            {
                byte once = EcSwitchCodec.FnLockByte(current, locked);
                Assert.Equal(once, EcSwitchCodec.FnLockByte(once, locked));
            }
        }
    }

    [Fact]
    public void FnLockByte_RoundTrips_TheDecoderSeesWhatTheBuilderCommanded()
    {
        foreach (byte current in AllBytes())
        {
            Assert.True(EcSwitchCodec.IsFnLockLocked(EcSwitchCodec.FnLockByte(current, true)));
            Assert.False(EcSwitchCodec.IsFnLockLocked(EcSwitchCodec.FnLockByte(current, false)));
        }
    }

    // --- AC recovery (0x0726 bit 3, shared with the handshake's bit 7) -----

    [Theory]
    [InlineData(0x80, false)] // the lab-box idle value: gate set, recovery off
    [InlineData(0x88, true)]  // the probed post-write value: gate SURVIVED
    [InlineData(0x08, true)]
    [InlineData(0x00, false)]
    public void IsAcRecoveryEnabled_DecodesTheProbedEncoding(byte registerByte, bool expected)
    {
        Assert.Equal(expected, EcSwitchCodec.IsAcRecoveryEnabled(registerByte));
    }

    [Fact]
    public void AcRecoveryByte_MovesOnlyBit3_AcrossEveryInputByte()
    {
        foreach (byte current in AllBytes())
        {
            foreach (bool enabled in new[] { true, false })
            {
                byte result = EcSwitchCodec.AcRecoveryByte(current, enabled);

                Assert.Equal(enabled, EcSwitchCodec.IsAcRecoveryEnabled(result));

                // THE acceptance-b property (U-108): the handshake bit 7 and
                // every other multiplexed bit survive the RMW.
                Assert.Equal(current & ~EcSwitchCodec.AcRecoveryBit, result & ~EcSwitchCodec.AcRecoveryBit);
            }
        }
    }

    [Fact]
    public void AcRecoveryByte_PreservesTheHandshakeGateBit7_OnEveryTransition()
    {
        foreach (byte current in AllBytes())
        {
            bool gateWasSet = (current & 0x80) != 0;

            foreach (bool enabled in new[] { true, false })
            {
                byte result = EcSwitchCodec.AcRecoveryByte(current, enabled);
                Assert.Equal(gateWasSet, (result & 0x80) != 0);
            }
        }
    }

    [Fact]
    public void AcRecoveryByte_IsIdempotent_ReapplyingTheSameStateChangesNothing()
    {
        foreach (byte current in AllBytes())
        {
            foreach (bool enabled in new[] { true, false })
            {
                byte once = EcSwitchCodec.AcRecoveryByte(current, enabled);
                Assert.Equal(once, EcSwitchCodec.AcRecoveryByte(once, enabled));
            }
        }
    }

    // --- Win-lock (status 0x0768 bit 0, toggle trigger 0x0767 bit 0) -------

    [Theory]
    [InlineData(0x00, false)] // lab-box idle: unlocked
    [InlineData(0x01, true)]  // probed post-fire: locked
    [InlineData(0x02, false)] // other status bits do not decode as win-lock
    public void IsWinLockLocked_DecodesTheProbedStatusBit(byte statusByte, bool expected)
    {
        Assert.Equal(expected, EcSwitchCodec.IsWinLockLocked(statusByte));
    }

    [Fact]
    public void WinLockTriggerByte_ArmsBit0OnTopLeavingEveryOtherTriggerBitAlone()
    {
        // The trigger byte multiplexes the lighting effects (logo bit 6,
        // rainbow bit 7, lightbar bits) — the fire must not disturb them.
        foreach (byte current in AllBytes())
        {
            byte result = EcSwitchCodec.WinLockTriggerByte(current);

            Assert.NotEqual(0, result & EcSwitchCodec.WinLockTriggerBit);
            Assert.Equal(current & ~EcSwitchCodec.WinLockTriggerBit, result & ~EcSwitchCodec.WinLockTriggerBit);
        }
    }

    [Fact]
    public void WinLockTriggerByte_IsIdempotent_ArmingTwiceIsOneFireBit()
    {
        foreach (byte current in AllBytes())
        {
            byte once = EcSwitchCodec.WinLockTriggerByte(current);
            Assert.Equal(once, EcSwitchCodec.WinLockTriggerByte(once));
        }
    }
}
