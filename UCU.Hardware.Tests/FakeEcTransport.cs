using System.ComponentModel;
using UCU.Hardware.Ec;

namespace UCU.Hardware.Tests;

/// <summary>
/// Register-level fake for the EC transport: every write is recorded in order, reads
/// come from a presettable register map.
///
/// This is the seam the whole EC engine is tested through. The EC is a state machine
/// driven by write <em>order</em> (write the table, then set SPLIT, then toggle
/// ENABLE_CUSTOM) and by delays, so the tests assert the recorded byte sequence, not
/// just a final value: that is what makes a hardware change reviewable without a
/// laptop, and it is the harness the engine-service phase reuses to prove the RPC
/// layer does not alter what reaches the firmware.
/// </summary>
public sealed class FakeEcTransport : IEcTransport
{
    private readonly Dictionary<ushort, byte> _registers = new();
    private readonly List<(ushort Address, byte Value)> _writes = new();
    private int _writesRemainingToFail;

    public IReadOnlyList<(ushort Address, byte Value)> Writes => _writes;

    public bool IsOpen { get; private set; }
    public bool Disposed { get; private set; }
    public int OpenCount { get; private set; }
    public int ReconnectCount { get; private set; }

    public void Preset(ushort address, byte value) => _registers[address] = value;

    public void Preset(ushort startAddress, params byte[] values)
    {
        for (int i = 0; i < values.Length; i++)
            _registers[(ushort)(startAddress + i)] = values[i];
    }

    /// <summary>Makes the next <paramref name="count"/> writes throw, to exercise reconnect.</summary>
    public void FailNextWrites(int count) => _writesRemainingToFail = count;

    public void Open()
    {
        OpenCount++;
        IsOpen = true;
    }

    public void Reconnect()
    {
        ReconnectCount++;
        IsOpen = true;
    }

    public byte Read(ushort address) => _registers.TryGetValue(address, out byte value) ? value : (byte)0;

    public void Write(ushort address, byte value)
    {
        if (_writesRemainingToFail > 0)
        {
            _writesRemainingToFail--;
            throw new Win32Exception(5, $"simulated EC write failure at 0x{address:X3}");
        }

        _registers[address] = value;
        _writes.Add((address, value));
    }

    public void ClearLog() => _writes.Clear();

    public void Dispose()
    {
        Disposed = true;
        IsOpen = false;
    }

    /// <summary>All values written to one register, in order.</summary>
    public IReadOnlyList<byte> WritesAt(ushort address) =>
        _writes.Where(w => w.Address == address).Select(w => w.Value).ToArray();

    /// <summary>The last value written to one register, or null when it was never written.</summary>
    public byte? LastWriteAt(ushort address)
    {
        IReadOnlyList<byte> values = WritesAt(address);
        return values.Count == 0 ? null : values[values.Count - 1];
    }

    /// <summary>The first value written to one register; throws when it was never written.</summary>
    public byte FirstWriteAt(ushort address) => WritesAt(address)[0];

    /// <summary>The 16 bytes written across a table region, in address order (last write wins per byte).</summary>
    public byte[] TableAt(ushort baseAddress, int length = 16)
    {
        var table = new byte[length];
        foreach ((ushort addr, byte value) in _writes)
        {
            int offset = addr - baseAddress;
            if (offset >= 0 && offset < length)
                table[offset] = value;
        }

        return table;
    }

    /// <summary>Index of the first write to <paramref name="address"/>, or -1.</summary>
    public int FirstWriteIndex(ushort address)
    {
        for (int i = 0; i < _writes.Count; i++)
            if (_writes[i].Address == address)
                return i;

        return -1;
    }

    /// <summary>Index of the last write to <paramref name="address"/>, or -1.</summary>
    public int LastWriteIndex(ushort address)
    {
        for (int i = _writes.Count - 1; i >= 0; i--)
            if (_writes[i].Address == address)
                return i;

        return -1;
    }

    /// <summary>
    /// The write log for a set of registers, rendered "0xADDR=0xVALUE" and joined.
    /// Sequence assertions read as one string, so a reordering shows up as a diff.
    /// </summary>
    public string SequenceOf(params ushort[] addresses)
    {
        var wanted = new HashSet<ushort>(addresses);
        return string.Join(" ", _writes
            .Where(w => wanted.Contains(w.Address))
            .Select(w => $"0x{w.Address:X4}=0x{w.Value:X2}"));
    }
}
