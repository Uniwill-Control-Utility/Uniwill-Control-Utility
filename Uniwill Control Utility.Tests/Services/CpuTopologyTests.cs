using Uniwill_Control_Utility.Services;

namespace Uniwill_Control_Utility.Tests.Services;

/// <summary>
/// The CCD/core layout the per-core Curve Optimizer groups its rows by. The cases are the
/// parts that matter: the field machine's 2x8 with a 3D V-Cache CCD, a 2x6, a single CCD,
/// and one CCD fused off in the BIOS (which must simply not appear).
/// </summary>
public sealed class CpuTopologyTests
{
    private const long Mb = 1024L * 1024L;

    /// <summary>Core masks for <paramref name="count"/> SMT cores: core i owns LP 2i, 2i+1.</summary>
    private static List<(ulong Mask, bool HyperThreaded)> SmtCores(int count, int firstCore = 0)
    {
        var cores = new List<(ulong, bool)>();
        for (int i = 0; i < count; i++)
            cores.Add((0b11UL << ((firstCore + i) * 2), true));
        return cores;
    }

    [Fact]
    public void TwoCcdsWithAnX3dDie_AreSplitOnTheL3Boundary()
    {
        var cores = SmtCores(8).Concat(SmtCores(8, firstCore: 8)).ToList();
        var l3 = new List<(ulong, long)> { (0x0000_FFFFUL, 96 * Mb), (0xFFFF_0000UL, 32 * Mb) };

        CpuTopology topology = CpuTopologyReader.Build(cores, l3);

        Assert.Equal(2, topology.Ccds.Count);
        Assert.Equal(16, topology.CoreCount);

        Assert.Equal(0, topology.Ccds[0].Index);
        Assert.True(topology.Ccds[0].IsX3D);
        Assert.Equal(8, topology.Ccds[0].Cores.Count);
        Assert.Equal(96 * Mb, topology.Ccds[0].L3Bytes);

        Assert.Equal(1, topology.Ccds[1].Index);
        Assert.False(topology.Ccds[1].IsX3D);
        Assert.All(topology.Ccds[1].Cores, core => Assert.Equal(1, core.CcdIndex));

        Assert.True(topology.HasX3D);
        Assert.Equal(new[] { 0, 1 }, topology.Ccds[0].Cores[0].LogicalProcessors);
        Assert.Equal(0, topology.Ccds[0].Cores[0].IndexInCcd);
        Assert.Equal(0, topology.Ccds[1].Cores[0].IndexInCcd);
        Assert.True(topology.Ccds[0].Cores[0].HyperThreaded);
    }

    [Fact]
    public void TwelveCoresAsTwoBySix_HaveNoX3d()
    {
        var cores = SmtCores(6).Concat(SmtCores(6, firstCore: 6)).ToList();
        var l3 = new List<(ulong, long)> { (0x0000_0FFFUL, 32 * Mb), (0x0FFF_F000UL, 32 * Mb) };

        CpuTopology topology = CpuTopologyReader.Build(cores, l3);

        Assert.Equal(2, topology.Ccds.Count);
        Assert.Equal(12, topology.CoreCount);
        Assert.False(topology.HasX3D);
        Assert.Equal(6, topology.Ccds[0].Cores.Count);
        Assert.Equal(6, topology.Ccds[1].Cores.Count);
    }

    [Fact]
    public void SingleCcd_IsOneGroup()
    {
        CpuTopology topology = CpuTopologyReader.Build(
            SmtCores(8),
            new List<(ulong, long)> { (0x0000_FFFFUL, 32 * Mb) });

        Assert.Single(topology.Ccds);
        Assert.Equal(8, topology.CoreCount);
        Assert.False(topology.HasX3D);
    }

    [Fact]
    public void CcdDisabledInTheBios_IsAbsentRatherThanEmpty()
    {
        // Only CCD0's cores and cache record exist - the shape a fused-off CCD1 produces.
        CpuTopology topology = CpuTopologyReader.Build(
            SmtCores(8),
            new List<(ulong, long)> { (0x0000_FFFFUL, 96 * Mb) });

        Assert.Single(topology.Ccds);
        Assert.True(topology.HasX3D);
        Assert.Equal(8, topology.CoreCount);
        Assert.All(topology.Cores, core => Assert.Equal(0, core.CcdIndex));
    }

    [Fact]
    public void NoCacheRecords_FallsBackToOneUnlabelledGroup()
    {
        CpuTopology topology = CpuTopologyReader.Build(SmtCores(4), Array.Empty<(ulong, long)>());

        Assert.Single(topology.Ccds);
        Assert.Equal(4, topology.CoreCount);
        Assert.False(topology.Ccds[0].IsX3D);
        Assert.Equal(0, topology.Ccds[0].L3Bytes);
    }

    [Fact]
    public void NoCores_IsEmpty()
    {
        CpuTopology topology = CpuTopologyReader.Build(
            Array.Empty<(ulong, bool)>(),
            new List<(ulong, long)> { (0xFFFFUL, 32 * Mb) });

        Assert.Empty(topology.Ccds);
        Assert.Equal(0, topology.CoreCount);
    }

    [Fact]
    public void CoreIndicesAreSequentialAcrossCcds()
    {
        var cores = SmtCores(8).Concat(SmtCores(8, firstCore: 8)).ToList();
        var l3 = new List<(ulong, long)> { (0x0000_FFFFUL, 96 * Mb), (0xFFFF_0000UL, 32 * Mb) };

        CpuTopology topology = CpuTopologyReader.Build(cores, l3);

        Assert.Equal(Enumerable.Range(0, 16), topology.Cores.Select(core => core.Index));
        Assert.Equal(Enumerable.Range(0, 8), topology.Ccds[0].Cores.Select(core => core.IndexInCcd));
        Assert.Equal(Enumerable.Range(0, 8), topology.Ccds[1].Cores.Select(core => core.IndexInCcd));
    }

    [Fact]
    public void Read_OnThisMachine_ReportsAUsableLayout()
    {
        // GetLogicalProcessorInformationEx is available on every supported Windows build,
        // so an empty layout here means the parse broke - not that the machine is odd.
        CpuTopology topology = CpuTopologyReader.Read();

        Assert.NotEmpty(topology.Ccds);
        Assert.True(topology.CoreCount > 0);
        Assert.Equal(topology.CoreCount, topology.Cores.Count());
        Assert.All(topology.Cores, core => Assert.NotEmpty(core.LogicalProcessors));
        Assert.Equal(Enumerable.Range(0, topology.Ccds.Count), topology.Ccds.Select(ccd => ccd.Index));
        Assert.All(topology.Cores, core => Assert.InRange(core.CcdIndex, 0, topology.Ccds.Count - 1));

        // Every logical processor the OS has must belong to exactly one core: the mask
        // arithmetic and the core count cannot disagree.
        int logical = topology.Cores.Sum(core => core.LogicalProcessors.Count);
        Assert.Equal(Environment.ProcessorCount, logical);
    }
}
