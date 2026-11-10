using UCU.Contracts;

namespace UCU.Contracts.Tests;

/// <summary>
/// The gpu.* wire shape: the V-F curve offsets and the DRS preference ride the
/// same source-generated serializer as every other verb, and the tuning result
/// (raw NVAPI status included) comes back as a typed result. Registering the DTO
/// here is what keeps the asInvoker app's privileged write path off reflection.
/// </summary>
public sealed class GpuTuningContractTests
{
    [Fact]
    public void ApplyVfCurveRoundTripsThroughTheSourceGeneratedSerializer()
    {
        EngineRequest request = new()
        {
            Id = "11",
            Verb = EngineVerbs.Gpu.ApplyVfCurve,
            Params = EngineSerializer.ToParams(new VfCurveOffsetsDto
            {
                OffsetsMhz = new Dictionary<int, int> { [72] = 150, [80] = -30 },
            }),
        };

        EngineRequest copy = EngineSerializer.Deserialize<EngineRequest>(EngineSerializer.SerializeToUtf8Bytes(request));
        VfCurveOffsetsDto parameters = EngineSerializer.BindParams<VfCurveOffsetsDto>(copy.Params);

        Assert.Equal(EngineVerbs.Gpu.ApplyVfCurve, copy.Verb);
        Assert.Equal(150, parameters.OffsetsMhz[72]);
        Assert.Equal(-30, parameters.OffsetsMhz[80]);
        Assert.Equal(2, parameters.OffsetsMhz.Count);

        // The result is a dispatcher result type, so it must survive the
        // object-typed mapping too.
        GpuTuningResultDto result = EngineSerializer.FromElement<GpuTuningResultDto>(
            EngineSerializer.ToParamsObject(new GpuTuningResultDto
            {
                Succeeded = false,
                Status = unchecked((int)0xFFFFFF77),
                AppliedPoints = 3,
                Detail = "NVAPI_INVALID_USER_PRIVILEGE",
            }));

        Assert.False(result.Succeeded);
        Assert.Equal(unchecked((int)0xFFFFFF77), result.Status);
        Assert.Equal(3, result.AppliedPoints);
    }

    [Fact]
    public void SetDrsPreferenceRoundTripsThroughTheSourceGeneratedSerializer()
    {
        EngineRequest request = new()
        {
            Id = "12",
            Verb = EngineVerbs.Gpu.SetDrsPreference,
            Params = EngineSerializer.ToParams(new DrsPreferenceDto { Preference = DrsGpuPreference.Discrete }),
        };

        EngineRequest copy = EngineSerializer.Deserialize<EngineRequest>(EngineSerializer.SerializeToUtf8Bytes(request));
        DrsPreferenceDto parameters = EngineSerializer.BindParams<DrsPreferenceDto>(copy.Params);

        Assert.Equal(EngineVerbs.Gpu.SetDrsPreference, copy.Verb);
        Assert.Equal(DrsGpuPreference.Discrete, parameters.Preference);

        // The enum's numeric value is the NVAPI GPU type, so the numbers are the contract.
        Assert.Equal(0, (int)DrsGpuPreference.Auto);
        Assert.Equal(1, (int)DrsGpuPreference.Integrated);
        Assert.Equal(2, (int)DrsGpuPreference.Discrete);
    }

    [Fact]
    public void SetClocksRoundTripsThroughTheSourceGeneratedSerializer()
    {
        EngineRequest request = new()
        {
            Id = "13",
            Verb = EngineVerbs.Gpu.SetClocks,
            Params = EngineSerializer.ToParams(new SetClocksDto { CoreOffsetMhz = 150, MemoryOffsetMhz = -300 }),
        };

        EngineRequest copy = EngineSerializer.Deserialize<EngineRequest>(EngineSerializer.SerializeToUtf8Bytes(request));
        SetClocksDto parameters = EngineSerializer.BindParams<SetClocksDto>(copy.Params);

        Assert.Equal(EngineVerbs.Gpu.SetClocks, copy.Verb);
        Assert.Equal(150, parameters.CoreOffsetMhz);
        Assert.Equal(-300, parameters.MemoryOffsetMhz);
    }
}
