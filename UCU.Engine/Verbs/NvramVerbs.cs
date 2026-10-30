using UCU.Contracts;
using UCU.Hardware.Firmware;

namespace UCU.Engine
{
    /// <summary>
    /// NVRAM rows: the UEFI firmware environment variables the app's pages used
    /// to read and write directly. SeSystemEnvironmentPrivilege cannot be
    /// enabled by an unelevated process, so on the field machines every one of
    /// those app-side accesses was silently dead — the CPU Advanced Performance
    /// Menu row's support probe found nothing, its write failed with access
    /// denied, and the GPU mux's firmware-visible mirror never persisted. No
    /// prompt ever appeared to explain: the reads just answered empty. The
    /// engine enables the privilege on its LocalSystem token, so the rows work
    /// as designed with no prompt anywhere.
    ///
    /// The raw get/set verbs carry the OEM variable's name and guid because
    /// the entry's offset layout is machine data the app owns (offset 98 is
    /// the mux mode, 110/111/51 the AP menu); the pair-write that must be
    /// atomic (the AP-menu unlock's two offsets) is the semantic verb, on the
    /// service, so no client can half-update the entry.
    /// </summary>
    internal static class NvramVerbs
    {
        public static void Register(Action<string, VerbHandler> add, IFwNvramService nvram, EcRawWriteGuard rawWriteGuard)
        {
            add(EngineVerbs.Device.GetNvramValue, (p, ct) =>
            {
                NvramValueDto dto = EngineSerializer.BindParams<NvramValueDto>(p);
                byte[]? data = nvram.Read(dto.Name, dto.Guid);

                return Task.FromResult<object?>(new NvramValueDto
                {
                    Name = dto.Name,
                    Guid = dto.Guid,
                    Data = data,
                });
            });

            add(EngineVerbs.Device.SetNvramValue, (p, ct) =>
            {
                NvramValueDto dto = EngineSerializer.BindParams<NvramValueDto>(p);

                // The pair name+guid is caller-chosen and the engine holds
                // SeSystemEnvironmentPrivilege, so the GUID is policy-gated: only
                // the OEM entry the rows own may be written (the AP menu lives in
                // it); writing a foreign firmware variable is how a board bricks
                // its boot, and no row of this app needs it.
                rawWriteGuard.GateNvramWrite(dto.Guid);

                return Task.FromResult<object?>(nvram.Write(dto.Name, dto.Guid, dto.Data ?? Array.Empty<byte>()));
            });

            add(EngineVerbs.Device.GetSupportsCpuApMenu, (p, ct) =>
                Task.FromResult<object?>(nvram.SupportsCpuApMenu()));

            add(EngineVerbs.Device.SetCpuApMenuUnlock, (p, ct) =>
            {
                BoolValueDto dto = EngineSerializer.BindParams<BoolValueDto>(p);
                return Task.FromResult<object?>(nvram.SetCpuApMenuUnlock(dto.Value));
            });
        }
    }
}
