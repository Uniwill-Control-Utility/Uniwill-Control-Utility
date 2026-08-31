using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Universal_x86_Tuning_Utility.Models;
using Universal_x86_Tuning_Utility.Properties;
using Universal_x86_Tuning_Utility.Scripts;
using Universal_x86_Tuning_Utility.Scripts.GPUs.NVIDIA;

namespace Universal_x86_Tuning_Utility.Services
{
    /// <summary>
    /// Generates RyzenAdj command strings from Presets page profile names
    /// (Office, Gaming, Turbo, Custom) for use by the Automations page
    /// and MainWindow power-mode handlers.
    /// </summary>
    public static class PresetProfileApplier
    {
        /// <summary>
        /// The profile names available from the Presets page.
        /// </summary>
        public static readonly string[] ProfileNames = { "Office", "Gaming", "Turbo", "Custom" };

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
        /// For Custom profile, skips GPU power if a V-F curve is active
        /// (power is applied separately through SetPowerLimit around the curve).
        /// Returns empty string for "None" or unknown profiles.
        /// </summary>
        public static string BuildCommandString(string profileName)
        {
            if (string.IsNullOrEmpty(profileName) || profileName.Equals("None", StringComparison.OrdinalIgnoreCase))
                return string.Empty;

            var parts = new List<string>();
            bool vfCurveActive = false;

            if (profileName.Equals("Custom", StringComparison.OrdinalIgnoreCase))
            {
                vfCurveActive = HasActiveVfCurve();
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
            await RyzenAdj_To_UXTU.TranslateAsync(command, appliedName: display, localizeAppliedName: false);

            // 2. V-F curve (Custom profile only, if active)
            if (profileName.Equals("Custom", StringComparison.OrdinalIgnoreCase) && HasActiveVfCurve())
            {
                ApplyVfCurve();
            }

            // 3. EC CPU power limits (AMD APU only)
            if (Family.TYPE == Family.ProcessorType.Amd_Apu)
            {
                var ec = RyzenAdj_To_UXTU.EcService;
                if (ec != null)
                {
                    int pl1, pl2;
                    if (profileName.Equals("Custom", StringComparison.OrdinalIgnoreCase))
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

                if (Settings.Default.customCoAll > 0)
                    parts.Add($"--set-coall={(byte)Settings.Default.customCoAll}");
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
        /// Returns the human-readable display name for a profile.
        /// </summary>
        public static string DisplayName(string profileName)
        {
            return profileName switch
            {
                string s when s.Equals("Office", StringComparison.OrdinalIgnoreCase) => "Office Preset",
                string s when s.Equals("Gaming", StringComparison.OrdinalIgnoreCase) => "Gaming Preset",
                string s when s.Equals("Turbo", StringComparison.OrdinalIgnoreCase) => "Turbo Preset",
                string s when s.Equals("Custom", StringComparison.OrdinalIgnoreCase) => "Custom Preset",
                _ => profileName
            };
        }
    }
}
