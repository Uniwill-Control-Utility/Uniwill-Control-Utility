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
    ///
    /// The power-ceiling pair rides the same domain for the same reason: the
    /// display-driver key is HKLM, and restarting the adapter is a LocalSystem
    /// act. Both answer the state DTO — a refusal (unpinned VBIOS, another
    /// tool's override) is data the user must see, never an error envelope.
    /// </summary>
    internal static class GpuVerbs
    {
        public static void Register(Action<string, VerbHandler> add, IGpuTuningService tuning, IGpuRomOverrideService romOverrides)
        {
            add(EngineVerbs.Gpu.ApplyVfCurve, (p, ct) =>
            {
                VfCurveOffsetsDto dto = EngineSerializer.BindParams<VfCurveOffsetsDto>(p);
                return Task.FromResult<object?>(tuning.ApplyVfCurveOffsets(dto.OffsetsMhz, dto.MemoryOffsetMhz));
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

            add(EngineVerbs.Gpu.GetRomOverrideState, (p, ct) =>
                Task.FromResult<object?>(romOverrides.GetState()));

            add(EngineVerbs.Gpu.SetRomOverride, (p, ct) =>
            {
                BoolValueDto dto = EngineSerializer.BindParams<BoolValueDto>(p);
                return Task.FromResult<object?>(romOverrides.SetEnabled(dto.Value));
            });

            add(EngineVerbs.Gpu.GetRailLimits, (p, ct) =>
                Task.FromResult<object?>(romOverrides.GetRailLimits()));

            add(EngineVerbs.Gpu.SetRailLimits, (p, ct) =>
            {
                GpuRailLimitsDto dto = EngineSerializer.BindParams<GpuRailLimitsDto>(p);
                return Task.FromResult<object?>(romOverrides.SetRailLimits(
                    dto.NvvddMilliamps, dto.MsvddMilliamps, dto.BoardInputMilliamps));
            });
        }
    }
}
