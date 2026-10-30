using System;
using System.Linq;
using Microsoft.Win32;

namespace UCU.Hardware.Cpu
{
    /// <summary>
    /// The SMU gate the app's Family class carried: the SMU address tables are
    /// written for one machine class (FireRange: Zen 5, brand string
    /// "Family 26 Model 68", HX suffix), and only that class's mailbox
    /// addresses are registered. The engine re-reads the same brand string the
    /// app did (HKLM\HARDWARE\DESCRIPTION\System\CentralProcessor\0) so the
    /// gate cannot disagree with the app's row visibility.
    /// </summary>
    public static class CpuModelDetection
    {
        // Zen 5 (FireRange) CPUID identifiers, as the app's gate parsed them
        // out of the brand string.
        private const int Zen5Family = 26;
        private const int Zen5FireRangeModel = 68;

        public static bool IsFireRange()
        {
            string name = ReadProcessorName();
            if (string.IsNullOrEmpty(name))
                return false;

            string[] words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            int familyIndex = Array.IndexOf(words, "Family") + 1;
            int modelIndex = Array.IndexOf(words, "Model") + 1;

            if (familyIndex == 0 || familyIndex >= words.Length)
                return false;
            if (modelIndex == 0 || modelIndex >= words.Length)
                return false;

            if (!int.TryParse(words[familyIndex], out int cpuFamily))
                return false;
            if (!int.TryParse(words[modelIndex], out int cpuModel))
                return false;

            // Only FireRange (Zen 5, Family 26, Model 68, HX suffix) is supported.
            return cpuFamily == Zen5Family && cpuModel == Zen5FireRangeModel
                && name.Contains("HX", StringComparison.Ordinal);
        }

        private static string ReadProcessorName()
        {
            try
            {
                using RegistryKey? key = Registry.LocalMachine.OpenSubKey(
                    @"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
                return key?.GetValue("ProcessorNameString") as string ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }
    }
}
