using NvAPIWrapper.GPU;
using NvAPIWrapper.Native;
using NvAPIWrapper.Native.GPU;
using NvAPIWrapper.Native.GPU.Structures;
using NvAPIWrapper.Native.Interfaces.GPU;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Universal_x86_Tuning_Utility.Scripts.Misc;
using static NvAPIWrapper.Native.GPU.Structures.PerformanceStates20InfoV1;

namespace Universal_x86_Tuning_Utility.Scripts.GPUs.NVIDIA
{
    internal class NvTuning
    {
        public const int MinCoreOffset = -900;
        public const int MinMemoryOffset = -900;
        public const int MaxCoreOffset = 4000;
        public const int MaxMemoryOffset = 4000;
        public const int MinClockLimit = 400;
        public const int MaxClockLimit = 4000;

        private const int UsableVfPointCount = 127;
        private const int VoltageStepMv = 25;
        private const int RampStartMv = 725;
        private const int VfToleranceMHz = 35;

        public readonly record struct PowerLimitInfo(int MinWatts, int CurrentWatts, int DefaultWatts, int MaxWatts);

        public readonly record struct ClockInfo(
            int CurrentCoreMHz,
            int CurrentMemoryMHz,
            int DefaultCoreMHz,
            int DefaultMemoryMHz,
            int MaxCoreMHz,
            int MaxMemoryMHz,
            int PState
        );

        public readonly record struct GpuInfo(
            string Name,
            int PState,
            int CurrentCoreMHz,
            int CurrentMemoryMHz,
            int DefaultCoreMHz,
            int DefaultMemoryMHz,
            int MaxCoreMHz,
            int MaxMemoryMHz,
            int CurrentPowerWatts,
            int DefaultPowerWatts,
            int MinPowerWatts,
            int MaxPowerWatts,
            int MaxGpuClockLockMHz
        );

        public readonly record struct VfPoint(int Index, int VoltageMv, int FrequencyMHz);

        public readonly record struct VfProbeResult(
            bool Success,
            int PointIndex,
            int BeforeVoltageMv,
            int BeforeFrequencyMHz,
            int AfterVoltageMv,
            int AfterFrequencyMHz,
            int RequestedFrequencyDeltaMHz,
            int RequestedVoltageDeltaMv
        );

        public static int SetClocks(int core, int memory, int voltage = 0)
        {
            if (core < MinCoreOffset || core > MaxCoreOffset ||
                memory < MinMemoryOffset || memory > MaxMemoryOffset)
                return 0;

            try
            {
                PhysicalGPU internalGpu = PhysicalGPU.GetPhysicalGPUs().FirstOrDefault();
                if (internalGpu == null) return -1;

                var coreClock = new PerformanceStates20ClockEntryV1(
                    PublicClockDomain.Graphics,
                    new PerformanceStates20ParameterDelta(core * 1000)
                );

                var memoryClock = new PerformanceStates20ClockEntryV1(
                    PublicClockDomain.Memory,
                    new PerformanceStates20ParameterDelta(memory * 1000)
                );

                var clocks = new[] { coreClock, memoryClock };
                var voltages = Array.Empty<PerformanceStates20BaseVoltageEntryV1>();

                var performanceStates = new[]
                {
                    new PerformanceState20(
                        PerformanceStateId.P0_3DPerformance,
                        clocks,
                        voltages
                    )
                };

                var overclock = new PerformanceStates20InfoV1(performanceStates, 2, 0);
                GPUApi.SetPerformanceStates20(internalGpu.Handle, overclock);

                return 1;
            }
            catch
            {
                return -1;
            }
        }

        public static int SetPowerLimit(int totalWatts, int dbOffset = 0)
        {
            try
            {
                if (totalWatts < 5 || totalWatts > 175)
                {
                    DiagnosticLogger.LogInfo($"SetPowerLimit({totalWatts}W, db={dbOffset}W): out of range 5-175W, skipped");
                    return 0;
                }

                // Laptop GPUs don't support nvmlDeviceSetPowerManagementLimit (returns NOT_SUPPORTED).
                // Use EC register writes directly (same method as tuxedo-drivers on Linux).
                // The EC DB offset register (0x0746) accepts 0-25W to add on top of base TGP.
                var ec = RyzenAdj_To_UXTU.EcService;
                if (ec == null)
                {
                    DiagnosticLogger.LogInfo($"SetPowerLimit({totalWatts}W, db={dbOffset}W): EC service unavailable");
                    return -1;
                }

                // GPU power control on these laptops works through EC registers only.
                // NVML returns NOT_SUPPORTED on laptops, and NvAPI has no GPU power
                // control functions (GPUInfoDLL.dll from XMG CC uses only read-only NvAPI).
                // The EC register sequence (from tuxedo-drivers on Linux):
                //   0x0743 = enable bits (GENERAL|DB|CTGP)
                //   0x0744 = cTGP offset
                //   0x0745 = TPP offset
                //   0x0746 = Dynamic Boost offset
                ec.ActivateGpuPowerControl(dbOffset);

                return 1;
            }
            catch (Exception ex)
            {
                DiagnosticLogger.LogInfo($"SetPowerLimit({totalWatts}W, db={dbOffset}W): EXCEPTION {ex.GetType().Name}: {ex.Message}");
                return -1;
            }
        }

        public static int ResetPowerLimitToDefault()
        {
            return TryGetPowerLimitInfo(out PowerLimitInfo info)
                ? SetPowerLimit(info.DefaultWatts)
                : -1;
        }

        public static bool TryGetPowerLimitInfo(out PowerLimitInfo info)
        {
            info = default;

            try
            {
                if (!TryGetNvmlGpu(out IntPtr gpu))
                {
                    DiagnosticLogger.LogInfo("TryGetPowerLimitInfo: NVML GPU handle failed");
                    return false;
                }

                int range = Nvml.nvmlDeviceGetPowerManagementLimitConstraints(gpu, out uint minMw, out uint maxMw);
                DiagnosticLogger.LogInfo($"TryGetPowerLimitInfo: constraints={range} (min={minMw}mW max={maxMw}mW)");

                // nvmlDeviceGetPowerManagementLimit returns NOT_SUPPORTED (3) on many laptop GPUs.
                // Fall back to the default value if current is unavailable.
                int current = Nvml.nvmlDeviceGetPowerManagementLimit(gpu, out uint currentMw);
                DiagnosticLogger.LogInfo($"TryGetPowerLimitInfo: current={current} ({currentMw}mW)");

                int def = Nvml.nvmlDeviceGetPowerManagementDefaultLimit(gpu, out uint defaultMw);
                DiagnosticLogger.LogInfo($"TryGetPowerLimitInfo: default={def} ({defaultMw}mW)");

                // Constraints and default are required; current is optional (laptops often return NOT_SUPPORTED).
                if (range != Nvml.NVML_SUCCESS || def != Nvml.NVML_SUCCESS)
                {
                    DiagnosticLogger.LogInfo($"TryGetPowerLimitInfo: FAILED range={range} def={def}");
                    return false;
                }

                // Use default as fallback for current if NOT_SUPPORTED.
                if (current != Nvml.NVML_SUCCESS)
                {
                    currentMw = defaultMw;
                    DiagnosticLogger.LogInfo($"TryGetPowerLimitInfo: current not supported, falling back to default ({defaultMw}mW)");
                }

                info = new PowerLimitInfo(
                    (int)(minMw / 1000),
                    (int)(currentMw / 1000),
                    (int)(defaultMw / 1000),
                    (int)(maxMw / 1000)
                );

                return true;
            }
            catch (Exception ex)
            {
                DiagnosticLogger.LogInfo($"TryGetPowerLimitInfo: EXCEPTION {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        public static bool TryGetClocks(out ClockInfo info)
        {
            info = default;

            try
            {
                if (!TryGetNvmlGpu(out IntPtr gpu)) return false;

                uint currentCore = 0, currentMemory = 0, defaultCore = 0, defaultMemory = 0;
                uint maxCore = 0, maxMemory = 0, pState = 0;

                Nvml.nvmlDeviceGetClockInfo(gpu, NvmlClockType.Graphics, out currentCore);
                Nvml.nvmlDeviceGetClockInfo(gpu, NvmlClockType.Memory, out currentMemory);

                Nvml.nvmlDeviceGetDefaultApplicationsClock(gpu, NvmlClockType.Graphics, out defaultCore);
                Nvml.nvmlDeviceGetDefaultApplicationsClock(gpu, NvmlClockType.Memory, out defaultMemory);

                Nvml.nvmlDeviceGetMaxClockInfo(gpu, NvmlClockType.Graphics, out maxCore);
                Nvml.nvmlDeviceGetMaxClockInfo(gpu, NvmlClockType.Memory, out maxMemory);

                Nvml.nvmlDeviceGetPerformanceState(gpu, out pState);

                info = new ClockInfo(
                    (int)currentCore,
                    (int)currentMemory,
                    (int)defaultCore,
                    (int)defaultMemory,
                    (int)maxCore,
                    (int)maxMemory,
                    (int)pState
                );

                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Reads the GPU clock offsets (core and memory) from NVML application clocks.
        /// On many GPUs, Afterburner sets NVML application clocks as well as NVAPI
        /// performance states. The offset is appClock - maxClock.
        /// Returns offsets in MHz.
        /// </summary>
        public static bool TryGetClockOffsets(out int coreOffset, out int memOffset)
        {
            coreOffset = 0;
            memOffset = 0;

            try
            {
                if (!TryGetNvmlGpu(out IntPtr gpu)) return false;

                uint appCore = 0, appMem = 0;
                uint maxCore = 0, maxMem = 0;

                int r1 = Nvml.nvmlDeviceGetDefaultApplicationsClock(gpu, NvmlClockType.Graphics, out appCore);
                int r2 = Nvml.nvmlDeviceGetDefaultApplicationsClock(gpu, NvmlClockType.Memory, out appMem);
                int r3 = Nvml.nvmlDeviceGetMaxClockInfo(gpu, NvmlClockType.Graphics, out maxCore);
                int r4 = Nvml.nvmlDeviceGetMaxClockInfo(gpu, NvmlClockType.Memory, out maxMem);

                if (r1 == 0 && r2 == 0 && r3 == 0 && r4 == 0)
                {
                    // Only return true if app clocks differ from max (i.e., an offset is set).
                    // When no overclock is applied, appClock == maxClock so offset is 0.
                    coreOffset = (int)(appCore - maxCore);
                    memOffset = (int)(appMem - maxMem);
                    return true;
                }
            }
            catch
            {
            }

            return false;
        }

        public static bool TryGetGpuInfo(out GpuInfo info)
        {
            info = default;

            bool powerOk = TryGetPowerLimitInfo(out PowerLimitInfo power);
            bool clocksOk = TryGetClocks(out ClockInfo clocks);

            if (!powerOk && !clocksOk) return false;

            info = new GpuInfo(
                GetGpuName(),
                clocks.PState,
                clocks.CurrentCoreMHz,
                clocks.CurrentMemoryMHz,
                clocks.DefaultCoreMHz,
                clocks.DefaultMemoryMHz,
                clocks.MaxCoreMHz,
                clocks.MaxMemoryMHz,
                power.CurrentWatts,
                power.DefaultWatts,
                power.MinWatts,
                power.MaxWatts,
                GetMaxGPUCLock()
            );

            return true;
        }

        public static string GetGpuName()
        {
            try
            {
                if (!TryGetNvmlGpu(out IntPtr gpu)) return string.Empty;

                var name = new StringBuilder(96);

                return Nvml.nvmlDeviceGetName(gpu, name, (uint)name.Capacity) == Nvml.NVML_SUCCESS
                    ? name.ToString()
                    : string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        public static int SetMaxGPUClock(int clock)
        {
            if (clock < MinClockLimit || clock >= MaxClockLimit)
                clock = 0;

            int currentLimit = GetMaxGPUCLock();

            if (currentLimit == clock)
                return 0;

            try
            {
                if (!TryGetNvmlGpu(out IntPtr gpu)) return -1;

                int result = clock > 0
                    ? Nvml.nvmlDeviceSetGpuLockedClocks(gpu, 0, (uint)clock)
                    : Nvml.nvmlDeviceResetGpuLockedClocks(gpu);

                return result == Nvml.NVML_SUCCESS ? 1 : -1;
            }
            catch
            {
                return -1;
            }
        }

        public static int ResetMaxGPUClock()
        {
            return SetMaxGPUClock(0);
        }

        public static int GetMaxGPUCLock()
        {
            try
            {
                PhysicalGPU internalGpu = PhysicalGPU.GetPhysicalGPUs().FirstOrDefault();
                if (internalGpu == null) return -1;

                PrivateClockBoostLockV2 data = GPUApi.GetClockBoostLock(internalGpu.Handle);

                return (int)data.ClockBoostLocks[0].VoltageInMicroV / 1000;
            }
            catch
            {
                return -1;
            }
        }

        public static bool TryGetVfCurve(out List<VfPoint> points)
        {
            points = new List<VfPoint>();

            try
            {
                if (!NvApiPrivate.TryGetFirstGpuHandle(out IntPtr gpu)) return false;

                IntPtr getStatusPtr = NvApiPrivate.QueryInterface(0x21537AD4);
                if (getStatusPtr == IntPtr.Zero) return false;

                var getStatus = Marshal.GetDelegateForFunctionPointer<NvApiPrivate.GpuBufferDelegate>(getStatusPtr);

                const int statusSize = 0x1C28;
                const int statusVersion = (1 << 16) | statusSize;
                const int statusMaskOffset = 0x04;
                const int statusNumClocksOffset = 0x14;
                const int statusEntriesOffset = 0x48;
                const int statusEntryStride = 0x1C;

                byte[] buffer = new byte[statusSize];

                BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(0, 4), statusVersion);

                for (int i = 0; i < 16; i++)
                    buffer[statusMaskOffset + i] = 0xFF;

                BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(statusNumClocksOffset, 4), 15);

                int status = getStatus(gpu, buffer);
                if (status != 0) return false;

                for (int i = 0; i < UsableVfPointCount; i++)
                {
                    int offset = statusEntriesOffset + i * statusEntryStride;

                    uint frequencyKhz = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(offset, 4));
                    uint voltageUv = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(offset + 4, 4));

                    if (frequencyKhz > 0 && voltageUv > 0)
                    {
                        points.Add(new VfPoint(
                            i,
                            (int)(voltageUv / 1000),
                            (int)(frequencyKhz / 1000)
                        ));
                    }
                }

                return points.Count > 0;
            }
            catch
            {
                return false;
            }
        }

        public static int ResetVfCurveOffsets()
        {
            try
            {
                if (!TryGetVfCurve(out List<VfPoint> curve)) return -1;

                int ok = 0;
                int fail = 0;

                foreach (VfPoint point in curve.Where(p => p.Index >= 0 && p.Index < UsableVfPointCount))
                {
                    if (SetVfPointControl(point.Index, 0, 0)) ok++;
                    else fail++;
                }

                if (ok == 0) return -1;
                return fail == 0 ? 1 : 0;
            }
            catch
            {
                return -1;
            }
        }

        public static int SetUndervoltCurveFromDefault(int requestedMaxVoltageMv, int requestedMaxClockMhz)
        {
            if (requestedMaxVoltageMv < 600 || requestedMaxVoltageMv > 1300) return 0;
            if (requestedMaxClockMhz < 300 || requestedMaxClockMhz > 4500) return 0;

            try
            {
                if (!TryGetVfCurve(out List<VfPoint> defaultCurve)) return -1;

                List<VfPoint> usable = defaultCurve
                    .Where(p => p.Index >= 0 && p.Index < UsableVfPointCount)
                    .OrderBy(p => p.VoltageMv)
                    .ThenBy(p => p.Index)
                    .ToList();

                if (usable.Count == 0) return -1;

                int alignedMaxVoltageMv = AlignToSupportedVoltage(usable, requestedMaxVoltageMv);

                VfPoint pivot = usable
                    .OrderBy(p => Math.Abs(p.VoltageMv - alignedMaxVoltageMv))
                    .ThenBy(p => Math.Abs(p.FrequencyMHz - requestedMaxClockMhz))
                    .First();

                VfPoint rampStart = usable
                    .Where(p => p.VoltageMv <= RampStartMv)
                    .OrderByDescending(p => p.VoltageMv)
                    .FirstOrDefault();

                if (rampStart.Equals(default(VfPoint)))
                    rampStart = usable.First();

                int ok = 0;
                int fail = 0;

                foreach (VfPoint point in usable)
                {
                    CalculateDesiredPoint(
                        point,
                        rampStart,
                        pivot,
                        alignedMaxVoltageMv,
                        requestedMaxClockMhz,
                        out int desiredVoltageMv,
                        out int desiredFrequencyMhz
                    );

                    int frequencyOffsetKhz = (desiredFrequencyMhz - point.FrequencyMHz) * 1000;
                    int voltageOffsetUv = (desiredVoltageMv - point.VoltageMv) * 1000;

                    if (SetVfPointControl(point.Index, frequencyOffsetKhz, voltageOffsetUv))
                        ok++;
                    else
                        fail++;
                }

                if (ok == 0) return -1;
                if (fail > 0) return 0;

                return VerifyUndervoltCurve(alignedMaxVoltageMv, requestedMaxClockMhz) ? 1 : 0;
            }
            catch
            {
                return -1;
            }
        }

        public static bool ProbeVfPointWrite(
            int pointIndex,
            int frequencyDeltaMhz,
            int voltageDeltaMv,
            out VfProbeResult result,
            bool restoreAfterProbe = true
        )
        {
            result = default;

            try
            {
                if (pointIndex < 0 || pointIndex >= UsableVfPointCount) return false;
                if (!TryGetVfCurve(out List<VfPoint> beforeCurve)) return false;

                VfPoint before = beforeCurve.FirstOrDefault(p => p.Index == pointIndex);
                if (before.Equals(default(VfPoint))) return false;

                bool writeOk = SetVfPointControl(
                    pointIndex,
                    frequencyDeltaMhz * 1000,
                    voltageDeltaMv * 1000
                );

                if (!writeOk) return false;

                if (!TryGetVfCurve(out List<VfPoint> afterCurve)) return false;

                VfPoint after = afterCurve.FirstOrDefault(p => p.Index == pointIndex);
                if (after.Equals(default(VfPoint))) return false;

                if (restoreAfterProbe)
                    SetVfPointControl(pointIndex, 0, 0);

                result = new VfProbeResult(
                    true,
                    pointIndex,
                    before.VoltageMv,
                    before.FrequencyMHz,
                    after.VoltageMv,
                    after.FrequencyMHz,
                    frequencyDeltaMhz,
                    voltageDeltaMv
                );

                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void CalculateDesiredPoint(
            VfPoint point,
            VfPoint rampStart,
            VfPoint pivot,
            int alignedMaxVoltageMv,
            int requestedMaxClockMhz,
            out int desiredVoltageMv,
            out int desiredFrequencyMhz
        )
        {
            if (point.VoltageMv <= rampStart.VoltageMv)
            {
                desiredVoltageMv = point.VoltageMv;
                desiredFrequencyMhz = point.FrequencyMHz;
                return;
            }

            if (point.VoltageMv < pivot.VoltageMv)
            {
                double denominator = Math.Max(1, pivot.VoltageMv - rampStart.VoltageMv);
                double progress = (point.VoltageMv - rampStart.VoltageMv) / denominator;

                desiredVoltageMv = AlignTo25Mv(
                    (int)Math.Round(
                        rampStart.VoltageMv +
                        ((alignedMaxVoltageMv - rampStart.VoltageMv) * progress)
                    )
                );

                desiredFrequencyMhz = (int)Math.Round(
                    rampStart.FrequencyMHz +
                    ((requestedMaxClockMhz - rampStart.FrequencyMHz) * progress)
                );

                return;
            }

            desiredVoltageMv = alignedMaxVoltageMv;
            desiredFrequencyMhz = requestedMaxClockMhz;
        }

        private static int AlignTo25Mv(int voltageMv)
        {
            return (int)Math.Round(voltageMv / (double)VoltageStepMv) * VoltageStepMv;
        }

        private static int AlignToSupportedVoltage(List<VfPoint> points, int requestedVoltageMv)
        {
            int aligned = AlignTo25Mv(requestedVoltageMv);

            return points
                .OrderBy(p => Math.Abs(p.VoltageMv - aligned))
                .ThenBy(p => Math.Abs(p.VoltageMv - requestedVoltageMv))
                .First()
                .VoltageMv;
        }

        private static bool VerifyUndervoltCurve(int maxVoltageMv, int targetClockMhz)
        {
            if (!TryGetVfCurve(out List<VfPoint> verified)) return false;

            List<VfPoint> upper = verified
                .Where(p => p.Index >= 0 && p.Index < UsableVfPointCount && p.VoltageMv >= maxVoltageMv)
                .OrderBy(p => p.VoltageMv)
                .ThenBy(p => p.Index)
                .ToList();

            if (upper.Count == 0) return false;

            int matchingClock = upper.Count(p => Math.Abs(p.FrequencyMHz - targetClockMhz) <= VfToleranceMHz);
            int matchingVoltage = upper.Count(p => Math.Abs(p.VoltageMv - maxVoltageMv) <= VoltageStepMv);

            return matchingClock >= Math.Max(1, upper.Count / 2) &&
                   matchingVoltage >= Math.Max(1, upper.Count / 2);
        }

        private static bool SetVfPointControl(int pointIndex, int frequencyOffsetKhz, int voltageOffsetUv)
        {
            try
            {
                if (pointIndex < 0 || pointIndex >= UsableVfPointCount) return false;
                if (!NvApiPrivate.TryGetFirstGpuHandle(out IntPtr gpu)) return false;

                IntPtr setControlPtr = NvApiPrivate.QueryInterface(0x0733E009);
                if (setControlPtr == IntPtr.Zero) return false;

                var setControl = Marshal.GetDelegateForFunctionPointer<NvApiPrivate.GpuBufferDelegate>(setControlPtr);

                const int controlSize = 0x2420;
                const int controlVersion = (1 << 16) | controlSize;
                const int controlMaskOffset = 0x04;
                const int controlEntriesOffset = 0x20;
                const int controlEntryStride = 0x48;

                byte[] buffer = new byte[controlSize];

                BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(0, 4), controlVersion);

                buffer[controlMaskOffset + pointIndex / 8] = (byte)(1 << (pointIndex % 8));

                int entryOffset = controlEntriesOffset + pointIndex * controlEntryStride;

                BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(entryOffset + 0, 4), frequencyOffsetKhz);
                BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(entryOffset + 4, 4), voltageOffsetUv);

                return setControl(gpu, buffer) == 0;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryGetNvmlGpu(out IntPtr gpu)
        {
            gpu = IntPtr.Zero;

            if (!Nvml.EnsureInitialised()) return false;

            return Nvml.nvmlDeviceGetHandleByIndex_v2(0, out gpu) == Nvml.NVML_SUCCESS;
        }
    }

    internal enum NvmlClockType : uint
    {
        Graphics = 0,
        SM = 1,
        Memory = 2,
        Video = 3
    }

    internal static class Nvml
    {
        private const string Dll = "nvml.dll";

        public const int NVML_SUCCESS = 0;

        private static bool _initAttempted;
        private static bool _initialised;

        public static bool EnsureInitialised()
        {
            if (_initAttempted) return _initialised;

            _initAttempted = true;
            _initialised = nvmlInit_v2() == NVML_SUCCESS;

            return _initialised;
        }

        [DllImport(Dll)] public static extern int nvmlInit_v2();
        [DllImport(Dll)] public static extern int nvmlShutdown();
        [DllImport(Dll)] public static extern int nvmlDeviceGetHandleByIndex_v2(uint index, out IntPtr device);

        [DllImport(Dll, CharSet = CharSet.Ansi)]
        public static extern int nvmlDeviceGetName(IntPtr device, StringBuilder name, uint length);

        [DllImport(Dll)] public static extern int nvmlDeviceGetPerformanceState(IntPtr device, out uint pState);
        [DllImport(Dll)] public static extern int nvmlDeviceGetClockInfo(IntPtr device, NvmlClockType type, out uint clockMHz);
        [DllImport(Dll)] public static extern int nvmlDeviceGetMaxClockInfo(IntPtr device, NvmlClockType type, out uint clockMHz);
        [DllImport(Dll)] public static extern int nvmlDeviceGetDefaultApplicationsClock(IntPtr device, NvmlClockType type, out uint clockMHz);
        [DllImport(Dll)] public static extern int nvmlDeviceGetPowerManagementLimit(IntPtr device, out uint limitMilliwatts);
        [DllImport(Dll)] public static extern int nvmlDeviceGetPowerManagementDefaultLimit(IntPtr device, out uint limitMilliwatts);

        [DllImport(Dll)]
        public static extern int nvmlDeviceGetPowerManagementLimitConstraints(
            IntPtr device,
            out uint minMilliwatts,
            out uint maxMilliwatts
        );

        [DllImport(Dll)] public static extern int nvmlDeviceSetPowerManagementLimit(IntPtr device, uint limitMilliwatts);
        [DllImport(Dll)] public static extern int nvmlDeviceSetGpuLockedClocks(IntPtr device, uint minGpuClockMHz, uint maxGpuClockMHz);
        [DllImport(Dll)] public static extern int nvmlDeviceResetGpuLockedClocks(IntPtr device);
    }

    internal static class NvApiPrivate
    {
        [DllImport("nvapi64.dll", EntryPoint = "nvapi_QueryInterface", CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr QueryInterface(uint functionId);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int NvApiInitializeDelegate();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int NvApiEnumPhysicalGpusDelegate([Out] IntPtr[] gpuHandles, out int gpuCount);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate int GpuBufferDelegate(IntPtr gpuHandle, [In, Out] byte[] buffer);

        public static bool TryGetFirstGpuHandle(out IntPtr gpu)
        {
            gpu = IntPtr.Zero;

            IntPtr initPtr = QueryInterface(0x0150E828);
            IntPtr enumPtr = QueryInterface(0xE5AC921F);

            if (initPtr == IntPtr.Zero || enumPtr == IntPtr.Zero) return false;

            var init = Marshal.GetDelegateForFunctionPointer<NvApiInitializeDelegate>(initPtr);
            var enumGpus = Marshal.GetDelegateForFunctionPointer<NvApiEnumPhysicalGpusDelegate>(enumPtr);

            init();

            IntPtr[] handles = new IntPtr[64];
            int status = enumGpus(handles, out int count);

            if (status != 0 || count <= 0) return false;

            gpu = handles[0];
            return true;
        }
    }
}