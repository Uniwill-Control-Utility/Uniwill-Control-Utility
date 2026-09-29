using System;
using System.Collections.Generic;
using Uniwill_Control_Utility.Models;

namespace Uniwill_Control_Utility.Services
{
    public sealed record TrayPresetOption(string Name, bool IsActive);

    public static class TrayPresetOptions
    {
        public static readonly string[] ProfileNames = { "Office", "Gaming", "Turbo", "Custom" };

        public static TrayPresetOption[] Build(IEnumerable<string> presetNames, string selectedPreset)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var options = new List<TrayPresetOption>();

            foreach (string presetName in presetNames)
            {
                if (string.IsNullOrWhiteSpace(presetName) || !seen.Add(presetName))
                    continue;

                options.Add(new TrayPresetOption(
                    presetName,
                    presetName.Equals(selectedPreset, StringComparison.OrdinalIgnoreCase)));
            }

            return options.ToArray();
        }

        public static string ResolveApplicationKey(
            string profileName,
            PresetMode activeMode,
            int activeSlot,
            bool slotHasChanges)
        {
            if (!profileName.Equals("Custom", StringComparison.OrdinalIgnoreCase) || !slotHasChanges)
                return profileName;

            return PresetProfileApplier.CustomProfileKey(activeSlot, activeMode);
        }
    }
}
