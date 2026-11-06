using UCU.Core.Ec;
using UCU.Hardware.Ec;

namespace UCU.Hardware.Tests;

/// <summary>
/// The engine's contract with its transport: reconnect-and-retry after a failed
/// transfer (the driver handle goes stale when the ACPI device is re-enumerated, e.g.
/// on resume from sleep), and dispose the transport with the engine.
/// </summary>
public sealed class EcEngineTransportTests
{
    [Fact]
    public void FailedWriteReconnectsAndRetriesTheSameWrite()
    {
        var transport = new FakeEcTransport();
        using var engine = new EcEngine(transport, EcPlatform.Amd);
        engine.Initialize();
        transport.FailNextWrites(1);

        engine.InitializeEc();

        Assert.Equal(1, transport.ReconnectCount);

        // The failed write is not recorded; the retry is, so the sequence is complete.
        Assert.StartsWith("0x0709=0x05 0x0708=0x01", transport.SequenceOf(0x0709, 0x0708, 0x0748));
    }

    [Fact]
    public void FailedReadReconnectsAndRetries()
    {
        var transport = new FailFirstReadTransport();
        using var engine = new EcEngine(transport, EcPlatform.Amd);
        engine.Initialize();

        engine.InitializeEc();

        Assert.Equal(1, transport.ReconnectCount);
    }

    [Fact]
    public void Dispose_DisposesTheTransport()
    {
        var transport = new FakeEcTransport();
        new EcEngine(transport, EcPlatform.Amd).Dispose();

        Assert.True(transport.Disposed);
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var transport = new FakeEcTransport();
        var engine = new EcEngine(transport, EcPlatform.Amd);

        engine.Dispose();
        engine.Dispose();

        Assert.True(transport.Disposed);
    }

    [Fact]
    public void Constructor_RejectsANullTransport()
    {
        Assert.Throws<ArgumentNullException>(() => new EcEngine(null!, EcPlatform.Amd));
    }

    [Fact]
    public void PublicRegisterWrites_GoThroughTheTransportUnderTheEngineLock()
    {
        var transport = new FakeEcTransport();
        using var engine = new EcEngine(transport, EcPlatform.Amd);
        engine.Initialize();

        engine.WriteECRegister(0x07FF, 0x5A);
        Assert.Equal(0x5A, transport.Read(0x07FF));

        engine.WriteECRegister(0x07FF, 0x5B);
        Assert.Equal(new byte[] { 0x5A, 0x5B }, transport.WritesAt(0x07FF).ToArray());
    }

    private sealed class FailFirstReadTransport : IEcTransport
    {
        private const ushort FailAddress = 0x0741;
        private int _readsRemainingToFail = 1;

        public bool IsOpen => true;

        public int ReconnectCount { get; private set; }

        public void Open()
        {
        }

        public void Reconnect() => ReconnectCount++;

        public byte Read(ushort address)
        {
            if (address == FailAddress && _readsRemainingToFail > 0)
            {
                _readsRemainingToFail--;
                throw new System.ComponentModel.Win32Exception(5, "simulated EC read failure");
            }

            return 0;
        }

        public void Write(ushort address, byte value)
        {
        }

        public void Dispose()
        {
        }
    }
}
