using UCU.Core.Ec;
using Uniwill_Control_Utility.Scripts;

namespace Uniwill_Control_Utility.Services
{
    /// <summary>
    /// Maps the app's CPU detection (<see cref="Family.TYPE"/>) onto the engine's
    /// <see cref="EcPlatform"/>.
    ///
    /// One place on purpose: the EC register encodings that differ by platform (the
    /// GPU MUX bytes) are selected by a value passed into the engine, so nothing
    /// downstream can encode a write with the platform left unresolved. The engine
    /// service resolves the same value from CPUID; keeping the mapping here makes the
    /// two agree by construction.
    /// </summary>
    public static class EcPlatformResolver
    {
        public static EcPlatform Current =>
            Family.TYPE == Family.ProcessorType.Intel ? EcPlatform.Intel : EcPlatform.Amd;
    }
}
