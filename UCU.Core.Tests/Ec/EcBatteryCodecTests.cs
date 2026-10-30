using UCU.Core.Ec;

namespace UCU.Core.Tests.Ec;

/// <summary>
/// The U-103 battery additions: the threshold PAIR the mode implies (derived —
/// the lab-box probe 2026-10-30 refuted the open-revo chassis' explicit
/// threshold registers on this family, docs\acpi\BatteryDiagnostic), the pair's
/// validation contract, and the read-only gate/override decoders. The mode
/// codec's decode is pinned at the register level in UCU.Hardware.Tests; the
/// encode/decode round-trip is pinned here at the pure level.
/// </summary>
public sealed class EcBatteryCodecTests
{
    // ---- mode round-trip (the legacy 100/80/60 path) --------------------------

    [Theory]
    [InlineData(0, 0x08)]
    [InlineData(1, 0x18)]
    [InlineData(2, 0x28)]
    public void Mode_EncodesTheRawField_AndDecodesBack(int mode, byte raw)
    {
        Assert.Equal(raw, EcBatteryCodec.EncodeMode(mode));
        Assert.Equal(mode, EcBatteryCodec.DecodeMode(raw));
    }

    [Fact]
    public void Mode_DecodeIgnoresTheFlagsSharingTheRegister()
    {
        // 0x1B: the Balanced field plus other bits the firmware owns (a touchpad
        // bit in the sibling chassis family) — masking is what makes the read stable.
        Assert.Equal(1, EcBatteryCodec.DecodeMode(0x1B));
    }

    // ---- threshold pair (derived view) ----------------------------------------

    [Theory]
    [InlineData(0, 100, 95)] // High Capacity: ceiling 100, restart 95
    [InlineData(1, 80, 75)]  // Balanced
    [InlineData(2, 60, 55)]  // Stationary: the floor ceiling, restart at the pair floor
    public void ThresholdsForMode_CeilingPlusHysteresis(int mode, int up, int down)
    {
        (int upPercent, int downPercent) = EcBatteryCodec.ThresholdsForMode(mode);

        Assert.Equal(up, upPercent);
        Assert.Equal(down, downPercent);

        // Whatever the mode, the derived pair satisfies the pair contract.
        EcBatteryCodec.ValidateThresholds(upPercent, downPercent);
    }

    [Theory]
    [InlineData(101, 95)] // ceiling above the table top
    [InlineData(59, 55)]  // ceiling below the table floor (Stationary is the minimum)
    [InlineData(80, 54)]  // restart below the pair floor
    [InlineData(80, 80)]  // plateau: the EC restarts charging below the ceiling
    [InlineData(80, 90)]  // inversion: restart above the ceiling
    public void ValidateThresholds_RejectsPairsTheEcCannotUse(int up, int down)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => EcBatteryCodec.ValidateThresholds(up, down));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(3)]
    public void ThresholdsForMode_RejectsAnUndefinedMode(int mode)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => EcBatteryCodec.ThresholdsForMode(mode));
    }

    // ---- gate status (0x0742!bit4) --------------------------------------------

    [Theory]
    [InlineData(0x10, true)]  // gate bit alone
    [InlineData(0x12, true)]  // gate bit beside other flags (the multiplexed byte)
    [InlineData(0x02, false)] // lab-box idle reading (probe 2026-10-30)
    [InlineData(0x20, false)] // a sibling flag set is NOT the gate
    [InlineData(0x00, false)]
    public void GateStatus_DecodesBit4Only(byte raw, bool expectedActive)
    {
        Assert.Equal(expectedActive, EcBatteryCodec.IsChargeGateActive(raw));
    }

    // ---- platform override (0x0770) -------------------------------------------

    [Theory]
    [InlineData(0xFF, false)] // the documented reset value (the lab box idles here)
    [InlineData(0x05, true)]  // the pin value the tools write
    [InlineData(0x04, true)]  // a legacy build artifact (open-revo's residual case)
    [InlineData(0x00, true)]  // anything not-reset is an override in effect
    public void PlatformOverride_AnythingButResetIsAnOverride(byte raw, bool expectedActive)
    {
        Assert.Equal(expectedActive, EcBatteryCodec.IsPlatformOverrideActive(raw));
    }
}
