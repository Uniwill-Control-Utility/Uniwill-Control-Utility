using UCU.Contracts;
using UCU.Hardware.Gpu;

namespace UCU.Engine
{
    /// <summary>
    /// The GPU tuning writes (V-F curve apply, DRS GPU preference). NVAPI refuses
    /// these to the asInvoker app's token (NVAPI_INVALID_USER_PRIVILEGE,
    /// 0xFFFFFF77), so the engine performs the interop as LocalSystem. A refusal
    /// is the answer, not a fault: the handler returns the result DTO with the
    /// raw status and the app logs it, which is what made the field failure
    /// visible in the first place.
    /// </summary>
    internal static class GpuVerbs
    {
        public static void Register(Action<string, VerbHandler> add, IGpuTuningService tuning)
        {
            add(EngineVerbs.Gpu.ApplyVfCurve, (p, ct) =>
            {
                VfCurveOffsetsDto dto = EngineSerializer.BindParams<VfCurveOffsetsDto>(p);
                return Task.FromResult<object?>(tuning.ApplyVfCurveOffsets(dto.OffsetsMhz));
            });

            add(EngineVerbs.Gpu.SetDrsPreference, (p, ct) =>
            {
                DrsPreferenceDto dto = EngineSerializer.BindParams<DrsPreferenceDto>(p);
                return Task.FromResult<object?>(tuning.SetDrsPreference(dto.Preference));
            });

            add(EngineVerbs.Gpu.SetClocks, (p, ct) =>
            {
                SetClocksDto dto = EngineSerializer.BindParams<SetClocksDto>(p);
                return Task.FromResult<object?>(tuning.SetClocks(dto.CoreOffsetMhz, dto.MemoryOffsetMhz));
            });
        }
    }
}
