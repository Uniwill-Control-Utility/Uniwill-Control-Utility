using System.ComponentModel;
using System.IO.Pipes;
using UCU.Client;
using UCU.Contracts;
using UCU.Hardware.Ec;

namespace UCU.Engine.Tests;

/// <summary>
/// The failure contract: each fault class surfaces at the client as a specific
/// <see cref="EngineErrorCode"/>, never as a raw exception, a hang, or a crash. These
/// are the paths the app's degraded banner and retry logic key off, so a code that
/// regresses to <c>Internal</c> is a user-visible bug.
/// </summary>
public sealed class EngineErrorMappingTests
{
    [Fact]
    public async Task UnknownVerb_IsReportedAsUnsupportedVerb()
    {
        await using var host = await EngineHostFixture.StartAsync();

        EngineException ex = await Assert.ThrowsAsync<EngineException>(() => host.Client.InvokeAsync("bogus.verb"));

        Assert.Equal(EngineErrorCode.UnsupportedVerb, ex.Code);
    }

    [Fact]
    public async Task ProtocolVersionSkew_IsReportedAsProtocolVersion()
    {
        await using var host = await EngineHostFixture.StartAsync();

        // A hand-built envelope: the client always sends its own version, so a skewed
        // peer has to be simulated at the frame level.
        EngineResponse response = await SendRawAsync(host.Options.PipeName, new EngineRequest
        {
            Id = "skew",
            ProtocolVersion = 99,
            Verb = EngineVerbs.Engine.Ping,
        });

        Assert.False(response.Ok);
        Assert.Equal(EngineErrorCode.ProtocolVersion, response.Error);
    }

    [Fact]
    public async Task MalformedFrame_IsReportedAsBadRequest()
    {
        await using var host = await EngineHostFixture.StartAsync();

        using var pipe = new NamedPipeClientStream(".", host.Options.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(5000);
        await FrameCodec.WriteFrameAsync(pipe, new byte[] { 0x6E, 0x6F, 0x74, 0x20, 0x4A, 0x53, 0x4F, 0x4E }, CancellationToken.None);
        byte[] payload = await FrameCodec.ReadFrameAsync(pipe, EngineProtocol.MaxResponseBytes, CancellationToken.None);

        EngineResponse response = EngineSerializer.Deserialize<EngineResponse>(payload);

        Assert.Equal(EngineErrorCode.BadRequest, response.Error);
    }

    [Fact]
    public async Task OutOfRangeValue_IsReportedAsInvalidValue_AndWritesNothing()
    {
        await using var host = await EngineHostFixture.StartAsync();

        EngineException ex = await Assert.ThrowsAsync<EngineException>(() => host.Battery.SetModeAsync(3));

        Assert.Equal(EngineErrorCode.InvalidValue, ex.Code);
        Assert.Empty(host.Fake.WritesAt(EcEngine.REG_BATTERY_MODE));
    }

    [Fact]
    public async Task MissingDriver_IsReportedAsHardwareUnavailable()
    {
        await using var host = await EngineHostFixture.StartAsync(transport: new UnopenableEcTransport());

        EngineException ex = await Assert.ThrowsAsync<EngineException>(() => host.Fan.GetStatusAsync());

        Assert.Equal(EngineErrorCode.HardwareUnavailable, ex.Code);
        Assert.False(await host.Engine.IsHardwareAvailableAsync());

        // The identity verbs answer even without the EC: the app shows version-skew and
        // a degraded banner off this call, so it must not fault.
        EngineInfoDto info = await host.Engine.GetInfoAsync();
        Assert.False(info.EcAvailable);
    }

    [Fact]
    public async Task SlowHardware_IsReportedAsTimeout_AndTheQueueSurvives()
    {
        // The fan duty table is the slow write: it lands on the fan queue during the
        // curve path, and only once (the handshake's copy), so the second call is clean.
        var transport = new SleepyWriteTransport(EcEngine.REG_CPU_FAN_SPEED_TABLE, sleepMs: 1500);

        await using var host = await EngineHostFixture.StartAsync(
            o => o with { HardwareCallBudget = TimeSpan.FromMilliseconds(300) },
            transport);

        EngineException ex = await Assert.ThrowsAsync<EngineException>(() => host.Fan.SetSpeedPercentAsync(50));

        Assert.Equal(EngineErrorCode.Timeout, ex.Code);

        // The write finished on the queue (a cancelled RPC never abandons a half-written
        // register): the handshake's 16 table bytes plus the curve's 16 all land, and
        // then a healthy read on the same domain succeeds.
        var transport2 = transport.Inner;
        // Drain window must exceed the injected sleep plus the EC sequencing delays.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        int TableWrites() => transport2.Writes.Count(w => w.Address >= EcEngine.REG_CPU_FAN_SPEED_TABLE
            && w.Address < EcEngine.REG_CPU_FAN_SPEED_TABLE + 16);
        while (TableWrites() < 32 && DateTime.UtcNow < deadline)
            await Task.Delay(25);
        Assert.Equal(32, TableWrites());

        // The verb's last unconditional write is the boost-clear on 0x0751 (the SPLIT
        // write that follows is conditional and skipped here). When it is on the wire,
        // the fan queue has released the verb.
        while (transport2.WritesAt(EcEngine.REG_MANUAL_FAN_CTRL).Count < 1 && DateTime.UtcNow < deadline)
            await Task.Delay(25);
        Assert.Equal(1, transport2.WritesAt(EcEngine.REG_MANUAL_FAN_CTRL).Count);

        // Any post-toggle settle delay inside the verb finishes before the probe.
        await Task.Delay(500);
        await host.Fan.GetFanPwmAsync(0);
    }

    private static async Task<EngineResponse> SendRawAsync(string pipeName, EngineRequest request)
    {
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(5000);
        await FrameCodec.WriteFrameAsync(pipe, EngineSerializer.SerializeToUtf8Bytes(request), CancellationToken.None);
        byte[] payload = await FrameCodec.ReadFrameAsync(pipe, EngineProtocol.MaxResponseBytes, CancellationToken.None);
        return EngineSerializer.Deserialize<EngineResponse>(payload);
    }

    private sealed class UnopenableEcTransport : IEcTransport
    {
        public bool IsOpen => false;

        public void Open() => throw new Win32Exception(2, "driver not present");

        public void Reconnect() => throw new Win32Exception(2, "driver not present");

        public byte Read(ushort address) => throw new InvalidOperationException();

        public void Write(ushort address, byte value) => throw new InvalidOperationException();

        public void Dispose()
        {
        }
    }

    private sealed class SleepyWriteTransport : IEcTransport
    {
        private readonly FakeEcTransport _inner = new();
        private readonly ushort _sleepAt;
        private readonly int _sleepMs;
        private int _slept;

        /// <summary>The recording transport behind the sleep, for write assertions.</summary>
        public FakeEcTransport Inner => _inner;

        public SleepyWriteTransport(ushort sleepAt, int sleepMs)
        {
            _sleepAt = sleepAt;
            _sleepMs = sleepMs;
        }

        public bool IsOpen => _inner.IsOpen;

        public void Open() => _inner.Open();

        public void Reconnect() => _inner.Reconnect();

        public byte Read(ushort address) => _inner.Read(address);

        public void Write(ushort address, byte value)
        {
            if (address == _sleepAt && _slept == 0)
            {
                _slept++;
                Thread.Sleep(_sleepMs);
            }

            _inner.Write(address, value);
        }

        public void Dispose() => _inner.Dispose();
    }
}
