using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Uniwill_Control_Utility.Services;

namespace Uniwill_Control_Utility.Models
{
    /// <summary>
    /// Which preset slot pool to use — Normal (Dynamic Boost available) or Boost (Power boost mode ON, pure TGP).
    /// </summary>
    public enum PresetMode
    {
        Normal,
        Boost
    }

    /// <summary>
    /// How the CPU's Curve Optimizer offsets are addressed. Unified writes one value for the
    /// whole package; PerCcd writes one per CCD; PerCore writes one per physical core.
    /// Per-core is the only mode that needs a topology to know how many cores exist.
    /// </summary>
    public enum CpuCoMode
    {
        Unified,
        PerCcd,
        PerCore
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

        /// <summary> CPU Curve Optimizer offset for CCD0 (signed, -50 to 0) </summary>
        public double CpuCoCcd0 { get; set; } = 0;

        /// <summary> CPU Curve Optimizer offset for CCD1 (signed, -50 to 0) </summary>
        public double CpuCoCcd1 { get; set; } = 0;

        /// <summary>
        /// The mode selector. <see cref="CpuCoUnified"/> is the pre-per-core field and stays
        /// authoritative for the unified case: a preset saved before per-core existed has the
        /// bool and no mode, and the deserializer's default (PerCcd) is exactly what
        /// <c>false</c> meant. See <see cref="Normalize"/>.
        /// </summary>
        public CpuCoMode CpuCoMode { get; set; } = CpuCoMode.PerCcd;

        /// <summary>
        /// Per-core offsets, indexed by <c>CpuCore.Index</c> from the topology, clamped to
        /// -50..0. Shorter than the core count (or empty) means "not configured": the missing
        /// cores fall back to their CCD's value when the map is built.
        /// </summary>
        public int[] CpuCoPerCore { get; set; } = Array.Empty<int>();

        /// <summary>
        /// Legacy mode selector (true = unified, false = per-CCD), kept so presets saved before
        /// per-core existed still load and so older builds can still read ours.
        /// <see cref="CpuCoMode"/> is the authority; keep this in step when writing.
        /// </summary>
        public bool CpuCoUnified { get; set; } = false;

        /// <summary> Unified CO offset value (signed, -50 to 0) </summary>
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

        /// <summary>
        /// The Extra GPU power rail currents this slot carries, in mA — the NVVDD (GPU core),
        /// MSVDD (memory) and board input rails. Their defaults are the tested 250 W tier's
        /// runtime values (the same numbers the page's <c>Tested*Milliamps</c> constants hold),
        /// so a preset file written before the fields existed loads with the tier's rails and
        /// keeps working.
        /// </summary>
        public const int DefaultRailNvvddMilliamps = 250_000;

        /// <inheritdoc cref="DefaultRailNvvddMilliamps"/>
        public const int DefaultRailMsvddMilliamps = 70_000;

        /// <inheritdoc cref="DefaultRailNvvddMilliamps"/>
        public const int DefaultRailBoardInputMilliamps = 14_026;

        /// <inheritdoc cref="DefaultRailNvvddMilliamps"/>
        public int GpuRailNvvddMilliamps { get; set; } = DefaultRailNvvddMilliamps;

        /// <inheritdoc cref="DefaultRailNvvddMilliamps"/>
        public int GpuRailMsvddMilliamps { get; set; } = DefaultRailMsvddMilliamps;

        /// <inheritdoc cref="DefaultRailNvvddMilliamps"/>
        public int GpuRailBoardInputMilliamps { get; set; } = DefaultRailBoardInputMilliamps;

        #region Fan Control Settings
        /// <summary> Whether Unified Fan Curve mode is enabled (stored per-preset). </summary>
        public bool FanUnifiedMode { get; set; }

        /// <summary> Unified preset name (Silent, Balanced, Performance, Full Speed, Off, Custom). </summary>
        public string? FanUnifiedPreset { get; set; }

        /// <summary> Custom duty values (0-100%) for unified mode, 11 zone indices. </summary>
        public int[]? FanUnifiedDuties { get; set; }

        /// <summary> Custom temperature thresholds for unified mode, 11 zone indices. </summary>
        public int[]? FanUnifiedTempThresholds { get; set; }

        /// <summary> CPU preset name (split mode). </summary>
        public string? FanCpuPreset { get; set; }

        /// <summary> Custom duty values (0-100%) for CPU, 11 zone indices. </summary>
        public int[]? FanCpuDuties { get; set; }

        /// <summary> Custom temperature thresholds for CPU, 11 zone indices. </summary>
        public int[]? FanCpuTempThresholds { get; set; }

        /// <summary> GPU preset name (split mode). </summary>
        public string? FanGpuPreset { get; set; }

        /// <summary> Custom duty values (0-100%) for GPU, 11 zone indices. </summary>
        public int[]? FanGpuDuties { get; set; }

        /// <summary> Custom temperature thresholds for GPU, 11 zone indices. </summary>
        public int[]? FanGpuTempThresholds { get; set; }

        /// <summary> Whether Smart Fan Mode is enabled. </summary>
        public bool FanSmartFanMode { get; set; }

        /// <summary> Temperature source for Smart Fan Mode: "cpu", "gpu", or "max". </summary>
        public string? FanSmartFanTempSource { get; set; }
        #endregion

        private static bool Differs(double value, double defaultValue) => Math.Abs(value - defaultValue) > 1e-9;

        /// <summary>True when any fan control field differs from its default (empty/false/null). </summary>
        public bool HasFanChanges => FanUnifiedMode ||
            !string.IsNullOrEmpty(FanUnifiedPreset) ||
            FanUnifiedDuties != null || FanUnifiedTempThresholds != null ||
            !string.IsNullOrEmpty(FanCpuPreset) ||
            FanCpuDuties != null || FanCpuTempThresholds != null ||
            !string.IsNullOrEmpty(FanGpuPreset) ||
            FanGpuDuties != null || FanGpuTempThresholds != null ||
            FanSmartFanMode ||
            !string.IsNullOrEmpty(FanSmartFanTempSource);

        /// <summary>
        /// Lowest Curve Optimizer offset the models and the UI accept. AMD's CO is a
        /// reduction only: a positive offset asks for more voltage, which no AGESA version
        /// this app targets documents, and an out-of-range value is the classic way to wedge
        /// a core. The app accepted -30 before the per-core work; -50 is the floor the field
        /// firmware accepts.
        /// </summary>
        public const int CoMinimum = -50;

        /// <summary>Clamps a Curve Optimizer offset to -50..0.</summary>
        public static int ClampCo(int value) => Math.Clamp(value, CoMinimum, 0);

        /// <summary>Clamps a Curve Optimizer offset to -50..0.</summary>
        public static double ClampCo(double value) => Math.Clamp(value, CoMinimum, 0);

        /// <summary>
        /// Brings a deserialized preset into the current shape: the legacy unified bool maps
        /// onto the mode, every offset is clamped to -50..0, and a null per-core array becomes
        /// empty. Pure and idempotent - it runs on load and never writes.
        /// </summary>
        public void Normalize()
        {
#pragma warning disable CS0618 // the legacy field is exactly what this maps
            if (CpuCoUnified)
                CpuCoMode = CpuCoMode.Unified;
#pragma warning restore CS0618

            CpuCoUnifiedValue = ClampCo(CpuCoUnifiedValue);
            CpuCoCcd0 = ClampCo(CpuCoCcd0);
            CpuCoCcd1 = ClampCo(CpuCoCcd1);

            // A hand-edited file must not leave a rail at zero or below: the tier's value stands,
            // which is also what an absent field already means.
            if (GpuRailNvvddMilliamps <= 0)
                GpuRailNvvddMilliamps = DefaultRailNvvddMilliamps;
            if (GpuRailMsvddMilliamps <= 0)
                GpuRailMsvddMilliamps = DefaultRailMsvddMilliamps;
            if (GpuRailBoardInputMilliamps <= 0)
                GpuRailBoardInputMilliamps = DefaultRailBoardInputMilliamps;

            if (CpuCoPerCore is null || CpuCoPerCore.Length == 0)
            {
                CpuCoPerCore = Array.Empty<int>();
                return;
            }

            for (int i = 0; i < CpuCoPerCore.Length; i++)
                CpuCoPerCore[i] = ClampCo(CpuCoPerCore[i]);
        }

        /// <summary>
        /// The per-core offsets to write: one entry per topology core, in core order. A value
        /// the preset stores wins; a missing one falls back to its CCD's offset, so a preset
        /// authored under per-CCD keeps its meaning when the user switches to per-core and no
        /// core is left at a stale value. Parts with more than two CCDs (not a target) take
        /// CCD0's value above index 1.
        /// </summary>
        public int[] BuildPerCoreMap(CpuTopology topology)
        {
            ArgumentNullException.ThrowIfNull(topology);

            var map = new int[topology.CoreCount];
            foreach (CpuCore core in topology.Cores)
            {
                int fallback = ClampCo((int)(core.CcdIndex == 0 ? CpuCoCcd0 : CpuCoCcd1));
                map[core.Index] = core.Index < CpuCoPerCore.Length
                    ? ClampCo(CpuCoPerCore[core.Index])
                    : fallback;
            }

            return map;
        }

        /// <summary>True when any CPU/GPU/V-F tuning control differs from its default.</summary>
        public bool HasTuningChanges => Differs(CpuSustained, 75) || Differs(CpuBoost, 85) || Differs(CpuTjMax, 95) ||
            Differs(CpuCoCcd0, 0) || Differs(CpuCoCcd1, 0) ||
            (CpuCoPerCore is not null && CpuCoPerCore.Any(value => value != 0)) ||
            Differs(IntelPl1, 45) || Differs(IntelPl2, 60) || Differs(IntelVolt, 0) ||
            Differs(GpuTgp, 150) || GpuDynamicBoost || Differs(GpuDbOffset, 0) ||
            Differs(GpuCoreOffset, 0) || Differs(GpuMemOffset, 0) ||
            VfCurveOffsets.Any(p => p.Value != 0);

        public bool HasChanges => HasTuningChanges || HasFanChanges;
    }

    /// <summary>
    /// Persists 5 Custom preset slots to a JSON file in the user's AppData folder.
    /// Maintains two separate pools — Normal (Dynamic Boost available) and Boost (Power boost mode ON, pure TGP).
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
        public static CustomPreset[] Load(PresetMode mode) => LoadFrom(FilePathFor(mode));

        /// <summary>
        /// Loads 5 preset slots from one file. The path-taking half exists so a test can drive the
        /// real read over a temporary file instead of the owner's presets.
        /// </summary>
        internal static CustomPreset[] LoadFrom(string path)
        {
            var presets = Enumerable.Range(0, CustomPreset.MaxSlots)
                .Select(_ => new CustomPreset())
                .ToArray();

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

                        // Shape the legacy file into the current model in memory only: the
                        // unified bool becomes the mode, offsets clamp to -50..0. Saving is
                        // the user's edit, never a side effect of reading.
                        presets[i].Normalize();
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
        public static void Save(CustomPreset[] presets, PresetMode mode) => SaveTo(FilePathFor(mode), presets);

        /// <summary>Writes the 5 slots to one file; see <see cref="LoadFrom"/>.</summary>
        internal static void SaveTo(string path, CustomPreset[] presets)
        {
            try
            {
                var json = JsonSerializer.Serialize(presets.ToList(), _serializeOptions);
                AtomicJsonFile.Write(path, json);
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
