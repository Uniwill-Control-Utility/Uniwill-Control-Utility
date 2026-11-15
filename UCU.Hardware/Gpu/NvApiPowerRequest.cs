using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace UCU.Hardware.Gpu
{
    /// <summary>
    /// The private NVAPI ids and buffer layouts the board-power request uses. All of them
    /// were measured on the field machine against MechControl-RE (its
    /// <c>nvidia_api.cpp</c>), not inferred from public documentation.
    /// </summary>
    public static class NvApiPowerLayout
    {
        public const uint InitializeId = 0x0150E828;
        public const uint EnumPhysicalGpusId = 0xE5AC921F;

        /// <summary>The power-policy table: entry i sits at 0x28 + i*0x38.</summary>
        public const uint PolicyTableId = 0x54924BF5;
        public const uint LimitReadId = 0x33AB0353;
        public const uint LimitSetId = 0x17695269;

        public const int PolicyBufferLength = 0x728;
        public const int ControlBufferLength = 0x528;

        /// <summary>Each field i of the control packet: value at 0x28 + i*0x28, method at 0x2c.</summary>
        public const int ControlFieldStride = 0x28;

        public const uint PolicyVersion = 0x10728;
        public const uint ControlVersion = 0x10528;

        /// <summary>The method byte the power-limit field carries (percentage of the policy).</summary>
        public const uint ControlMethod = 0xF8;

        public const int MaxPolicyEntries = 32;

        /// <summary>Canary band either side of every buffer handed to the driver.</summary>
        public const int GuardBandLength = 4096;
        public const byte GuardByte = 0xCD;

        public const int PollAttempts = 20;
        public const int PollDelayMilliseconds = 100;

        public static string Unavailable(uint id) => $"NVAPI interface 0x{id:X8} unavailable";
    }

    /// <summary>One private NVAPI entry point's shape: (device, buffer) → status.</summary>
    public delegate int NvApiGpuCall(IntPtr gpu, IntPtr buffer);

    /// <summary>
    /// The NVAPI entry points behind a seam, so the whole packet flow (policy parse, set,
    /// read-back poll) runs against a scripted table instead of <c>nvapi64.dll</c>.
    /// </summary>
    public interface INvApiPowerCalls
    {
        /// <summary><c>nvapi_QueryInterface</c>; false when the driver does not export the id.</summary>
        bool TryResolve(uint id, out NvApiGpuCall call);

        /// <summary><c>nvapi_Initialize</c> + <c>EnumPhysicalGPUs</c>; false with a reason.</summary>
        bool TryGetGpu(out IntPtr gpu, out string? reason);
    }

    /// <summary>
    /// What the driver answered: the request it carries (null = none), the policy range
    /// it allows, and why the answer is not usable when it is not. A failure is data.
    /// </summary>
    public sealed record GpuPowerRequestState(
        int? RequestedMilliwatts, int? MinMilliwatts, int? MaxMilliwatts, string? Reason)
    {
        public bool Succeeded => Reason == null;
    }

    /// <summary>The driver's board-power request, which is what actually delivers watts.</summary>
    public interface IGpuPowerRequest
    {
        /// <summary>What the driver currently carries; <c>RequestedMilliwatts</c> null = none.</summary>
        GpuPowerRequestState Read();

        /// <summary>
        /// Asks for <paramref name="milliwatts"/> and polls the read-back until the driver
        /// carries it. Refused (with a reason) outside the policy range, without touching
        /// the driver.
        /// </summary>
        GpuPowerRequestState Request(int milliwatts);
    }

    /// <summary>
    /// The NVAPI board-power path. Writing the ROM records only raises the policy
    /// <em>ceiling</em>; the driver still enforces what its own request says (measured:
    /// records staged with a 250 W ceiling and no request → 150 W enforced, the EC's
    /// number). This is the request half.
    ///
    /// Every buffer is handed to the driver between two 4 KiB canary bands: the
    /// reference does the same because a driver writing past our buffer is otherwise
    /// silent corruption of our own heap.
    /// </summary>
    public sealed class NvApiPowerRequest : IGpuPowerRequest
    {
        private readonly INvApiPowerCalls _calls;

        public NvApiPowerRequest()
            : this(new NvApiPowerCalls())
        {
        }

        public NvApiPowerRequest(INvApiPowerCalls calls) => _calls = calls;

        public GpuPowerRequestState Read() => Execute(request: null);

        public GpuPowerRequestState Request(int milliwatts) => Execute(request: milliwatts);

        private GpuPowerRequestState Execute(int? request)
        {
            if (!_calls.TryGetGpu(out IntPtr gpu, out string? gpuReason))
                return new GpuPowerRequestState(null, null, null, gpuReason ?? "NVAPI enumerated no GPU");

            if (!_calls.TryResolve(NvApiPowerLayout.PolicyTableId, out NvApiGpuCall policy))
                return new GpuPowerRequestState(null, null, null, NvApiPowerLayout.Unavailable(NvApiPowerLayout.PolicyTableId));

            byte[] table = new byte[NvApiPowerLayout.PolicyBufferLength];
            Write32(table, 0, NvApiPowerLayout.PolicyVersion);

            if (!TryCall(gpu, policy, table, out int tableStatus, out string? tableCall))
                return new GpuPowerRequestState(null, null, null, tableCall);

            if (tableStatus != 0)
                return new GpuPowerRequestState(null, null, null, $"the board-power policy table read failed (status {tableStatus})");

            if (!TryParsePolicy(table, out int index, out uint policyMin, out uint policyMax, out string? policyReason))
                return new GpuPowerRequestState(null, null, null, policyReason);

            int min = (int)policyMin;
            int max = (int)policyMax;

            if (request is int wanted)
            {
                // Refused before the call: a request the driver would silently clamp or
                // answer with the previous limit is worse than a refusal.
                if (wanted < min || wanted > max)
                    return new GpuPowerRequestState(null, min, max, $"request outside the policy range {min}-{max} mW");

                if (!_calls.TryResolve(NvApiPowerLayout.LimitSetId, out NvApiGpuCall set))
                    return new GpuPowerRequestState(null, min, max, NvApiPowerLayout.Unavailable(NvApiPowerLayout.LimitSetId));

                byte[] packet = BuildControl(index, (uint)wanted);
                if (!TryCall(gpu, set, packet, out int setStatus, out string? setCall))
                    return new GpuPowerRequestState(null, min, max, setCall);

                if (setStatus != 0)
                    return new GpuPowerRequestState(null, min, max, $"the power request was refused (status {setStatus})");
            }

            if (!_calls.TryResolve(NvApiPowerLayout.LimitReadId, out NvApiGpuCall read))
                return new GpuPowerRequestState(null, min, max, NvApiPowerLayout.Unavailable(NvApiPowerLayout.LimitReadId));

            // The driver answers a set asynchronously, so one read is not confirmation.
            for (int attempt = 0; ; attempt++)
            {
                byte[] packet = BuildControl(index, null);
                if (!TryCall(gpu, read, packet, out int readStatus, out string? readCall))
                    return new GpuPowerRequestState(null, min, max, readCall);

                if (readStatus != 0)
                    return new GpuPowerRequestState(null, min, max, $"the power request read failed (status {readStatus})");

                if (!TryReadEcho(packet, index, out uint raw, out string? echoReason))
                    return new GpuPowerRequestState(null, min, max, echoReason);

                // 0 and UINT32_MAX both mean "no request is set".
                uint requested = raw == 0 || raw == uint.MaxValue ? 0u : raw;
                bool settled = true;
                if (request is int target)
                    settled = requested == (uint)target;

                if (settled || attempt + 1 >= NvApiPowerLayout.PollAttempts)
                {
                    if (request is int missed && requested != (uint)missed)
                    {
                        return new GpuPowerRequestState(null, min, max,
                            "the driver did not accept the request: it reports " +
                            (requested == 0 ? "no request" : $"{requested} mW") +
                            $", not {missed} mW");
                    }

                    return new GpuPowerRequestState(requested == 0 ? null : (int)requested, min, max, null);
                }

                Thread.Sleep(NvApiPowerLayout.PollDelayMilliseconds);
            }
        }

        /// <summary>
        /// The board-power entry of a policy table: exactly one entry may be the mW
        /// board-power type, and the table must echo its own version.
        /// </summary>
        internal static bool TryParsePolicy(
            byte[] table, out int index, out uint min, out uint max, out string? reason)
        {
            index = -1;
            min = 0;
            max = 0;
            reason = null;

            if (table.Length < NvApiPowerLayout.PolicyBufferLength || Read32(table, 0) != NvApiPowerLayout.PolicyVersion)
            {
                reason = "the board-power policy table did not echo its version";
                return false;
            }

            uint mask = Read32(table, 4);
            uint foundMin = 0;
            uint foundMax = 0;

            for (int i = 0; i < NvApiPowerLayout.MaxPolicyEntries; i++)
            {
                if ((mask & (1u << i)) == 0)
                    continue;

                int entry = 0x28 + i * 0x38;

                if (Read32(table, entry) > 1)     // board-power type
                    continue;

                if (Read32(table, entry + 4) != 0) // unit: mW
                    continue;

                if (index >= 0)
                {
                    reason = "the board-power policy table carries more than one board-power entry";
                    return false;
                }

                index = i;
                foundMin = Read32(table, entry + 8);
                foundMax = Read32(table, entry + 16);
            }

            if (index < 0)
            {
                reason = "the board-power policy table carries no board-power entry";
                return false;
            }

            min = foundMin;
            max = foundMax;
            return true;
        }

        /// <summary>The 0x528-byte control packet: version, selector, method, and the value for a set.</summary>
        internal static byte[] BuildControl(int index, uint? milliwatts)
        {
            byte[] packet = new byte[NvApiPowerLayout.ControlBufferLength];
            Write32(packet, 0, NvApiPowerLayout.ControlVersion);
            Write32(packet, 4, 1u << index);
            Write32(packet, 0x2c + index * NvApiPowerLayout.ControlFieldStride, NvApiPowerLayout.ControlMethod);

            if (milliwatts is uint value)
                Write32(packet, 0x28 + index * NvApiPowerLayout.ControlFieldStride, value);

            return packet;
        }

        /// <summary>
        /// The read-back echo: a packet whose version, selector or method came back
        /// changed is another field's answer, so its value is not ours to trust.
        /// </summary>
        internal static bool TryReadEcho(byte[] packet, int index, out uint milliwatts, out string? reason)
        {
            milliwatts = 0;
            reason = null;

            if (packet.Length < NvApiPowerLayout.ControlBufferLength || Read32(packet, 0) != NvApiPowerLayout.ControlVersion)
            {
                reason = "the power request read did not echo its structure version";
                return false;
            }

            if (Read32(packet, 4) != 1u << index)
            {
                reason = "the power request read did not echo its selector";
                return false;
            }

            if (Read32(packet, 0x2c + index * NvApiPowerLayout.ControlFieldStride) != NvApiPowerLayout.ControlMethod)
            {
                reason = "the power request read did not echo its method";
                return false;
            }

            milliwatts = Read32(packet, 0x28 + index * NvApiPowerLayout.ControlFieldStride);
            return true;
        }

        internal static bool GuardBandsIntact(byte[] guarded)
        {
            for (int i = 0; i < NvApiPowerLayout.GuardBandLength; i++)
            {
                if (guarded[i] != NvApiPowerLayout.GuardByte ||
                    guarded[guarded.Length - 1 - i] != NvApiPowerLayout.GuardByte)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>A packet handed over between two canary bands, then handed back.</summary>
        internal static bool TryCall(IntPtr gpu, NvApiGpuCall call, byte[] buffer, out int status, out string? reason)
        {
            status = 0;
            reason = null;

            byte[] guarded = new byte[buffer.Length + 2 * NvApiPowerLayout.GuardBandLength];
            guarded.AsSpan().Fill(NvApiPowerLayout.GuardByte);
            Buffer.BlockCopy(buffer, 0, guarded, NvApiPowerLayout.GuardBandLength, buffer.Length);

            GCHandle handle = GCHandle.Alloc(guarded, GCHandleType.Pinned);
            try
            {
                status = call(gpu, handle.AddrOfPinnedObject() + NvApiPowerLayout.GuardBandLength);

                if (!GuardBandsIntact(guarded))
                {
                    reason = "the driver wrote past the buffer";
                    return false;
                }

                Buffer.BlockCopy(guarded, NvApiPowerLayout.GuardBandLength, buffer, 0, buffer.Length);
                return true;
            }
            finally
            {
                handle.Free();
            }
        }

        internal static uint Read32(byte[] buffer, int offset) =>
            BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(offset, 4));

        internal static void Write32(byte[] buffer, int offset, uint value) =>
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset, 4), value);
    }

    /// <summary>
    /// The production table: <c>nvapi64.dll</c>'s <c>nvapi_QueryInterface</c>. The
    /// document's own probe (docs/gpu/NvBiosProbe) loads NVAPI the same way.
    /// </summary>
    public sealed class NvApiPowerCalls : INvApiPowerCalls
    {
        public bool TryResolve(uint id, out NvApiGpuCall call)
        {
            call = null!;

            try
            {
                IntPtr entry = QueryInterface(id);
                if (entry == IntPtr.Zero)
                    return false;

                call = Marshal.GetDelegateForFunctionPointer<NvApiGpuCall>(entry);
                return true;
            }
            catch (DllNotFoundException)
            {
                return false;
            }
        }

        public bool TryGetGpu(out IntPtr gpu, out string? reason)
        {
            gpu = IntPtr.Zero;
            reason = null;

            try
            {
                IntPtr initialize = QueryInterface(NvApiPowerLayout.InitializeId);
                if (initialize == IntPtr.Zero)
                {
                    reason = NvApiPowerLayout.Unavailable(NvApiPowerLayout.InitializeId);
                    return false;
                }

                int status = Marshal.GetDelegateForFunctionPointer<InitializeCall>(initialize)();
                if (status != 0)
                {
                    reason = $"nvapi_Initialize failed (status {status})";
                    return false;
                }

                IntPtr enumerate = QueryInterface(NvApiPowerLayout.EnumPhysicalGpusId);
                if (enumerate == IntPtr.Zero)
                {
                    reason = NvApiPowerLayout.Unavailable(NvApiPowerLayout.EnumPhysicalGpusId);
                    return false;
                }

                IntPtr[] handles = new IntPtr[64];
                status = Marshal.GetDelegateForFunctionPointer<EnumPhysicalGpusCall>(enumerate)(handles, out uint count);
                if (status != 0)
                {
                    reason = $"EnumPhysicalGPUs failed (status {status})";
                    return false;
                }

                if (count != 1)
                {
                    reason = $"NVAPI enumerates {count} GPUs; the board-power request needs exactly one";
                    return false;
                }

                gpu = handles[0];
                return true;
            }
            catch (DllNotFoundException)
            {
                reason = "nvapi64.dll is not available on this machine";
                return false;
            }
        }

        [DllImport("nvapi64.dll", EntryPoint = "nvapi_QueryInterface")]
        private static extern IntPtr QueryInterface(uint id);

        private delegate int InitializeCall();

        private delegate int EnumPhysicalGpusCall([Out] IntPtr[] handles, out uint count);
    }
}
