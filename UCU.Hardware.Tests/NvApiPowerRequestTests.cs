using System.Buffers.Binary;
using System.Runtime.InteropServices;
using UCU.Hardware.Gpu;

namespace UCU.Hardware.Tests;

/// <summary>
/// The NVAPI board-power request, driven against a scripted function table. The layouts
/// (policy table 0x728 bytes with entries at 0x28 + i*0x38, control packet 0x528 with the
/// value at 0x28 + i*0x28 and the method at 0x2c) and the guard bands are from the
/// measured reference (MechControl-RE nvidia_api.cpp); a wrong offset here writes another
/// power field of the driver.
/// </summary>
public sealed class NvApiPowerRequestTests
{
    [Fact]
    public void Request_SendsTheControlPacket_AndConfirmsTheReadBack()
    {
        var driver = new Driver();
        var request = new NvApiPowerRequest(driver.Table());

        GpuPowerRequestState state = request.Request(225_000);

        Assert.True(state.Succeeded);
        Assert.Equal(225_000, state.RequestedMilliwatts);
        Assert.Equal(5_000, state.MinMilliwatts);
        Assert.Equal(250_000, state.MaxMilliwatts);
        Assert.Equal(1, driver.SetCalls);

        // The exact packet the driver was handed.
        byte[] packet = driver.SetPacket!;
        Assert.Equal(0x528, packet.Length);
        Assert.Equal(0x10528u, U32(packet, 0));      // structure version
        Assert.Equal(1u, U32(packet, 4));            // selector: 1 << index
        Assert.Equal(0xF8u, U32(packet, 0x2C));      // method
        Assert.Equal(225_000u, U32(packet, 0x28));   // the requested milliwatts
    }

    [Fact]
    public void Read_ReportsNone_ForZeroAndUintMax()
    {
        var driver = new Driver { Requested = 0 };
        GpuPowerRequestState none = new NvApiPowerRequest(driver.Table()).Read();

        Assert.True(none.Succeeded);
        Assert.Null(none.RequestedMilliwatts);

        driver.Requested = uint.MaxValue;
        GpuPowerRequestState sentinel = new NvApiPowerRequest(driver.Table()).Read();

        Assert.True(sentinel.Succeeded);
        Assert.Null(sentinel.RequestedMilliwatts);

        driver.Requested = 190_000;
        Assert.Equal(190_000, new NvApiPowerRequest(driver.Table()).Read().RequestedMilliwatts);
    }

    [Fact]
    public void Request_OutsideThePolicyRange_IsRefusedWithoutCallingTheDriver()
    {
        var driver = new Driver();
        var request = new NvApiPowerRequest(driver.Table());

        GpuPowerRequestState tooMuch = request.Request(300_000);
        Assert.False(tooMuch.Succeeded);
        Assert.Equal("request outside the policy range 5000-250000 mW", tooMuch.Reason);
        Assert.Equal(0, driver.SetCalls);

        GpuPowerRequestState tooLittle = request.Request(1_000);
        Assert.Equal("request outside the policy range 5000-250000 mW", tooLittle.Reason);
        Assert.Equal(0, driver.SetCalls);
    }

    [Fact]
    public void Request_WithoutTheSetInterface_IsRefusedByName()
    {
        Driver driver = new Driver().Without(NvApiPowerLayout.LimitSetId);

        GpuPowerRequestState state = new NvApiPowerRequest(driver.Table()).Request(225_000);

        Assert.False(state.Succeeded);
        Assert.Equal("NVAPI interface 0x17695269 unavailable", state.Reason);
    }

    [Fact]
    public void Request_WhenTheDriverKeepsAnotherValue_IsRefusedWithBothNumbers()
    {
        // The field failure: the call succeeds, the request does not take.
        var driver = new Driver { Accept = false, Requested = 150_000 };

        GpuPowerRequestState state = new NvApiPowerRequest(driver.Table()).Request(225_000);

        Assert.False(state.Succeeded);
        Assert.Equal("the driver did not accept the request: it reports 150000 mW, not 225000 mW", state.Reason);
        Assert.Null(state.RequestedMilliwatts);
        Assert.True(driver.SetCalls > 0);
    }

    [Fact]
    public void Request_WhenTheDriverReportsNoValueAtAll_SaysSo()
    {
        var driver = new Driver { Accept = false, Requested = 0 };

        GpuPowerRequestState state = new NvApiPowerRequest(driver.Table()).Request(225_000);

        Assert.Equal("the driver did not accept the request: it reports no request, not 225000 mW", state.Reason);
    }

    [Fact]
    public void Request_WhenNoSingleGpuCanBeEnumerated_IsRefused()
    {
        var table = new FakeTable { GpuFailure = "NVAPI enumerates 0 GPUs; the board-power request needs exactly one" };

        GpuPowerRequestState state = new NvApiPowerRequest(table).Request(225_000);

        Assert.False(state.Succeeded);
        Assert.Contains("exactly one", state.Reason!, StringComparison.Ordinal);
    }

    [Fact]
    public void Policy_AmbiguousOrMissingEntries_AreRefused()
    {
        var ambiguous = new Driver { Entry1Type = 0 };
        Assert.Equal(
            "the board-power policy table carries more than one board-power entry",
            new NvApiPowerRequest(ambiguous.Table()).Request(225_000).Reason);

        var missing = new Driver { NoBoardEntry = true };
        Assert.Equal(
            "the board-power policy table carries no board-power entry",
            new NvApiPowerRequest(missing.Table()).Request(225_000).Reason);

        var wrongVersion = new Driver { PolicyEcho = 0x10729 };
        Assert.Equal(
            "the board-power policy table did not echo its version",
            new NvApiPowerRequest(wrongVersion.Table()).Request(225_000).Reason);

        var failed = new Driver { PolicyStatus = 3 };
        Assert.Equal(
            "the board-power policy table read failed (status 3)",
            new NvApiPowerRequest(failed.Table()).Request(225_000).Reason);
    }

    [Fact]
    public void Policy_IgnoresEntriesThatAreNotMilliwattBoardPower()
    {
        // A second entry of another type, or one in another unit, is not the policy we
        // want, so it does not make the table ambiguous.
        var otherType = new Driver { Entry1Type = 2 };
        Assert.Equal(225_000, new NvApiPowerRequest(otherType.Table()).Request(225_000).RequestedMilliwatts);

        var otherUnit = new Driver { Entry1Type = 0, Entry1Unit = 1 };
        Assert.Equal(225_000, new NvApiPowerRequest(otherUnit.Table()).Request(225_000).RequestedMilliwatts);
    }

    [Fact]
    public void Read_WithAMismatchedEcho_IsRefused()
    {
        var driver = new Driver { EchoMethod = false };

        GpuPowerRequestState state = new NvApiPowerRequest(driver.Table()).Read();

        Assert.False(state.Succeeded);
        Assert.Equal("the power request read did not echo its method", state.Reason);
    }

    [Fact]
    public void Read_WhenTheDriverWritesPastTheBuffer_IsRefused()
    {
        // The reference arms every buffer with 4 KiB canaries; this is what catches a
        // driver writing past it instead of corrupting our heap silently.
        var driver = new Driver { Overrun = true };

        GpuPowerRequestState state = new NvApiPowerRequest(driver.Table()).Read();

        Assert.False(state.Succeeded);
        Assert.Equal("the driver wrote past the buffer", state.Reason);
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
    /// A scripted driver: the policy table, the read echo and the set call, with the
    /// knobs the measured failures need (no echo, an overrun, a value that does not take).
    /// </summary>
    private sealed class Driver
    {
        private readonly HashSet<uint> _omitted = new();

        public uint Requested;
        public uint Min = 5_000;
        public uint Max = 250_000;
        public uint PolicyEcho = NvApiPowerLayout.PolicyVersion;
        public int PolicyStatus;
        public bool NoBoardEntry;
        public int Entry1Type = 2;
        public int Entry1Unit;
        public bool EchoMethod = true;
        public bool Overrun;
        public bool Accept = true;
        public byte[]? SetPacket;
        public int SetCalls;

        public Driver Without(uint id)
        {
            _omitted.Add(id);
            return this;
        }

        public FakeTable Table()
        {
            var table = new FakeTable();
            table.Calls[NvApiPowerLayout.PolicyTableId] = Policy;
            table.Calls[NvApiPowerLayout.LimitReadId] = Read;
            if (!_omitted.Contains(NvApiPowerLayout.LimitSetId))
                table.Calls[NvApiPowerLayout.LimitSetId] = Set;

            return table;
        }

        private int Policy(IntPtr gpu, IntPtr buffer)
        {
            if (PolicyStatus != 0)
                return PolicyStatus;

            Write(buffer, 0, PolicyEcho);
            Write(buffer, 4, NoBoardEntry ? 0u : 3u);   // entries 0 and 1 present

            // Entry 0: the board-power policy in milliwatts.
            Write(buffer, 0x28, 0u);
            Write(buffer, 0x2C, 0u);
            Write(buffer, 0x30, Min);
            Write(buffer, 0x34, 95_000);
            Write(buffer, 0x38, Max);

            // Entry 1: another policy unless a test makes it a second board-power entry.
            Write(buffer, 0x60, (uint)Entry1Type);
            Write(buffer, 0x64, (uint)Entry1Unit);
            Write(buffer, 0x68, 1_000u);
            Write(buffer, 0x6C, 2_000u);
            Write(buffer, 0x70, 3_000u);
            return 0;
        }

        private int Read(IntPtr gpu, IntPtr buffer)
        {
            // The request packet carries 0xF8 already: a mismatch has to be written over it.
            Write(buffer, 0x2C, EchoMethod ? NvApiPowerLayout.ControlMethod : 0xF7);

            Write(buffer, 0x28, Requested);

            if (Overrun)
                Marshal.WriteByte(buffer, NvApiPowerLayout.ControlBufferLength, 0x00);

            return 0;
        }

        private int Set(IntPtr gpu, IntPtr buffer)
        {
            SetCalls++;

            byte[] packet = new byte[NvApiPowerLayout.ControlBufferLength];
            Marshal.Copy(buffer, packet, 0, packet.Length);
            SetPacket = packet;

            uint value = (uint)Marshal.ReadInt32(buffer, 0x28);
            if (Accept)
                Requested = value;

            return 0;
        }

        private static void Write(IntPtr buffer, int offset, uint value) =>
            Marshal.WriteInt32(buffer, offset, unchecked((int)value));
    }
}
