using Uniwill_Control_Utility.Models;
using Uniwill_Control_Utility.Services;

namespace Uniwill_Control_Utility.Tests.Services;

/// <summary>
/// The per-core Curve Optimizer model: legacy migration, the -50..0 range, the per-core map
/// fallback, and the SMU commands each mode emits for a given topology.
/// </summary>
public sealed class CurveOptimizerModelTests
{
    private const long Mb = 1024L * 1024L;

    private static CpuTopology TwoByEight() =>
        CpuTopologyReader.Build(
            Enumerable.Range(0, 16).Select(i => (0b11UL << (i * 2), true)),
            new List<(ulong, long)> { (0x0000_FFFFUL, 96 * Mb), (0xFFFF_0000UL, 32 * Mb) });

    private static CpuTopology OneByEight() =>
        CpuTopologyReader.Build(
            Enumerable.Range(0, 8).Select(i => (0b11UL << (i * 2), true)),
            new List<(ulong, long)> { (0x0000_FFFFUL, 96 * Mb) });

    [Fact]
    public void LegacyUnifiedPreset_MigratesToTheUnifiedMode()
    {
        var preset = new CustomPreset { CpuCoUnified = true, CpuCoMode = CpuCoMode.PerCcd };

        preset.Normalize();

        Assert.Equal(CpuCoMode.Unified, preset.CpuCoMode);
    }

    [Fact]
    public void LegacyPerCcdPreset_StaysPerCcd()
    {
        var preset = new CustomPreset { CpuCoUnified = false, CpuCoCcd0 = -15, CpuCoCcd1 = -25 };

        preset.Normalize();

        Assert.Equal(CpuCoMode.PerCcd, preset.CpuCoMode);
        Assert.Equal(-15, preset.CpuCoCcd0);
        Assert.Equal(-25, preset.CpuCoCcd1);
    }

    [Theory]
    [InlineData(10, 0)]
    [InlineData(0, 0)]
    [InlineData(-1, -1)]
    [InlineData(-50, -50)]
    [InlineData(-80, -50)]
    public void OffsetsClampToTheMinusFiftyToZeroRange(int input, int expected)
    {
        Assert.Equal(expected, CustomPreset.ClampCo(input));
        Assert.Equal(expected, CustomPreset.ClampCo((double)input));
    }

    [Fact]
    public void Normalize_ClampsEveryOffsetIncludingThePerCoreArray()
    {
        var preset = new CustomPreset
        {
            CpuCoUnifiedValue = -80,
            CpuCoCcd0 = 5,
            CpuCoCcd1 = -70,
            CpuCoMode = CpuCoMode.PerCore,
            CpuCoPerCore = new[] { -90, 3, -20 }
        };

        preset.Normalize();

        Assert.Equal(-50, preset.CpuCoUnifiedValue);
        Assert.Equal(0, preset.CpuCoCcd0);
        Assert.Equal(-50, preset.CpuCoCcd1);
        Assert.Equal(new[] { -50, 0, -20 }, preset.CpuCoPerCore);
    }

    [Fact]
    public void PerCoreMap_FallsBackToTheCcdValueWhenTheArrayIsEmpty()
    {
        var preset = new CustomPreset { CpuCoMode = CpuCoMode.PerCore, CpuCoCcd0 = -20, CpuCoCcd1 = -10 };

        int[] map = preset.BuildPerCoreMap(TwoByEight());

        Assert.Equal(16, map.Length);
        Assert.All(Enumerable.Range(0, 8), i => Assert.Equal(-20, map[i]));
        Assert.All(Enumerable.Range(8, 8), i => Assert.Equal(-10, map[i]));
    }

    [Fact]
    public void PerCoreMap_PrefersStoredValuesOverTheCcdFallback()
    {
        var preset = new CustomPreset
        {
            CpuCoMode = CpuCoMode.PerCore,
            CpuCoCcd0 = -20,
            CpuCoCcd1 = -10,
            CpuCoPerCore = new[] { -5, -6 }
        };

        int[] map = preset.BuildPerCoreMap(TwoByEight());

        Assert.Equal(-5, map[0]);
        Assert.Equal(-6, map[1]);
        Assert.Equal(-20, map[7]);   // fell back to CCD0
        Assert.Equal(-10, map[15]);  // fell back to CCD1
    }

    [Fact]
    public void PerCcd_EmitsOneBatchTokenForEveryExistingCore_NotAFixedSixteen()
    {
        // A part with CCD1 disabled: eight cores, so eight values in one batch - the old loop
        // sent 16 separate commands and addressed four cores that do not exist.
        var parts = new List<string>();
        var preset = new CustomPreset { CpuCoMode = CpuCoMode.PerCcd, CpuCoCcd0 = -15, CpuCoCcd1 = -25 };

        PresetProfileApplier.AppendCoCommands(parts, preset, OneByEight());

        string batch = Assert.Single(parts);
        Assert.StartsWith("--set-coper-batch=", batch);

        string[] values = batch["--set-coper-batch=".Length..].Split(',');
        Assert.Equal(8, values.Length);
        // CCD0 offset -15 on core 0: 0x100000 - 15, printed as decimal.
        Assert.Equal("1048561", values[0]);
    }

    [Fact]
    public void PerCore_EmitsOneBatchToken_WithPerCoreValues()
    {
        var parts = new List<string>();
        var preset = new CustomPreset
        {
            CpuCoMode = CpuCoMode.PerCore,
            CpuCoCcd0 = -30,
            CpuCoCcd1 = -30,
            CpuCoPerCore = new[] { -5, -6, -7, -8 }
        };

        PresetProfileApplier.AppendCoCommands(parts, preset, TwoByEight());

        string batch = Assert.Single(parts);
        string[] values = batch["--set-coper-batch=".Length..].Split(',');

        Assert.Equal(16, values.Length);
        Assert.Equal("1048571", values[0]);   // core 0, -5
        // Core 8 of CCD1 at -30 (the fallback): prefix 0x100 << 20 = 268435456, plus
        // 0x100000 - 30.
        Assert.Equal("269484002", values[8]);
    }

    [Fact]
    public void ASingleCore_StillUsesThePlainCommand()
    {
        var parts = new List<string>();
        var preset = new CustomPreset { CpuCoMode = CpuCoMode.PerCcd, CpuCoCcd0 = -20 };
        CpuTopology oneCore = CpuTopologyReader.Build(
            new List<(ulong, bool)> { (0b11UL, true) },
            new List<(ulong, long)> { (0b11UL, 96 * Mb) });

        PresetProfileApplier.AppendCoCommands(parts, preset, oneCore);

        Assert.Equal("--set-coper=1048556", Assert.Single(parts));
    }

    [Theory]
    [InlineData("1048571,1048570", 2)]
    [InlineData("1048571", 1)]
    [InlineData("1048571,,1048570", 2)]
    [InlineData("1048571,garbage,1048570", 2)]
    [InlineData("", 0)]
    public void CoperBatchPayload_ParsesTheValuesAndDropsJunk(string payload, int expected)
    {
        Assert.Equal(expected, Scripts.RyzenAdj_To_UCU.ParseCoperBatch(payload).Length);
    }

    [Fact]
    public void Unified_EmitsOnePackageWideCommand()
    {
        var parts = new List<string>();
        var preset = new CustomPreset { CpuCoMode = CpuCoMode.Unified, CpuCoUnifiedValue = -20 };

        PresetProfileApplier.AppendCoCommands(parts, preset, TwoByEight());

        Assert.Single(parts);
        Assert.Equal("--set-coall=16777196", parts[0]);
    }
}
