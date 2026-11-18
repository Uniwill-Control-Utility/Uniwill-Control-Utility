using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace UCU.Hardware.Gpu
{
    /// <summary>
    /// The private NVAPI ids and packet layout of the laptop power budget (the driver's
    /// "F7" value). Ported from MechControl-RE (its <c>nvidia_api.cpp</c> Native struct:
    /// <c>body</c>, <c>find</c>, <c>init_budget</c>, <c>f7</c>, <c>platform</c>) and
    /// measured on the field machine on 2026-11-15:
    /// <code>
    /// budget before: f7 150000 mW, platform 425000
    /// raise to 225 W -> f7 225000, platform 500000
    /// then request 225000 -> NVML enforced 225000 (was 150000)
    /// </code>
    /// The driver enforces <c>min(request, F7 budget)</c>: the staged ROM records and the
    /// driver request were already correct and were capped by the 150 W budget. This is
    /// the missing lever, and it must never be guessed at (a wrong buffer is written to
    /// the driver's own dispatch entry point).
    /// </summary>
    public static class NvApiF7Layout
    {
        /// <summary>The QBOOST platform budget packet (single-argument calls).</summary>
        public const uint PlatformReadId = 0x93456591;
        public const uint PlatformWriteId = 0xB78734AB;

        /// <summary>
        /// The extended initializer the platform packet needs. 0xAD298D3F is the one the
        /// reference resolves; 0xD7C61344 answers -4 and is not a substitute.
        /// </summary>
        public const uint PlatformInitializeId = 0xAD298D3F;

        /// <summary>The F7 packet's fixed fields (the reference's <c>f7</c>).</summary>
        public const uint DispatchMethod = 0x07000046;
        public const uint GetMethod = 0x2080A632;
        public const uint SetMethod = 0x2080E633;

        public const int HandleOffset = 0x30;
        public const int MethodOffset = 0x34;
        public const int CompletionOffset = 0x2C;
        public const int SelectorOffset = 0x3C;
        public const int ValueOffset = 0x64;
        public const int ValueStride = 12;

        /// <summary>The marker byte that follows the value slot (the packet's own tag).</summary>
        public const byte Marker = 0xF7;

        /// <summary>The ABI sizes the measured nvapi64 builds use; anything else is refused.</summary>
        public const uint NativeSizeAda = 0x122D70;
        public const uint NativeSizeSmall = 0x60B30;

        public const int PlatformBufferLength = 0xC88;
        public const uint PlatformVersion = 0x10C88;

        /// <summary>How many bytes the dispatch reads off the stack after the tail call.</summary>
        public const int TailCallLength = 4;

        public const int MaxBoardIndex = 32;
    }

    /// <summary>
    /// The driver's laptop power budget in mW: the F7 (board) budget and the platform
    /// (QBOOST) budget. Both are values the EC publishes; a raise takes headroom from the
    /// platform budget so the adapter's total draw stays inside its own limit.
    /// </summary>
    public readonly record struct GpuPowerBudget(int F7Milliwatts, int PlatformMilliwatts)
    {
        /// <summary>
        /// The reference's <c>Budget::raise</c>: the F7 budget is the target when that is
        /// a raise, and the platform budget grows by exactly the same delta (never more —
        /// the two together are one shared budget). False when the sum overflows, which is
        /// a refusal, not a clamp.
        /// </summary>
        public bool TryRaise(int targetMilliwatts, out GpuPowerBudget raised)
        {
            raised = this;

            if (targetMilliwatts <= 0)
                return false;

            int f7 = Math.Max(F7Milliwatts, targetMilliwatts);
            long platform = (long)PlatformMilliwatts + (f7 - F7Milliwatts);
            if (platform > int.MaxValue)
                return false;

            raised = new GpuPowerBudget(f7, (int)platform);
            return true;
        }
    }

    /// <summary>
    /// What the driver answered: the budget it carries, and why the answer is not usable
    /// when it is not. A failure is data — never an exception on its way to a verb.
    /// </summary>
    public sealed record GpuPowerBudgetState(int? F7Milliwatts, int? PlatformMilliwatts, string? Reason)
    {
        public bool Succeeded => Reason == null;

        public GpuPowerBudget? Budget =>
            Succeeded && F7Milliwatts is int f7 && PlatformMilliwatts is int platform
                ? new GpuPowerBudget(f7, platform)
                : null;
    }

    /// <summary>
    /// The native primitives the F7 path needs, behind a seam: the production
    /// implementation touches <c>nvapi64.dll</c> and its own code pages, a fake scripts
    /// them. Nothing here validates anything — every check stays in
    /// <see cref="NvApiPowerBudget"/> so the fake exercises the real logic.
    /// </summary>
    public interface INvApiF7Calls
    {
        /// <summary>
        /// Starts one operation's NVAPI session, as <see cref="INvApiPowerCalls.Open"/>: every
        /// entry point (and the function bodies read for them) belongs to that one load.
        /// </summary>
        IDisposable Open() => NvApiNoScope.Instance;

        /// <summary><c>nvapi_QueryInterface</c>; false when the driver does not export the id.</summary>
        bool TryQuery(uint id, out IntPtr entry);

        /// <summary><c>nvapi_Initialize</c> + <c>EnumPhysicalGPUs</c>; false with a reason.</summary>
        bool TryGetGpu(out IntPtr gpu, out string? reason);

        /// <summary>
        /// The function's in-memory body (<c>RtlLookupFunctionEntry</c>) and the image base
        /// it belongs to. False when the pointer is not a function boundary.
        /// </summary>
        bool TryBody(IntPtr entry, out byte[] code, out ulong image, out string? reason);

        bool TryGpuCall(IntPtr entry, IntPtr gpu, IntPtr buffer, out int status, out string? reason);

        bool TryPlainCall(IntPtr entry, IntPtr buffer, out int status, out string? reason);

        /// <summary><c>InitializeEx(flag)</c> — an int argument, not a buffer.</summary>
        bool TryInitializeEx(IntPtr entry, uint flag, out int status, out string? reason);

        /// <summary>The F7 dispatch: <c>(method, buffer, size, 0, 1, 0, 0)</c>.</summary>
        bool TryDispatch(IntPtr target, uint method, IntPtr buffer, uint size, out int status, out string? reason);
    }

    /// <summary>
    /// The laptop power budget the driver enforces against every power request. The store
    /// raises it around the request and puts it back on restore.
    /// </summary>
    public interface INvApiPowerBudget
    {
        /// <summary>The budget the driver carries now, or the reason it is unreadable.</summary>
        GpuPowerBudgetState Read();

        /// <summary>
        /// Raises (or lowers) the budget: platform up first, then F7 with an exact
        /// readback, then platform down last — the order that never leaves the two
        /// disagreeing mid-way. Refused outside the board-power policy.
        /// </summary>
        GpuPowerBudgetState Set(GpuPowerBudget target);

        /// <summary>
        /// The reference's <c>restore_budget</c>: puts each half of the budget back to
        /// <paramref name="baseline"/> only while the driver still carries the value we
        /// applied — a budget another writer moved is left alone.
        /// </summary>
        GpuPowerBudgetState Restore(GpuPowerBudget baseline, GpuPowerBudget applied);
    }

    /// <summary>
    /// The F7 / platform budget path. Faithful to the measured reference, including every
    /// validation: the transport is found by disassembling the two power interfaces'
    /// bodies, the ABI is size-checked, the GET and SET entries must resolve to the same
    /// dispatch pointer in the same image, and every packet is handed to the driver
    /// between two 4 KiB <c>0xCD</c> canary bands and read back field by field.
    /// </summary>
    public sealed class NvApiPowerBudget : INvApiPowerBudget
    {
        /// <summary>The ABI head inside the F7 GET interface: the size follows it.</summary>
        private static readonly byte[] AbiHead =
        {
            0xC7, 0x44, 0x24, 0x30, 0x00, 0x00, 0x00, 0x00,
            0xC6, 0x44, 0x24, 0x28, 0x00,
            0xC6, 0x44, 0x24, 0x20, 0x01,
            0x45, 0x33, 0xC9, 0x41, 0xB8,
        };

        /// <summary><c>mov rdx, r14; mov ecx, 0x7000046; call …</c> — the dispatch call.</summary>
        private static readonly byte[] Tail =
        {
            0x49, 0x8B, 0xD6, 0xB9, 0x46, 0x00, 0x00, 0x07, 0xE8,
        };

        private static readonly byte[] GetIdentity =
        {
            0x45, 0x89, 0x7E, 0x30, 0x41, 0xC7, 0x46, 0x34, 0x32, 0xA6, 0x80, 0x20,
        };

        private static readonly byte[] SetIdentity = { 0x33, 0xE6, 0x80, 0x20 };

        private readonly INvApiF7Calls _calls;

        public NvApiPowerBudget()
            : this(new NvApiF7Calls())
        {
        }

        public NvApiPowerBudget(INvApiF7Calls calls) => _calls = calls;

        public GpuPowerBudgetState Read() => Execute(target: null);

        public GpuPowerBudgetState Set(GpuPowerBudget target) => Execute(target);

        public GpuPowerBudgetState Restore(GpuPowerBudget baseline, GpuPowerBudget applied)
        {
            GpuPowerBudgetState current = Read();
            if (current.Budget is not GpuPowerBudget live)
                return current;

            GpuPowerBudget desired = live;
            if (live.F7Milliwatts == applied.F7Milliwatts)
                desired = desired with { F7Milliwatts = baseline.F7Milliwatts };
            if (live.PlatformMilliwatts == applied.PlatformMilliwatts)
                desired = desired with { PlatformMilliwatts = baseline.PlatformMilliwatts };

            return desired == live ? current : Set(desired);
        }

        private GpuPowerBudgetState Execute(GpuPowerBudget? target)
        {
            using IDisposable session = _calls.Open();

            if (!_calls.TryGetGpu(out IntPtr gpu, out string? gpuReason))
                return new GpuPowerBudgetState(null, null, gpuReason ?? "NVAPI enumerated no GPU");

            if (!TryResolveDispatch(out F7Transport transport, out string? reason) || !TryInitializePlatform(out reason))
                return new GpuPowerBudgetState(null, null, reason);

            if (!TryBoardPolicy(gpu, out int index, out int min, out int max, out reason))
                return new GpuPowerBudgetState(null, null, reason);

            if (!TryF7(transport, gpu, index, value: null, out int f7, out reason))
                return new GpuPowerBudgetState(null, null, reason);

            if (!TryPlatformRead(out int platform, out reason))
                return new GpuPowerBudgetState(f7, null, reason);

            if (target is not GpuPowerBudget want)
                return new GpuPowerBudgetState(f7, platform, null);

            if (want.F7Milliwatts < min || want.F7Milliwatts > max)
            {
                return new GpuPowerBudgetState(
                    f7, platform, $"the F7 budget {want.F7Milliwatts} mW is outside the board-power policy {min}-{max} mW");
            }

            if (want.PlatformMilliwatts <= 0)
                return new GpuPowerBudgetState(f7, platform, "the platform budget target is not a usable value");

            // Platform headroom first, then the board budget, then any platform decrease:
            // the reference's order, which never leaves F7 above what the platform allows.
            if (want.PlatformMilliwatts > platform)
            {
                if (!TryPlatformWrite(want.PlatformMilliwatts, out reason))
                    return new GpuPowerBudgetState(f7, platform, reason);

                platform = want.PlatformMilliwatts;
            }

            if (want.F7Milliwatts != f7)
            {
                if (!TryF7(transport, gpu, index, (uint)want.F7Milliwatts, out _, out reason))
                    return new GpuPowerBudgetState(f7, platform, reason);

                // The set is only done when a second read says so. A driver that answers
                // the set but keeps the old value is the failure this refuses to report.
                if (!TryF7(transport, gpu, index, value: null, out int readback, out reason))
                    return new GpuPowerBudgetState(f7, platform, reason);

                if (readback != want.F7Milliwatts)
                {
                    return new GpuPowerBudgetState(
                        readback, platform, $"the F7 budget did not read back as written ({readback} mW)");
                }

                f7 = readback;
            }

            if (want.PlatformMilliwatts < platform)
            {
                if (!TryPlatformWrite(want.PlatformMilliwatts, out reason))
                    return new GpuPowerBudgetState(f7, platform, reason);

                platform = want.PlatformMilliwatts;
            }

            return new GpuPowerBudgetState(f7, platform, null);
        }

        /// <summary>
        /// The dispatch pointer and the ABI size of this operation's nvapi64, resolved from
        /// the two power interfaces' own bodies. Every step is a refusal: a signature that is
        /// not there (or is there twice), an ABI this code does not know, or GET and SET
        /// pointing somewhere else.
        /// </summary>
        private bool TryResolveDispatch(out F7Transport transport, out string? reason)
        {
            transport = default;
            reason = null;

            if (!_calls.TryQuery(NvApiPowerLayout.LimitReadId, out IntPtr get))
            {
                reason = NvApiPowerLayout.Unavailable(NvApiPowerLayout.LimitReadId);
                return false;
            }

            if (!_calls.TryQuery(NvApiPowerLayout.LimitSetId, out IntPtr set))
            {
                reason = NvApiPowerLayout.Unavailable(NvApiPowerLayout.LimitSetId);
                return false;
            }

            if (!_calls.TryBody(get, out byte[] getCode, out ulong getImage, out reason) ||
                !_calls.TryBody(set, out byte[] setCode, out ulong setImage, out reason))
            {
                return false;
            }

            int head = Find(getCode, AbiHead, "F7 ABI head", out reason);
            if (head < 0)
                return false;

            int sizeAt = head + AbiHead.Length;
            if (sizeAt + 4 > getCode.Length)
            {
                reason = "the F7 ABI head ends its function body";
                return false;
            }

            uint nativeSize = Read32(getCode, sizeAt);
            if (nativeSize != NvApiF7Layout.NativeSizeAda && nativeSize != NvApiF7Layout.NativeSizeSmall)
            {
                reason = $"unsupported F7 ABI 0x{nativeSize:X}";
                return false;
            }

            int tail = sizeAt + 4;
            if (tail + Tail.Length + NvApiF7Layout.TailCallLength > getCode.Length ||
                !getCode.AsSpan(tail, Tail.Length).SequenceEqual(Tail))
            {
                reason = "the F7 dispatch signature was not found in this nvapi64";
                return false;
            }

            if (Find(getCode, GetIdentity, "F7 GET identity", out reason) < 0 ||
                Find(setCode, SetIdentity, "F7 SET identity", out reason) < 0)
            {
                return false;
            }

            int setTail = Find(setCode, Tail, "F7 SET dispatch", out reason);
            if (setTail < 0)
                return false;

            if (setTail + Tail.Length + NvApiF7Layout.TailCallLength > setCode.Length)
            {
                reason = "the F7 SET dispatch signature was not found in this nvapi64";
                return false;
            }

            long target = (long)get + tail + Tail.Length + NvApiF7Layout.TailCallLength +
                          (int)Read32(getCode, tail + Tail.Length);
            long other = (long)set + setTail + Tail.Length + NvApiF7Layout.TailCallLength +
                         (int)Read32(setCode, setTail + Tail.Length);

            if (target != other)
            {
                reason = "the GET and SET dispatch targets differ";
                return false;
            }

            if (getImage != setImage)
            {
                reason = "the F7 GET and SET interfaces are in different nvapi64 images";
                return false;
            }

            if (!_calls.TryBody(new IntPtr(target), out _, out ulong targetImage, out reason))
                return false;

            if (targetImage != getImage)
            {
                reason = "the F7 dispatch target is not in the nvapi64 image";
                return false;
            }

            transport = new F7Transport(new IntPtr(target), nativeSize);
            return true;
        }

        private bool TryInitializePlatform(out string? reason)
        {
            reason = null;

            if (!_calls.TryQuery(NvApiF7Layout.PlatformInitializeId, out IntPtr initialize))
            {
                reason = NvApiPowerLayout.Unavailable(NvApiF7Layout.PlatformInitializeId);
                return false;
            }

            if (!_calls.TryInitializeEx(initialize, 1, out int status, out reason))
                return false;

            if (status != 0)
            {
                reason = $"the platform budget could not be initialized (status {status})";
                return false;
            }

            return true;
        }

        /// <summary>
        /// The board-power policy: which entry is the board (its index is the F7 selector)
        /// and the range a budget target must sit in. Same table and parse as the power
        /// request, because it is the same policy the request is validated against.
        /// </summary>
        private bool TryBoardPolicy(IntPtr gpu, out int index, out int min, out int max, out string? reason)
        {
            index = -1;
            min = 0;
            max = 0;

            if (!_calls.TryQuery(NvApiPowerLayout.PolicyTableId, out IntPtr entry))
            {
                reason = NvApiPowerLayout.Unavailable(NvApiPowerLayout.PolicyTableId);
                return false;
            }

            byte[] table = new byte[NvApiPowerLayout.PolicyBufferLength];
            Write32(table, 0, NvApiPowerLayout.PolicyVersion);

            if (!TryCallGuarded(entry, gpu, plain: false, table, out int status, out reason))
                return false;

            if (status != 0)
            {
                reason = $"the board-power policy table read failed (status {status})";
                return false;
            }

            if (!NvApiPowerRequest.TryParsePolicy(table, out index, out uint foundMin, out uint foundMax, out reason))
                return false;

            if (foundMin > foundMax || foundMax == 0 || foundMax > int.MaxValue)
            {
                reason = "the board-power policy range is not plausible";
                return false;
            }

            min = (int)foundMin;
            max = (int)foundMax;
            return true;
        }

        /// <summary>
        /// One F7 packet: get when <paramref name="value"/> is null, set when it is not.
        /// The response is the driver's own echo, so every documented field is checked and
        /// every other byte from 0x38 on must still be the zero this code wrote.
        /// </summary>
        private bool TryF7(F7Transport transport, IntPtr gpu, int index, uint? value, out int result, out string? reason)
        {
            result = 0;
            uint nativeSize = transport.NativeSize;

            ulong handle = unchecked((ulong)gpu.ToInt64());
            if (index < 0 || index >= NvApiF7Layout.MaxBoardIndex || handle == 0 || handle > uint.MaxValue)
            {
                reason = "the F7 GPU handle or board index is not usable";
                return false;
            }

            uint method = value is null ? NvApiF7Layout.GetMethod : NvApiF7Layout.SetMethod;
            int offset = NvApiF7Layout.ValueOffset + index * NvApiF7Layout.ValueStride;
            if (offset + 8 > nativeSize)
            {
                reason = "the F7 ABI is too small for this board index";
                return false;
            }

            byte[] guarded = new byte[nativeSize + 2 * NvApiPowerLayout.GuardBandLength];
            guarded.AsSpan().Fill(NvApiPowerLayout.GuardByte);
            Array.Clear(guarded, NvApiPowerLayout.GuardBandLength, (int)nativeSize);

            int at = NvApiPowerLayout.GuardBandLength;
            Write32(guarded, at + NvApiF7Layout.HandleOffset, (uint)handle);
            Write32(guarded, at + NvApiF7Layout.MethodOffset, method);
            Write32(guarded, at + NvApiF7Layout.SelectorOffset, 1u << index);
            Write32(guarded, at + offset, value ?? 0);
            guarded[at + offset + 4] = NvApiF7Layout.Marker;

            GCHandle pin = GCHandle.Alloc(guarded, GCHandleType.Pinned);
            int status;
            try
            {
                if (!_calls.TryDispatch(
                        transport.Dispatch, NvApiF7Layout.DispatchMethod, pin.AddrOfPinnedObject() + at, nativeSize, out status, out reason))
                {
                    return false;
                }
            }
            finally
            {
                pin.Free();
            }

            if (!NvApiPowerRequest.GuardBandsIntact(guarded))
            {
                reason = "the driver wrote past the F7 buffer";
                return false;
            }

            if (Read32(guarded, at + NvApiF7Layout.HandleOffset) != (uint)handle ||
                Read32(guarded, at + NvApiF7Layout.MethodOffset) != method ||
                Read32(guarded, at + NvApiF7Layout.SelectorOffset) != 1u << index ||
                Read32(guarded, at + offset + 4) != NvApiF7Layout.Marker)
            {
                reason = "the F7 response did not echo its handle, method or selector";
                return false;
            }

            for (int i = 0x38; i < nativeSize; i++)
            {
                if (i == 0x38 ||
                    i == offset - 4 || i == offset - 3 ||
                    (i >= NvApiF7Layout.SelectorOffset && i < NvApiF7Layout.SelectorOffset + 4) ||
                    (i >= offset && i < offset + 8))
                {
                    continue;
                }

                if (guarded[at + i] != 0)
                {
                    reason = $"the F7 response wrote outside its own fields (byte 0x{i:X})";
                    return false;
                }
            }

            if (status != 0)
            {
                reason = $"the F7 dispatch failed (status {status})";
                return false;
            }

            if (Read32(guarded, at + NvApiF7Layout.CompletionOffset) != 1)
            {
                reason = "the F7 response is incomplete";
                return false;
            }

            uint raw = Read32(guarded, at + offset);

            // Seen after the driver reset the GPU on waking: only a GPU restart cleared it.
            if (raw == 0 || raw == uint.MaxValue || raw > int.MaxValue)
            {
                reason = "the laptop power budget is unreadable; restart the GPU";
                return false;
            }

            result = (int)raw;
            return true;
        }

        private bool TryPlatformRead(out int target, out string? reason) =>
            TryPlatformReadPacket(out _, out target, out reason);

        private bool TryPlatformReadPacket(out byte[] packet, out int target, out string? reason)
        {
            packet = new byte[NvApiF7Layout.PlatformBufferLength];
            target = 0;

            if (!_calls.TryQuery(NvApiF7Layout.PlatformReadId, out IntPtr read))
            {
                reason = NvApiPowerLayout.Unavailable(NvApiF7Layout.PlatformReadId);
                return false;
            }

            Write32(packet, 0, 1);
            Write32(packet, 4, NvApiF7Layout.PlatformVersion);

            // The platform interfaces take the buffer alone (int(void*)).
            if (!TryCallGuarded(read, IntPtr.Zero, plain: true, packet, out int status, out reason))
                return false;

            if (status != 0)
            {
                reason = $"the platform budget read failed (status {status})";
                return false;
            }

            uint raw = Read32(packet, NvApiF7Layout.CompletionOffset);
            if (Read32(packet, 0) != 1 ||
                Read32(packet, 4) != NvApiF7Layout.PlatformVersion ||
                packet[8] != 1 ||
                raw == 0 || raw == uint.MaxValue || raw > int.MaxValue)
            {
                reason = "the platform budget packet did not echo its controller, version or target";
                return false;
            }

            target = (int)raw;
            return true;
        }

        private bool TryPlatformWrite(int target, out string? reason)
        {
            if (!_calls.TryQuery(NvApiF7Layout.PlatformWriteId, out IntPtr write))
            {
                reason = NvApiPowerLayout.Unavailable(NvApiF7Layout.PlatformWriteId);
                return false;
            }

            // The write carries a packet the driver has just answered, so the read comes first.
            if (!TryPlatformReadPacket(out byte[] packet, out _, out reason))
                return false;

            Write32(packet, NvApiF7Layout.CompletionOffset, (uint)target);

            if (!TryCallGuarded(write, IntPtr.Zero, plain: true, packet, out int status, out reason))
                return false;

            if (status != 0)
            {
                reason = $"the platform budget write failed (status {status})";
                return false;
            }

            if (!TryPlatformRead(out int readback, out reason))
                return false;

            if (readback != target)
            {
                reason = "the platform budget did not read back as written";
                return false;
            }

            return true;
        }

        /// <summary>A packet handed over between two canary bands, then read back intact.</summary>
        private bool TryCallGuarded(
            IntPtr entry, IntPtr gpu, bool plain, byte[] packet, out int status, out string? reason)
        {
            status = 0;

            byte[] guarded = new byte[packet.Length + 2 * NvApiPowerLayout.GuardBandLength];
            guarded.AsSpan().Fill(NvApiPowerLayout.GuardByte);
            Buffer.BlockCopy(packet, 0, guarded, NvApiPowerLayout.GuardBandLength, packet.Length);

            GCHandle pin = GCHandle.Alloc(guarded, GCHandleType.Pinned);
            try
            {
                IntPtr buffer = pin.AddrOfPinnedObject() + NvApiPowerLayout.GuardBandLength;
                bool called = plain
                    ? _calls.TryPlainCall(entry, buffer, out status, out reason)
                    : _calls.TryGpuCall(entry, gpu, buffer, out status, out reason);

                if (!called)
                    return false;

                if (!NvApiPowerRequest.GuardBandsIntact(guarded))
                {
                    reason = "the driver wrote past the buffer";
                    return false;
                }

                Buffer.BlockCopy(guarded, NvApiPowerLayout.GuardBandLength, packet, 0, packet.Length);
                return true;
            }
            finally
            {
                pin.Free();
            }
        }

        /// <summary>
        /// The reference's <c>find</c>: a signature must be unique. Two matches mean this
        /// nvapi64 is not the one the rest of the checks were measured on, so nothing is
        /// dispatched at all.
        /// </summary>
        private static int Find(byte[] code, byte[] pattern, string what, out string? reason)
        {
            reason = null;
            int found = -1;

            for (int i = 0; i + pattern.Length <= code.Length; i++)
            {
                if (!code.AsSpan(i, pattern.Length).SequenceEqual(pattern))
                    continue;

                if (found >= 0)
                {
                    reason = $"the {what} signature is ambiguous in this nvapi64";
                    return -1;
                }

                found = i;
            }

            if (found < 0)
            {
                reason = $"the {what} signature was not found in this nvapi64";
                return -1;
            }

            return found;
        }

        private static uint Read32(byte[] buffer, int offset) =>
            BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(offset, 4));

        private static void Write32(byte[] buffer, int offset, uint value) =>
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset, 4), value);

        /// <summary>
        /// The dispatch entry and ABI size resolved from this operation's nvapi64: they are a
        /// pointer into the image that answered, so they die with the session that loaded it.
        /// </summary>
        private readonly record struct F7Transport(IntPtr Dispatch, uint NativeSize);
    }

    /// <summary>
    /// The production transport: <c>nvapi64.dll</c>'s private entry points, the function
    /// bodies <c>RtlLookupFunctionEntry</c> finds for them, and the dispatch delegate.
    /// </summary>
    public sealed class NvApiF7Calls : INvApiF7Calls
    {
        /// <summary>One nvapi64 RUNTIME_FUNCTION entry.</summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct RuntimeFunction
        {
            public uint BeginAddress;
            public uint EndAddress;
            public uint UnwindInfoAddress;
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int DispatchCall(uint method, IntPtr buffer, uint size, ulong reserved, byte flag, byte zero, uint zero2);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int GpuCall(IntPtr gpu, IntPtr buffer);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int PlainCall(IntPtr buffer);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int InitializeExCall(uint flag);

        private readonly NvApiPowerCalls _gpu = new();

        /// <summary>
        /// The same nvapi64 load the GPU handle came from: the entry points and the code
        /// pages the F7 checks read are one image, alive for this operation only.
        /// </summary>
        public IDisposable Open() => _gpu.Open();

        public bool TryQuery(uint id, out IntPtr entry) => _gpu.TryQuery(id, out entry);

        public bool TryGetGpu(out IntPtr gpu, out string? reason) => _gpu.TryGetGpu(out gpu, out reason);

        public bool TryBody(IntPtr entry, out byte[] code, out ulong image, out string? reason)
        {
            code = Array.Empty<byte>();
            image = 0;
            reason = null;

            if (entry == IntPtr.Zero)
            {
                reason = "the F7 transport entry point is null";
                return false;
            }

            ulong pc = unchecked((ulong)entry.ToInt64());
            IntPtr found = RtlLookupFunctionEntry(pc, out image, IntPtr.Zero);
            if (found == IntPtr.Zero)
            {
                reason = "the F7 transport is not a function entry in this nvapi64";
                return false;
            }

            try
            {
                RuntimeFunction function = Marshal.PtrToStructure<RuntimeFunction>(found);
                if (image + function.BeginAddress != pc)
                {
                    reason = "the F7 entry point is not the start of a function in this nvapi64";
                    return false;
                }

                long size = (long)function.EndAddress - function.BeginAddress;
                if (size <= 0 || size > 0x4000)
                {
                    reason = $"the F7 function body is not plausible ({size} bytes)";
                    return false;
                }

                code = new byte[size];
                Marshal.Copy(entry, code, 0, (int)size);
                return true;
            }
            catch (Exception ex)
            {
                // Reading foreign code pages is the one place a malformed function table
                // can surface as a managed exception: it stays a refusal.
                reason = $"the F7 function body could not be read: {ex.Message}";
                return false;
            }
        }

        public bool TryGpuCall(IntPtr entry, IntPtr gpu, IntPtr buffer, out int status, out string? reason)
        {
            status = 0;
            reason = null;

            try
            {
                status = Marshal.GetDelegateForFunctionPointer<GpuCall>(entry)(gpu, buffer);
                return true;
            }
            catch (Exception ex) when (ex is ArgumentException or MarshalDirectiveException)
            {
                reason = "the NVAPI entry point does not take a (GPU, buffer) call";
                return false;
            }
        }

        public bool TryPlainCall(IntPtr entry, IntPtr buffer, out int status, out string? reason)
        {
            status = 0;
            reason = null;

            try
            {
                status = Marshal.GetDelegateForFunctionPointer<PlainCall>(entry)(buffer);
                return true;
            }
            catch (Exception ex) when (ex is ArgumentException or MarshalDirectiveException)
            {
                reason = "the NVAPI entry point does not take a single-buffer call";
                return false;
            }
        }

        public bool TryInitializeEx(IntPtr entry, uint flag, out int status, out string? reason)
        {
            status = 0;
            reason = null;

            try
            {
                status = Marshal.GetDelegateForFunctionPointer<InitializeExCall>(entry)(flag);
                return true;
            }
            catch (Exception ex) when (ex is ArgumentException or MarshalDirectiveException)
            {
                reason = "the NVAPI initializer does not take a flag";
                return false;
            }
        }

        public bool TryDispatch(IntPtr target, uint method, IntPtr buffer, uint size, out int status, out string? reason)
        {
            status = 0;
            reason = null;

            try
            {
                // The trailing (0, 1, 0, 0) is the reference's dispatch packet, measured.
                status = Marshal.GetDelegateForFunctionPointer<DispatchCall>(target)(method, buffer, size, 0, 1, 0, 0);
                return true;
            }
            catch (Exception ex) when (ex is ArgumentException or MarshalDirectiveException)
            {
                reason = "the F7 dispatch target is not callable";
                return false;
            }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr RtlLookupFunctionEntry(ulong controlPc, out ulong imageBase, IntPtr historyTable);
    }
}
