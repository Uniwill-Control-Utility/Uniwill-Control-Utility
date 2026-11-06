using System;
using System.Threading;
using Serilog;
using UCU.Hardware.Cpu.Interop;

namespace UCU.Hardware.Cpu.Intel
{
    /// <summary>
    /// The Intel MSR stack, VERBATIM-ported from the app's IntelManagement
    /// (MSR TDP 0x610, voltage offset 0x150 with the cpu/gpu/cache/sa command
    /// planes, clock ratio 0x1AD, power balance 0x63A/0x642). The bit masks
    /// and encodings are the app's, re-homed for the engine's LocalSystem
    /// token: IntelPawnIO's device open is access-denied to an unelevated
    /// app, so every Intel MSR apply on the field machines was silently dead
    /// (the same PawnIO ACL that killed the SMU applies). The methods keep
    /// the app's catch-and-log semantics — the engine log is the witness —
    /// and answer bool so the verb carries "the write landed" honestly to the
    /// app. What was deliberately NOT ported: the driver-blocklist write
    /// (weakening CI Config from a service is not on the menu) and the
    /// determineCPU wrapper around it.
    /// </summary>
    internal static class IntelMsrStack
    {
        private static readonly object objLock = new object();
        private static readonly object initializationLock = new object();

        // MSR addresses
        private const uint MsrTdp = 0x610;
        private const uint MsrVoltageOffset = 0x150;
        private const uint MsrClockRatio = 0x1AD;

        // Power balance MSR addresses
        private const uint MsrPowerBalanceCpu = 0x63A;
        private const uint MsrPowerBalanceGpu = 0x642;

        // Voltage plane command bases
        private const ulong VoltageCommandCpu = 0x80000011UL;
        private const ulong VoltageCommandGpu = 0x80000111UL;
        private const ulong VoltageCommandCache = 0x80000211UL;
        private const ulong VoltageCommandSa = 0x80000411UL;

        // TDP MSR bit masks
        private const uint TdpHighBase = 0x00438000u;
        private const uint TdpLowBase = 0x00DD8000u;
        private const uint TdpValueMask = 0xFFFu;

        // Voltage offset scaling
        private const double VoltageScaleFactor = 1.024;
        private const int VoltageShift = 21;

        // Retry / sleep delays (ms)
        private const int MsrWriteDelayMs = 100;

        // Clock ratio bounds
        private const int MaxClockRatios = 8;
        private const byte ClockRatioMin = 0;
        private const byte ClockRatioMax = 255;

        private static IntelPawnIo? msrPawnIo;
        private static IntelPawnIo? mchbarPawnIo;
        private static IntelPawnIO? intelPawnIO;

        public static void Initialise()
        {
            lock (initializationLock)
            {
                if (intelPawnIO != null && intelPawnIO.IsLoaded)
                    return;

                try
                {
                    msrPawnIo = IntelPawnIo.LoadModuleFromFile(EngineAssetPaths.IntelMsrModule);

                    try
                    {
                        mchbarPawnIo = IntelPawnIo.LoadModuleFromFile(EngineAssetPaths.IntelMchbarModule);
                    }
                    catch (Exception ex)
                    {
                        // MCHBAR is optional (the reads answer 0 without it);
                        // the app treated a missing module as "skip".
                        Log.Information("Intel MCHBAR module unavailable: {Message}", ex.Message);
                    }

                    if (msrPawnIo == null || !msrPawnIo.IsLoaded)
                    {
                        // U-110: the Intel MSR verbs answer bool (the ported
                        // stack's shape) and this log is the diagnostic
                        // surface, so the open code rides the message:
                        // 1275 names Core Isolation as the blocker.
                        Log.Error(
                            new InvalidOperationException(
                                "IntelMSR.bin failed to load." + IntelPawnIo.DescribeLastOpenFailure()),
                            "Failed to initialise Intel PawnIO.");
                        return;
                    }

                    intelPawnIO = new IntelPawnIO(msrPawnIo, mchbarPawnIo);
                    intelPawnIO.Open();
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Failed to initialise Intel PawnIO.");
                }
            }
        }

        public static void Deinitialize()
        {
            lock (initializationLock)
            {
                try
                {
                    intelPawnIO?.Close();

                    msrPawnIo?.Close();
                    mchbarPawnIo?.Close();

                    intelPawnIO = null;
                    msrPawnIo = null;
                    mchbarPawnIo = null;
                }
                catch
                {
                    // Device close failures during deinitialization are non-fatal.
                }
            }
        }

        /// <summary>
        /// Writes the TDP MSR (0x610): PL2 in the high dword, PL1 in the low.
        /// The app's changeTDPAll is the same call with pl==pl (the verb
        /// carries the pair). True when the MSR write landed.
        /// </summary>
        public static bool SetTdp(int pl1, int pl2)
        {
            try
            {
                EnsureInitialised();
                return runIntelTDPChangeMSR(pl1, pl2);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to change Intel TDP MSR.");
                return false;
            }
        }

        public static bool SetPowerBalance(int value, int cpuOrGpu)
        {
            if (value < 0 || value > 31)
                return true; // the app's silent no-op: nothing to do, not a failure

            try
            {
                EnsureInitialised();

                if (cpuOrGpu == 0)
                    return changePowerBalance(MsrPowerBalanceCpu, value);
                if (cpuOrGpu == 1)
                    return changePowerBalance(MsrPowerBalanceGpu, value);

                return true;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to change Intel power balance.");
                return false;
            }
        }

        public static bool SetVoltageOffset(int value, int voltagePlane)
        {
            try
            {
                EnsureInitialised();

                ulong command = voltagePlane switch
                {
                    0 => VoltageCommandCpu,
                    1 => VoltageCommandGpu,
                    2 => VoltageCommandCache,
                    3 => VoltageCommandSa,
                    _ => 0UL
                };

                if (command == 0)
                    return true; // the app's silent no-op for an unknown plane

                ulong data = unchecked((uint)((int)Math.Round(value * VoltageScaleFactor) << VoltageShift));
                ulong msrValue = (command << 32) | data;

                intelPawnIO!.WriteMsr(MsrVoltageOffset, msrValue);

                Thread.Sleep(MsrWriteDelayMs);
                return true;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to change Intel voltage offset.");
                return false;
            }
        }

        public static bool SetClockRatios(int[] clockRatios)
        {
            try
            {
                EnsureInitialised();

                if (clockRatios == null || clockRatios.Length == 0)
                    return true;

                ulong value = 0;
                int ratioCount = Math.Min(clockRatios.Length, MaxClockRatios);
                for (int i = 0; i < ratioCount; ++i)
                    value = (value << 8) | (byte)Math.Clamp(clockRatios[i], ClockRatioMin, ClockRatioMax);

                intelPawnIO!.WriteMsr(MsrClockRatio, value);

                Thread.Sleep(MsrWriteDelayMs);
                return true;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to change Intel clock ratio offset.");
                return false;
            }
        }

        public static int[] GetClockRatios()
        {
            try
            {
                EnsureInitialised();

                ulong value = intelPawnIO!.ReadMsr(MsrClockRatio);
                int[] intParts = new int[MaxClockRatios];

                for (int i = 0; i < MaxClockRatios; i++)
                    intParts[i] = (int)((value >> ((MaxClockRatios - 1 - i) * 8)) & 0xFF);

                return intParts;
            }
            catch
            {
                // Reading clock ratios is non-critical; return empty on failure.
                return Array.Empty<int>();
            }
        }

        private static bool runIntelTDPChangeMSR(int pl1TDP, int pl2TDP)
        {
            try
            {
                EnsureInitialised();

                if (pl1TDP <= 0 || pl2TDP < 0)
                    return true; // the app's silent no-op bounds check

                lock (objLock)
                {
                    uint high = TdpHighBase | ((uint)(pl2TDP * 8) & TdpValueMask);
                    uint low = TdpLowBase | ((uint)((pl1TDP - 1) * 8) & TdpValueMask);

                    ulong value = ((ulong)high << 32) | low;

                    intelPawnIO!.WriteMsr(MsrTdp, value);

                    Thread.Sleep(MsrWriteDelayMs);
                }

                return true;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to write Intel TDP MSR.");
                return false;
            }
        }

        private static bool changePowerBalance(uint address, int value)
        {
            try
            {
                EnsureInitialised();

                intelPawnIO!.WriteMsr(address, unchecked((ulong)value));

                Thread.Sleep(100);
                return true;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to change Intel power balance via MSR.");
                return false;
            }
        }

        private static void EnsureInitialised()
        {
            if (intelPawnIO != null && intelPawnIO.IsLoaded)
                return;

            Initialise();
        }

        // Not ported on purpose: the app's checkDriverBlockRegistry (the
        // HKLM Control CI Config VulnerableDriverBlocklistEnable write — an
        // approved dead-code removal, and a LocalSystem service has no
        // business weakening the driver blocklist) and its determineCPU
        // wrapper, and the changeGpuClock stub that only ever returned -1.
    }
}
