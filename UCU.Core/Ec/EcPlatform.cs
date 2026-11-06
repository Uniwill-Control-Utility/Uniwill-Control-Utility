namespace UCU.Core.Ec
{
    /// <summary>
    /// CPU family the EC firmware encodings differ by. Today only the GPU MUX
    /// registers are platform-sensitive; everything else is platform-independent.
    /// The engine resolves this once from hardware (CPUID) and passes it to the
    /// codecs, so no codec reads global platform state and every encoding is a
    /// pure function of its arguments.
    /// </summary>
    public enum EcPlatform
    {
        /// <summary>AMD laptop CPU (FireRange / AM5). Raw MUX bytes 0/1/2.</summary>
        Amd = 0,

        /// <summary>Intel laptop CPU. Raw MUX bytes 4/2/1 (see <see cref="EcMuxCodec"/>).</summary>
        Intel = 1,
    }

    /// <summary>
    /// Semantic GPU MUX mode used by the UI and the RPC contract. The raw EC/NVRAM
    /// byte for a mode is platform-dependent; translate only through
    /// <see cref="EcMuxCodec"/>.
    /// </summary>
    public enum GpuMuxMode
    {
        /// <summary>MS Hybrid: MUX open, both GPUs active.</summary>
        Hybrid = 0x00,

        /// <summary>MUX closed, discrete GPU only.</summary>
        DgpuOnly = 0x01,

        /// <summary>MUX open, integrated GPU only.</summary>
        IgpuOnly = 0x02,
    }
}
