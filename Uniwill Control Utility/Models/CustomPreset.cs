using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Uniwill_Control_Utility.Models
{
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
    /// </summary>
    public static class CustomPresetStore
    {
        private static string? _filePath;

        private static string FilePath
        {
            get
            {
                if (_filePath != null) return _filePath;
                var appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UCU");
                Directory.CreateDirectory(appData);
                _filePath = Path.Combine(appData, "custom_presets.json");
                return _filePath;
            }
        }

        public static CustomPreset[] Load()
        {
            var presets = Enumerable.Range(0, CustomPreset.MaxSlots)
                .Select(_ => new CustomPreset())
                .ToArray();

            if (!File.Exists(FilePath))
                return presets;

            try
            {
                var json = File.ReadAllText(FilePath);
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

        public static void Save(CustomPreset[] presets)
        {
            try
            {
                var json = JsonSerializer.Serialize(presets.ToList(), new JsonSerializerOptions
                {
                    WriteIndented = true
                });
                File.WriteAllText(FilePath, json);
            }
            catch
            {
                // Silent fail — presets are in-memory
            }
        }
    }
}
