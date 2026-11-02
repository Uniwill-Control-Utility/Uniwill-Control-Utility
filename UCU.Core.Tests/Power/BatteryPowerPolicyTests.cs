using UCU.Core.Power;

namespace UCU.Core.Tests.Power;

/// <summary>
/// The battery envelope clamp. Measured 2026-11-02 with a CPU stress test and a
/// GPU benchmark together, both laptops on DC:
///   Intel Core Ultra 9 275HX laptop: CPU 30-40 W, GPU 35 W
///   AMD (XMG Neo 16)               : CPU 30-40 W, GPU 40 W
/// Asking above the ceiling is not just ignored - a 160 W CPU request made the
/// Intel part drop to 30 W - so the app clamps its request and says so.
/// </summary>
public sealed class BatteryPowerPolicyTests
{
    [Fact]
    public void OnAc_CpuRequestPassesThrough()
    {
        var verdict = BatteryPowerPolicy.ClampCpu(160, 200, onBattery: false, cpuCeiling: 40);

        Assert.False(verdict.Clamped);
        Assert.Equal(160, verdict.Pl1);
        Assert.Equal(200, verdict.Pl2);
    }

    [Fact]
    public void OnBattery_HighCpuRequestIsClampedToTheEnvelope()
    {
        var verdict = BatteryPowerPolicy.ClampCpu(160, 200, onBattery: true, cpuCeiling: 40);

        Assert.True(verdict.Clamped);
        Assert.Equal(40, verdict.Pl1);
        Assert.Equal(40, verdict.Pl2);
        Assert.Equal(160, verdict.RequestedPl1);
        Assert.Equal(200, verdict.RequestedPl2);
    }

    [Fact]
    public void OnBattery_CpuRequestBelowTheEnvelopeIsKept()
    {
        var verdict = BatteryPowerPolicy.ClampCpu(25, 30, onBattery: true, cpuCeiling: 40);

        Assert.False(verdict.Clamped);
        Assert.Equal(25, verdict.Pl1);
        Assert.Equal(30, verdict.Pl2);
    }

    [Fact]
    public void OnAc_GpuRequestPassesThrough()
    {
        var verdict = BatteryPowerPolicy.ClampGpu(115, 25, onBattery: false, gpuCeiling: 35);

        Assert.False(verdict.Clamped);
        Assert.Equal(115, verdict.TgpWatts);
        Assert.Equal(25, verdict.DbWatts);
    }

    [Fact]
    public void OnBattery_GpuIsClampedToTheCeilingAndDynamicBoostIsDropped()
    {
        var verdict = BatteryPowerPolicy.ClampGpu(115, 25, onBattery: true, gpuCeiling: 35);

        Assert.True(verdict.Clamped);
        Assert.Equal(35, verdict.TgpWatts);
        Assert.Equal(0, verdict.DbWatts);
        Assert.Equal(115, verdict.RequestedTgp);
        Assert.Equal(25, verdict.RequestedDb);
    }

    [Fact]
    public void OnBattery_GpuBelowTheCeilingKeepsItsValueButStillDropsDynamicBoost()
    {
        var verdict = BatteryPowerPolicy.ClampGpu(30, 10, onBattery: true, gpuCeiling: 40);

        Assert.True(verdict.Clamped);
        Assert.Equal(30, verdict.TgpWatts);
        Assert.Equal(0, verdict.DbWatts);
    }

    [Theory]
    [InlineData(true, 40, 35)]
    [InlineData(false, 40, 40)]
    public void EnvelopeFollowsTheCpuVendor(bool intelCpu, int expectedCpu, int expectedGpu)
    {
        BatteryPowerPolicy.Envelope envelope = BatteryPowerPolicy.EnvelopeFor(intelCpu);

        Assert.Equal(expectedCpu, envelope.CpuWatts);
        Assert.Equal(expectedGpu, envelope.GpuWatts);
    }

    [Fact]
    public void UnknownPlatformFallsBackToTheConservativeEnvelope()
    {
        BatteryPowerPolicy.Envelope envelope = BatteryPowerPolicy.EnvelopeFor(null);

        Assert.Equal(BatteryPowerPolicy.ConservativeEnvelope.CpuWatts, envelope.CpuWatts);
        Assert.Equal(BatteryPowerPolicy.ConservativeEnvelope.GpuWatts, envelope.GpuWatts);
        Assert.True(envelope.CpuWatts <= BatteryPowerPolicy.IntelEnvelope.CpuWatts);
        Assert.True(envelope.GpuWatts <= BatteryPowerPolicy.AmdEnvelope.GpuWatts);
    }

    [Fact]
    public void MissingCeilingFallsBackToTheConservativeOne()
    {
        var cpu = BatteryPowerPolicy.ClampCpu(120, 120, onBattery: true, cpuCeiling: 0);
        var gpu = BatteryPowerPolicy.ClampGpu(120, 20, onBattery: true, gpuCeiling: 0);

        Assert.Equal(BatteryPowerPolicy.ConservativeEnvelope.CpuWatts, cpu.Pl1);
        Assert.Equal(BatteryPowerPolicy.ConservativeEnvelope.GpuWatts, gpu.TgpWatts);
    }
}
