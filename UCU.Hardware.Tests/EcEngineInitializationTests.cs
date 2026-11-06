using UCU.Core.Ec;
using UCU.Hardware.Ec;

namespace UCU.Hardware.Tests;

/// <summary>
/// The EC activation handshake: the firmware ignores custom fan tables until this
/// exact sequence has been written, so the order is the behaviour under test.
/// </summary>
public sealed class EcEngineInitializationTests
{
    [Fact]
    public void InitializeEc_WritesTheOemActivationSequenceInOrder()
    {
        var transport = new FakeEcTransport();
        using var engine = new EcEngine(transport, EcPlatform.Amd);
        engine.Initialize();

        engine.InitializeEc();

        Assert.Equal(
            "0x0709=0x05 0x0708=0x01 0x0748=0x81 0x0726=0x80 0x0727=0x40 0x07C5=0xE0 0x07C7=0x0C 0x07C6=0x04 0x0706=0x41",
            transport.SequenceOf(0x0709, 0x0708, 0x0748, 0x0726, 0x0727, 0x07C5, 0x07C7, 0x07C6, 0x0706));
    }

    [Fact]
    public void InitializeEc_WritesTheOemDefaultFanTablesBeforeAcceptingCustomOnes()
    {
        var transport = new FakeEcTransport();
        using var engine = new EcEngine(transport, EcPlatform.Amd);
        engine.Initialize();

        engine.InitializeEc();

        // Captured from the MechControl Frida trace: these are the values the EC
        // expects to see during init, and they are the last-resort fan state.
        Assert.Equal(
            new byte[] { 48, 52, 56, 60, 64, 68, 72, 76, 80, 85, 255, 255, 255, 255, 255, 255 },
            transport.TableAt(EcEngine.REG_CPU_TEMP_END_TABLE));
        Assert.Equal(
            new byte[] { 0, 64, 70, 80, 100, 110, 120, 140, 160, 180, 180, 180, 180, 180, 180, 180 },
            transport.TableAt(EcEngine.REG_CPU_FAN_SPEED_TABLE));
        Assert.Equal(
            new byte[] { 46, 50, 54, 58, 62, 66, 70, 74, 78, 81, 255, 255, 255, 255, 255, 255 },
            transport.TableAt(EcEngine.REG_GPU_TEMP_END_TABLE));
    }

    [Fact]
    public void InitializeEc_KeepsTheFirmwareBitsItIsNotOwns()
    {
        var transport = new FakeEcTransport();
        transport.Preset(0x0726, 0x01);
        transport.Preset(0x0727, 0x02);
        transport.Preset(0x07C5, 0x01);
        using var engine = new EcEngine(transport, EcPlatform.Amd);
        engine.Initialize();

        engine.InitializeEc();

        Assert.Equal((byte)0x81, transport.LastWriteAt(0x0726)!.Value);
        Assert.Equal((byte)0x42, transport.LastWriteAt(0x0727)!.Value);
        Assert.Equal((byte)0xE1, transport.LastWriteAt(0x07C5)!.Value);
    }

    [Fact]
    public void InitializeEc_RunsOncePerEngineLifetime()
    {
        var transport = new FakeEcTransport();
        using var engine = new EcEngine(transport, EcPlatform.Amd);
        engine.Initialize();

        engine.InitializeEc();
        int writesAfterFirst = transport.Writes.Count;
        engine.InitializeEc();

        Assert.Equal(writesAfterFirst, transport.Writes.Count);
    }

    [Fact]
    public void Initialize_OpensWithoutWriting_AndReportsSuccess()
    {
        var transport = new FakeEcTransport();
        transport.Preset(EcEngine.REG_PROJECT_ID, 0x2A);
        using var engine = new EcEngine(transport, EcPlatform.Amd);

        Assert.True(engine.Initialize());

        Assert.Equal(1, transport.OpenCount);
        Assert.Empty(transport.Writes);
    }

    [Fact]
    public void Initialize_ReportsFailureWhenTheTransportCannotOpen()
    {
        var transport = new UnopenableEcTransport();
        using var engine = new EcEngine(transport, EcPlatform.Amd);

        Assert.False(engine.Initialize());
    }

    [Fact]
    public void Platform_IsWhatTheHostResolved()
    {
        using var engine = new EcEngine(new FakeEcTransport(), EcPlatform.Intel);

        // The platform selects the MUX encoding, so the engine has to report it for
        // callers (and the RPC contract) that persist a raw byte, e.g. the NVRAM mirror.
        Assert.Equal(EcPlatform.Intel, engine.Platform);
    }

    private sealed class UnopenableEcTransport : IEcTransport
    {
        public bool IsOpen => false;

        public void Open() => throw new System.ComponentModel.Win32Exception(2, "driver missing");

        public void Reconnect() => throw new System.ComponentModel.Win32Exception(2, "driver missing");

        public byte Read(ushort address) => throw new InvalidOperationException();

        public void Write(ushort address, byte value) => throw new InvalidOperationException();

        public void Dispose()
        {
        }
    }
}
