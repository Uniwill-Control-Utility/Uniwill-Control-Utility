using System.Buffers.Binary;
using System.Runtime.InteropServices;
using UCU.Hardware.Gpu;

namespace UCU.Hardware.Tests;

/// <summary>
/// The NVAPI runtime rail currents, driven against a scripted function table. The layouts
/// (rail policy 0xA70 with entries at 0x3C + i*0x58, channels 0xCA8 with specs at
/// 0x28 + c*0x3C, control 0xA4C with the value at 0x20 + i*0x28 and the read echo at
/// 0x1C + i*0x28) and the selection rules are the measured reference's
/// (MechControl-RE nvidia_api.cpp Api::rails/control/rail/set_currents); a wrong offset
/// here writes another rail of the driver.
/// </summary>
public sealed class NvApiRailControlTests
{
    [Fact]
    public void Read_ReportsTheThreeRailsAndTheLivePolicyTheyAreBoundedBy()
    {
        var driver = new Driver();

        GpuRailLimits limits = new NvApiRailControl(driver.Table()).Read();

        Assert.True(limits.Succeeded);
        Assert.Equal(new GpuRailValue(210_000, 210_000, 294_000), limits.NvVdd);
        Assert.Equal(new GpuRailValue(60_000, 60_000, 84_000), limits.MsVdd);
        Assert.Equal(new GpuRailValue(11_053, 11_053, 14_026), limits.BoardInput);
    }

    [Fact]
    public void Read_BeforeTheRecordsWidenThePolicy_ReportsTheStockMaxima()
    {
        // The sliders before an unlock: the ROM records are what widen these maxima, so the
        // range the UI shows is 210/60/11.053 mA and the tier's currents do not fit it.
        var driver = new Driver { NvMax = 210_000, MsMax = 60_000, BoardMax = 11_053 };

        GpuRailLimits limits = new NvApiRailControl(driver.Table()).Read();

        Assert.Equal(210_000, limits.NvVdd.MaxMilliamps);
        Assert.Equal(60_000, limits.MsVdd.MaxMilliamps);
        Assert.Equal(11_053, limits.BoardInput.MaxMilliamps);
    }

    [Fact]
    public void Set_WritesEveryRequestedRail_WithOnlyThoseRailsSelected()
    {
        var driver = new Driver();

        GpuRailLimits applied = new NvApiRailControl(driver.Table())
            .Set(250_000, 70_000, 14_026);

        Assert.True(applied.Succeeded);
        Assert.Equal(1, driver.SetCalls);

        byte[] packet = driver.SetPacket!;
        Assert.Equal(0xA4C, packet.Length);
        Assert.Equal(0x10A4Cu, U32(packet, 0));

        // The mask selects exactly the three rails that were written, and each value sits
        // in its own rail's control field.
        uint expected = (1u << 0) | (1u << 1) | (1u << 2);
        Assert.Equal(expected, U32(packet, 4));
        Assert.Equal(250_000u, U32(packet, 0x20 + 0 * 0x28));
        Assert.Equal(70_000u, U32(packet, 0x20 + 1 * 0x28));
        Assert.Equal(14_026u, U32(packet, 0x20 + 2 * 0x28));

        Assert.Equal(250_000, applied.NvVdd.CurrentMilliamps);
        Assert.Equal(70_000, applied.MsVdd.CurrentMilliamps);
        Assert.Equal(14_026, applied.BoardInput.CurrentMilliamps);
    }

    [Fact]
    public void Set_WithOneValue_MasksAndTouchesOnlyThatRail()
    {
        var driver = new Driver();

        GpuRailLimits applied = new NvApiRailControl(driver.Table()).Set(250_000, null, null);

        Assert.Equal(1u << 0, U32(driver.SetPacket!, 4));
        Assert.Equal(250_000, applied.NvVdd.CurrentMilliamps);
        Assert.Equal(60_000, applied.MsVdd.CurrentMilliamps);
        Assert.Equal(11_053, applied.BoardInput.CurrentMilliamps);
    }

    [Fact]
    public void Set_OutsideTheLivePolicy_IsRefusedWithoutCallingTheDriver()
    {
        // The stock policy: the tier's current does not fit, and that is the refusal that
        // says the records did not take (both numbers, the rail by name).
        var driver = new Driver { NvMax = 210_000 };

        GpuRailLimits refused = new NvApiRailControl(driver.Table()).Set(250_000, null, null);

        Assert.False(refused.Succeeded);
        Assert.Equal("the NVVDD rail 250000 mA is outside the live rail policy 210000-210000 mA", refused.Reason);
        Assert.Equal(0, driver.SetCalls);
        Assert.Null(refused.NvVdd.CurrentMilliamps);
        Assert.Null(refused.NvVdd.MaxMilliamps);
    }

    [Fact]
    public void Set_WithoutTheBoardInputPolicy_IsRefusedByRail()
    {
        var driver = new Driver { BoardChannelUnit = 6 };

        GpuRailLimits refused = new NvApiRailControl(driver.Table()).Set(null, null, 14_026);

        Assert.False(refused.Succeeded);
        Assert.Equal("the board input rail policy is not available on this driver", refused.Reason);
        Assert.Equal(0, driver.SetCalls);
    }

    [Fact]
    public void Set_WhenTheDriverKeepsTheStockRail_IsRefusedWithTheRailAndBothNumbers()
    {
        var driver = new Driver { Accept = false };

        GpuRailLimits refused = new NvApiRailControl(driver.Table()).Set(250_000, null, null);

        Assert.False(refused.Succeeded);
        Assert.Equal("the NVVDD rail reads 210000 mA, not 250000 mA", refused.Reason);
        Assert.True(driver.SetCalls > 0);
    }

    [Fact]
    public void Set_WhenTheDriverMovesARailItWasNotGiven_IsRefused()
    {
        var driver = new Driver { Disturb = true };

        GpuRailLimits refused = new NvApiRailControl(driver.Table()).Set(250_000, null, null);

        Assert.False(refused.Succeeded);
        Assert.Equal("the unselected MSVDD rail moved from 60000 mA to 70000 mA", refused.Reason);
    }

    [Fact]
    public void Set_WhenTheDriverWritesPastTheBuffer_IsRefused()
    {
        // Every buffer is armed with 4 KiB canaries, like the F7 and power-request paths.
        var driver = new Driver { Overrun = true };

        GpuRailLimits refused = new NvApiRailControl(driver.Table()).Read();

        Assert.False(refused.Succeeded);
        Assert.Equal("the driver wrote past the buffer", refused.Reason);
    }

    [Fact]
    public void Selection_SkipsType13EntriesThatAreNotNvvdd()
    {
        // The RTX 40 driver reports NVVDD as policy type 13; the same type with another
        // channel id is not an MSVDD policy and must be skipped, not taken.
        var driver = new Driver();
        driver.Add(entry: 3, type: 13, channel: 3, channelUnit: 6, channelId: 16);

        GpuRailLimits limits = new NvApiRailControl(driver.Table()).Read();

        Assert.True(limits.Succeeded);
        Assert.Equal(60_000, limits.MsVdd.CurrentMilliamps);   // still entry 1 (type 19)
        Assert.Equal(84_000, limits.MsVdd.MaxMilliamps);
    }

    [Fact]
    public void Selection_RefusesASecondNvvddOrMsvddEntry()
    {
        var duplicateNv = new Driver();
        duplicateNv.Add(entry: 4, type: 19, channel: 4, channelUnit: 6, channelId: 1);
        Assert.Equal(
            "the rail policy table carries more than one NVVDD entry",
            new NvApiRailControl(duplicateNv.Table()).Read().Reason);

        var duplicateMs = new Driver();
        duplicateMs.Add(entry: 5, type: 19, channel: 5, channelUnit: 6, channelId: 16);
        Assert.Equal(
            "the rail policy table carries more than one MSVDD entry",
            new NvApiRailControl(duplicateMs.Table()).Read().Reason);

        var duplicateBoard = new Driver();
        duplicateBoard.Add(entry: 6, type: 4, channel: 6, channelUnit: 8, channelId: 7);
        Assert.Equal(
            "the rail policy table carries more than one board-input entry",
            new NvApiRailControl(duplicateBoard.Table()).Read().Reason);
    }

    [Fact]
    public void Selection_RefusesAMissingNvvdd()
    {
        // Channel id 2 is neither 1 (NVVDD) nor 16 (MSVDD): the entry is not a rail at all.
        var driver = new Driver { NvId = 2 };

        GpuRailLimits refused = new NvApiRailControl(driver.Table()).Read();

        Assert.False(refused.Succeeded);
        Assert.Equal("the rail policy table carries no NVVDD entry", refused.Reason);
    }

    [Fact]
    public void Selection_NeedsChannelUnit8AndARealChannelForTheBoardInput()
    {
        // The board input is optional: the measured rule (type 4, channel unit 8, channel id
        // not UINT32_MAX) is what tells it apart, and an entry that fails it is left out
        // rather than taken as the input.
        var wrongUnit = new Driver { BoardChannelUnit = 6 };
        Assert.Null(new NvApiRailControl(wrongUnit.Table()).Read().BoardInput.CurrentMilliamps);

        var noChannel = new Driver { BoardChannelId = uint.MaxValue };
        Assert.Null(new NvApiRailControl(noChannel.Table()).Read().BoardInput.CurrentMilliamps);

        // Both still read the rails they do have: the input is not required.
        GpuRailLimits limits = new NvApiRailControl(noChannel.Table()).Read();
        Assert.True(limits.Succeeded);
        Assert.Equal(210_000, limits.NvVdd.CurrentMilliamps);
    }

    [Fact]
    public void Configuration_NeedsTheMilliampUnitOnBothTables()
    {
        var wrongEntryUnit = new Driver { NvEntryUnit = 2 };
        Assert.Null(new NvApiRailControl(wrongEntryUnit.Table()).Read().NvVdd.CurrentMilliamps);

        var wrongChannelUnit = new Driver { NvChannelUnit = 8 };
        Assert.Equal(
            "the rail policy table carries no NVVDD entry",
            new NvApiRailControl(wrongChannelUnit.Table()).Read().Reason);
    }

    [Fact]
    public void Read_WithAMismatchedTypeOrAnImpossibleValue_IsRefused()
    {
        // The control packet's per-rail type must be the policy's own type: a mismatch is
        // another rail's field, so its value is not ours to trust.
        var wrongType = new Driver { EchoType = 19 };
        Assert.Equal(
            "the NVVDD control entry is not that rail's policy type",
            new NvApiRailControl(wrongType.Table()).Read().Reason);

        var outOfRange = new Driver { NvCurrent = 300_000 };
        Assert.Equal(
            "the NVVDD rail reads 300000 mA, outside its policy range 210000-294000 mA",
            new NvApiRailControl(outOfRange.Table()).Read().Reason);
    }

    [Fact]
    public void Table_Refusals_NameTheStep()
    {
        var wrongVersion = new Driver { PolicyEcho = 0x40A71 };
        Assert.Equal(
            "the rail policy table did not echo its version",
            new NvApiRailControl(wrongVersion.Table()).Read().Reason);

        var failed = new Driver { PolicyStatus = 3 };
        Assert.Equal(
            "the rail policy table read failed (status 3)",
            new NvApiRailControl(failed.Table()).Read().Reason);

        var noControl = new Driver().Without(NvApiRailLayout.ControlId);
        Assert.Equal(
            "NVAPI interface 0x8B3E7343 unavailable",
            new NvApiRailControl(noControl.Table()).Read().Reason);

        var noSet = new Driver().Without(NvApiRailLayout.SetCurrentsId);
        Assert.Equal(
            "NVAPI interface 0xAFFC2279 unavailable",
            new NvApiRailControl(noSet.Table()).Set(250_000, null, null).Reason);

        var noSingleGpu = new FakeTable { GpuFailure = "NVAPI enumerates 0 GPUs" };
        Assert.Equal(
            "NVAPI enumerates 0 GPUs",
            new NvApiRailControl(noSingleGpu).Read().Reason);
    }

    private static uint U32(byte[] data, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4));

    private sealed class FakeTable : INvApiPowerCalls
    {
        public Dictionary<uint, NvApiGpuCall> Calls { get; } = new();

        public string? GpuFailure { get; set; }

        public bool TryResolve(uint id, out NvApiGpuCall call)
        {
            if (Calls.TryGetValue(id, out NvApiGpuCall? found))
            {
                call = found;
                return true;
            }

            call = null!;
            return false;
        }

        public bool TryGetGpu(out IntPtr gpu, out string? reason)
        {
            gpu = new IntPtr(1);
            reason = GpuFailure;
            return GpuFailure == null;
        }
    }

    /// <summary>
    /// A scripted driver with the field machine's rail layout: NVVDD as policy type 13 on
    /// channel 0 (id 1, unit 6), MSVDD as type 19 on channel 1 (id 16, unit 6) and the
    /// board input as type 4 on channel 2 (unit 8). Every measured failure gets a knob.
    /// </summary>
    private sealed class Driver
    {
        private readonly HashSet<uint> _omitted = new();

        public uint NvType = 13;
        public uint NvId = 1;
        public uint NvEntryUnit = 1;
        public uint NvChannelUnit = 6;
        public uint NvCurrent = 210_000;
        public uint NvMax = 294_000;

        public uint MsMax = 84_000;
        public uint BoardMax = 14_026;
        public uint BoardChannelUnit = 8;
        public uint BoardChannelId = 0x1A2B_3C4D;

        public uint PolicyEcho = NvApiRailLayout.PolicyVersion;
        public int PolicyStatus;
        public uint? EchoType;
        public bool Accept = true;
        public bool Disturb;
        public bool Overrun;
        public byte[]? SetPacket;
        public int SetCalls;

        public Driver Without(uint id)
        {
            _omitted.Add(id);
            return this;
        }

        public void Add(int entry, uint type, uint channel, uint channelUnit = 6, uint channelId = 1) =>
            _entries.Add(new Entry(entry, type, channel, 1, channelUnit, channelId));

        private readonly List<Entry> _entries = new();

        private List<Entry> Entries
        {
            get
            {
                var entries = new List<Entry>
                {
                    new(0, NvType, 0, NvEntryUnit, NvChannelUnit, NvId),
                    new(1, 19, 1, 1, 6, 16),
                    new(2, 4, 2, 1, BoardChannelUnit, BoardChannelId),
                };
                entries.AddRange(_entries);
                return entries;
            }
        }

        public FakeTable Table()
        {
            var table = new FakeTable();
            table.Calls[NvApiRailLayout.PolicyTableId] = Policy;
            table.Calls[NvApiRailLayout.ChannelsId] = Channels;
            if (!_omitted.Contains(NvApiRailLayout.ControlId))
                table.Calls[NvApiRailLayout.ControlId] = Control;
            if (!_omitted.Contains(NvApiRailLayout.SetCurrentsId))
                table.Calls[NvApiRailLayout.SetCurrentsId] = Set;

            return table;
        }

        private int Policy(IntPtr gpu, IntPtr buffer)
        {
            if (PolicyStatus != 0)
                return PolicyStatus;

            Write(buffer, 0, PolicyEcho);

            uint mask = 0;
            foreach (Entry entry in Entries)
                mask |= 1u << entry.Index;

            Write(buffer, 4, mask);

            foreach (Entry entry in Entries)
            {
                int at = NvApiRailLayout.PolicyEntryBase + entry.Index * NvApiRailLayout.PolicyEntryStride;
                Write(buffer, at, entry.Type);
                Write(buffer, at + 4, entry.Channel);
                Write(buffer, at + 8, entry.EntryUnit);
                Write(buffer, at + 12, Min(entry));
                Write(buffer, at + 16, Min(entry));
                Write(buffer, at + 20, Max(entry));
            }

            return 0;
        }

        private int Channels(IntPtr gpu, IntPtr buffer)
        {
            Write(buffer, 0, NvApiRailLayout.ChannelsVersion);

            uint mask = 0;
            foreach (Entry entry in Entries)
                mask |= 1u << (int)entry.Channel;

            Write(buffer, NvApiRailLayout.ChannelsMaskOffset, mask);

            foreach (Entry entry in Entries)
            {
                int at = NvApiRailLayout.ChannelEntryBase + (int)entry.Channel * NvApiRailLayout.ChannelEntryStride;
                Write(buffer, at + 12, entry.ChannelUnit);
                Write(buffer, at + 16, entry.ChannelId);
            }

            return 0;
        }

        private int Control(IntPtr gpu, IntPtr buffer)
        {
            foreach (Entry entry in Entries)
            {
                int at = NvApiRailLayout.ReadValueBase + entry.Index * NvApiRailLayout.ReadValueStride;
                Write(buffer, at, EchoType ?? entry.Type);
                Write(buffer, at + 4, Current(entry.Index));
            }

            if (Overrun)
                Marshal.WriteByte(buffer, NvApiRailLayout.ControlBufferLength, 0x00);

            return 0;
        }

        private int Set(IntPtr gpu, IntPtr buffer)
        {
            SetCalls++;

            byte[] packet = new byte[NvApiRailLayout.ControlBufferLength];
            Marshal.Copy(buffer, packet, 0, packet.Length);
            SetPacket = packet;

            uint mask = U32(packet, 4);
            foreach (Entry entry in Entries)
            {
                if ((mask & (1u << entry.Index)) == 0)
                    continue;

                uint value = U32(packet, NvApiRailLayout.ControlValueBase + entry.Index * NvApiRailLayout.ControlValueStride);
                if (Accept)
                    _current[entry.Index] = value;
            }

            if (Disturb)
                _current[1] = 70_000;

            return 0;
        }

        private readonly Dictionary<int, uint> _current = new();

        private uint Current(int index) =>
            _current.TryGetValue(index, out uint value) ? value : index switch
            {
                0 => NvCurrent,
                1 => 60_000,
                _ => 11_053,
            };

        private static uint Min(Entry entry) => entry.Index == 0 ? 210_000u : entry.Index == 1 ? 60_000u : 11_053u;

        private uint Max(Entry entry) => entry.Index switch
        {
            0 => NvMax,
            1 => MsMax,
            _ => BoardMax,
        };

        private static void Write(IntPtr buffer, int offset, uint value) =>
            Marshal.WriteInt32(buffer, offset, unchecked((int)value));

        private readonly record struct Entry(
            int Index, uint Type, uint Channel, uint EntryUnit, uint ChannelUnit, uint ChannelId);
    }
}
