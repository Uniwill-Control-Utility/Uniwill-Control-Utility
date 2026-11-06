using System.Buffers.Binary;

namespace UCU.Contracts
{
    /// <summary>Raised when a peer sends a frame that violates the framing rules.</summary>
    public sealed class ProtocolViolationException : Exception
    {
        public ProtocolViolationException(string message) : base(message)
        {
        }
    }

    /// <summary>
    /// One JSON document per frame, preceded by a 4-byte little-endian length.
    ///
    /// Deliberately minimal: the transport is a local named pipe, so there is no
    /// compression, encryption or multiplexing to negotiate - the pipe already gives
    /// ordering, integrity and access control, and one request per pipe connection
    /// (the client opens, writes, reads, closes) means no request id needs to be
    /// matched by the receiver. The id is kept in the envelope for logging and for the
    /// day a connection is reused.
    ///
    /// The length prefix is what keeps a malformed peer from wedging a reader: the
    /// reader allocates only after validating the length against the domain cap.
    /// </summary>
    public static class FrameCodec
    {
        public static async Task WriteFrameAsync(Stream stream, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
        {
            if (payload.Length > EngineProtocol.MaxResponseBytes)
                throw new ProtocolViolationException($"frame of {payload.Length} bytes exceeds the cap");

            byte[] header = new byte[EngineProtocol.LengthPrefixBytes];
            BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);

            await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Reads one frame. Throws <see cref="EndOfStreamException"/> when the peer
        /// closes cleanly mid-message, which the engine and the client both treat as
        /// "this connection is dead, reconnect".
        /// </summary>
        public static async Task<byte[]> ReadFrameAsync(Stream stream, int maxPayloadBytes, CancellationToken cancellationToken)
        {
            byte[] header = new byte[EngineProtocol.LengthPrefixBytes];
            await ReadExactlyAsync(stream, header, 0, EngineProtocol.LengthPrefixBytes, cancellationToken).ConfigureAwait(false);

            int length = BinaryPrimitives.ReadInt32LittleEndian(header);
            if (length <= 0)
                throw new ProtocolViolationException($"frame length {length} is not positive");

            if (length > maxPayloadBytes)
                throw new ProtocolViolationException($"frame length {length} exceeds the cap of {maxPayloadBytes}");

            byte[] payload = new byte[length];
            await ReadExactlyAsync(stream, payload, 0, length, cancellationToken).ConfigureAwait(false);
            return payload;
        }

        private static async Task ReadExactlyAsync(Stream stream, byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            int read = 0;
            while (read < count)
            {
                int got = await stream.ReadAsync(buffer.AsMemory(offset + read, count - read), cancellationToken).ConfigureAwait(false);
                if (got == 0)
                    throw new EndOfStreamException("peer closed the pipe before the frame was complete");

                read += got;
            }
        }
    }
}
