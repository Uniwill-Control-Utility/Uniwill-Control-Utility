using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Uniwill_Control_Utility.Models;
using Uniwill_Control_Utility.Properties;
using Uniwill_Control_Utility.Scripts;
using Uniwill_Control_Utility.Scripts.GPUs.NVIDIA;

namespace Uniwill_Control_Utility.Services
{
    /// <summary>
    /// Generates RyzenAdj command strings from Presets page profile names
    /// (Office, Gaming, Turbo, Custom 1..5) for use by the Automations page
    /// and MainWindow power-mode handlers.
    /// </summary>
    public static class PresetProfileApplier
    {
        /// <summary>
        /// The built-in profile names (always available).
        /// </summary>
        public static readonly string[] BuiltInProfileNames = { "Office", "Gaming", "Turbo" };

        /// <summary>
        /// Returns all available profile names: built-in profiles plus saved custom presets.
        /// Custom presets are only included if the user has saved changes to that slot.
        /// </summary>
        public static string[] GetProfileNames()
        {
            var list = new List<string>(BuiltInProfileNames);

            var customPresets = CustomPresetStore.Load();
            for (int i = 0; i < customPresets.Length; i++)
            {
                if (customPresets[i].HasChanges)
                {
                    list.Add(CustomProfileKey(i));
                }
            }

            return list.ToArray();
        }

        /// <summary>
        /// Returns the profile key for a custom preset slot (0-based).
        /// e.g. slot 0 → "Custom 1", slot 4 → "Custom 5"
        /// </summary>
        public static string CustomProfileKey(int slot)
        {
            return $"Custom {slot + 1}";
        }

        /// <summary>
        /// Parses a custom profile key back to a 0-based slot index.
        /// Returns -1 if the key is not a custom preset.
        /// </summary>
        public static int TryParseCustomSlot(string profileName)
        {
            if (string.IsNullOrEmpty(profileName))
                return -1;

            // Match "Custom N" where N is 1-5
            if (profileName.Length > 7 && profileName.StartsWith("Custom ", StringComparison.OrdinalIgnoreCase))
            {
                string numPart = profileName.Substring(7);
                if (int.TryParse(numPart, out int slot) && slot >= 1 && slot <= CustomPreset.MaxSlots)
                    return slot - 1;
            }

            return -1;
        }

        /// <summary>
        /// Checks if a profile name is a built-in profile.
        /// </summary>
        public static bool IsBuiltInProfile(string profileName)
        {
            return profileName.Equals("Office", StringComparison.OrdinalIgnoreCase) ||
                   profileName.Equals("Gaming", StringComparison.OrdinalIgnoreCase) ||
                   profileName.Equals("Turbo", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Checks if a V-F curve preset is currently active on disk.
        /// </summary>
        public static bool HasActiveVfCurve()
        {
            var presets = VfCurvePresetStore.Load();
            return presets.Any(p => p.HasChanges);
        }

        /// <summary>
        /// Builds a RyzenAdj command string for the given profile name.
        /// For custom presets, loads values from the saved preset slot.
        /// Skips GPU power if a V-F curve is active on that slot
        /// (power is applied separately through SetPowerLimit around the curve).
        /// Returns empty string for "None" or unknown profiles.
        /// </summary>
        public static string BuildCommandString(string profileName)
        {
            if (string.IsNullOrEmpty(profileName) || profileName.Equals("None", StringComparison.OrdinalIgnoreCase))
                return string.Empty;

            var parts = new List<string>();

            // Check if this is a custom preset (e.g. "Custom 1", "Custom 3")
            int customSlot = TryParseCustomSlot(profileName);
            if (customSlot >= 0)
            {
                var presets = CustomPresetStore.Load();
                if (customSlot < presets.Length)
                {
                    var preset = presets[customSlot];
                    bool vfCurveActive = preset.VfCurveOffsets.Count > 0;
                    BuildCustomCommandFromPreset(parts, preset, skipGpuPower: vfCurveActive);
                }
                else
                {
                    return string.Empty;
                }
            }
            else if (profileName.Equals("Custom", StringComparison.OrdinalIgnoreCase))
            {
                // Legacy "Custom" (no slot number) — use Settings values.
                bool vfCurveActive = HasActiveVfCurve();
                BuildCustomCommand(parts, skipGpuPower: vfCurveActive);
            }
            else
            {
                BuildSkuProfileCommand(profileName, parts);
            }

            return string.Join(" ", parts) + " ";
        }

        /// <summary>
        /// Fully applies a profile: RyzenAdj commands, V-F curve (Custom only),
        /// and EC CPU power limits (AMD APU only). Mirrors Presets.applyProfileAsync().
        /// </summary>
        public static async Task ApplyProfileAsync(string profileName, string? displayName)
        {
            string command = BuildCommandString(profileName);
            if (string.IsNullOrWhiteSpace(command))
                return;

            string display = string.IsNullOrEmpty(displayName) ? DisplayName(profileName) : displayName;

            // 1. RyzenAdj / SMU commands
            await RyzenAdj_To_UCU.TranslateAsync(command, appliedName: display, localizeAppliedName: false);

            // 2. V-F curve (Custom profile only, if active)
            int customSlot = TryParseCustomSlot(profileName);
            if (customSlot >= 0)
            {
                var presets = CustomPresetStore.Load();
                if (customSlot < presets.Length && presets[customSlot].VfCurveOffsets.Count > 0)
                {
                    ApplyVfCurveFromPreset(presets[customSlot]);
                }
            }
            else if (profileName.Equals("Custom", StringComparison.OrdinalIgnoreCase) && HasActiveVfCurve())
            {
                ApplyVfCurve();
            }

            // 3. EC CPU power limits (AMD APU only)
            if (Family.TYPE == Family.ProcessorType.Amd_Apu)
            {
                var ec = RyzenAdj_To_UCU.EcService;
                if (ec != null)
                {
                    int pl1, pl2;
                    if (customSlot >= 0)
                    {
                        var presets = CustomPresetStore.Load();
                        pl1 = customSlot < presets.Length ? (int)presets[customSlot].CpuSustained : 75;
                        pl2 = customSlot < presets.Length ? (int)presets[customSlot].CpuBoost : 85;
                    }
                    else if (profileName.Equals("Custom", StringComparison.OrdinalIgnoreCase))
                    {
                        pl1 = (int)Settings.Default.customSustained;
                        pl2 = (int)Settings.Default.customBoost;
                    }
                    else
                    {
                        var skuProfile = SkuResolver.GetProfile(profileName.ToLowerInvariant());
                        if (skuProfile != null)
                        {
                            pl1 = skuProfile.CpuPl1;
                            pl2 = skuProfile.CpuPl2;
                        }
                        else
                        {
                            (pl1, pl2) = profileName.ToLowerInvariant() switch
                            {
                                "office" => (45, 45),
                                "gaming" => (75, 85),
                                "turbo" => (135, 135),
                                _ => (75, 85)
                            };
                        }
                    }
                    ec.ApplyCpuPowerLimits(pl1, pl2);
                }
            }
        }

        private static void ApplyVfCurve()
        {
            var presets = VfCurvePresetStore.Load();
            int activeSlot = 0;
            for (int i = 0; i < presets.Length; i++)
            {
                if (presets[i].HasChanges)
                {
                    activeSlot = i;
                    break;
                }
            }

            var offsets = activeSlot < presets.Length && presets[activeSlot].HasChanges
                ? presets[activeSlot].Offsets : new Dictionary<int, int>();

            if (offsets.Count == 0)
                return;

            // Read GPU power settings from Custom profile saved values
            int tgp = (int)Settings.Default.customNvPower;
            bool dbEnabled = Settings.Default.customNvDb;
            int dbOffset = dbEnabled ? (int)Settings.Default.customNvDbOffset : 0;

            // Apply EC power limit BEFORE the curve write. The NVIDIA driver
            // on Blackwell laptops may check the EC power ceiling when applying
            // VF offsets and further reduce power if it sees a low ceiling.
            NvTuning.SetPowerLimit(tgp + dbOffset, dbOffset);

            int result = NvTuning.ApplyVfCurveOffsets_ClockBoostTable(offsets);
            Debug.WriteLine($"[PresetProfileApplier] V-F curve applied: {(result > 0 ? "OK" : "failed")}, slot={activeSlot}");

            // Re-apply power limit AFTER in case the driver overrides the EC ceiling.
            NvTuning.SetPowerLimit(tgp + dbOffset, dbOffset);
        }

        private static void ApplyVfCurveFromPreset(CustomPreset preset)
        {
            var offsets = preset.VfCurveOffsets;
            if (offsets.Count == 0)
                return;

            int tgp = (int)preset.GpuTgp;
            bool dbEnabled = preset.GpuDynamicBoost;
            int dbOffset = dbEnabled ? (int)preset.GpuDbOffset : 0;

            NvTuning.SetPowerLimit(tgp + dbOffset, dbOffset);

            int result = NvTuning.ApplyVfCurveOffsets_ClockBoostTable(offsets);
            Debug.WriteLine($"[PresetProfileApplier] V-F curve applied from preset: {(result > 0 ? "OK" : "failed")}");

            NvTuning.SetPowerLimit(tgp + dbOffset, dbOffset);
        }

        /// <summary>
        /// Builds RyzenAdj command parts from a CustomPreset object.
        /// </summary>
        private static void BuildCustomCommandFromPreset(List<string> parts, CustomPreset preset, bool skipGpuPower)
        {
            if (Family.TYPE == Family.ProcessorType.Amd_Apu)
            {
                int slowLimit = (int)preset.CpuSustained * 1000;
                int fastLimit = (int)preset.CpuBoost * 1000;
                int tjMax = (int)preset.CpuTjMax;

                parts.Add($"--slow-limit={slowLimit}");
                parts.Add($"--fast-limit={fastLimit}");
                parts.Add($"--tctl-temp={tjMax}");
                parts.Add($"--cHTC-temp={tjMax}");

                if (preset.CpuCoUnified)
                {
                    int coValue = (int)preset.CpuCoUnifiedValue;
                    uint coEncoded = coValue < 0
                        ? (uint)(0x1000000 - Math.Abs(coValue))
                        : (uint)coValue;
                    parts.Add($"--set-coall={coEncoded}");
                }
                else
                {
                    int coCcd0 = (int)preset.CpuCoCcd0;
                    int coCcd1 = (int)preset.CpuCoCcd1;
                    for (int ccd = 0; ccd < 2; ccd++)
                    {
                        int offset = ccd == 0 ? coCcd0 : coCcd1;
                        for (int core = 0; core < 8; core++)
                        {
                            uint encoded = BuildCoperArg(ccd, core, offset);
                            parts.Add($"--set-coper={encoded}");
                        }
                    }
                }
            }
            else if (Family.TYPE == Family.ProcessorType.Intel)
            {
                parts.Add($"--intel-pl={preset.IntelPl1:F0},{preset.IntelPl2:F0}");
                if (preset.IntelVolt < 0)
                    parts.Add($"--intel-volt-cpu={preset.IntelVolt:F0}");
            }

            if (!skipGpuPower && !string.IsNullOrEmpty(NvTuning.GetGpuName()))
            {
                int tgp = (int)preset.GpuTgp;
                bool dbEnabled = preset.GpuDynamicBoost;
                int dbOffset = dbEnabled ? (int)preset.GpuDbOffset : 0;
                int totalWatts = tgp + dbOffset;
                int core = (int)preset.GpuCoreOffset;
                int mem = (int)preset.GpuMemOffset;
                parts.Add($"--NVIDIA-Clocks=0-{core}-{mem}-{totalWatts}-{dbOffset}");
            }
        }

        private static void BuildSkuProfileCommand(string profileName, List<string> parts)
        {
            var skuProfile = SkuResolver.GetProfile(profileName.ToLowerInvariant());
            int cpuPl1, cpuPl2, gpuTgp;
            bool gpuDb;
            int gpuDbOffset;

            if (skuProfile != null)
            {
                cpuPl1 = skuProfile.CpuPl1;
                cpuPl2 = skuProfile.CpuPl2;
                gpuTgp = skuProfile.GpuTgp;
                gpuDb = skuProfile.DynamicBoost;
                gpuDbOffset = skuProfile.DynamicBoostOffset;
            }
            else
            {
                // Fallback defaults if SKU not resolved
                (cpuPl1, cpuPl2, gpuTgp, gpuDb, gpuDbOffset) = profileName.ToLowerInvariant() switch
                {
                    "office" => (45, 45, 95, false, 0),
                    "gaming" => (75, 85, 150, false, 0),
                    "turbo" => (135, 135, 150, true, 25),
                    _ => (75, 85, 150, false, 0)
                };
            }

            if (Family.TYPE == Family.ProcessorType.Amd_Apu)
            {
                int slowLimit = cpuPl1 * 1000;
                int fastLimit = cpuPl2 * 1000;

                parts.Add($"--slow-limit={slowLimit}");
                parts.Add($"--fast-limit={fastLimit}");
                parts.Add("--tctl-temp=95");
                parts.Add("--cHTC-temp=95");
            }
            else if (Family.TYPE == Family.ProcessorType.Intel)
            {
                parts.Add($"--intel-pl={cpuPl1},{cpuPl2}");
            }

            // NVIDIA GPU power
            if (!string.IsNullOrEmpty(NvTuning.GetGpuName()))
            {
                int totalWatts = gpuTgp + (gpuDb ? gpuDbOffset : 0);
                parts.Add($"--NVIDIA-Clocks=0-0-0-{totalWatts}-{(gpuDb ? gpuDbOffset : 0)}");
            }
        }

        private static void BuildCustomCommand(List<string> parts, bool skipGpuPower)
        {
            if (Family.TYPE == Family.ProcessorType.Amd_Apu)
            {
                int slowLimit = (int)Settings.Default.customSustained * 1000;
                int fastLimit = (int)Settings.Default.customBoost * 1000;
                int tjMax = (int)Settings.Default.customTjMax;

                parts.Add($"--slow-limit={slowLimit}");
                parts.Add($"--fast-limit={fastLimit}");
                parts.Add($"--tctl-temp={tjMax}");
                parts.Add($"--cHTC-temp={tjMax}");

                // Curve Optimizer — unified mode uses set-coall, per-CCD uses set-coper.
                bool coUnified = Settings.Default.customCoUnified;
                if (coUnified)
                {
                    int coValue = (int)Settings.Default.customCoUnifiedValue;
                    uint coEncoded = coValue < 0
                        ? (uint)(0x100000 - Math.Abs(coValue))
                        : (uint)coValue;
                    parts.Add($"--set-coall={coEncoded}");
                }
                else
                {
                    // Per-CCD mode: use set-coper for each core.
                    // Encoding (Renoir+/AM5): ((ccd << 8) | core_in_ccd) << 20 | offset20
                    // offset20: 20-bit biased — negative = 0x100000 - |offset|
                    int coCcd0 = (int)Settings.Default.customCoCcd0;
                    int coCcd1 = (int)Settings.Default.customCoCcd1;
                    for (int ccd = 0; ccd < 2; ccd++)
                    {
                        int offset = ccd == 0 ? coCcd0 : coCcd1;
                        for (int core = 0; core < 8; core++)
                        {
                            uint encoded = BuildCoperArg(ccd, core, offset);
                            parts.Add($"--set-coper={encoded}");
                        }
                    }
                }
            }
            else if (Family.TYPE == Family.ProcessorType.Intel)
            {
                parts.Add($"--intel-pl={Settings.Default.customIntelPl1:F0},{Settings.Default.customIntelPl2:F0}");
                if (Settings.Default.customIntelVolt < 0)
                    parts.Add($"--intel-volt-cpu={Settings.Default.customIntelVolt:F0}");
            }

            // NVIDIA GPU power — skip if V-F curve is active (power applied via SetPowerLimit instead)
            if (!skipGpuPower && !string.IsNullOrEmpty(NvTuning.GetGpuName()))
            {
                int tgp = (int)Settings.Default.customNvPower;
                bool dbEnabled = Settings.Default.customNvDb;
                int dbOffset = dbEnabled ? (int)Settings.Default.customNvDbOffset : 0;
                int totalWatts = tgp + dbOffset;
                int core = (int)Settings.Default.customNvCore;
                int mem = (int)Settings.Default.customNvMem;
                parts.Add($"--NVIDIA-Clocks=0-{core}-{mem}-{totalWatts}-{dbOffset}");
            }
        }

        /// <summary>
        /// Encodes a per-core Curve Optimizer argument (Renoir+/AM5 format).
        /// Layout: ((ccd << 8) | core) << 20 | offset20
        /// offset20: 20-bit biased — negative = 0x100000 - |offset|
        /// </summary>
        private static uint BuildCoperArg(int ccd, int core, int offset)
        {
            int magnitude = Math.Min(Math.Abs(offset), 0xFFFFF);
            uint encoded20 = offset < 0
                ? (uint)((0x100000 - magnitude) & 0xFFFFF)
                : (uint)(magnitude & 0xFFFFF);
            uint prefix = (uint)(((ccd << 8) | (core & 7)) << 20);
            return prefix | encoded20;
        }

        /// <summary>
        /// Returns the human-readable display name for a profile.
        /// Built-in profiles return the profile name as-is.
        /// Custom presets are displayed as "Custom 1", "Custom 2", etc.
        /// </summary>
        public static string DisplayName(string profileName)
        {
            // Check for custom preset slots first (e.g. "Custom 3")
            int customSlot = TryParseCustomSlot(profileName);
            if (customSlot >= 0)
                return $"Custom {customSlot + 1}";

            return profileName switch
            {
                string s when s.Equals("Office", StringComparison.OrdinalIgnoreCase) => "Office",
                string s when s.Equals("Gaming", StringComparison.OrdinalIgnoreCase) => "Gaming",
                string s when s.Equals("Turbo", StringComparison.OrdinalIgnoreCase) => "Turbo",
                string s when s.Equals("Custom", StringComparison.OrdinalIgnoreCase) => "Custom",
                _ => profileName
            };
        }
    }
}
