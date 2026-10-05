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
using Uniwill_Control_Utility.Scripts.Misc;
using static NvAPIWrapper.Native.GPU.Structures.PerformanceStates20InfoV1;

namespace Uniwill_Control_Utility.Scripts.GPUs.NVIDIA
{
    /// <summary>
    /// Global guard that blocks ALL native GPU interop calls (NVML and NvAPI)
    /// for a cooldown period after a reset. This prevents ExecutionEngineException
    /// when the GPU driver is in a transitional state after display hotplug,
    /// hibernation resume, or KVM switch (which may also disconnect USB devices,
    /// extending the transitional state).
    /// </summary>
    internal static class GpuInteropGuard
    {
        private static DateTime _lastResetUtc = DateTime.MinValue;

        /// <summary>
        /// Cooldown after reset. Extended to 15 seconds because KVM switches
        /// that also handle USB disconnection can keep the GPU driver in a
        /// transitional state longer than display-only hotplug events.
        /// </summary>
        private static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(15);

        /// <summary>Call this proactively after display change, resume, or any event that may invalidate GPU driver state.</summary>
        public static void Reset()
        {
            _lastResetUtc = DateTime.UtcNow;
        }

        /// <summary>Returns true if a native GPU call should be blocked right now.</summary>
        public static bool IsBlocked => DateTime.UtcNow - _lastResetUtc < Cooldown;
    }

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

        // Power limit range (watts)
        private const int MinPowerLimitWatts = 5;
        private const int MaxPowerLimitWattsDefault = 175;
        private const int MaxPowerLimitWattsBoost = 250;
        private const int DefaultPowerLimitFallbackWatts = 80;

        /// <summary>
        /// Returns the effective maximum power limit based on the user's power boost setting.
        /// With boost enabled: 250W. Otherwise: 175W.
        /// </summary>
        public static int MaxPowerLimitWatts =>
            Properties.Settings.Default.EnablePowerBoostMode
                ? MaxPowerLimitWattsBoost
                : MaxPowerLimitWattsDefault;

        // Undervolt curve range limits
        private const int MinUndervoltVoltageMv = 600;
        private const int MaxUndervoltVoltageMv = 1300;
        private const int MinUndervoltClockMhz = 300;
        private const int MaxUndervoltClockMhz = 4500;

        // Unit conversion factors
        private const int Kilo = 1000;

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

            if (GpuInteropGuard.IsBlocked) return 0;

            try
            {
                PhysicalGPU? internalGpu = PhysicalGPU.GetPhysicalGPUs().FirstOrDefault();
                if (internalGpu == null) return -1;

                var coreClock = new PerformanceStates20ClockEntryV1(
                    PublicClockDomain.Graphics,
                    new PerformanceStates20ParameterDelta(core * Kilo)
                );

                var memoryClock = new PerformanceStates20ClockEntryV1(
                    PublicClockDomain.Memory,
                    new PerformanceStates20ParameterDelta(memory * Kilo)
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
            catch (Exception ex)
            {
                // NvAPI may throw SEHException or AccessViolationException on driver errors.
                DiagnosticLogger.LogInfo($"SetClocks: EXCEPTION {ex.GetType().Name}: {ex.Message}");
                return -1;
            }
        }

        public static int SetPowerLimit(int totalWatts, int dbOffset = 0)
        {
            try
            {
                if (totalWatts < MinPowerLimitWatts || totalWatts > MaxPowerLimitWatts)
                {
                    DiagnosticLogger.LogInfo($"SetPowerLimit({totalWatts}W, db={dbOffset}W): out of range {MinPowerLimitWatts}-{MaxPowerLimitWatts}W, skipped");
                    return 0;
                }

                // Laptop GPUs don't support nvmlDeviceSetPowerManagementLimit (returns NOT_SUPPORTED).
                // Use EC register writes directly (same method as tuxedo-drivers on Linux).
                // The EC DB offset register (0x0746) accepts 0-25W to add on top of base TGP.
                var ec = RyzenAdj_To_UCU.EcService;
                if (ec == null)
                {
                    DiagnosticLogger.LogInfo($"SetPowerLimit({totalWatts}W, db={dbOffset}W): EC service unavailable");
                    return -1;
                }

                // Compute cTGP offset from target wattage.
                // Per UCC/tuxedo-drivers: cTGP offset = targetWatts - defaultWatts.
                // The kernel driver writes this value directly to EC register 0x0744.
                int ctgpOffset = 0;
                if (TryGetPowerLimitInfo(out PowerLimitInfo info))
                {
                    ctgpOffset = Math.Clamp(totalWatts - info.DefaultWatts, 0, 255);
                    DiagnosticLogger.LogInfo($"SetPowerLimit: default={info.DefaultWatts}W, target={totalWatts}W ? cTGP offset={ctgpOffset}");
                }
                else
                {
                    // Fallback: assume DefaultPowerLimitFallbackWatts default (matches UCC fallback).
                    ctgpOffset = Math.Clamp(totalWatts - DefaultPowerLimitFallbackWatts, 0, 255);
                    DiagnosticLogger.LogInfo($"SetPowerLimit: NVML default unavailable, assuming 80W ? cTGP offset={ctgpOffset}");
                }

                // GPU power control on these laptops works through EC registers only.
                // NVML returns NOT_SUPPORTED on laptops, and NvAPI has no GPU power
                // control functions (GPUInfoDLL.dll from XMG CC uses only read-only NvAPI).
                // The EC register sequence (from tuxedo-drivers on Linux):
                //   0x0743 = enable bits (GENERAL|DB|CTGP)
                //   0x0744 = cTGP offset (targetWatts - defaultWatts)
                //   0x0745 = TPP offset
                //   0x0746 = Dynamic Boost offset
                ec.ActivateGpuPowerControl(ctgpOffset, dbOffset);

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
                    (int)(minMw / Kilo),
                    (int)(currentMw / Kilo),
                    (int)(defaultMw / Kilo),
                    (int)(maxMw / Kilo)
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
            catch (Exception ex)
            {
                // NVML P/Invoke may throw SEHException on GPU removal or driver errors.
                DiagnosticLogger.LogInfo($"TryGetClocks: EXCEPTION {ex.GetType().Name}: {ex.Message}");
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
            catch (Exception ex)
            {
                // NVML P/Invoke may throw SEHException on GPU removal or driver errors.
                DiagnosticLogger.LogInfo($"TryGetClockOffsets: EXCEPTION {ex.GetType().Name}: {ex.Message}");
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

        private static string? _cachedGpuName;

        public static string GetGpuName()
        {
            if (_cachedGpuName != null)
                return _cachedGpuName;

            try
            {
                if (!TryGetNvmlGpu(out IntPtr gpu)) return string.Empty;

                var name = new StringBuilder(96);

                if (Nvml.nvmlDeviceGetName(gpu, name, (uint)name.Capacity) == Nvml.NVML_SUCCESS)
                {
                    _cachedGpuName = name.ToString();
                    return _cachedGpuName;
                }
            }
            catch (Exception ex)
            {
                // NVML P/Invoke may throw SEHException on GPU removal or driver errors.
                DiagnosticLogger.LogInfo($"GetGpuName: EXCEPTION {ex.GetType().Name}: {ex.Message}");
            }

            return string.Empty;
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
            catch (Exception ex)
            {
                // NVML P/Invoke may throw SEHException on GPU removal or driver errors.
                DiagnosticLogger.LogInfo($"SetMaxGPUClock: EXCEPTION {ex.GetType().Name}: {ex.Message}");
                return -1;
            }
        }

        public static int ResetMaxGPUClock()
        {
            return SetMaxGPUClock(0);
        }

        public static int GetMaxGPUCLock()
        {
            if (GpuInteropGuard.IsBlocked) return 0;

            try
            {
                PhysicalGPU? internalGpu = PhysicalGPU.GetPhysicalGPUs().FirstOrDefault();
                if (internalGpu == null) return -1;

                PrivateClockBoostLockV2 data = GPUApi.GetClockBoostLock(internalGpu.Handle);

                return (int)data.ClockBoostLocks[0].VoltageInMicroV / Kilo;
            }
            catch (Exception ex)
            {
                // NvAPI may throw SEHException or AccessViolationException on driver errors.
                DiagnosticLogger.LogInfo($"GetMaxGPUCLock: EXCEPTION {ex.GetType().Name}: {ex.Message}");
                return -1;
            }
        }

        public static bool TryGetVfCurve(out List<VfPoint> points)
        {
            points = new List<VfPoint>();

            if (GpuInteropGuard.IsBlocked) return false;

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
                            (int)(voltageUv / Kilo),
                            (int)(frequencyKhz / Kilo)
                        ));
                    }
                }

                return points.Count > 0;
            }
            catch (Exception ex)
            {
                // NvAPI may throw SEHException or AccessViolationException on driver errors.
                DiagnosticLogger.LogInfo($"TryGetVfCurve: EXCEPTION {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        public static int ResetVfCurveOffsets()
        {
            if (GpuInteropGuard.IsBlocked) return 0;

            DiagnosticLogger.LogInfo("ResetVfCurveOffsets: reset GPU clock offsets to zero");
            try
            {
                // Use the same public NVAPI path as the Core Clock Offset slider
                // (GPUApi.SetPerformanceStates20) to apply a uniform zero offset.
                // This is the reliable path that works on Blackwell laptops.
                PhysicalGPU? internalGpu = PhysicalGPU.GetPhysicalGPUs().FirstOrDefault();
                if (internalGpu == null) return -1;

                var coreClock = new PerformanceStates20ClockEntryV1(
                    PublicClockDomain.Graphics,
                    new PerformanceStates20ParameterDelta(0)
                );

                var memoryClock = new PerformanceStates20ClockEntryV1(
                    PublicClockDomain.Memory,
                    new PerformanceStates20ParameterDelta(0)
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
                DiagnosticLogger.LogInfo("ResetVfCurveOffsets: SetPerformanceStates20 completed");

                // Also clear any per-point V-F curve offsets applied via the ClockBoostTable path.
                // Build a full zero-offset dictionary for all 127 points.
                var zeroOffsets = new Dictionary<int, int>();
                for (int i = 0; i < UsableVfPointCount; i++)
                    zeroOffsets[i] = 0;
                ApplyVfCurveOffsets_ClockBoostTable(zeroOffsets);

                return 1;
            }
            catch (Exception ex)
            {
                DiagnosticLogger.LogInfo($"ResetVfCurveOffsets: EXCEPTION {ex.Message}");
                return -1;
            }
        }

        public static int SetUndervoltCurveFromDefault(int requestedMaxVoltageMv, int requestedMaxClockMhz)
        {
            if (requestedMaxVoltageMv < MinUndervoltVoltageMv || requestedMaxVoltageMv > MaxUndervoltVoltageMv) return 0;
            if (requestedMaxClockMhz < MinUndervoltClockMhz || requestedMaxClockMhz > MaxUndervoltClockMhz) return 0;

            if (GpuInteropGuard.IsBlocked) return 0;

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

                    int frequencyOffsetKhz = (desiredFrequencyMhz - point.FrequencyMHz) * Kilo;
                    int voltageOffsetUv = (desiredVoltageMv - point.VoltageMv) * Kilo;

                    if (SetVfPointControl(point.Index, frequencyOffsetKhz, voltageOffsetUv))
                        ok++;
                    else
                        fail++;
                }

                if (ok == 0) return -1;
                if (fail > 0) return 0;

                return VerifyUndervoltCurve(alignedMaxVoltageMv, requestedMaxClockMhz) ? 1 : 0;
            }
            catch (Exception ex)
            {
                // NvAPI may throw SEHException or AccessViolationException on driver errors.
                DiagnosticLogger.LogInfo($"SetUndervoltCurveFromDefault: EXCEPTION {ex.GetType().Name}: {ex.Message}");
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
                    frequencyDeltaMhz * Kilo,
                    voltageDeltaMv * Kilo
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
            catch (Exception ex)
            {
                // NvAPI may throw SEHException or AccessViolationException on driver errors.
                DiagnosticLogger.LogInfo($"ProbeVfPointWrite: EXCEPTION {ex.GetType().Name}: {ex.Message}");
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

        /// <summary>
        /// Applies a batch of per-point frequency offsets from a V-F curve preset.
        /// Reads the control table once, then writes each point individually.
        /// Returns the number of points successfully written.
        /// </summary>
        public static int ApplyVfCurveOffsets(Dictionary<int, int> offsetsMHz)
        {
            if (GpuInteropGuard.IsBlocked) return 0;

            DiagnosticLogger.LogInfo($"ApplyVfCurveOffsets: {offsetsMHz.Count} points received");
            int ok = 0;
            int skip = 0;
            int fail = 0;

            foreach (var kvp in offsetsMHz)
            {
                if (kvp.Value == 0) { skip++; continue; }
                bool written = SetVfPointControl(kvp.Key, kvp.Value * Kilo, 0);
                DiagnosticLogger.LogInfo($"  Point {kvp.Key}: offset={kvp.Value}MHz, result={written}");
                if (written) ok++; else fail++;
            }

            DiagnosticLogger.LogInfo($"ApplyVfCurveOffsets: {ok} ok, {fail} failed, {skip} skipped");
            return ok;
        }

        private static bool SetVfPointControl(int pointIndex, int frequencyOffsetKhz, int voltageOffsetUv)
        {
            try
            {
                if (pointIndex < 0 || pointIndex >= UsableVfPointCount) return false;
                if (!NvApiPrivate.TryGetFirstGpuHandle(out IntPtr gpu)) return false;

                // Read current control table via getControl before writing (required by driver).
                // Green-curve reads the base control, modifies the delta, then writes back.
                if (!ReadControlTable(gpu, out byte[] buffer)) return false;

                IntPtr setControlPtr = NvApiPrivate.QueryInterface(0x0733E009);
                if (setControlPtr == IntPtr.Zero) return false;

                var setControl = Marshal.GetDelegateForFunctionPointer<NvApiPrivate.GpuBufferDelegate>(setControlPtr);

                // Clear the 32-byte mask region, then set the single point bit.
                Array.Clear(buffer, 0x04, 32);
                buffer[0x04 + pointIndex / 8] = (byte)(1 << (pointIndex % 8));

                // Delta offset: baseOffset + pointIndex * stride + deltaOffset
                // From green-curve: controlEntryBaseOffset=0x44, stride=0x24, deltaOffset=0x14
                int deltaOffset = 0x44 + pointIndex * 0x24 + 0x14;
                BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(deltaOffset, 4), frequencyOffsetKhz);

                return setControl(gpu, buffer) == 0;
            }
            catch (Exception ex)
            {
                // NvAPI may throw SEHException or AccessViolationException on driver errors.
                DiagnosticLogger.LogInfo($"SetVfPointControl: EXCEPTION {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Applies per-point frequency offsets using NvAPI_GPU_SetClockBoostTable (0x0733E009)
        /// with the correct PrivateClockBoostTableV1 struct layout from LTT.NvAPIWrapper.
        /// Struct: version(4) + masks(4*4) + unknown1(12*4) + gpuDeltas(80*36) + memFilled(23*4) + memDeltas(23*4) + unknown2(1529*4)
        /// Each GPUDelta: 9 uint32 fields, _FrequencyDeltaInkHz at offset 0x14 (20 bytes per entry).
        /// Total struct size: 0x2420 (9248 bytes) - matches our existing controlSize.
        /// </summary>
        public static int ApplyVfCurveOffsets_ClockBoostTable(Dictionary<int, int> offsetsMHz)
        {
            if (GpuInteropGuard.IsBlocked) return 0;

            DiagnosticLogger.LogInfo($"ApplyVfCurveOffsets_ClockBoostTable: {offsetsMHz.Count} points");
            try
            {
                if (!NvApiPrivate.TryGetFirstGpuHandle(out IntPtr gpu)) return -1;

                IntPtr setPtr = NvApiPrivate.QueryInterface(0x0733E009);
                if (setPtr == IntPtr.Zero)
                {
                    DiagnosticLogger.LogInfo("ApplyVfCurveOffsets_ClockBoostTable: setControl(0x0733E009) not available");
                    return -1;
                }

                var setControl = Marshal.GetDelegateForFunctionPointer<NvApiPrivate.GpuBufferDelegate>(setPtr);

                // Green-curve buffer layout (verified working):
                // Offset 0x00: version (v1, (1<<16) | size)
                // Offset 0x04: 32-byte bitmap mask (one bit per point index)
                // Offset 0x24: padding / unknown (0x24 to 0x44)
                // Offset 0x44: entries start, stride = 0x24 (36 bytes) per point
                //   Each entry: 5 uint32 + FrequencyDeltaInkHz(4) + 3 uint32 = 36 bytes
                //   FrequencyDeltaInkHz at offset 0x14 within each entry
                // Total buffer: 0x2420 (9248 bytes) — supports up to 255 point indices

                const int bufferSize = 0x2420;
                const int entryStride = 0x24; // 36 bytes per entry
                const int freqDeltaOffset = 0x14; // _FrequencyDeltaInkHz within each entry

                byte[] buffer = new byte[bufferSize];

                // Version: v1, size = bufferSize
                BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(0, 4), (1 << 16) | bufferSize);

                // Set mask bits and frequency deltas for all points.
                // Match green-curve's per-point layout: clear 32-byte mask region,
                // then set bits and write deltas in a single batch.
                foreach (var kvp in offsetsMHz)
                {
                    int pointIndex = kvp.Key;
                    if (pointIndex < 0 || pointIndex >= UsableVfPointCount) continue;

                    // Set mask bit — bitmap at offset 0x04, one bit per point index
                    int maskByteOffset = 0x04 + (pointIndex / 8);
                    int maskBit = pointIndex % 8;
                    buffer[maskByteOffset] |= (byte)(1 << maskBit);

                    // Write frequency delta (kHz)
                    int deltaOffset = 0x44 + pointIndex * entryStride + freqDeltaOffset;
                    if (deltaOffset + 4 <= bufferSize)
                    {
                        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(deltaOffset, 4), kvp.Value * Kilo);
                    }
                }

                int status = setControl(gpu, buffer);
                DiagnosticLogger.LogInfo($"ApplyVfCurveOffsets_ClockBoostTable: setControl returned 0x{status:X}");
                return status == 0 ? offsetsMHz.Count : -1;
            }
            catch (Exception ex)
            {
                DiagnosticLogger.LogInfo($"ApplyVfCurveOffsets_ClockBoostTable: EXCEPTION {ex.Message}");
                return -1;
            }
        }

        /// <summary>
        /// Applies per-point frequency offsets using NvAPI_GPU_SetClockBoostLock (0x39442CFB).
        /// This is marked as NVAPI_ID_CURVE_SET in LTT.NvAPIWrapper.
        /// PrivateClockBoostLockV2 layout:
        //  version(4) + unknown(4) + count(4) + locks[32]
        /// Each ClockBoostLock: domain(4) + mode(4) + lockMode(4) + value(4) + voltageInMicroV(4) + flag(4) = 24 bytes
        /// Total: 12 + 32*24 = 780 bytes
        /// LockMode: 0=None, 1=P-State, 2=Frequency, 3=Manual
        /// For frequency locks, voltageInMicroV field holds the frequency in kHz.
        /// </summary>
        public static int ApplyVfCurveOffsets_ClockBoostLock(Dictionary<int, int> offsetsMHz)
        {
            if (GpuInteropGuard.IsBlocked) return 0;

            DiagnosticLogger.LogInfo($"ApplyVfCurveOffsets_ClockBoostLock: {offsetsMHz.Count} points");
            try
            {
                if (!NvApiPrivate.TryGetFirstGpuHandle(out IntPtr gpu)) return -1;

                IntPtr setPtr = NvApiPrivate.QueryInterface(0x39442CFB);
                if (setPtr == IntPtr.Zero)
                {
                    DiagnosticLogger.LogInfo("ApplyVfCurveOffsets_ClockBoostLock: 0x39442CFB not available");
                    return -1;
                }

                var setControl = Marshal.GetDelegateForFunctionPointer<NvApiPrivate.GpuBufferDelegate>(setPtr);

                // PrivateClockBoostLockV2 layout (Pack=8):
                // Offset 0x00: _Version (StructureVersion = uint32, v2)
                // Offset 0x04: _Unknown (uint32)
                // Offset 0x08: _ClockBoostLocksCount (uint32)
                // Offset 0x0C: _ClockBoostLocks[32] (24 bytes each)
                // Total: 0x0C + 32*24 = 0x30C (780 bytes)

                const int bufferSize = 0x30C; // 780 bytes — Marshal.SizeOf<PrivateClockBoostLockV2>()
                const int lockStride = 24;
                const int maxLocks = 32;

                byte[] buffer = new byte[bufferSize];

                // Version: v2
                BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(0, 4), (2 << 16) | bufferSize);
                // Count
                int lockCount = Math.Min(offsetsMHz.Count, maxLocks);
                BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(0x08, 4), lockCount);

                int i = 0;
                foreach (var kvp in offsetsMHz)
                {
                    if (i >= maxLocks) break;
                    if (kvp.Value == 0) { i++; continue; }

                    int lockOffset = 0x0C + i * lockStride;
                    if (lockOffset + lockStride > bufferSize) break;

                    // ClockBoostLock (Pack=8):
                    // _ClockDomain(4) + _Mode(4) + _LockMode(4) + _Value(4) + _VoltageInMicroV(4) + _Flag(4)
                    // For frequency lock: domain=0(Graphics), lockMode=2(Frequency), voltageInMicroV=freqInKhz
                    BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(lockOffset, 4), 0);       // Graphics domain
                    BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(lockOffset + 4, 4), 0);  // mode
                    BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(lockOffset + 8, 4), 2);  // Frequency lock
                    BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(lockOffset + 12, 4), (uint)kvp.Key); // point index
                    BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(lockOffset + 16, 4), (uint)(kvp.Value * Kilo)); // freq delta in kHz
                    BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(lockOffset + 20, 4), 0);  // flag

                    i++;
                }

                int status = setControl(gpu, buffer);
                DiagnosticLogger.LogInfo($"ApplyVfCurveOffsets_ClockBoostLock: setControl returned 0x{status:X}");
                return status == 0 ? i : -1;
            }
            catch (Exception ex)
            {
                DiagnosticLogger.LogInfo($"ApplyVfCurveOffsets_ClockBoostLock: EXCEPTION {ex.Message}");
                return -1;
            }
        }

        /// <summary>
        /// Applies per-point frequency offsets using NvAPI_GPU_SetClocks (0x6F151055).
        /// Unexposed function — buffer layout is unknown. Using a minimal structure
        /// based on PerformanceStates20Info patterns.
        /// </summary>
        public static int ApplyVfCurveOffsets_SetClocks(Dictionary<int, int> offsetsMHz)
        {
            if (GpuInteropGuard.IsBlocked) return 0;

            DiagnosticLogger.LogInfo($"ApplyVfCurveOffsets_SetClocks: {offsetsMHz.Count} points");
            try
            {
                if (!NvApiPrivate.TryGetFirstGpuHandle(out IntPtr gpu)) return -1;

                IntPtr setPtr = NvApiPrivate.QueryInterface(0x6F151055);
                if (setPtr == IntPtr.Zero)
                {
                    DiagnosticLogger.LogInfo("ApplyVfCurveOffsets_SetClocks: 0x6F151055 not available");
                    return -1;
                }

                var setControl = Marshal.GetDelegateForFunctionPointer<NvApiPrivate.GpuBufferDelegate>(setPtr);

                // Unknown buffer layout. Try a minimal structure similar to PerformanceStates20InfoV1.
                // This is speculative — we're guessing based on the pattern of other NvAPI GPU set functions.
                const int bufferSize = 0x200;
                byte[] buffer = new byte[bufferSize];

                // Version: v1
                BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(0, 4), (1 << 16) | bufferSize);

                // Write frequency offsets as a simple array after version
                int i = 0;
                foreach (var kvp in offsetsMHz)
                {
                    int offset = 0x04 + i * 4;
                    if (offset + 4 > bufferSize) break;
                    BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(offset, 4), kvp.Value * Kilo);
                    i++;
                }

                int status = setControl(gpu, buffer);
                DiagnosticLogger.LogInfo($"ApplyVfCurveOffsets_SetClocks: setControl returned 0x{status:X}");
                return status == 0 ? i : -1;
            }
            catch (Exception ex)
            {
                DiagnosticLogger.LogInfo($"ApplyVfCurveOffsets_SetClocks: EXCEPTION {ex.Message}");
                return -1;
            }
        }

        /// <summary>
        /// Reads the current VF control table via NvAPI getControl (0x23F1B133).
        /// Required before setControl — the driver expects a valid base state.
        /// </summary>
        private static bool ReadControlTable(IntPtr gpu, out byte[] buffer)
        {
            buffer = Array.Empty<byte>();

            IntPtr getControlPtr = NvApiPrivate.QueryInterface(0x23F1B133);
            if (getControlPtr == IntPtr.Zero) return false;

            var getControl = Marshal.GetDelegateForFunctionPointer<NvApiPrivate.GpuBufferDelegate>(getControlPtr);

            const int controlSize = 0x2420;
            const int controlVersion = (1 << 16) | controlSize;

            buffer = new byte[controlSize];
            BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(0, 4), controlVersion);

            // Set 32-byte mask to all 1s (request all points).
            for (int i = 0; i < 32; i++)
                buffer[0x04 + i] = 0xFF;

            return getControl(gpu, buffer) == 0;
        }

        private static bool TryGetNvmlGpu(out IntPtr gpu)
        {
            gpu = IntPtr.Zero;

            if (!Nvml.EnsureInitialised()) return false;

            try
            {
                return Nvml.nvmlDeviceGetHandleByIndex_v2(0, out gpu) == Nvml.NVML_SUCCESS;
            }
            catch
            {
                // NVML P/Invoke may throw SEHException/AccessViolationException when the
                // GPU driver is in a bad state (e.g., after display hotplug). Reset NVML
                // so the next call reinitializes with fresh handles.
                Nvml.Reset();
                return false;
            }
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
            // Block all NVML calls during the global GPU interop cooldown.
            // This prevents the Adaptive page timer (5s) or other callers from
            // hitting NVML before the GPU driver has settled after display changes.
            if (GpuInteropGuard.IsBlocked)
                return false;

            if (_initAttempted) return _initialised;

            _initAttempted = true;
            _initialised = nvmlInit_v2() == NVML_SUCCESS;

            return _initialised;
        }

        /// <summary>
        /// Shuts down NVML and resets the initialization flags so the next EnsureInitialised()
        /// will reinitialize. Also signals the global GPU interop guard so NvAPI calls
        /// are also blocked during cooldown. Call this proactively after events that invalidate
        /// GPU driver state (display hotplug, hibernation resume) to prevent
        /// ExecutionEngineException from stale handles.
        /// </summary>
        public static void Reset()
        {
            try { nvmlShutdown(); } catch { /* NVML may be in a bad state */ }
            _initAttempted = false;
            _initialised = false;
            GpuInteropGuard.Reset();
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

            if (GpuInteropGuard.IsBlocked) return false;

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

    /// <summary>
    /// VFCurve-specific NvAPI interfaces discovered from RTHAL.dll reverse engineering.
    /// MSI Afterburner uses these instead of the generic setControl(0x0733E009).
    /// </summary>
    internal static class VfCurveApi
    {
        /// <summary>VFCurve getInfo interface (discovered from RTHAL.dll function 0x82F40).</summary>
        public const uint InterfaceGetInfo = 0x2A70;

        /// <summary>VFCurve setControl interface (discovered from RTHAL.dll function 0x82F40).</summary>
        public const uint InterfaceSetControl = 0x2A7C;

        /// <summary>
        /// Buffer size for VFCurve getInfo (from RTHAL 0x82F40: lea eax, [ebp-0x2A74]).
        /// </summary>
        public const int GetInfoBufferSize = 0x2A74; // 10868 bytes

        /// <summary>
        /// Buffer size for VFCurve SetCurve (from RTHAL 0x816D0: sub esp, 0x5A0).
        /// </summary>
        public const int SetCurveBufferSize = 0x5A0; // 1440 bytes

        /// <summary>
        /// Buffer size for VFCurve SetClockBoost (from RTHAL 0x3EB90: push 0x14).
        /// </summary>
        public const int SetClockBoostBufferSize = 0x14; // 20 bytes

        /// <summary>
        /// Result of querying the VFCurve-specific interfaces.
        /// </summary>
        public readonly record struct VfCurveInterfaceResult(
            bool GetInfoAvailable,
            bool SetControlAvailable,
            IntPtr GetInfoPtr,
            IntPtr SetControlPtr,
            string Detail
        );

        /// <summary>
        /// Queries the VFCurve-specific NvAPI interfaces to check availability.
        /// Read-only — does not call the interfaces, only queries them.
        /// </summary>
        public static VfCurveInterfaceResult QueryInterfaces()
        {
            try
            {
                IntPtr getInfoPtr = NvApiPrivate.QueryInterface(InterfaceGetInfo);
                IntPtr setControlPtr = NvApiPrivate.QueryInterface(InterfaceSetControl);

                string detail = string.Format(
                    "VFCurve interfaces: getInfo(0x{0:X}) = {1}, setControl(0x{2:X}) = {3}",
                    InterfaceGetInfo,
                    getInfoPtr == IntPtr.Zero ? "null" : getInfoPtr.ToString(),
                    InterfaceSetControl,
                    setControlPtr == IntPtr.Zero ? "null" : setControlPtr.ToString()
                );

                return new VfCurveInterfaceResult(
                    getInfoPtr != IntPtr.Zero,
                    setControlPtr != IntPtr.Zero,
                    getInfoPtr,
                    setControlPtr,
                    detail
                );
            }
            catch (Exception ex)
            {
                return new VfCurveInterfaceResult(false, false, IntPtr.Zero, IntPtr.Zero,
                    $"Exception during query: {ex.Message}");
            }
        }

        /// <summary>
        /// Attempts to read VFCurve data using the VFCurve-specific getInfo interface (0x2A70).
        /// Read-only probe — does not modify GPU state.
        /// </summary>
        public static bool TryReadVfCurveGetInfo(
            IntPtr gpu,
            out byte[] buffer,
            out string detail)
        {
            buffer = Array.Empty<byte>();
            detail = string.Empty;

            try
            {
                IntPtr getInfoPtr = NvApiPrivate.QueryInterface(InterfaceGetInfo);
                if (getInfoPtr == IntPtr.Zero)
                {
                    detail = "VFCurve getInfo interface (0x2A70) not available";
                    return false;
                }

                var getInfo = Marshal.GetDelegateForFunctionPointer<NvApiPrivate.GpuBufferDelegate>(getInfoPtr);

                buffer = new byte[GetInfoBufferSize];

                // Set version field (same pattern as existing getControl/getStatus calls).
                int version = (1 << 16) | GetInfoBufferSize;
                BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(0, 4), version);

                int status = getInfo(gpu, buffer);

                if (status != 0)
                {
                    detail = string.Format(
                        "VFCurve getInfo returned 0x{0:X} (expected 0)",
                        status
                    );
                    return false;
                }

                // Log first 64 bytes as hex for analysis.
                var hexParts = new System.Collections.Generic.List<string>();
                for (int i = 0; i < Math.Min(64, buffer.Length); i += 16)
                {
                    string hex = BitConverter.ToString(buffer, i, Math.Min(16, buffer.Length - i));
                    hexParts.Add(hex);
                }
                detail = string.Format(
                    "VFCurve getInfo succeeded ({0} bytes). First 64 bytes:\n{1}",
                    buffer.Length,
                    string.Join("\n", hexParts)
                );

                return true;
            }
            catch (Exception ex)
            {
                detail = $"Exception: {ex.GetType().Name}: {ex.Message}";
                return false;
            }
        }

        /// <summary>
        /// Runs a full diagnostic probe of the VFCurve-specific interfaces.
        /// Read-only — does not modify GPU state.
        /// </summary>
        public static string RunDiagnostic()
        {
            var lines = new System.Collections.Generic.List<string>();
            lines.Add("=== VFCurve Interface Diagnostic ===");

            // 1. Query interfaces
            var query = QueryInterfaces();
            lines.Add(query.Detail);

            // 2. Try to get GPU handle
            if (!NvApiPrivate.TryGetFirstGpuHandle(out IntPtr gpu))
            {
                lines.Add("GPU handle: FAILED");
                return string.Join("\n", lines);
            }
            lines.Add($"GPU handle: {gpu}");

            // 3. Try getInfo call (read-only)
            if (query.GetInfoAvailable)
            {
                bool getInfoOk = TryReadVfCurveGetInfo(gpu, out byte[] infoBuffer, out string getInfoDetail);
                lines.Add($"getInfo probe: {(getInfoOk ? "SUCCESS" : "FAILED")}");
                lines.Add(getInfoDetail);
            }
            else
            {
                lines.Add("getInfo probe: SKIPPED (interface not available)");
            }

            // 4. Compare with generic interfaces
            lines.Add("");
            lines.Add("=== Generic Interface Comparison ===");
            IntPtr genericGetInfo = NvApiPrivate.QueryInterface(0x507B4B59);
            IntPtr genericGetControl = NvApiPrivate.QueryInterface(0x23F1B133);
            IntPtr genericSetControl = NvApiPrivate.QueryInterface(0x0733E009);
            IntPtr genericGetStatus = NvApiPrivate.QueryInterface(0x21537AD4);

            lines.Add($"Generic getInfo(0x507B4B59): {(genericGetInfo == IntPtr.Zero ? "null" : genericGetInfo.ToString())}");
            lines.Add($"Generic getControl(0x23F1B133): {(genericGetControl == IntPtr.Zero ? "null" : genericGetControl.ToString())}");
            lines.Add($"Generic setControl(0x0733E009): {(genericSetControl == IntPtr.Zero ? "null" : genericSetControl.ToString())}");
            lines.Add($"Generic getStatus(0x21537AD4): {(genericGetStatus == IntPtr.Zero ? "null" : genericGetStatus.ToString())}");

            return string.Join("\n", lines);
        }
    }
}