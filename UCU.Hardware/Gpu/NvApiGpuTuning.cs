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
        GpuTuningResultDto ApplyVfCurveOffsets(IReadOnlyDictionary<int, int> offsetsMhz);

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
    /// (1 &lt;&lt; 16) | size, the 32-byte point mask at 0x04, entries from 0x44 with a
    /// 0x24 stride and the frequency delta in kHz at +0x14. Buffer construction is
    /// a pure function so a test can assert the bytes without a driver.
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

        /// <summary>The points the boost table can carry; the app's UsableVfPointCount.</summary>
        internal const int UsableVfPointCount = 127;

        internal const int DrsBufferSize = 64;

        /// <summary>The offset sliders' range, the app's Min/Max Core/MemoryOffset.</summary>
        internal const int MinCoreOffset = -900;
        internal const int MaxCoreOffset = 4000;
        internal const int MinMemoryOffset = -900;
        internal const int MaxMemoryOffset = 4000;

        private const int Kilo = 1000;

        public GpuTuningResultDto ApplyVfCurveOffsets(IReadOnlyDictionary<int, int> offsetsMhz)
        {
            try
            {
                if (!TryGetGpuHandle(out IntPtr gpu))
                    return Refused("no NVAPI GPU handle");

                IntPtr setPtr = NvApi.QueryInterface(NvApi.SetClockBoostTableId);
                if (setPtr == IntPtr.Zero)
                    return Refused("NvAPI_GPU_SetClockBoostTable (0x0733E009) is not available");

                var setControl = Marshal.GetDelegateForFunctionPointer<NvApi.GpuBufferDelegate>(setPtr);

                byte[] buffer = BuildClockBoostTableBuffer(offsetsMhz, out int appliedPoints);
                int status = setControl(gpu, buffer);

                Log.Information(
                    "gpu.apply_vf_curve: setControl(0x0733E009) returned 0x{Status:X8} for {Points} points",
                    status, appliedPoints);

                return new GpuTuningResultDto
                {
                    Succeeded = status == 0,
                    Status = status,
                    AppliedPoints = status == 0 ? appliedPoints : 0,
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
        /// The PrivateClockBoostTableV1 buffer the setControl interface expects.
        /// Indices outside the usable range are skipped rather than written: the
        /// mask bitmap only spans 32 bytes, so a stray index would land in another
        /// field of the struct.
        /// </summary>
        internal static byte[] BuildClockBoostTableBuffer(IReadOnlyDictionary<int, int> offsetsMhz, out int appliedPoints)
        {
            byte[] buffer = new byte[ClockBoostTableSize];

            BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(0, 4), (1 << 16) | ClockBoostTableSize);

            appliedPoints = 0;
            foreach ((int pointIndex, int deltaMhz) in offsetsMhz)
            {
                if (pointIndex < 0 || pointIndex >= UsableVfPointCount)
                    continue;

                buffer[ClockBoostMaskOffset + (pointIndex / 8)] |= (byte)(1 << (pointIndex % 8));

                int deltaOffset = ClockBoostEntryBase + (pointIndex * ClockBoostEntryStride) + ClockBoostDeltaOffset;
                BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(deltaOffset, 4), deltaMhz * Kilo);

                appliedPoints++;
            }

            return buffer;
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
