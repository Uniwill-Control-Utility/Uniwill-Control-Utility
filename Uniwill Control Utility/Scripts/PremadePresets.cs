using System;
using System.Text.RegularExpressions;
using Uniwill_Control_Utility.Scripts.Misc;

namespace Uniwill_Control_Utility.Scripts
{
    internal static class PremadePresets
    {
        public static string ExtremePreset { get; set; } = string.Empty;
        public static string PerformancePreset { get; set; } = string.Empty;
        public static string BalPreset { get; set; } = string.Empty;
        public static string EcoPreset { get; set; } = string.Empty;

        public static void SetPremadePresets()
        {
            try
            {
                if (Family.TYPE == Family.ProcessorType.Amd_Apu)
                {
                    // Only FireRange (Ryzen 9955HX / 9955HX3D on Socket AM5) is supported.
                    if (Family.FAM == Family.RyzenFamily.FireRange)
                    {
                        EcoPreset = "--tctl-temp=95 --cHTC-temp=95 --stapm-limit=35000 --fast-limit=45000 --stapm-time=64 --slow-limit=35000 --slow-time=128 --vrm-current=180000 --vrmmax-current=180000 --vrmsoc-current=180000 --vrmsocmax-current=180000 --vrmgfx-current=180000 --Win-Power=0 ";
                        BalPreset = "--tctl-temp=95 --cHTC-temp=95 --stapm-limit=65000 --fast-limit=75000 --stapm-time=64 --slow-limit=65000 --slow-time=128 --vrm-current=180000 --vrmmax-current=180000 --vrmsoc-current=180000 --vrmsocmax-current=180000 --vrmgfx-current=180000 --Win-Power=1 ";
                        PerformancePreset = "--tctl-temp=95 --cHTC-temp=95 --stapm-limit=100000 --fast-limit=120000 --stapm-time=64 --slow-limit=100000 --slow-time=128 --vrm-current=240000 --vrmmax-current=240000 --vrmsoc-current=240000 --vrmsocmax-current=240000 --vrmgfx-current=240000 --Win-Power=2 ";
                        ExtremePreset = "--tctl-temp=95 --cHTC-temp=95 --stapm-limit=125000 --fast-limit=145000 --stapm-time=64 --slow-limit=125000 --slow-time=128 --vrm-current=240000 --vrmmax-current=240000 --vrmsoc-current=240000 --vrmsocmax-current=240000 --vrmgfx-current=240000 --Win-Power=2 ";
                    }
                }
                else if (Family.TYPE == Family.ProcessorType.Intel)
                {
                    SetIntelPremadePresets();
                }
            }
            catch (Exception ex)
            {
                // Broad catch is intentional: Family properties may throw during initialization
                // (e.g., CPUID read failures, unexpected processor families). Log and continue
                // so the app starts with empty presets rather than crashing.
                Misc.DiagnosticLogger.LogError(ex, "Failed to set premade presets");
            }
        }

        private static void SetIntelPremadePresets()
        {
            var limits = GetIntelPowerLimits(Family.CPUName);
            var ecoPl1 = Math.Max(4, (int)Math.Round(limits.Pl1 * 0.55));
            var ecoPl2 = Math.Max(ecoPl1 + 2, (int)Math.Round(limits.Pl1 * 0.8));
            var balancedPl1 = Math.Max(4, (int)Math.Round(limits.Pl1 * 0.8));
            var balancedPl2 = Math.Max(balancedPl1 + 2, (int)Math.Round(limits.Pl2 * 0.5));
            var performancePl1 = limits.Pl1;
            var performancePl2 = Math.Max(performancePl1 + 2, (int)Math.Round(limits.Pl2 * 0.75));

            EcoPreset = BuildIntelPreset(ecoPl1, ecoPl2, 0);
            BalPreset = BuildIntelPreset(balancedPl1, balancedPl2, 1);
            PerformancePreset = BuildIntelPreset(performancePl1, performancePl2, 2);
            ExtremePreset = BuildIntelPreset(limits.Pl1, limits.Pl2, 2);
        }

        /// <summary>
        /// Build an Intel preset string. Only writes MSR 0x610 (TDP limits) and switches power scheme.
        /// --Win-CPU was removed because it sets PERFBOOSTMODE and PERFEPP via powercfg,
        /// which can slam the CPU to minimum frequency and cause system lockup.
        /// </summary>
        private static string BuildIntelPreset(int pl1, int pl2, int powerMode)
        {
            pl2 = Math.Max(pl1 + 2, pl2);
            return $"--intel-pl={pl1},{pl2} --Win-Power={powerMode} ";
        }

        private static (int Pl1, int Pl2) GetIntelPowerLimits(string processorName)
        {
            var name = processorName.ToUpperInvariant();
            var match = Regex.Match(name, @"(?:I[3579]-|ULTRA\s+[3579]\s+)(?<model>\d{3,5})(?<suffix>[A-Z0-9]*)");
            var model = match.Success ? match.Groups["model"].Value : string.Empty;
            var suffix = match.Success ? match.Groups["suffix"].Value : string.Empty;
            var generation = 0;
            if (model.Length == 5 && int.TryParse(model.AsSpan(0, 2), out var twoDigitGeneration)) generation = twoDigitGeneration;
            else if (model.Length == 4 && (model.StartsWith("10", StringComparison.Ordinal) || model.StartsWith("11", StringComparison.Ordinal)) && int.TryParse(model.AsSpan(0, 2), out var laterFourDigitGeneration)) generation = laterFourDigitGeneration;
            else if (model.Length >= 4 && int.TryParse(model.AsSpan(0, 1), out var oneDigitGeneration)) generation = oneDigitGeneration;

            if (name.Contains("ULTRA", StringComparison.Ordinal))
            {
                if (suffix.StartsWith("H", StringComparison.Ordinal)) return (28, 115);
                if (suffix.StartsWith("U", StringComparison.Ordinal)) return (15, 57);
                if (suffix.StartsWith("V", StringComparison.Ordinal)) return (17, 37);
            }

            if (suffix.Contains("HX", StringComparison.Ordinal)) return (55, 157);
            if (suffix.StartsWith("H", StringComparison.Ordinal) || suffix.Contains("HK", StringComparison.Ordinal) || suffix.Contains("HQ", StringComparison.Ordinal)) return generation >= 12 ? (45, 115) : (45, 90);
            if (suffix.StartsWith("P", StringComparison.Ordinal)) return (28, 64);
            if (suffix.StartsWith("Y", StringComparison.Ordinal)) return (7, 15);
            if (suffix.StartsWith("U", StringComparison.Ordinal) || suffix.Contains("G", StringComparison.Ordinal)) return generation >= 11 ? (15, 55) : (15, 25);
            if (suffix.StartsWith("T", StringComparison.Ordinal)) return (35, 65);
            if (suffix.Contains("K", StringComparison.Ordinal) || suffix.Contains("X", StringComparison.Ordinal)) return generation >= 12 ? (125, 241) : (95, 119);
            if (generation >= 12)
            {
                if (name.Contains("I3-", StringComparison.Ordinal)) return (60, 89);
                if (name.Contains("I5-", StringComparison.Ordinal)) return (65, 117);
                if (name.Contains("I7-", StringComparison.Ordinal)) return (65, 180);
                return (65, 219);
            }

            if (name.Contains("I3-", StringComparison.Ordinal)) return (65, 75);
            if (name.Contains("I5-", StringComparison.Ordinal)) return (65, 95);
            if (name.Contains("I7-", StringComparison.Ordinal)) return (65, 125);
            if (name.Contains("I9-", StringComparison.Ordinal)) return (65, 165);
            return (35, 65);
        }
    }
}
