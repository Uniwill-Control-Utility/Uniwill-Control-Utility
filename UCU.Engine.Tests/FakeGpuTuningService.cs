using UCU.Contracts;
using UCU.Hardware.Gpu;

namespace UCU.Engine.Tests;

/// <summary>
/// The scripted NVAPI the gpu.* verbs run against: it records what the verb asked
/// for and answers a scripted result, so the wire can be exercised without a
/// real clock/voltage write.
/// </summary>
public sealed class FakeGpuTuningService : IGpuTuningService
{
    public Dictionary<int, int>? LastOffsets { get; private set; }

    public DrsGpuPreference? LastPreference { get; private set; }

    public (int Core, int Memory)? LastClocks { get; private set; }

    /// <summary>The answer both writes report (the default is an accepted write).</summary>
    public GpuTuningResultDto Answer { get; set; } = new() { Succeeded = true, Status = 0, AppliedPoints = 0 };

    public GpuTuningResultDto ApplyVfCurveOffsets(IReadOnlyDictionary<int, int> offsetsMhz)
    {
        LastOffsets = new Dictionary<int, int>(offsetsMhz);
        return Answer with { AppliedPoints = offsetsMhz.Count };
    }

    public GpuTuningResultDto SetDrsPreference(DrsGpuPreference preference)
    {
        LastPreference = preference;
        return Answer;
    }

    public GpuTuningResultDto SetClocks(int coreOffsetMhz, int memoryOffsetMhz)
    {
        LastClocks = (coreOffsetMhz, memoryOffsetMhz);
        return Answer;
    }
}
