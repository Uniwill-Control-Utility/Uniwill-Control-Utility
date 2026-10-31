using UCU.Core.Supervision;

namespace UCU.Core.Tests.Supervision;

/// <summary>
/// The power-tuning guardian's drift decision (U-107): compare the applied
/// contract against the EC's answer and decide which halves to re-apply.
/// The expected GPU bytes mirror the engine's activation sequence (enable
/// GENERAL|CTGP, plus DB when db &gt; 0; the pool byte 255 only on the
/// DB-enabled path; the custom-profile bit arms with the activation), so the
/// encodings are pinned at both ends: what ActivateGpuPowerControl writes is
/// exactly what this policy expects to find. A null truth half is "no
/// answer", never drift, and a contract half that was never applied (stock
/// presets clear it) is never healed.
/// </summary>
public sealed class PowerContractPolicyTests
{
    private const int Ctgp = 70;   // targetWatts - defaultWatts, mid-range
    private const int Db = 25;

    private static PowerContract GpuContract(int ctgp = Ctgp, int db = Db) =>
        new() { HasGpu = true, GpuCtgpOffset = ctgp, GpuDbOffset = db };

    private static PowerContract CpuContract(int pl1 = 75, int pl2 = 85) =>
        new() { HasCpu = true, Pl1Watts = pl1, Pl2Watts = pl2 };

    /// <summary>The bytes the engine leaves behind for this contract — the
    /// clean truth, built from the policy's own expected rules so a rule
    /// change moves truth and expectation together and only the drift
    /// scenarios below pin drift explicitly.</summary>
    private static GpuPowerTruth GpuTruthFor(int ctgp = Ctgp, int db = Db, bool customActive = true) =>
        new(PowerContractPolicy.ExpectedGpuEnable(db), (byte)ctgp,
            PowerContractPolicy.ExpectedGpuTpp(db), (byte)db, customActive);

    [Fact]
    public void CleanGpuContract_MatchingTruth_HealsNothing()
    {
        PowerHealPlan plan = PowerContractPolicy.ComputeHealPlan(GpuContract(), GpuTruthFor(), null);

        Assert.True(plan.IsClean);
        Assert.Null(plan.GpuReason);
    }

    [Fact]
    public void DbDisabledContract_ExpectsNoDbBitAndClosedPool()
    {
        // The db-disabled activation: enable without the DB bit (the NVIDIA
        // driver must see no boost to chase) and pool 0.
        Assert.Equal(0x05, PowerContractPolicy.ExpectedGpuEnable(0));
        Assert.Equal(0x00, PowerContractPolicy.ExpectedGpuTpp(0));
        Assert.Equal(0x07, PowerContractPolicy.ExpectedGpuEnable(1));
        Assert.Equal(0xFF, PowerContractPolicy.ExpectedGpuTpp(1));
    }

    [Theory]
    [InlineData(0x05, Ctgp, 0xFF, Db, true, "enable")]   // DB bit cleared (third party wrote enable without DB)
    [InlineData(0x07, Ctgp - 1, 0xFF, Db, true, "cTGP")] // cTGP rewritten (Afterburner reset)
    [InlineData(0x07, Ctgp, 0x00, Db, true, "pool")]     // pool closed
    [InlineData(0x07, Ctgp, 0xFF, 0, true, "DB")]        // DB zeroed
    [InlineData(0x07, Ctgp, 0xFF, Db, false, "custom-profile")] // OEM CC cleared 0x0727 bit6
    public void GpuDrift_AnyFieldMoved_HealsGpuAndNamesTheEvidence(
        byte enable, byte ctgp, byte tpp, byte db, bool customActive, string expectedReasonFragment)
    {
        var truth = new GpuPowerTruth(enable, ctgp, tpp, db, customActive);

        PowerHealPlan plan = PowerContractPolicy.ComputeHealPlan(GpuContract(), truth, null);

        Assert.True(plan.HealGpu);
        Assert.NotNull(plan.GpuReason);
        Assert.Contains(expectedReasonFragment, plan.GpuReason, StringComparison.OrdinalIgnoreCase);
        // The heal carries the contract's exact offsets, not the drifted truth.
        Assert.Equal(Ctgp, plan.GpuCtgpOffset);
        Assert.Equal(Db, plan.GpuDbOffset);
    }

    [Fact]
    public void CpuDrift_EitherLimitMoved_HealsCpuWithEvidence()
    {
        PowerHealPlan plan = PowerContractPolicy.ComputeHealPlan(
            CpuContract(75, 85), null, new CpuPowerTruth(45, 85));

        Assert.True(plan.HealCpu);
        Assert.Contains("PL1 45W (want 75W)", plan.CpuReason, StringComparison.Ordinal);
        Assert.Equal(75, plan.Pl1Watts);
        Assert.Equal(85, plan.Pl2Watts);
    }

    [Fact]
    public void CpuMatchingTruth_HealsNothing()
    {
        PowerHealPlan plan = PowerContractPolicy.ComputeHealPlan(
            CpuContract(75, 85), null, new CpuPowerTruth(75, 85));

        Assert.True(plan.IsClean);
    }

    [Fact]
    public void NullTruth_IsNoAnswer_NotDrift()
    {
        // A cycle where the EC half cannot be read never heals toward bytes
        // on the word of missing data.
        PowerHealPlan plan = PowerContractPolicy.ComputeHealPlan(
            GpuContract() with { HasCpu = true, Pl1Watts = 75, Pl2Watts = 85 },
            gpu: null,
            cpu: null);

        Assert.True(plan.IsClean);
    }

    [Fact]
    public void EmptyContract_HealsNothing_HasNothingToRestore()
    {
        // Stock presets clear the contract: the firmware's factory table
        // rules, and healing custom bytes over it would restore what the
        // preset deliberately yielded.
        var truth = new GpuPowerTruth(0x00, 0, 0, 0, false); // OEM defaults

        PowerHealPlan plan = PowerContractPolicy.ComputeHealPlan(PowerContract.Empty, truth, new CpuPowerTruth(0, 0));

        Assert.True(plan.IsClean);
    }

    [Fact]
    public void BothHalvesDrift_OnePlanCarriesBoth()
    {
        PowerContract both = new()
        {
            HasGpu = true, GpuCtgpOffset = Ctgp, GpuDbOffset = 0,
            HasCpu = true, Pl1Watts = 45, Pl2Watts = 45,
        };

        var gpu = new GpuPowerTruth(0x00, 0x37, 0x00, 0x00, false); // enable cleared, ctgp at the 55-deactivate value
        var cpu = new CpuPowerTruth(75, 85);                        // restored toward BIOS-side values

        PowerHealPlan plan = PowerContractPolicy.ComputeHealPlan(both, gpu, cpu);

        Assert.True(plan.HealGpu);
        Assert.True(plan.HealCpu);
        Assert.Equal(Ctgp, plan.GpuCtgpOffset);
        Assert.Equal(0, plan.GpuDbOffset);
        Assert.Equal(45, plan.Pl1Watts);
        Assert.NotNull(plan.GpuReason);
        Assert.NotNull(plan.CpuReason);
    }

    [Fact]
    public void GpuReason_ListsEveryMovedField()
    {
        // "Heal GPU" from an all-zeros truth must log ALL the evidence, not
        // the first mismatch: the field's question is what the tuner did.
        var allZeros = new GpuPowerTruth(0x00, 0x00, 0x00, 0x00, false);

        PowerHealPlan plan = PowerContractPolicy.ComputeHealPlan(GpuContract(), allZeros, null);

        Assert.True(plan.HealGpu);
        Assert.Contains("custom-profile", plan.GpuReason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("enable", plan.GpuReason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("cTGP", plan.GpuReason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("pool", plan.GpuReason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("DB", plan.GpuReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PolicyExpectedBytes_MirrorTheEngineActivation_EncodingParity()
    {
        // EcEngine.ActivateGpuPowerControl writes enable = GENERAL|CTGP
        // (0x05), plus DB (0x02) when db > 0, and pool 255 only on the
        // DB-enabled path. If the engine's encoding moves, this test moves —
        // the policy's expectations are the same rules, stated in Core.
        Assert.Equal(0x01 | 0x04, PowerContractPolicy.ExpectedGpuEnable(0));
        Assert.Equal(0x01 | 0x04 | 0x02, PowerContractPolicy.ExpectedGpuEnable(25));
        Assert.Equal((byte)0, PowerContractPolicy.ExpectedGpuTpp(0));
        Assert.Equal((byte)255, PowerContractPolicy.ExpectedGpuTpp(25));
    }
}
