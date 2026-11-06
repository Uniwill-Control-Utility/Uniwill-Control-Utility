using System.Text.Json;

namespace UCU.Engine
{
    /// <summary>
    /// One verb's implementation: bind its parameters, call the EC engine, return the
    /// result object (the dispatcher serializes it). Handlers run on the verb's domain
    /// queue, so they may block on hardware delays freely; the queue is single-threaded
    /// by design.
    /// </summary>
    internal delegate Task<object?> VerbHandler(JsonElement? parameters, CancellationToken cancellationToken);
}
