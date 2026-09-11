using CpuAffinityUtility;
using RyzenSmu;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Uniwill_Control_Utility.Scripts.GPUs.AMD;
using Uniwill_Control_Utility.Scripts.GPUs.NVIDIA;
using Uniwill_Control_Utility.Scripts.Intel_Backend;
using Uniwill_Control_Utility.Scripts.Misc;
using Uniwill_Control_Utility.Services;
using Settings = Uniwill_Control_Utility.Properties.Settings;

namespace Uniwill_Control_Utility.Scripts
{
    internal static class RyzenAdj_To_UCU
    {
        [DllImport("powrprof.dll", EntryPoint = "PowerSetActiveOverlayScheme")]
        public static extern uint PowerSetActiveOverlayScheme(Guid OverlaySchemeGuid);

        private static readonly Guid BalancedPowerSchemeGuid = Guid.Empty;
        private static readonly Guid HighPerformancePowerSchemeGuid = new("DED574B5-45A0-4F42-8737-46345C09C238");
        private static readonly Guid PowerSaverPowerSchemeGuid = new("961CC777-2547-4F9D-8174-7D86181b8A7A");

        private const string WinPowerValueSaver = "0";
        private const string WinPowerValueBalanced = "1";
        private const string WinPowerValueHighPerformance = "2";

        // Optional EC service reference for --ec-* command routing.
        // Set from App.xaml.cs or MainWindowViewModel after DI resolves the service.
        internal static UniwillECService? EcService { get; set; }

        public static void Translate(string ryzenAdjString, bool isAutoReapply = false, bool isAutoOC = false, string? appliedName = null, bool localizeAppliedName = false) =>
            _ = TranslateAsync(ryzenAdjString, isAutoReapply, isAutoOC, appliedName, localizeAppliedName);

        public static Task TranslateAsync(string ryzenAdjString, bool isAutoReapply = false, bool isAutoOC = false, string? appliedName = null, bool localizeAppliedName = false) =>
            Task.Run(() => TranslateCore(ryzenAdjString, isAutoReapply, isAutoOC, appliedName, localizeAppliedName));

        #pragma warning disable S1172 // Unused parameters
        private static void TranslateCore(string ryzenAdjString, bool _isAutoReapply, bool isAutoOC, string? appliedName, bool localizeAppliedName)
        #pragma warning restore S1172
        {
            try
            {
                if (string.IsNullOrWhiteSpace(ryzenAdjString))
                    return;

                string normalizedCommands = ryzenAdjString.Trim();
                string[] ryzenAdjCommands = normalizedCommands
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();

                DiagnosticLogger.LogDebug($"Translate string: {normalizedCommands}");
                foreach (string ryzenAdjCommand in ryzenAdjCommands)
                {
                        try
                        {
                            int separatorIndex = ryzenAdjCommand.IndexOf('=');
                            string ryzenAdjCommandString = (separatorIndex >= 0 ? ryzenAdjCommand[..separatorIndex] : ryzenAdjCommand).TrimStart('-');
                            string ryzenAdjCommandValueString = separatorIndex >= 0 ? ryzenAdjCommand[(separatorIndex + 1)..] : "0";

                            DiagnosticLogger.LogDebug($"Processing: {ryzenAdjCommandString}={ryzenAdjCommandValueString}");

                            if (ryzenAdjCommandString.Contains("UCUSR"))
                            {
                                UCUSR(ryzenAdjCommandString, ryzenAdjCommandValueString);
                            }
                            else if (ryzenAdjCommandString.Contains("CCD-Affinity"))
                            {
                                CpuAffinityManager.SetGlobalAffinity(Convert.ToInt32(ryzenAdjCommandValueString));
                            }
                            else if (ryzenAdjCommandString.Contains("Win-Power"))
                            {
                                // ignore return value - fire and forget system calls
                                if (ryzenAdjCommandValueString == WinPowerValueSaver) _ = PowerSetActiveOverlayScheme(PowerSaverPowerSchemeGuid);
                                else if (ryzenAdjCommandValueString == WinPowerValueBalanced) _ = PowerSetActiveOverlayScheme(BalancedPowerSchemeGuid);
                                else if (ryzenAdjCommandValueString == WinPowerValueHighPerformance) _ = PowerSetActiveOverlayScheme(HighPerformancePowerSchemeGuid);
                            }
                            else if (ryzenAdjCommandString.Contains("Win-CPU"))
                            {
                                WindowsProcessorPower.Apply(ryzenAdjCommandValueString);
                            }
                            else if (ryzenAdjCommandString.Contains("Refresh-Rate"))
                            {
                                Uniwill_Control_Utility.Scripts.Misc.Display.ApplySettings(Convert.ToInt32(ryzenAdjCommandValueString));
                            }
                            else if (ryzenAdjCommandString.Contains("ADLX"))
                            {
                                ADLX(ryzenAdjCommandString, ryzenAdjCommandValueString);
                            }
                            else if (ryzenAdjCommandString.Contains("NVIDIA"))
                            {
                                NVIDIA(ryzenAdjCommandString, ryzenAdjCommandValueString);
                            }
                            else if (ryzenAdjCommandString.Contains("intel"))
                            {
                                if (ryzenAdjCommandString == "intel-pl")
                                {
                                    string[] limits = ryzenAdjCommandValueString.Split(',');
                                    if (limits.Length == 2)
                                        IntelManagement.changeTDP(int.Parse(limits[0]), int.Parse(limits[1]));
                                    else
                                        IntelManagement.changeTDPAll(int.Parse(ryzenAdjCommandValueString));
                                }
                                else if (ryzenAdjCommandValueString.Contains('-'))
                                {
                                    if (ryzenAdjCommandString == "intel-ratio")
                                    {
                                        string[] stringArray = ryzenAdjCommandValueString.Split('-');
                                        int[] intArray = stringArray.Select(int.Parse).ToArray();

                                        IntelManagement.changeClockRatioOffset(intArray);
                                    }
                                }
                                else
                                {
                                    int ryzenAdjCommandValue = Convert.ToInt32(ryzenAdjCommandValueString);

                                    if (ryzenAdjCommandString == "intel-volt-cpu") IntelManagement.changeVoltageOffset(ryzenAdjCommandValue, 0);
                                    else if (ryzenAdjCommandString == "intel-volt-gpu") IntelManagement.changeVoltageOffset(ryzenAdjCommandValue, 1);
                                    else if (ryzenAdjCommandString == "intel-volt-cache") IntelManagement.changeVoltageOffset(ryzenAdjCommandValue, 2);
                                    else if (ryzenAdjCommandString == "intel-volt-sa") IntelManagement.changeVoltageOffset(ryzenAdjCommandValue, 3);
                                    else if (ryzenAdjCommandString == "intel-bal-cpu") IntelManagement.changePowerBalance(ryzenAdjCommandValue, 0);
                                    else if (ryzenAdjCommandString == "intel-bal-gpu") IntelManagement.changePowerBalance(ryzenAdjCommandValue, 1);
                                    else if (ryzenAdjCommandString == "intel-gpu") IntelManagement.changeGpuClock(ryzenAdjCommandValue);
                                }
                            }
                            else if (ryzenAdjCommandString.StartsWith("ec-"))
                            {
                                // EC register write commands for Uniwill EC (XMG / TUXEDO laptops).
                                if (EcService is null)
                                {
                                    DiagnosticLogger.LogDebug($"EC command ignored: {ryzenAdjCommand} (EC service not available)");
                                    continue;
                                }

                                // On first EC command in a batch, set OVERBOOST profile.
                                // This replicates the Linux tuxedo_io driver's uw_set_tdp()
                                // which calls uw_set_performance_profile_v1(PROFILE_OVERBOOST)
                                // before writing PL1/PL2/PL4. The OVERBOOST profile signals
                                // the EC to raise the power ceiling above the default 55W.
                                if (ryzenAdjCommandString == "ec-overboost")
                                {
                                    EcService.SetOverboostProfile();
                                    DiagnosticLogger.LogDebug($"EC applied: {ryzenAdjCommandString}");
                                    continue;
                                }

                                int ecValue = Convert.ToInt32(ryzenAdjCommandValueString);

                                if (ryzenAdjCommandString == "ec-pl1") EcService.SetPl1(ecValue);
                                else if (ryzenAdjCommandString == "ec-pl2") EcService.SetPl2(ecValue);
                                else if (ryzenAdjCommandString == "ec-pl4") EcService.SetPl4(ecValue);
                                else if (ryzenAdjCommandString == "ec-pl5") EcService.SetPl5(ecValue);
                                else if (ryzenAdjCommandString == "ec-tcc-offset") EcService.SetTccOffset(ecValue);
                                else if (ryzenAdjCommandString == "ec-mode") EcService.SetPerformanceMode(ecValue);
                                else DiagnosticLogger.LogDebug($"Unknown EC command: {ryzenAdjCommandString}");

                                DiagnosticLogger.LogDebug($"EC applied: {ryzenAdjCommandString}={ecValue}");
                            }
                            else
                            {
                                // Parse as int first so negative CO values (e.g. -15) don't throw
                                // OverflowException from Convert.ToUInt32(). The cast to uint
                                // produces the correct two's complement encoding (e.g. -15 -> 0xFFFFFFF1).
                                int ryzenAdjCommandValueSigned = Convert.ToInt32(ryzenAdjCommandValueString);
                                uint ryzenAdjCommandValue = (uint)ryzenAdjCommandValueSigned;

                                if (ryzenAdjCommand.Contains("skin")) ryzenAdjCommandValue *= 256; // millidegrees to decidegrees

                                if (ryzenAdjCommand.Contains("coall") && Settings.Default.isAutoUvCPU && !isAutoOC) continue;
                                if (ryzenAdjCommand.Contains("coper") && Settings.Default.isAutoUvCPU && !isAutoOC) continue;
                                if (ryzenAdjCommand.Contains("cogfx") && Settings.Default.isAutoUviGPU && !isAutoOC) continue;

                                DiagnosticLogger.LogInfo($"SMU sending: {ryzenAdjCommandString} = 0x{ryzenAdjCommandValue:X} ({ryzenAdjCommandValue})");

                                if (ryzenAdjCommandValue <= 0 && !ryzenAdjCommandString.Contains("co")) SMUCommands.applySettings(ryzenAdjCommandString, 0x0);
                                else SMUCommands.applySettings(ryzenAdjCommandString, ryzenAdjCommandValue);

                                // CRITICAL: 50ms delay between SMU commands. The SMU firmware
                                // needs time to process each command. Without this delay,
                                // batch commands cause the SMU to enforce a conservative
                                // 55W default. The original UCU includes this delay.
                                const int SmuCommandDelayMs = 50;
                                Thread.Sleep(SmuCommandDelayMs);
                            }
                        }
                        catch (Exception ex)
                        {
                            DiagnosticLogger.LogError(ex, $"Failed to process command: {ryzenAdjCommand}");
                        }
                }

                if (!isAutoOC)
                {
                    LastAppliedSettingsService.Update(normalizedCommands, appliedName, localizeAppliedName);
                }
            }
            catch (Exception ex)
            {
                DiagnosticLogger.LogError(ex, "Failed to translate RyzenAdj command string");
            }
        }

private static void ADLX(string command, string value)
        {
            try
            {
                DiagnosticLogger.LogDebug($"ADLX: {command}={value}");
                string[] variables = value.Split('-');

                if (command == "ADLX-Lag") _ = ADLXBackend.SetAntiLag(int.Parse(variables[0]), bool.Parse(variables[1]));
                if (command == "ADLX-Boost") _ = ADLXBackend.SetBoost(int.Parse(variables[0]), bool.Parse(variables[1]), int.Parse(variables[2]));
                if (command == "ADLX-RSR")
                {
                    _ = ADLXBackend.SetRSR(bool.Parse(variables[0]));
                    _ = ADLXBackend.SetRSRSharpness(int.Parse(variables[1]));
                }
                if (command == "ADLX-Chill") _ = ADLXBackend.SetChill(int.Parse(variables[0]), bool.Parse(variables[1]), int.Parse(variables[2]), int.Parse(variables[3]));
                if (command == "ADLX-Sync") _ = ADLXBackend.SetEnhancedSync(int.Parse(variables[0]), bool.Parse(variables[1]));
                if (command == "ADLX-ImageSharp") _ = ADLXBackend.SetImageSharpning(int.Parse(variables[0]), bool.Parse(variables[1]), int.Parse(variables[2]));
            }
            catch (Exception ex)
            {
                DiagnosticLogger.LogError(ex, "Failed to apply ADLX settings");
            }
        }

        private static void UCUSR(string command, string value)
        {
            try
            {
                DiagnosticLogger.LogDebug($"UCUSR: {command}={value}");
                string[] variables = value.Split('-');

                if (command == "UCUSR")
                {
                    Uniwill_Control_Utility.Properties.Settings.Default.AdapterIdx = 0;
                    Uniwill_Control_Utility.Properties.Settings.Default.isMagpie = Convert.ToBoolean(variables[0]);
                    Uniwill_Control_Utility.Properties.Settings.Default.VSync = Convert.ToBoolean(variables[1]);
                    Uniwill_Control_Utility.Properties.Settings.Default.Sharpness = Convert.ToDouble(variables[2]);
                    Uniwill_Control_Utility.Properties.Settings.Default.ResMode = Convert.ToInt32(variables[3]);
                    Uniwill_Control_Utility.Properties.Settings.Default.AutoRestore = Convert.ToBoolean(variables[0]);
                    Uniwill_Control_Utility.Properties.Settings.Default.Save();
                }
            }
            catch (Exception ex)
            {
                DiagnosticLogger.LogError(ex, "Failed to apply UCUSR settings");
            }
        }

        private static void NVIDIA(string command, string value)
        {
            try
            {
                DiagnosticLogger.LogDebug($"NVIDIA: {command}={value}");
                DiagnosticLogger.LogInfo($"NVIDIA: {command}={value}");

                // Split on '-' but keep negative numbers intact.
                // "0--300-0-175-25" ? ["0", "-300", "0", "175", "25"]
                // Strategy: replace "--" with "-|", split on "-", then merge empty tokens back to the next token.
                string[] variables = ParseNvidiaClocks(value);
                DiagnosticLogger.LogInfo($"NVIDIA: parsed {variables.Length} tokens: [{string.Join(", ", variables)}]");

                if (command == "NVIDIA-Clocks" && variables.Length == 2)
                {
                    DiagnosticLogger.LogInfo($"NVIDIA: SetClocks({variables[0]}, {variables[1]})");
                    NvTuning.SetClocks(int.Parse(variables[0]), int.Parse(variables[1]));
                }
                else if (command == "NVIDIA-Clocks" && variables.Length == 3)
                {
                    DiagnosticLogger.LogInfo($"NVIDIA: SetMaxGPUClock({variables[0]}), SetClocks({variables[1]}, {variables[2]})");
                    NvTuning.SetMaxGPUClock(int.Parse(variables[0]));
                    NvTuning.SetClocks(int.Parse(variables[1]), int.Parse(variables[2]));
                }
                else if (command == "NVIDIA-Clocks" && variables.Length == 4)
                {
                    // Legacy: 0-core-mem-watts (no DB offset, total watts = watts)
                    DiagnosticLogger.LogInfo($"NVIDIA: SetMaxGPUClock({variables[0]}), SetClocks({variables[1]}, {variables[2]}), SetPowerLimit({variables[3]}, dbOffset=0)");
                    NvTuning.SetMaxGPUClock(int.Parse(variables[0]));
                    NvTuning.SetClocks(int.Parse(variables[1]), int.Parse(variables[2]));
                    NvTuning.SetPowerLimit(int.Parse(variables[3]), 0);
                    DiagnosticLogger.LogInfo($"NVIDIA: SetPowerLimit returned");
                }
                else if (command == "NVIDIA-Clocks" && variables.Length == 5)
                {
                    // New: 0-core-mem-totalWatts-dbOffset
                    DiagnosticLogger.LogInfo($"NVIDIA: SetMaxGPUClock({variables[0]}), SetClocks({variables[1]}, {variables[2]}), SetPowerLimit({variables[3]}, dbOffset={variables[4]})");
                    NvTuning.SetMaxGPUClock(int.Parse(variables[0]));
                    NvTuning.SetClocks(int.Parse(variables[1]), int.Parse(variables[2]));
                    NvTuning.SetPowerLimit(int.Parse(variables[3]), int.Parse(variables[4]));
                    DiagnosticLogger.LogInfo($"NVIDIA: SetPowerLimit returned");
                }
            }
            catch (Exception ex)
            {
                DiagnosticLogger.LogError(ex, "Failed to apply NVIDIA clocks");
            }
        }

        /// <summary>
        /// Split a dash-delimited value while keeping negative numbers intact.
        /// "0--300-0-175-25" ? ["0", "-300", "0", "175", "25"]
        /// "0-100-200" ? ["0", "100", "200"]
        /// </summary>
        private static string[] ParseNvidiaClocks(string value)
        {
            // Replace "--" (separator followed by negative sign) with a unique delimiter.
            // Then split on both "-" and the delimiter, reassembling negatives.
            string normalized = value.Replace("--", "-\u0000-");
            string[] parts = normalized.Split('-');

            var result = new System.Collections.Generic.List<string>();
#pragma warning disable S127 // Loops should not modify iteration conditions
            for (int i = 0; i < parts.Length; i++)
            {
                if (parts[i] == "\u0000")
                {
                    // This was a "--" ? the next part is the negative number.
                    if (i + 1 < parts.Length)
                    {
                        result.Add("-" + parts[i + 1]);
                        i++; // skip the consumed part
                    }
                }
                else
                {
                    result.Add(parts[i]);
                }
            }
#pragma warning restore S127

            return result.ToArray();
        }


    }
}