using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Uniwill_Control_Utility.Models;
using Uniwill_Control_Utility.Scripts;
using Uniwill_Control_Utility.Scripts.GPUs.NVIDIA;

namespace Uniwill_Control_Utility.Services
{
    /// <summary>
    /// Resolves the current laptop SKU from the skus.json database.
    /// Matches on CPU vendor (AMD/Intel) and GPU model (from NVML).
    /// </summary>
    public static class SkuResolver
    {
        private static SkuEntry? _current;
        private static bool _initialized;

        /// <summary>
        /// The currently matched SKU entry. Lazy-loaded via Initialize().
        /// </summary>
        public static SkuEntry? Current => _current;

        /// <summary>
        /// Loads the SKU database and matches the current hardware.
        /// Call once at application startup after Family.setType() and NVML init.
        /// </summary>
        public static void Initialize()
        {
            if (_initialized)
                return;

            _initialized = true;

            var database = LoadDatabase();
            if (database == null)
                return;

            var cpuVendor = Family.TYPE == Family.ProcessorType.Intel ? "Intel" : "AMD";
            var gpuName = NvTuning.GetGpuName();

            if (string.IsNullOrEmpty(gpuName))
                return;

            _current = MatchSku(database, cpuVendor, gpuName);
        }

        /// <summary>
        /// Returns the power limits for the current SKU based on cooling method.
        /// </summary>
        /// <param name="watercoolerConnected">Whether a Flydigi watercooler is actively connected.</param>
        /// <returns>The matching power limits, or null if no SKU is resolved.</returns>
        public static SkuPowerLimitsCooling? GetPowerLimits(bool watercoolerConnected)
        {
            var sku = Current;
            if (sku == null)
                return null;

            return watercoolerConnected ? sku.PowerLimits.Water : sku.PowerLimits.Air;
        }

        /// <summary>
        /// Returns a named profile (office, gaming, turbo) for the current SKU.
        /// </summary>
        /// <param name="profileName">One of "office", "gaming", or "turbo" (case-insensitive).</param>
        /// <returns>The matching profile entry, or null if no SKU is resolved or the profile name is invalid.</returns>
        public static SkuProfileEntry? GetProfile(string profileName)
        {
            var sku = Current;
            if (sku == null)
                return null;

            return profileName switch
            {
                _ when profileName.Equals("office", StringComparison.OrdinalIgnoreCase) => sku.Profiles.Office,
                _ when profileName.Equals("gaming", StringComparison.OrdinalIgnoreCase) => sku.Profiles.Gaming,
                _ when profileName.Equals("turbo", StringComparison.OrdinalIgnoreCase) => sku.Profiles.Turbo,
                _ => null
            };
        }

        private static SkuDatabase? LoadDatabase()
        {
            try
            {
                var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SkuData", "skus.json");
                if (!File.Exists(path))
                    return null;

                var json = File.ReadAllText(path);
                return JsonConvert.DeserializeObject<SkuDatabase>(json);
            }
            catch
            {
                return null;
            }
        }

        private static SkuEntry? MatchSku(SkuDatabase database, string cpuVendor, string gpuName)
        {
            // Filter by CPU vendor from the entry's cpu.vendor field, then match GPU.
            // Platform-agnostic — does not rely on XMG chassis codes (A25/E25) in the SKU ID,
            // so PCSpecialist, TUXEDO, and other rebrands with the same hardware will match.
            // Order by NvmlNamePattern length descending so "5070 Ti" is checked before "5070".
            var candidates = database.Skus
                .Where(s => s.Cpu.Vendor.Equals(cpuVendor, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(s => s.Gpu.NvmlNamePattern.Length)
                .ToList();

            foreach (var candidate in candidates)
            {
                if (gpuName.Contains(candidate.Gpu.NvmlNamePattern, StringComparison.OrdinalIgnoreCase))
                    return candidate;
            }

            return null;
        }
    }
}
