using CpuAffinityUtility;
using RyzenSmu;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Uniwill_Control_Utility.Scripts.GPUs.NVIDIA;
using Uniwill_Control_Utility.Scripts.Intel_Backend;
using Uniwill_Control_Utility.Scripts.Misc;
using Uniwill_Control_Utility.Services;
using Settings = Uniwill_Control_Utility.Properties.Settings;
using UCU.Contracts.Capabilities;

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
        internal static IPowerCapability? EcService { get; set; }

        /// <summary>
        /// Fan capability companion: preset apply writes fan curves too, and the
        /// capabilities split EC work by domain, so the static wiring carries both.
        /// </summary>
        internal static IFanCapability? FanService { get; set; }

        public static void Translate(string ryzenAdjString, bool isAutoReapply = false, string? appliedName = null, bool localizeAppliedName = false) =>
            _ = TranslateAsync(ryzenAdjString, isAutoReapply, appliedName, localizeAppliedName);

        public static Task TranslateAsync(string ryzenAdjString, bool isAutoReapply = false, string? appliedName = null, bool localizeAppliedName = false) =>
            Task.Run(() => TranslateCore(ryzenAdjString, isAutoReapply, appliedName, localizeAppliedName));

        #pragma warning disable S1172 // Unused parameters
        private static void TranslateCore(string ryzenAdjString, bool _isAutoReapply, string? appliedName, bool localizeAppliedName)
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

                // The EC verbs in this batch are one pipe connect each. Two facts make
                // them worth guarding: a connect to the missing pipe of a stopped
                // engine costs the full 5 s connect timeout, and a wedged EC costs the
                // engine's 30 s call budget (the client waits up to its 45 s response
                // budget) - so a preset with seven ec-* writes costs 35 s with the
                // engine down and minutes with the EC wedged, on the startup path.
                // Neither state changes part-way through a batch, so: skip the ec-*
                // commands when the service is not running, and stop sending them at
                // the first unavailable/timeout. The non-EC commands (SMU, Intel,
                // NVIDIA, Windows) are app-side and keep running.
                bool ecBatchStopped = false;

                foreach (string ryzenAdjCommand in ryzenAdjCommands)
                {
                        try
                        {
                            int separatorIndex = ryzenAdjCommand.IndexOf('=');
                            string ryzenAdjCommandString = (separatorIndex >= 0 ? ryzenAdjCommand[..separatorIndex] : ryzenAdjCommand).TrimStart('-');
                            string ryzenAdjCommandValueString = separatorIndex >= 0 ? ryzenAdjCommand[(separatorIndex + 1)..] : "0";

                            DiagnosticLogger.LogDebug($"Processing: {ryzenAdjCommandString}={ryzenAdjCommandValueString}");

                            if (ryzenAdjCommandString.Contains("CCD-Affinity"))
                            {
                                CpuAffinityManager.SetGlobalAffinity(Convert.ToInt32(ryzenAdjCommandValueString));
                            }
                            else if (ryzenAdjCommandString.Contains("Win-Power"))
                            {
                                // The active overlay scheme is a standard-user
                                // powrprof act (Settings' power-mode toggle uses
                                // it) - no elevation to route, so the engine is
                                // not needed here. What WAS missing is the
                                // witness: the calls discarded the status code,
                                // so a failed scheme switch left no trace. Log
                                // the answer for each apply.
                                string overlayScheme;
                                uint overlayStatus;

                                if (ryzenAdjCommandValueString == WinPowerValueSaver)
                                {
                                    overlayStatus = PowerSetActiveOverlayScheme(PowerSaverPowerSchemeGuid);
                                    overlayScheme = "saver";
                                }
                                else if (ryzenAdjCommandValueString == WinPowerValueBalanced)
                                {
                                    overlayStatus = PowerSetActiveOverlayScheme(BalancedPowerSchemeGuid);
                                    overlayScheme = "balanced";
                                }
                                else if (ryzenAdjCommandValueString == WinPowerValueHighPerformance)
                                {
                                    overlayStatus = PowerSetActiveOverlayScheme(HighPerformancePowerSchemeGuid);
                                    overlayScheme = "high-performance";
                                }
                                else
                                {
                                    overlayStatus = 0;
                                    overlayScheme = "none";
                                }

                                DiagnosticLogger.LogDebug($"Win power overlay -> {overlayScheme}: status=0x{overlayStatus:X}");
                            }
                            else if (ryzenAdjCommandString.Contains("Win-CPU"))
                            {
                                WindowsProcessorPower.Apply(ryzenAdjCommandValueString);
                            }
                            else if (ryzenAdjCommandString.Contains("Refresh-Rate"))
                            {
                                Uniwill_Control_Utility.Scripts.Misc.Display.ApplySettings(Convert.ToInt32(ryzenAdjCommandValueString));
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

                                if (ecBatchStopped)
                                {
                                    DiagnosticLogger.LogDebug($"EC command skipped: this batch already lost the engine ({ryzenAdjCommandString})");
                                    continue;
                                }

                                var engineState = App.GetService<Uniwill_Control_Utility.Services.EngineAvailability>();
                                if (engineState is not null && !engineState.IsRunning)
                                {
                                    DiagnosticLogger.LogDebug($"EC command skipped: the engine service is not running ({ryzenAdjCommandString})");
                                    continue;
                                }

                                // On first EC command in a batch, set OVERBOOST profile.
                                // This replicates the Linux tuxedo_io driver's uw_set_tdp()
                                // which calls uw_set_performance_profile_v1(PROFILE_OVERBOOST)
                                // before writing PL1/PL2/PL4. The OVERBOOST profile signals
                                // the EC to raise the power ceiling above the default 55W.
                                if (ryzenAdjCommandString == "ec-overboost")
                                {
                                    EcService.SetOverboostProfileAsync().GetAwaiter().GetResult();
                                    DiagnosticLogger.LogDebug($"EC applied: {ryzenAdjCommandString}");
                                    continue;
                                }

                                int ecValue = Convert.ToInt32(ryzenAdjCommandValueString);

                                if (ryzenAdjCommandString == "ec-pl1") EcService.SetPowerLimitAsync(UCU.Contracts.PowerLimitKind.Pl1, ecValue).GetAwaiter().GetResult();
                                else if (ryzenAdjCommandString == "ec-pl2") EcService.SetPowerLimitAsync(UCU.Contracts.PowerLimitKind.Pl2, ecValue).GetAwaiter().GetResult();
                                else if (ryzenAdjCommandString == "ec-pl4") EcService.SetPowerLimitAsync(UCU.Contracts.PowerLimitKind.Pl4, ecValue).GetAwaiter().GetResult();
                                else if (ryzenAdjCommandString == "ec-pl5") EcService.SetPowerLimitAsync(UCU.Contracts.PowerLimitKind.Pl5, ecValue).GetAwaiter().GetResult();
                                else if (ryzenAdjCommandString == "ec-tcc-offset") EcService.SetTccOffsetAsync(ecValue).GetAwaiter().GetResult();
                                else if (ryzenAdjCommandString == "ec-mode") EcService.SetPerformanceModeAsync(ecValue).GetAwaiter().GetResult();
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

                                DiagnosticLogger.LogInfo($"SMU sending: {ryzenAdjCommandString} = 0x{ryzenAdjCommandValue:X} ({ryzenAdjCommandValue})");

                                // Engine SMU verb FIRST (PawnIO's device ACL admits
                                // System/Administrators, so the app's own driver open
                                // is access-denied unelevated — the silent death the
                                // field saw); the app's SMU stack stays as SmuAccess's
                                // fallback for a machine whose engine is not answering.
                                if (ryzenAdjCommandValue <= 0 && !ryzenAdjCommandString.Contains("co")) SmuAccess.Execute(ryzenAdjCommandString, 0x0);
                                else SmuAccess.Execute(ryzenAdjCommandString, ryzenAdjCommandValue);

                                // CRITICAL: 50ms delay between SMU commands. The SMU firmware
                                // needs time to process each command. Without this delay,
                                // batch commands cause the SMU to enforce a conservative
                                // 55W default. The original UCU includes this delay.
                                const int SmuCommandDelayMs = 50;
                                Thread.Sleep(SmuCommandDelayMs);
                            }
                        }
                        catch (UCU.Contracts.EngineException ex) when (
                            ex.Code == UCU.Contracts.EngineErrorCode.HardwareUnavailable
                            || ex.Code == UCU.Contracts.EngineErrorCode.Timeout
                            || ex.Code == UCU.Contracts.EngineErrorCode.Busy)
                        {
                            // The engine answered "hardware unavailable / busy / too slow".
                            // For an EC write that will not improve while the batch runs,
                            // so stop sending EC verbs and keep the app-side ones.
                            if (ryzenAdjCommand.Contains("ec-", StringComparison.Ordinal))
                            {
                                ecBatchStopped = true;
                                DiagnosticLogger.LogDebug($"EC commands stopped for this batch: engine {ex.Code} at {ryzenAdjCommand}");
                            }
                            else
                            {
                                DiagnosticLogger.LogError(ex, $"Failed to process command: {ryzenAdjCommand}");
                            }
                        }
                        catch (Exception ex)
                        {
                            DiagnosticLogger.LogError(ex, $"Failed to process command: {ryzenAdjCommand}");
                        }
                }

                LastAppliedSettingsService.Update(normalizedCommands, appliedName, localizeAppliedName);
            }
            catch (Exception ex)
            {
                DiagnosticLogger.LogError(ex, "Failed to translate RyzenAdj command string");
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

                // When GPU clock control is disabled in Settings, skip all clock-related
                // NvAPI calls (SetMaxGPUClock, SetClocks) so that third-party tools like
                // MSI Afterburner keep their offsets. Power limit changes still apply.
                bool gpuClockEnabled = Settings.Default.EnableGpuClockControl;

                if (command == "NVIDIA-Clocks" && variables.Length == 2)
                {
                    if (!gpuClockEnabled)
                    {
                        DiagnosticLogger.LogInfo("NVIDIA: SetClocks skipped — GPU clock control disabled");
                        return;
                    }
                    DiagnosticLogger.LogInfo($"NVIDIA: SetClocks({variables[0]}, {variables[1]})");
                    NvTuning.SetClocks(int.Parse(variables[0]), int.Parse(variables[1]));
                }
                else if (command == "NVIDIA-Clocks" && variables.Length == 3)
                {
                    if (!gpuClockEnabled)
                    {
                        DiagnosticLogger.LogInfo("NVIDIA: SetMaxGPUClock + SetClocks skipped — GPU clock control disabled");
                        return;
                    }
                    DiagnosticLogger.LogInfo($"NVIDIA: SetMaxGPUClock({variables[0]}), SetClocks({variables[1]}, {variables[2]})");
                    NvTuning.SetMaxGPUClock(int.Parse(variables[0]));
                    NvTuning.SetClocks(int.Parse(variables[1]), int.Parse(variables[2]));
                }
                else if (command == "NVIDIA-Clocks" && variables.Length == 4)
                {
                    // Legacy: 0-core-mem-watts (no DB offset, total watts = watts)
                    if (!gpuClockEnabled)
                    {
                        DiagnosticLogger.LogInfo($"NVIDIA: SetMaxGPUClock + SetClocks skipped — GPU clock control disabled; applying power limit only");
                    }
                    else
                    {
                        DiagnosticLogger.LogInfo($"NVIDIA: SetMaxGPUClock({variables[0]}), SetClocks({variables[1]}, {variables[2]}), SetPowerLimit({variables[3]}, dbOffset=0)");
                        NvTuning.SetMaxGPUClock(int.Parse(variables[0]));
                        NvTuning.SetClocks(int.Parse(variables[1]), int.Parse(variables[2]));
                    }
                    NvTuning.SetPowerLimit(int.Parse(variables[3]), 0);
                    DiagnosticLogger.LogInfo($"NVIDIA: SetPowerLimit returned");
                }
                else if (command == "NVIDIA-Clocks" && variables.Length == 5)
                {
                    // New: 0-core-mem-totalWatts-dbOffset
                    if (!gpuClockEnabled)
                    {
                        DiagnosticLogger.LogInfo($"NVIDIA: SetMaxGPUClock + SetClocks skipped — GPU clock control disabled; applying power limit only");
                    }
                    else
                    {
                        DiagnosticLogger.LogInfo($"NVIDIA: SetMaxGPUClock({variables[0]}), SetClocks({variables[1]}, {variables[2]}), SetPowerLimit({variables[3]}, dbOffset={variables[4]})");
                        NvTuning.SetMaxGPUClock(int.Parse(variables[0]));
                        NvTuning.SetClocks(int.Parse(variables[1]), int.Parse(variables[2]));
                    }
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