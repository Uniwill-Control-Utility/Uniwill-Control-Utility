using System.Text;
using UCU.Contracts;

namespace UCU.Contracts.Tests;

/// <summary>
/// The framing rules. These are the rules a mismatched or hostile peer can violate, so
/// the reader must never allocate on an unvalidated length, never hang on a truncated
/// frame, and never accept a frame that grows the pipe buffer.
/// </summary>
public sealed class FrameCodecTests
{
    [Fact]
    public async Task RoundTripsAPayload()
    {
        using var stream = new MemoryStream();

        byte[] payload = Encoding.UTF8.GetBytes("{\"verb\":\"engine.ping\"}");
        await FrameCodec.WriteFrameAsync(stream, payload, CancellationToken.None);
        stream.Position = 0;

        byte[] read = await FrameCodec.ReadFrameAsync(stream, EngineProtocol.MaxRequestBytes, CancellationToken.None);

        Assert.Equal(payload, read);
    }

    [Fact]
    public async Task ReadsConsecutiveFramesInOrder()
    {
        using var stream = new MemoryStream();

        foreach (string text in new[] { "first", "second", "third" })
            await FrameCodec.WriteFrameAsync(stream, Encoding.UTF8.GetBytes(text), CancellationToken.None);

        stream.Position = 0;

        for (int i = 0; i < 3; i++)
        {
            byte[] frame = await FrameCodec.ReadFrameAsync(stream, EngineProtocol.MaxRequestBytes, CancellationToken.None);
            Assert.Equal(new[] { "first", "second", "third" }[i], Encoding.UTF8.GetString(frame));
        }
    }

    [Fact]
    public async Task UsesLittleEndianLengthPrefix()
    {
        using var stream = new MemoryStream();

        await FrameCodec.WriteFrameAsync(stream, new byte[] { 1, 2, 3 }, CancellationToken.None);

        Assert.Equal(new byte[] { 3, 0, 0, 0, 1, 2, 3 }, stream.ToArray());
    }

    [Fact]
    public async Task RejectsAWriteOverTheCap()
    {
        using var stream = new MemoryStream();
        byte[] oversized = new byte[EngineProtocol.MaxResponseBytes + 1];

        await Assert.ThrowsAsync<ProtocolViolationException>(() =>
            FrameCodec.WriteFrameAsync(stream, oversized, CancellationToken.None));
    }

    [Fact]
    public async Task RejectsALengthOverTheCapBeforeAllocating()
    {
        // A hand-built header claiming a huge frame must fail without reading a body.
        using var stream = new MemoryStream(new byte[] { 0x00, 0x00, 0x10, 0x00 });

        await Assert.ThrowsAsync<ProtocolViolationException>(() =>
            FrameCodec.ReadFrameAsync(stream, EngineProtocol.MaxRequestBytes, CancellationToken.None));
    }

    [Theory]
    [InlineData(new byte[] { 0x00, 0x00, 0x00, 0x00 })]   // zero length
    [InlineData(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF })]   // -1
    [InlineData(new byte[] { 0x00, 0x00, 0x00, 0x80 })]   // int.MinValue
    public async Task RejectsNonPositiveLengths(byte[] header)
    {
        using var stream = new MemoryStream(header);

        await Assert.ThrowsAsync<ProtocolViolationException>(() =>
            FrameCodec.ReadFrameAsync(stream, EngineProtocol.MaxRequestBytes, CancellationToken.None));
    }

    [Fact]
    public async Task TruncatedFrameIsReportedAsAClosedPipe()
    {
        using var stream = new MemoryStream(new byte[] { 10, 0, 0, 0, 1, 2, 3 });

        await Assert.ThrowsAsync<EndOfStreamException>(() =>
            FrameCodec.ReadFrameAsync(stream, EngineProtocol.MaxRequestBytes, CancellationToken.None));
    }

    [Fact]
    public async Task EmptyStreamIsReportedAsAClosedPipe()
    {
        using var stream = new MemoryStream();

        await Assert.ThrowsAsync<EndOfStreamException>(() =>
            FrameCodec.ReadFrameAsync(stream, EngineProtocol.MaxRequestBytes, CancellationToken.None));
    }

    [Fact]
    public async Task CancellationStopsAReader()
    {
        using var stream = new BlockingReadStream();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            FrameCodec.ReadFrameAsync(stream, EngineProtocol.MaxRequestBytes, cts.Token));
    }

    private sealed class BlockingReadStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => 0; set { } }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            var tcs = new TaskCompletionSource();
            using var registration = cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
            await tcs.Task;
            return 0;
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => 0;

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
        }
    }
}
