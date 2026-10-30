using UCU.Hardware.Firmware;

namespace UCU.Engine.Tests;

/// <summary>
/// The scripted firmware variable store the NVRAM verbs run against. The
/// production service writes real UEFI variables (a factory-data entry on the
/// firmware's SPI flash), which is exactly what no test may touch. The store
/// answers reads, records writes, and verifies the AP-menu pair-write the way
/// the production service does — by re-reading the offsets.
/// </summary>
public sealed class FakeFwNvramService : IFwNvramService
{
    private readonly Dictionary<string, byte[]> _variables = new(StringComparer.OrdinalIgnoreCase);

    public List<(string Name, int Bytes)> Writes { get; } = new();

    /// <summary>Seed the OemMagicVariable entry with the given offsets set,
    /// sized to cover the AP-menu offsets by default (120 bytes).</summary>
    public FakeFwNvramService WithOemMagic(byte supportByte = 1, byte apSwitch = 0, byte memOcSwitch = 0)
    {
        byte[] buffer = new byte[120];
        buffer[FwNvramService.OffsetCpuApMenuSupport] = supportByte;
        buffer[FwNvramService.OffsetCpuApMenuSwitch] = apSwitch;
        buffer[FwNvramService.OffsetMemOcSwitch] = memOcSwitch;
        _variables[FwNvramService.OemMagicVariable] = buffer;
        return this;
    }

    public byte[]? Read(string name, string guid) =>
        _variables.TryGetValue(name, out byte[]? data) ? (byte[])data.Clone() : null;

    public bool Write(string name, string guid, byte[] data)
    {
        Writes.Add((name, data.Length));
        _variables[name] = (byte[])data.Clone();
        return true;
    }

    public bool SupportsCpuApMenu()
    {
        byte[]? nvram = Read(FwNvramService.OemMagicVariable, FwNvramService.OemMagicVariableGuid);
        return nvram is { Length: > FwNvramService.OffsetCpuApMenuSupport }
            && nvram[FwNvramService.OffsetCpuApMenuSupport] != 0;
    }

    public bool SetCpuApMenuUnlock(bool enabled)
    {
        byte[]? nvram = Read(FwNvramService.OemMagicVariable, FwNvramService.OemMagicVariableGuid);
        if (nvram is null || nvram.Length <= FwNvramService.OffsetCpuApMenuSwitch)
            return false;

        byte[] modified = (byte[])nvram.Clone();
        modified[FwNvramService.OffsetCpuApMenuSwitch] = (byte)(enabled ? 1 : 0);
        modified[FwNvramService.OffsetMemOcSwitch] = (byte)(enabled ? 1 : 0);
        Write(FwNvramService.OemMagicVariable, FwNvramService.OemMagicVariableGuid, modified);

        byte[]? check = Read(FwNvramService.OemMagicVariable, FwNvramService.OemMagicVariableGuid);
        return check is { Length: > FwNvramService.OffsetCpuApMenuSwitch }
            && check[FwNvramService.OffsetCpuApMenuSwitch] == (byte)(enabled ? 1 : 0)
            && check[FwNvramService.OffsetMemOcSwitch] == (byte)(enabled ? 1 : 0);
    }
}
