using Microsoft.Win32;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using Uniwill_Control_Utility.Properties;

namespace Uniwill_Control_Utility.Scripts.Intel_Backend
{
    public static class Intel_Management
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

        // Registry paths
        private const string DriverBlockRegistryPath = @"SYSTEM\CurrentControlSet\Control\CI\Config";
        private const string DriverBlockRegistryValue = "VulnerableDriverBlocklistEnable";

        public static string BaseDir = Settings.Default.Path;

        private static IntelPawnIo? msrPawnIo;
        private static IntelPawnIo? mchbarPawnIo;
        private static IntelPawnIO? intelPawnIO;

        private static string? MCHBAR;

        public static void Initialise()
        {
            lock (initializationLock)
            {
                if (intelPawnIO != null && intelPawnIO.IsLoaded)
                    return;

                try
                {
                    string? msrPath = ResolveIntelModulePath("IntelMSR.bin");

                    if (msrPath == null)
                    {
                        Misc.DiagnosticLogger.LogError(
                            new FileNotFoundException("IntelMSR.bin not found."),
                            "Failed to initialise Intel PawnIO.");
                        return;
                    }

                    msrPawnIo = IntelPawnIo.LoadModuleFromFile(msrPath);

                    string? mchbarPath = ResolveIntelModulePath("IntelMCHBAR.bin");

                    if (mchbarPath != null)
                        mchbarPawnIo = IntelPawnIo.LoadModuleFromFile(mchbarPath);

                    if (msrPawnIo == null || !msrPawnIo.IsLoaded)
                    {
                        Misc.DiagnosticLogger.LogError(
                            new InvalidOperationException("IntelMSR.bin failed to load."),
                            "Failed to initialise Intel PawnIO.");
                        return;
                    }

                    intelPawnIO = new IntelPawnIO(msrPawnIo, mchbarPawnIo);
                    intelPawnIO.Open();

                    DetermineIntelMCHBAR();
                }
                catch (Exception ex)
                {
                    Misc.DiagnosticLogger.LogError(ex, "Failed to initialise Intel PawnIO.");
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
                    MCHBAR = null;
                }
                catch
                {
                    // Device close failures during deinitialization are non-fatal.
                }
            }
        }

        public static void changeTDPAll(int pl)
        {
            try
            {
                EnsureInitialised();
                runIntelTDPChangeMSR(pl, pl);
            }
            catch (Exception ex)
            {
                Misc.DiagnosticLogger.LogError(ex, "Failed to change Intel TDP MSR.");
            }
        }

        public static void changeTDP(int pl1, int pl2)
        {
            try
            {
                EnsureInitialised();
                runIntelTDPChangeMSR(pl1, Math.Max(pl1 + 2, pl2));
            }
            catch (Exception ex)
            {
                Misc.DiagnosticLogger.LogError(ex, "Failed to change Intel PL1 and PL2 MSRs.");
            }
        }

        public static void changePowerBalance(int value, int cpuOrGpu)
        {
            if (value < 0 || value > 31)
                return;

            try
            {
                EnsureInitialised();

                if (cpuOrGpu == 0)
                    changePowerBalance(MsrPowerBalanceCpu, value);
                else if (cpuOrGpu == 1)
                    changePowerBalance(MsrPowerBalanceGpu, value);
            }
            catch (Exception ex)
            {
                Misc.DiagnosticLogger.LogError(ex, "Failed to change Intel power balance.");
            }
        }

        public static void changeVoltageOffset(int value, int voltagePlane)
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
                    return;

                ulong data = unchecked((uint)((int)Math.Round(value * VoltageScaleFactor) << VoltageShift));
                ulong msrValue = (command << 32) | data;

                intelPawnIO!.WriteMsr(MsrVoltageOffset, msrValue);

                Thread.Sleep(MsrWriteDelayMs);
            }
            catch (Exception ex)
            {
                Misc.DiagnosticLogger.LogError(ex, "Failed to change Intel voltage offset.");
            }
        }

        public static void changeClockRatioOffset(int[] clockRatios)
        {
            try
            {
                EnsureInitialised();

                if (clockRatios == null || clockRatios.Length == 0)
                    return;

                ulong value = 0;
                int ratioCount = Math.Min(clockRatios.Length, MaxClockRatios);
                for (int i = 0; i < ratioCount; ++i)
                    value = (value << 8) | (byte)Math.Clamp(clockRatios[i], ClockRatioMin, ClockRatioMax);

                intelPawnIO!.WriteMsr(MsrClockRatio, value);

                Thread.Sleep(MsrWriteDelayMs);
            }
            catch (Exception ex)
            {
                Misc.DiagnosticLogger.LogError(ex, "Failed to change Intel clock ratio offset.");
            }
        }

        public static int[] readClockRatios()
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

        public static int changeGpuClock(int value)
        {
            /*
             * Not currently supported.
             *
             */
            return -1;
        }

        private static void runIntelTDPChangeMSR(int pl1TDP, int pl2TDP)
        {
            try
            {
                EnsureInitialised();

                if (pl1TDP <= 0 || pl2TDP < 0)
                    return;

                lock (objLock)
                {
                    uint high = TdpHighBase | ((uint)(pl2TDP * 8) & TdpValueMask);
                    uint low = TdpLowBase | ((uint)((pl1TDP - 1) * 8) & TdpValueMask);

                    ulong value = ((ulong)high << 32) | low;

                    intelPawnIO!.WriteMsr(MsrTdp, value);

                    Thread.Sleep(MsrWriteDelayMs);
                }
            }
            catch (Exception ex)
            {
                Misc.DiagnosticLogger.LogError(ex, "Failed to write Intel TDP MSR.");
            }
        }

        private static void changePowerBalance(uint address, int value)
        {
            try
            {
                EnsureInitialised();

                intelPawnIO!.WriteMsr(address, unchecked((ulong)value));

                Thread.Sleep(100);
            }
            catch (Exception ex)
            {
                Misc.DiagnosticLogger.LogError(ex, "Failed to change Intel power balance via MSR.");
            }
        }

        public static void checkDriverBlockRegistry()
        {
            try
            {
                RegistryKey? myKey = Registry.LocalMachine.OpenSubKey(
                    DriverBlockRegistryPath,
                    true);

                if (myKey != null)
                {
                    object? value = myKey.GetValue(DriverBlockRegistryValue);

                    if (value?.ToString() == "1")
                    {
                        myKey.SetValue(
                            DriverBlockRegistryValue,
                            "0",
                            RegistryValueKind.String);
                    }

                    myKey.Close();
                }
            }
            catch { /* Registry key close failed; key may already be closed */ }
        }

        public static void determineCPU()
        {
            checkDriverBlockRegistry();
            EnsureInitialised();
            DetermineIntelMCHBAR();
        }

        private static bool DetermineIntelMCHBAR()
        {
            try
            {
                EnsureInitialised();

                if (intelPawnIO == null || !intelPawnIO.HasMchbar)
                    return false;

                ulong mchbar = intelPawnIO.GetMchbarAddress();

                if (mchbar == 0)
                    return false;

                MCHBAR = "0x" + mchbar.ToString("X");

                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void EnsureInitialised()
        {
            if (intelPawnIO != null && intelPawnIO.IsLoaded)
                return;

            Initialise();
        }

        private static string? ResolveIntelModulePath(string fileName)
        {
            string binName = Path.ChangeExtension(fileName, ".bin");

            string[] candidates =
            {
                Path.Combine(BaseDir, "Assets", "Intel", "PawnIO", binName),
                Path.Combine(BaseDir, "Assets", "Intel", "PawnIO", fileName),

                binName,
                fileName
            };

            return candidates.FirstOrDefault(File.Exists);
        }

    }
}
