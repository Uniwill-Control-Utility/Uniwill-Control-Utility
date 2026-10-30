using UCU.Hardware.Cpu.Intel;

namespace UCU.Engine.Tests;

/// <summary>
/// The scripted MSR register file the Intel verbs run against. The production
/// service writes real CPU power MSRs through PawnIO, which is exactly what
/// no test may touch. The fake mirrors the production surface's answer
/// shapes: writes record to the register file and answer true, a scripted
/// address answers false (the "write failed" the ported stack logs), and
/// reads answer the scripted value (empty for "read failed").
/// </summary>
public sealed class FakeIntelMsrService : IIntelMsrService
{
    public Dictionary<uint, ulong> Registers { get; } = new();

    /// <summary>Addresses whose write answers false (the ported stack logs the
    /// HRESULT; the verb carries the failure honestly).</summary>
    public HashSet<uint> FailingWrites { get; } = new();

    public int[] ClockRatios { get; set; } = new[] { 40, 40, 38, 38, 36, 36, 34, 34 };

    public bool GetClockRatiosFails { get; set; }

    public List<string> Calls { get; } = new();

    public bool SetTdp(int pl1, int pl2, CancellationToken cancellationToken = default)
    {
        Calls.Add($"tdp:{pl1}:{pl2}");
        if (FailingWrites.Contains(0x610))
            return false;

        // The ported stack's encoding, so the verb test can pin the pair arrives intact.
        uint high = 0x00438000u | ((uint)(pl2 * 8) & 0xFFFu);
        uint low = 0x00DD8000u | ((uint)((pl1 - 1) * 8) & 0xFFFu);
        Registers[0x610] = ((ulong)high << 32) | low;
        return true;
    }

    public bool SetPowerBalance(int value, int cpuOrGpu, CancellationToken cancellationToken = default)
    {
        Calls.Add($"balance:{value}:{cpuOrGpu}");

        if (value < 0 || value > 31)
            return true; // the ported stack's silent no-op

        uint address = cpuOrGpu == 0 ? 0x63Au : 0x642u;
        if (FailingWrites.Contains(address))
            return false;

        Registers[address] = unchecked((ulong)value);
        return true;
    }

    public bool SetVoltageOffset(int value, int plane, CancellationToken cancellationToken = default)
    {
        Calls.Add($"voltage:{value}:{plane}");

        if (plane is < 0 or > 3)
            return true; // the ported stack's silent no-op for an unknown plane

        if (FailingWrites.Contains(0x150))
            return false;

        ulong command = plane switch
        {
            0 => 0x80000011UL,
            1 => 0x80000111UL,
            2 => 0x80000211UL,
            _ => 0x80000411UL,
        };
        ulong data = unchecked((uint)((int)Math.Round(value * 1.024) << 21));
        Registers[0x150] = (command << 32) | data;
        return true;
    }

    public bool SetClockRatios(int[] clockRatios, CancellationToken cancellationToken = default)
    {
        Calls.Add($"ratios:{string.Join(',', clockRatios)}");

        if (FailingWrites.Contains(0x1AD))
            return false;

        ulong value = 0;
        foreach (int ratio in clockRatios.Take(8))
            value = (value << 8) | (byte)Math.Clamp(ratio, 0, 255);

        Registers[0x1AD] = value;
        return true;
    }

    public int[] GetClockRatios(CancellationToken cancellationToken = default)
    {
        Calls.Add("get:ratios");
        return GetClockRatiosFails ? Array.Empty<int>() : ClockRatios;
    }
}
