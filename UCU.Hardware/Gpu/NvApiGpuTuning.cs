using System.Buffers.Binary;
using System.Runtime.InteropServices;
using NvAPIWrapper.GPU;
using NvAPIWrapper.Native;
using NvAPIWrapper.Native.Exceptions;
using NvAPIWrapper.Native.GPU;
using NvAPIWrapper.Native.GPU.Structures;
using Serilog;
using UCU.Contracts;
using static NvAPIWrapper.Native.GPU.Structures.PerformanceStates20InfoV1;

namespace UCU.Hardware.Gpu
{
    /// <summary>
    /// The seam the engine's gpu.* verbs run on: the production implementation
    /// performs the NVAPI writes as LocalSystem; the RPC tests inject a scripted
    /// answer, because a real clock/voltage write in a test process is exactly
    /// what no test may do.
    /// </summary>
    public interface IGpuTuningService
    {
        GpuTuningResultDto ApplyVfCurveOffsets(IReadOnlyDictionary<int, int> offsetsMhz, int memoryOffsetMhz);

        GpuTuningResultDto SetDrsPreference(DrsGpuPreference preference);

        GpuTuningResultDto SetClocks(int coreOffsetMhz, int memoryOffsetMhz);
    }

    /// <summary>
    /// NVAPI tuning writes (NvAPI_GPU_SetClockBoostTable 0x0733E009, the V-F curve
    /// apply; NvAPI_GPU_SetPreferredGPU 0x8B34C9A2, the DRS GPU preference; and the
    /// pstate clock offsets through the managed wrapper). All three are the interop
    /// the app already ships, moved behind the pipe: NVAPI refuses them to an
    /// unelevated token with NVAPI_INVALID_USER_PRIVILEGE (0xFFFFFF77), so the
    /// asInvoker app's writes were refused outright (field log; the clock sliders
    /// throw it on every call while the driver's read-only line never appears). The
    /// engine's token is LocalSystem, where the same calls are accepted.
    ///
    /// Buffer layout is pinned by the working app-side path
    /// (Scripts/GPUs/NVIDIA/NvTuning.cs), so the two cannot drift: version word
    /// (1 &lt;&lt; 16) | size, the 16-byte point mask at 0x04, unknown1 at
    /// 0x14..0x23 (not mask), entries from 0x44 with a 0x24 stride and the
    /// frequency delta in kHz at +0x14. Buffer construction is a pure function so
    /// a test can assert the bytes without a driver.
    ///
    /// Never throws: a refused write is an answer. The raw NVAPI status rides the
    /// result DTO, which is how the field's 0xFFFFFF77 shows up in the engine log.
    /// </summary>
    public sealed class NvApiGpuTuning : IGpuTuningService
    {
        internal const int ClockBoostTableSize = 0x2420;
        internal const int ClockBoostEntryBase = 0x44;
        internal const int ClockBoostEntryStride = 0x24;
        internal const int ClockBoostDeltaOffset = 0x14;
        internal const int ClockBoostMaskOffset = 0x04;

        /// <summary>The mask field is 4 x uint32 (0x04..0x13); 0x14..0x23 is unknown1, not mask.</summary>
        internal const int ClockBoostMaskBytes = 16;

        /// <summary>
        /// Points per read window. Measured 2026-11-16: a 4- or 8-byte mask span is answered
        /// 0x00000000 and echoed back, our old 32-byte 0xFF mask is answered 0xFFFFFFFF (it
        /// also stamped 0x14..0x23, which is unknown1). Reads therefore walk the 127 usable
        /// points in 64-point (8-byte) windows instead of claiming 32.
        /// </summary>
        internal const int ClockBoostMaskWindowPoints = 64;

        /// <summary>The points the boost table can carry; the app's UsableVfPointCount.</summary>
        internal const int UsableVfPointCount = 127;

        internal const int DrsBufferSize = 64;

        /// <summary>The offset sliders' range, the app's Min/Max Core/MemoryOffset.</summary>
        internal const int MinCoreOffset = -900;
        internal const int MaxCoreOffset = 4000;
        internal const int MinMemoryOffset = -900;
        internal const int MaxMemoryOffset = 4000;

        private const int Kilo = 1000;

        public GpuTuningResultDto ApplyVfCurveOffsets(IReadOnlyDictionary<int, int> offsetsMhz, int memoryOffsetMhz)
        {
            try
            {
                if (!TryGetGpuHandle(out IntPtr gpu))
                    return Refused("no NVAPI GPU handle");

                IntPtr setPtr = NvApi.QueryInterface(NvApi.SetClockBoostTableId);
                if (setPtr == IntPtr.Zero)
                    return Refused("NvAPI_GPU_SetClockBoostTable (0x0733E009) is not available");

                var setControl = Marshal.GetDelegateForFunctionPointer<NvApi.GpuBufferDelegate>(setPtr);

                // Clocks first, curve last (measured 2026-11-16): a pstate write AFTER the
                // curve erases it - that is what the old post-curve memory write did - while
                // a curve write leaves an already-applied pstate memory offset in force.
                GpuTuningResultDto? failure = memoryOffsetMhz == 0 ? null : ApplyMemoryOffset(memoryOffsetMhz);

                // Read-modify-write: the driver hands back the fields we do not understand, so
                // they survive the write. A refused read falls back to the from-zero table (the
                // old behaviour) and says so rather than passing it off as preservation.
                byte[]? current = TryReadClockBoostTable(gpu, out byte[] readback) ? readback : null;
                if (current == null)
                    Log.Information("gpu.apply_vf_curve: curve readback (0x23F1B133) refused - writing a from-zero table, fields outside the point deltas are not preserved");

                // Points only: measured 2026-11-16, a memory value inside this buffer (0xB84
                // is the base of GPU point 80, not a memory region) makes the driver refuse
                // the entire call, so the curve never lands. The memory offset is the separate
                // pstate write above.
                byte[] buffer = PrepareClockBoostWriteTable(current, offsetsMhz, out int appliedPoints);
                int status = setControl(gpu, buffer);

                Log.Information(
                    "gpu.apply_vf_curve: setControl(0x0733E009) returned 0x{Status:X8} for {Points} points, memory offset {MemoryOffset} MHz, {Mode}",
                    status, appliedPoints, memoryOffsetMhz, current == null ? "from zero" : "read-modify-write");

                if (status != 0)
                    return new GpuTuningResultDto
                    {
                        Succeeded = false,
                        Status = status,
                        AppliedPoints = 0,
                        Detail = failure == null
                            ? "the driver refused the V-F curve write; no offset was applied"
                            : $"the driver refused the V-F curve write, and {failure.Detail}",
                    };

                // A regression shows up here instead of in the owner's screenshots.
                VerifyCurveSurvived(gpu, offsetsMhz, appliedPoints);

                if (failure != null)
                    return failure with
                    {
                        AppliedPoints = appliedPoints,
                        Detail = $"the V-F curve applied ({appliedPoints} points) but {failure.Detail}",
                    };

                return new GpuTuningResultDto
                {
                    Succeeded = true,
                    Status = 0,
                    AppliedPoints = appliedPoints,
                };
            }
            catch (Exception ex)
            {
                // NVAPI can throw SEHException/AccessViolationException shape faults.
                Log.Information("gpu.apply_vf_curve: {Type} {Message}", ex.GetType().Name, ex.Message);
                return new GpuTuningResultDto { Succeeded = false, Status = -1, Detail = ex.Message };
            }
        }

        public GpuTuningResultDto SetDrsPreference(DrsGpuPreference preference)
        {
            try
            {
                if (!TryGetGpuHandle(out IntPtr gpu))
                    return Refused("no NVAPI GPU handle");

                IntPtr setPtr = NvApi.QueryInterface(NvApi.SetPreferredGpuId);
                if (setPtr == IntPtr.Zero)
                    return Refused("NvAPI_GPU_SetPreferredGPU (0x8B34C9A2) is not available");

                var setPreferred = Marshal.GetDelegateForFunctionPointer<NvApi.GpuBufferDelegate>(setPtr);

                int status = setPreferred(gpu, BuildDrsPreferenceBuffer(preference));

                Log.Information("gpu.set_drs_preference: setPreferred({Preference}) returned 0x{Status:X8}", preference, status);

                return new GpuTuningResultDto { Succeeded = status == 0, Status = status };
            }
            catch (Exception ex)
            {
                Log.Information("gpu.set_drs_preference: {Type} {Message}", ex.GetType().Name, ex.Message);
                return new GpuTuningResultDto { Succeeded = false, Status = -1, Detail = ex.Message };
            }
        }

        public GpuTuningResultDto SetClocks(int coreOffsetMhz, int memoryOffsetMhz)
        {
            if (coreOffsetMhz < MinCoreOffset || coreOffsetMhz > MaxCoreOffset ||
                memoryOffsetMhz < MinMemoryOffset || memoryOffsetMhz > MaxMemoryOffset)
            {
                // The app's sliders carry this range; outside it is a caller bug, not a write.
                Log.Information("gpu.set_clocks: {Core}/{Memory} MHz outside {Min}..{Max}", coreOffsetMhz, memoryOffsetMhz, MinCoreOffset, MaxCoreOffset);
                return Refused($"{coreOffsetMhz}/{memoryOffsetMhz} MHz is outside the {MinCoreOffset}..{MaxCoreOffset} MHz offset range");
            }

            try
            {
                PhysicalGPU? gpu = PhysicalGPU.GetPhysicalGPUs().FirstOrDefault();
                if (gpu == null)
                    return Refused("no NVAPI GPU handle");

                // The driver's own declaration: a success-shaped write after a refused
                // modification is how a page keeps showing offsets the GPU never took.
                if (gpu.PerformanceStatesInfo.IsReadOnly)
                {
                    Log.Information("gpu.set_clocks: the driver reports performance states read-only, offsets not applied");
                    return Refused("the driver reports performance states read-only; the clock offsets were not applied");
                }

                var clocks = new[]
                {
                    new PerformanceStates20ClockEntryV1(
                        PublicClockDomain.Graphics,
                        new PerformanceStates20ParameterDelta(coreOffsetMhz * Kilo)),
                    new PerformanceStates20ClockEntryV1(
                        PublicClockDomain.Memory,
                        new PerformanceStates20ParameterDelta(memoryOffsetMhz * Kilo)),
                };

                var states = new[]
                {
                    new PerformanceState20(
                        PerformanceStateId.P0_3DPerformance,
                        clocks,
                        Array.Empty<PerformanceStates20BaseVoltageEntryV1>()),
                };

                // overclockExpansionVersion 2 is the app's working value for this chassis generation.
                GPUApi.SetPerformanceStates20(gpu.Handle, new PerformanceStates20InfoV1(states, 2, 0));

                Log.Information("gpu.set_clocks: graphics {Core} MHz, memory {Memory} MHz accepted", coreOffsetMhz, memoryOffsetMhz);
                return new GpuTuningResultDto { Succeeded = true, Status = 0 };
            }
            catch (NVIDIAApiException ex)
            {
                Log.Information("gpu.set_clocks: NVIDIAApiException 0x{Status:X8} {Message}", (int)ex.Status, ex.Message);
                return new GpuTuningResultDto { Succeeded = false, Status = (int)ex.Status, Detail = ex.Message };
            }
            catch (Exception ex)
            {
                Log.Information("gpu.set_clocks: {Type} {Message}", ex.GetType().Name, ex.Message);
                return new GpuTuningResultDto { Succeeded = false, Status = -1, Detail = ex.Message };
            }
        }

        /// <summary>
        /// The pstate write that carries the profile's memory offset, ahead of the curve. It must
        /// come first: a pstate write AFTER the curve erases the curve (measured 2026-11-16),
        /// while a curve write leaves an already-applied memory offset in force. Memory only -
        /// the V-F curve owns the core clock. Never throws; a refusal is an answer.
        /// </summary>
        private static GpuTuningResultDto? ApplyMemoryOffset(int memoryOffsetMhz)
        {
            try
            {
                // The wrapper's pstate API takes its own handle type, so this is a second
                // enumeration of the same single GPU rather than a conversion.
                PhysicalGPU? physical = PhysicalGPU.GetPhysicalGPUs().FirstOrDefault();
                if (physical == null)
                    return new GpuTuningResultDto
                    {
                        Succeeded = false,
                        Status = -1,
                        Detail = $"the {memoryOffsetMhz} MHz memory offset could not be written: no NVAPI GPU handle",
                    };

                GPUApi.SetPerformanceStates20(physical.Handle, BuildMemoryClockState(memoryOffsetMhz));
                Log.Information("gpu.apply_vf_curve: memory offset {MemoryOffset} MHz applied before the curve", memoryOffsetMhz);
                return null;
            }
            catch (Exception ex)
            {
                int memoryStatus = ex is NVIDIAApiException api ? (int)api.Status : -1;
                Log.Information("gpu.apply_vf_curve: memory offset {MemoryOffset} MHz refused 0x{Status:X8} {Message}", memoryOffsetMhz, memoryStatus, ex.Message);
                return new GpuTuningResultDto
                {
                    Succeeded = false,
                    Status = memoryStatus,
                    Detail = $"the {memoryOffsetMhz} MHz memory offset was refused: {ex.Message}",
                };
            }
        }

        /// <summary>
        /// The PrivateClockBoostTableV1 buffer for a from-zero write: the point deltas and
        /// nothing else. The delta array starts at 0x44 with a 0x24 stride and extends past 80
        /// entries (all <see cref="UsableVfPointCount"/> points are writable), so 0xB84 is the
        /// base of point 80's entry - not a memory region. The memory region is not mapped in
        /// this buffer: writing a memory value there makes the driver refuse the whole call
        /// (measured 2026-11-16), which is why the offset is a separate pstate write.
        /// Indices outside the usable range are skipped rather than written: the mask bitmap
        /// only spans <see cref="ClockBoostMaskBytes"/> bytes, so a stray index would land in
        /// another field of the struct.
        /// </summary>
        internal static byte[] BuildClockBoostTableBuffer(
            IReadOnlyDictionary<int, int> offsetsMhz,
            out int appliedPoints) =>
            PrepareClockBoostWriteTable(null, offsetsMhz, out appliedPoints);

        /// <summary>
        /// The buffer to write: the driver's current table when the read answered
        /// (<paramref name="current"/>), otherwise a zero one, with this preset's point deltas
        /// applied. Only the mask and the requested deltas are touched, so the version word, the
        /// fields we do not understand and the deltas of points this preset leaves alone are
        /// written back as read.
        /// </summary>
        internal static byte[] PrepareClockBoostWriteTable(
            byte[]? current,
            IReadOnlyDictionary<int, int> offsetsMhz,
            out int appliedPoints)
        {
            byte[] buffer = current ?? new byte[ClockBoostTableSize];

            BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(0, 4), (1 << 16) | ClockBoostTableSize);

            // Only the mask field is rewritten: 0x14..0x23 is unknown1, and a mask that reached
            // into it (the old 32-byte 0xFF) is what the driver refused.
            buffer.AsSpan(ClockBoostMaskOffset, ClockBoostMaskBytes).Clear();

            appliedPoints = 0;
            foreach ((int pointIndex, int deltaMhz) in offsetsMhz)
            {
                if (pointIndex < 0 || pointIndex >= UsableVfPointCount)
                    continue;

                buffer[ClockBoostMaskOffset + (pointIndex / 8)] |= (byte)(1 << (pointIndex % 8));

                int deltaOffset = ClockBoostEntryBase + (pointIndex * ClockBoostEntryStride) + ClockBoostDeltaOffset;
                BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(deltaOffset, 4), deltaMhz * Kilo);

                // A zero delta is still written (it clears a stale point) but is not counted:
                // the readback cannot tell a written zero from a point the driver dropped, and
                // counting them made a working curve log "63/101 … did not survive".
                if (deltaMhz != 0)
                    appliedPoints++;
            }

            return buffer;
        }

        /// <summary>
        /// One read window of the boost table: the version word and mask bits for up to
        /// <see cref="ClockBoostMaskWindowPoints"/> points, never more than 8 mask bytes.
        /// </summary>
        internal static byte[] BuildClockBoostReadTable(int firstPoint)
        {
            byte[] table = new byte[ClockBoostTableSize];
            BinaryPrimitives.WriteInt32LittleEndian(table.AsSpan(0, 4), (1 << 16) | ClockBoostTableSize);

            int lastPoint = Math.Min(firstPoint + ClockBoostMaskWindowPoints, UsableVfPointCount);
            for (int point = Math.Max(0, firstPoint); point < lastPoint; point++)
                table[ClockBoostMaskOffset + (point / 8)] |= (byte)(1 << (point % 8));

            return table;
        }

        /// <summary>
        /// Reads the driver's current boost table (GetClockBoostTable 0x23F1B133) into the full
        /// 0x2420 buffer, walking the usable points in 8-byte mask windows. False when the read
        /// is unavailable or refused - the caller falls back to the from-zero table and says so.
        /// </summary>
        private static bool TryReadClockBoostTable(IntPtr gpu, out byte[] table)
        {
            table = Array.Empty<byte>();

            IntPtr getPtr = NvApi.QueryInterface(NvApi.GetClockBoostTableId);
            if (getPtr == IntPtr.Zero)
                return false;

            var getTable = Marshal.GetDelegateForFunctionPointer<NvApi.GpuBufferDelegate>(getPtr);

            byte[] merged = new byte[ClockBoostTableSize];
            for (int firstPoint = 0; firstPoint < UsableVfPointCount; firstPoint += ClockBoostMaskWindowPoints)
            {
                byte[] read = BuildClockBoostReadTable(firstPoint);
                if (getTable(gpu, read) != 0)
                    return false;

                // Everything but the per-window mask: the version echo, unknown1 and the
                // selected point deltas.
                Array.Copy(read, ClockBoostMaskOffset + ClockBoostMaskBytes, merged, ClockBoostMaskOffset + ClockBoostMaskBytes,
                    ClockBoostTableSize - (ClockBoostMaskOffset + ClockBoostMaskBytes));
            }

            BinaryPrimitives.WriteInt32LittleEndian(merged.AsSpan(0, 4), (1 << 16) | ClockBoostTableSize);
            table = merged;
            return true;
        }


        /// <summary>
        /// The pstate write that carries a memory offset. Only a Memory entry is supplied: the
        /// V-F curve owns the core clock, so this never carries the profile's core offset. (The
        /// wrapper's fixed-slot struct may serialize its zero-valued Graphics slot alongside;
        /// a zero delta is not an offset.) The delta is the same kHz parameter the app's clock
        /// sliders use, so the two paths cannot disagree about the unit.
        /// </summary>
        internal static PerformanceStates20InfoV1 BuildMemoryClockState(int memoryOffsetMhz)
        {
            var clocks = new[]
            {
                new PerformanceStates20ClockEntryV1(
                    PublicClockDomain.Memory,
                    new PerformanceStates20ParameterDelta(memoryOffsetMhz * Kilo)),
            };

            var states = new[]
            {
                new PerformanceState20(
                    PerformanceStateId.P0_3DPerformance,
                    clocks,
                    Array.Empty<PerformanceStates20BaseVoltageEntryV1>()),
            };

            // overclockExpansionVersion 2 is the app's working value for this chassis generation.
            return new PerformanceStates20InfoV1(states, 2, 0);
        }

        /// <summary>
        /// Reads the curve back (GetClockBoostTable 0x23F1B133) with the same accepted mask shape
        /// the write used, and logs how many applied point deltas still read as written. A refused
        /// read or a fault logs "unverified" rather than claiming either way: this is a witness for
        /// the log, never a second verdict that could turn a landed write into a failure.
        /// </summary>
        private static void VerifyCurveSurvived(
            IntPtr gpu,
            IReadOnlyDictionary<int, int> offsetsMhz,
            int appliedPoints)
        {
            try
            {
                if (!TryReadClockBoostTable(gpu, out byte[] table))
                {
                    Log.Information("gpu.apply_vf_curve: curve readback (0x23F1B133) refused - the {Points} applied points are unverified", appliedPoints);
                    return;
                }

                int matching = CountMatchingDeltas(table, offsetsMhz);
                Log.Information(
                    "gpu.apply_vf_curve: curve readback: {Matching}/{Applied} applied points still read as written{Suffix}",
                    matching,
                    appliedPoints,
                    matching == appliedPoints ? string.Empty : " - the curve did not survive the writes");
            }
            catch (Exception ex)
            {
                Log.Information("gpu.apply_vf_curve: curve readback faulted {Type} {Message} - unverified", ex.GetType().Name, ex.Message);
            }
        }

        /// <summary>How many requested point deltas the read-back table still carries, in kHz.</summary>
        internal static int CountMatchingDeltas(byte[] table, IReadOnlyDictionary<int, int> offsetsMhz)
        {
            int matching = 0;
            foreach ((int pointIndex, int deltaMhz) in offsetsMhz)
            {
                if (pointIndex < 0 || pointIndex >= UsableVfPointCount)
                    continue;

                int deltaOffset = ClockBoostEntryBase + (pointIndex * ClockBoostEntryStride) + ClockBoostDeltaOffset;
                if (BinaryPrimitives.ReadInt32LittleEndian(table.AsSpan(deltaOffset, 4)) == deltaMhz * Kilo)
                    matching++;
            }

            return matching;
        }

        /// <summary>The SetPreferredGPU buffer: the GPU type as a uint32 at offset 0, the rest padding.</summary>
        internal static byte[] BuildDrsPreferenceBuffer(DrsGpuPreference preference)
        {
            byte[] buffer = new byte[DrsBufferSize];
            BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(0, 4), (int)preference);
            return buffer;
        }

        private static GpuTuningResultDto Refused(string detail)
        {
            Log.Information("gpu tuning: {Detail}", detail);
            return new GpuTuningResultDto { Succeeded = false, Status = -1, Detail = detail };
        }

        private static bool TryGetGpuHandle(out IntPtr gpu)
        {
            gpu = IntPtr.Zero;

            IntPtr initPtr = NvApi.QueryInterface(NvApi.InitializeId);
            IntPtr enumPtr = NvApi.QueryInterface(NvApi.EnumPhysicalGpusId);
            if (initPtr == IntPtr.Zero || enumPtr == IntPtr.Zero)
                return false;

            Marshal.GetDelegateForFunctionPointer<NvApi.InitializeDelegate>(initPtr)();

            IntPtr[] handles = new IntPtr[64];
            int status = Marshal.GetDelegateForFunctionPointer<NvApi.EnumPhysicalGpusDelegate>(enumPtr)(handles, out int count);
            if (status != 0 || count <= 0)
                return false;

            gpu = handles[0];
            return true;
        }

        private static class NvApi
        {
            internal const uint InitializeId = 0x0150E828;
            internal const uint EnumPhysicalGpusId = 0xE5AC921F;
            internal const uint SetClockBoostTableId = 0x0733E009;
            internal const uint GetClockBoostTableId = 0x23F1B133;
            internal const uint SetPreferredGpuId = 0x8B34C9A2;

            [DllImport("nvapi64.dll", EntryPoint = "nvapi_QueryInterface", CallingConvention = CallingConvention.Cdecl)]
            internal static extern IntPtr QueryInterface(uint functionId);

            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            internal delegate int InitializeDelegate();

            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            internal delegate int EnumPhysicalGpusDelegate([Out] IntPtr[] gpuHandles, out int gpuCount);

            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            internal delegate int GpuBufferDelegate(IntPtr gpuHandle, [In, Out] byte[] buffer);
        }
    }
}
