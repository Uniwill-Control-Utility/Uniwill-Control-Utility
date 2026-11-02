using System;
using System.Linq;
using Microsoft.Win32;

namespace UCU.Hardware.Cpu
{
    /// <summary>
    /// The SMU gate the app's Family class carried: the SMU address tables are
    /// written for one machine class (FireRange: Zen 5, Family 26 Model 68), and
    /// only that class's mailbox addresses are registered.
    ///
    /// The family/model pair comes from the CPU key's <c>Identifier</c> value
    /// ("AMD64 Family 26 Model 68 Stepping 0"). The first implementation parsed
    /// the **brand string** instead, which only carries that shape when Windows
    /// falls back to a generic processor name - on a machine that resolves a real
    /// name ("AMD Ryzen 9 9955HX3D 16-Core Processor") the parse failed, the gate
    /// answered false, and the engine registered ZERO SMU commands: every
    /// <c>set-coper</c>/<c>slow-limit</c>/<c>fast-limit</c> in a preset apply then
    /// threw "Command 'x' not found" (field, 2026-11-02 - 16 stack traces per
    /// Core-Optimizer apply, and the tuning silently never reached the hardware).
    /// The brand string stays as the fallback for machines without an Identifier.
    /// </summary>
    public static class CpuModelDetection
    {
        // Zen 5 (FireRange) CPUID identifiers, as the app's gate parsed them.
        private const int Zen5Family = 26;
        private const int Zen5FireRangeModel = 68;

        public static bool IsFireRange()
        {
            foreach (string source in new[] { ReadProcessorIdentifier(), ReadProcessorName() })
            {
                if (TryParseFamilyModel(source, out int family, out int model))
                    return family == Zen5Family && model == Zen5FireRangeModel;
            }

            return false;
        }

        internal static bool TryParseFamilyModel(string? text, out int family, out int model)
        {
            family = 0;
            model = 0;
            if (string.IsNullOrWhiteSpace(text))
                return false;

            string[] words = text!.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            int familyIndex = Array.IndexOf(words, "Family") + 1;
            int modelIndex = Array.IndexOf(words, "Model") + 1;

            if (familyIndex == 0 || familyIndex >= words.Length)
                return false;
            if (modelIndex == 0 || modelIndex >= words.Length)
                return false;

            return int.TryParse(words[familyIndex], out family)
                && int.TryParse(words[modelIndex], out model);
        }

        private static string ReadProcessorIdentifier() => ReadProcessorValue("Identifier");

        private static string ReadProcessorName() => ReadProcessorValue("ProcessorNameString");

        private static string ReadProcessorValue(string name)
        {
            try
            {
                using RegistryKey? key = Registry.LocalMachine.OpenSubKey(
                    @"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
                return key?.GetValue(name) as string ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }
    }
}
