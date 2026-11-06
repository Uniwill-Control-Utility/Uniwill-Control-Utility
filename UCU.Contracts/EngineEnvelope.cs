using System.Text.Json;

namespace UCU.Contracts
{
    /// <summary>
    /// Why a request failed, as a stable code. The client maps these onto exceptions
    /// the UI already handles, and the codes are what the engine writes to its log:
    /// a user-visible "hardware unavailable" must be distinguishable from a bug.
    /// </summary>
    public enum EngineErrorCode
    {
        None = 0,

        /// <summary>The verb is not in this engine's verb table (version skew).</summary>
        UnsupportedVerb = 1,

        /// <summary>The envelope or parameters do not match what the verb expects.</summary>
        BadRequest = 2,

        /// <summary>
        /// A value was well-formed JSON but outside what the firmware accepts, so the
        /// write was refused. This is the UCU.Core validation seam (EcDutyCodec,
        /// EcPowerCodec, ...): a bad preset is reported, not clamped into the EC.
        /// </summary>
        InvalidValue = 3,

        /// <summary>Request protocol version does not match the engine's.</summary>
        ProtocolVersion = 4,

        /// <summary>The connecting process is not allowed to issue this verb.</summary>
        Unauthorized = 5,

        /// <summary>The EC driver is not present, not started, or the EC is not responding.</summary>
        HardwareUnavailable = 6,

        /// <summary>The EC rejected or mis-applied a transfer (IOCTL failure, verification mismatch).</summary>
        HardwareFault = 7,

        /// <summary>The engine's hardware queue is saturated; the caller should back off.</summary>
        Busy = 8,

        /// <summary>The hardware operation did not finish within the engine's budget.</summary>
        Timeout = 9,

        /// <summary>Unexpected engine fault; the details are in the engine log.</summary>
        Internal = 10,
    }

    /// <summary>Client-side exception carrying an <see cref="EngineErrorCode"/>.</summary>
    public sealed class EngineException : Exception
    {
        public EngineException(EngineErrorCode code, string? message)
            : base(message ?? $"engine call failed: {code}")
        {
            Code = code;
        }

        public EngineErrorCode Code { get; }
    }

    /// <summary>
    /// One engine call. Parameters travel as a <see cref="JsonElement"/> so the verb
    /// table can stay open: adding a verb does not require touching the envelope, and
    /// the engine binds the parameters against the verb's own DTO type.
    /// </summary>
    public sealed record EngineRequest
    {
        /// <summary>Caller-generated id, echoed in the response and used in logs.</summary>
        public string Id { get; init; } = string.Empty;

        public int ProtocolVersion { get; init; } = EngineProtocol.Version;

        public string Verb { get; init; } = string.Empty;

        public JsonElement? Params { get; init; }
    }

    /// <summary>The outcome of one engine call.</summary>
    public sealed record EngineResponse
    {
        public string Id { get; init; } = string.Empty;

        public bool Ok { get; init; }

        public JsonElement? Result { get; init; }

        public EngineErrorCode? Error { get; init; }

        /// <summary>Human-readable detail. Logged by the client, shown for hardware faults.</summary>
        public string? Message { get; init; }

        public static EngineResponse Success(string id, JsonElement? result = null) =>
            new EngineResponse { Id = id, Ok = true, Result = result };

        public static EngineResponse Failure(string id, EngineErrorCode code, string? message = null) =>
            new EngineResponse { Id = id, Ok = false, Error = code, Message = message };
    }
}
