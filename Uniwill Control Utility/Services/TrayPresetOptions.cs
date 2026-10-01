using System;
using System.Collections.Generic;
using System.Linq;
using Uniwill_Control_Utility.Models;

namespace Uniwill_Control_Utility.Services
{
    public sealed record TrayPresetOption(string Name, bool IsActive);

    public static class TrayPresetOptions
    {
        public static readonly string[] ProfileNames = { "Office", "Gaming", "Turbo" };

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

        /// <summary>
        /// Builds the list of saved Custom preset display names for the active preset mode.
        /// Only includes slots where HasChanges is true (user actually saved something).
        /// </summary>
        public static string[] BuildCustomPresetNames(PresetMode mode, bool isPowerBoostMode)
        {
            var presets = CustomPresetStore.Load(mode);
            var result = new List<string>();

            for (int i = 0; i < presets.Length; i++)
            {
                if (presets[i].HasChanges)
                {
                    string modeSuffix = isPowerBoostMode ? " (Boost)" : "";
                    result.Add($"Custom {i + 1}{modeSuffix}");
                }
            }

            return result.ToArray();
        }

        /// <summary>
        /// Returns the display name for a Custom preset at the given slot index (0-based).
        /// </summary>
        public static string CustomPresetDisplayName(int slotIndex, bool isPowerBoostMode)
        {
            string modeSuffix = isPowerBoostMode ? " (Boost)" : "";
            return $"Custom {slotIndex + 1}{modeSuffix}";
        }

        /// <summary>
        /// Checks if the selected preset is a Custom preset (e.g., "Custom 1 (Boost)" or "Custom").
        /// </summary>
        public static bool IsCustomPreset(string presetName)
        {
            return presetName.Equals("Custom", StringComparison.OrdinalIgnoreCase) ||
                   presetName.StartsWith("Custom ", StringComparison.OrdinalIgnoreCase);
        }

        public static string ResolveApplicationKey(
            string profileName,
            PresetMode activeMode,
            int activeSlot,
            bool slotHasChanges)
        {
            if (!IsCustomPreset(profileName) || !slotHasChanges)
                return profileName;

            return PresetProfileApplier.CustomProfileKey(activeSlot, activeMode);
        }
    }
}
