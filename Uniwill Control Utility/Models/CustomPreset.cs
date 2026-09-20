using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Uniwill_Control_Utility.Models
{
    /// <summary>
    /// Which preset slot pool to use — Normal (Dynamic Boost available) or Boost (GPU Power Boost ON, pure TGP).
    /// </summary>
    public enum PresetMode
    {
        Normal,
        Boost
    }

    /// <summary>
    /// A single Custom preset slot that stores ALL tuning controls (CPU + GPU + V-F curve).
    /// </summary>
    public class CustomPreset
    {
        public const int MaxSlots = 5;

        /// <summary> CPU Sustained (SPL) in watts </summary>
        public double CpuSustained { get; set; } = 75;

        /// <summary> CPU Boost (FPL) in watts </summary>
        public double CpuBoost { get; set; } = 85;

        /// <summary> CPU TjMax in degrees Celsius </summary>
        public double CpuTjMax { get; set; } = 95;

        /// <summary> CPU Curve Optimizer offset for CCD0 (signed, -30 to +30) </summary>
        public double CpuCoCcd0 { get; set; } = 0;

        /// <summary> CPU Curve Optimizer offset for CCD1 (signed, -30 to +30) </summary>
        public double CpuCoCcd1 { get; set; } = 0;

        /// <summary> Unified CO mode (true = single offset, false = per-CCD) </summary>
        public bool CpuCoUnified { get; set; } = false;

        /// <summary> Unified CO offset value (signed, -30 to 0) </summary>
        public double CpuCoUnifiedValue { get; set; } = 0;

        /// <summary> Intel PL1 in watts </summary>
        public double IntelPl1 { get; set; } = 45;

        /// <summary> Intel PL2 in watts </summary>
        public double IntelPl2 { get; set; } = 60;

        /// <summary> Intel CPU Undervolt in mV </summary>
        public double IntelVolt { get; set; } = 0;

        /// <summary> GPU TGP in watts </summary>
        public double GpuTgp { get; set; } = 150;

        /// <summary> GPU Dynamic Boost enabled </summary>
        public bool GpuDynamicBoost { get; set; } = false;

        /// <summary> GPU Dynamic Boost Offset in watts </summary>
        public double GpuDbOffset { get; set; } = 0;

        /// <summary> GPU Core Clock Offset in MHz </summary>
        public double GpuCoreOffset { get; set; } = 0;

        /// <summary> GPU Memory Clock Offset in MHz </summary>
        public double GpuMemOffset { get; set; } = 0;

        /// <summary> V-F curve offsets: pointIndex → frequencyOffsetMHz </summary>
        public Dictionary<int, int> VfCurveOffsets { get; set; } = new();

        private static bool Differs(double value, double defaultValue) => Math.Abs(value - defaultValue) > 1e-9;

        public bool HasChanges => Differs(CpuSustained, 75) || Differs(CpuBoost, 85) || Differs(CpuTjMax, 95) ||
            Differs(CpuCoCcd0, 0) || Differs(CpuCoCcd1, 0) ||
            Differs(IntelPl1, 45) || Differs(IntelPl2, 60) || Differs(IntelVolt, 0) ||
            Differs(GpuTgp, 150) || GpuDynamicBoost || Differs(GpuDbOffset, 0) ||
            Differs(GpuCoreOffset, 0) || Differs(GpuMemOffset, 0) ||
            VfCurveOffsets.Any(p => p.Value != 0);
    }

    /// <summary>
    /// Persists 5 Custom preset slots to a JSON file in the user's AppData folder.
    /// Maintains two separate pools — Normal (Dynamic Boost available) and Boost (GPU Power Boost ON, pure TGP).
    /// </summary>
    public static class CustomPresetStore
    {
        private static readonly JsonSerializerOptions _serializeOptions = new()
        {
            WriteIndented = true
        };

        private static string AppDataDirectory
        {
            get
            {
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UCU");
                Directory.CreateDirectory(dir);
                return dir;
            }
        }

        private static string FilePathFor(PresetMode mode)
        {
            return Path.Combine(AppDataDirectory, mode == PresetMode.Normal ? "custom_presets_normal.json" : "custom_presets_boost.json");
        }

        /// <summary>
        /// Legacy path for backward compatibility — maps to Normal mode.
        /// </summary>
        public static string FilePath => FilePathFor(PresetMode.Normal);

        /// <summary>
        /// Loads 5 preset slots for the given mode.
        /// </summary>
        public static CustomPreset[] Load(PresetMode mode)
        {
            var presets = Enumerable.Range(0, CustomPreset.MaxSlots)
                .Select(_ => new CustomPreset())
                .ToArray();

            var path = FilePathFor(mode);
            if (!File.Exists(path))
                return presets;

            try
            {
                var json = File.ReadAllText(path);
                var loaded = JsonSerializer.Deserialize<List<CustomPreset>>(json);
                if (loaded != null)
                {
                    for (int i = 0; i < CustomPreset.MaxSlots && i < loaded.Count; i++)
                    {
                        presets[i] = loaded[i] ?? new CustomPreset();
                    }
                }
            }
            catch
            {
                // Corrupt file — return empty presets
            }

            return presets;
        }

        /// <summary>
        /// Legacy overload — loads Normal mode presets for backward compatibility.
        /// </summary>
        public static CustomPreset[] Load() => Load(PresetMode.Normal);

        /// <summary>
        /// Saves 5 preset slots for the given mode.
        /// </summary>
        public static void Save(CustomPreset[] presets, PresetMode mode)
        {
            try
            {
                var json = JsonSerializer.Serialize(presets.ToList(), _serializeOptions);
                File.WriteAllText(FilePathFor(mode), json);
            }
            catch
            {
                // Silent fail — presets are in-memory
            }
        }

        /// <summary>
        /// Legacy overload — saves to Normal mode for backward compatibility.
        /// </summary>
        public static void Save(CustomPreset[] presets) => Save(presets, PresetMode.Normal);

        /// <summary>
        /// Migrates legacy custom_presets.json to the new Normal-mode file if it doesn't already exist.
        /// Call once at startup.
        /// </summary>
        public static void MigrateLegacyFile()
        {
            var legacyPath = Path.Combine(AppDataDirectory, "custom_presets.json");
            var normalPath = FilePathFor(PresetMode.Normal);

            if (File.Exists(legacyPath) && !File.Exists(normalPath))
            {
                try
                {
                    File.Copy(legacyPath, normalPath);
                }
                catch
                {
                    // Ignore migration failures — Normal pool will start empty
                }
            }
        }
    }
}
