using System;
using System.Diagnostics;
using Uniwill_Control_Utility.Scripts.Misc;

namespace Uniwill_Control_Utility.Scripts
{
    internal static class WindowsProcessorPower
    {
        private static readonly object Sync = new();

        // AMD Power Slider subgroup and setting GUIDs (AMD laptops only).
        // Values: 0=Battery saver, 1=Better battery, 2=Better performance, 3=Best performance
        private const string AmdPowerSliderSubgroup = "c763b4ec-0e50-4b6b-9bed-2b92a6ee884e";
        private const string AmdPowerSliderSetting = "7ec1751b-60ed-4588-afb5-9819d3d77d90";

        public static void Apply(string value)
        {
            var values = value.Split(',');
            if ((values.Length < 4 || values.Length > 8) ||
                !int.TryParse(values[0], out var boostMode) ||
                !int.TryParse(values[1], out var maximumState) ||
                !int.TryParse(values[2], out var maximumFrequency) ||
                !int.TryParse(values[3], out var energyPreference))
            {
                return;
            }

            var minimumState = values.Length >= 5 && int.TryParse(values[4], out var parsedMinimumState)
                ? parsedMinimumState
                : -1;
            var minimumUnparkedCores = values.Length >= 6 && int.TryParse(values[5], out var parsedMinimumUnparkedCores)
                ? parsedMinimumUnparkedCores
                : -1;
            var maximumUnparkedCores = values.Length >= 7 && int.TryParse(values[6], out var parsedMaximumUnparkedCores)
                ? parsedMaximumUnparkedCores
                : -1;
            var amdPowerSlider = values.Length >= 8 && int.TryParse(values[7], out var parsedAmdPowerSlider)
                ? parsedAmdPowerSlider
                : -1;

            if (minimumState >= 0 && maximumState >= 0)
            {
                minimumState = Math.Min(minimumState, maximumState);
            }
            if (minimumUnparkedCores >= 0 && maximumUnparkedCores >= 0)
            {
                minimumUnparkedCores = Math.Min(minimumUnparkedCores, maximumUnparkedCores);
            }

            lock (Sync)
            {
                ApplySetting("PERFBOOSTMODE", boostMode, 0, 6);
                ApplySetting("PROCTHROTTLEMIN", minimumState, 0, 100);
                ApplySetting("PROCTHROTTLEMAX", maximumState, 1, 100);
                ApplySetting("PROCFREQMAX", maximumFrequency, 100, 64000);
                ApplySetting("PERFEPP", energyPreference, 0, 100);
                ApplySetting("CPMINCORES", minimumUnparkedCores, 0, 100);
                ApplySetting("CPMAXCORES", maximumUnparkedCores, 0, 100);

                // AMD Power Slider — critical on AMD mobile platforms.
                // Balanced plan defaults to 0x02 (Better performance) which enforces
                // a conservative ~55W power ceiling. Setting to 0x03 (Best performance)
                // allows the full CPU power envelope (100W+).
                if (amdPowerSlider >= 0)
                {
                    ApplyAmdPowerSlider(amdPowerSlider);
                }

                RunPowerCfg("/setactive", "SCHEME_CURRENT");
            }
        }

        private static void ApplyAmdPowerSlider(int value)
        {
            value = Math.Clamp(value, 0, 3);
            DiagnosticLogger.LogDebug($"[WinPower] Setting AMD Power Slider to {value} (0=Battery saver, 3=Best performance)");
            RunPowerCfg("/setacvalueindex", "SCHEME_CURRENT", AmdPowerSliderSubgroup, AmdPowerSliderSetting, value.ToString());
            RunPowerCfg("/setdcvalueindex", "SCHEME_CURRENT", AmdPowerSliderSubgroup, AmdPowerSliderSetting, value.ToString());
        }

        private static void ApplySetting(string alias, int value, int minimum, int maximum)
        {
            if (value < 0)
            {
                return;
            }

            value = Math.Clamp(value, minimum, maximum);
            RunPowerCfg("/setacvalueindex", "SCHEME_CURRENT", "SUB_PROCESSOR", alias, value.ToString());
            RunPowerCfg("/setdcvalueindex", "SCHEME_CURRENT", "SUB_PROCESSOR", alias, value.ToString());
        }

        private static void RunPowerCfg(params string[] arguments)
        {
            try
            {
                var startInfo = new ProcessStartInfo("powercfg.exe")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true
                };

                foreach (var argument in arguments)
                {
                    startInfo.ArgumentList.Add(argument);
                }

                using var process = Process.Start(startInfo);
                if (process == null)
                {
                    return;
                }

                process.WaitForExit(5000);
                var err = process.HasExited ? process.StandardError.ReadToEnd() : string.Empty;
                DiagnosticLogger.LogDebug($"[WinPower] powercfg {string.Join(" ", arguments)} → exitCode={process.ExitCode}{(!string.IsNullOrEmpty(err) ? $" err={err.Trim()}" : "")}");
            }
            catch (Exception ex)
            {
                DiagnosticLogger.LogError(ex, "Failed to apply Windows processor power settings");
            }
        }
    }
}
