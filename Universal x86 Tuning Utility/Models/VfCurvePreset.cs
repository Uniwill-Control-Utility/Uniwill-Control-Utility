using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Universal_x86_Tuning_Utility.Models
{
    /// <summary>
    /// A single V-F curve preset slot (up to 5).
    /// Stores per-point frequency offsets: pointIndex → MHz offset.
    /// </summary>
    public class VfCurvePreset
    {
        public const int MaxSlots = 5;

        public string Name { get; set; } = string.Empty;

        /// <summary> pointIndex → frequencyOffsetMHz </summary>
        public Dictionary<int, int> Offsets { get; set; } = new();

        public bool HasChanges => Offsets.Any(p => p.Value != 0);

        public void Clear()
        {
            Name = string.Empty;
            Offsets.Clear();
        }
    }

    /// <summary>
    /// Persists 5 V-F curve preset slots to a JSON file in the user's AppData folder.
    /// </summary>
    public static class VfCurvePresetStore
    {
        private static string? _filePath;

        private static string FilePath
        {
            get
            {
                if (_filePath != null) return _filePath;
                var appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UXTU");
                Directory.CreateDirectory(appData);
                _filePath = Path.Combine(appData, "vf_presets.json");
                return _filePath;
            }
        }

        public static VfCurvePreset[] Load()
        {
            var presets = Enumerable.Range(0, VfCurvePreset.MaxSlots)
                .Select(_ => new VfCurvePreset())
                .ToArray();

            if (!File.Exists(FilePath))
                return presets;

            try
            {
                var json = File.ReadAllText(FilePath);
                var loaded = JsonSerializer.Deserialize<List<VfCurvePreset>>(json);
                if (loaded != null)
                {
                    for (int i = 0; i < VfCurvePreset.MaxSlots && i < loaded.Count; i++)
                    {
                        presets[i] = loaded[i] ?? new VfCurvePreset();
                    }
                }
            }
            catch
            {
                // Corrupt file — return empty presets
            }

            return presets;
        }

        public static void Save(VfCurvePreset[] presets)
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
