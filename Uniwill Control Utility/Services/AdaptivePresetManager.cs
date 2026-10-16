using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml;

namespace Uniwill_Control_Utility.Services
{
    public class AdaptivePreset
    {
        // HydroUI (Hydro UI) per-game settings
        public bool WcEnabled { get; set; } = false;
        public string WcPumpVoltage { get; set; } = "V7";
        public string WcFanSpeed { get; set; } = "Percent50";
        public string WcRgbMode { get; set; } = "Static";
        public string WcRgbColor { get; set; } = "Red";

        // BS2 Pro per-game settings
        public bool Bs2ProEnabled { get; set; } = false;
        public string Bs2ProFanMode { get; set; } = "Off"; // "Off", "Gear", "Rpm", "Curve"
        public int Bs2ProGear { get; set; } = 1;           // 1-4 (Quiet/Standard/Strong/Overclock)
        public ushort Bs2ProRpm { get; set; } = 2000;      // 1300-4000 manual RPM
        public string Bs2ProCurveProfileId { get; set; } = string.Empty; // GUID of active curve profile
        public string Bs2ProCurvePointsJson { get; set; } = string.Empty; // JSON of curve points for per-game Custom curve

        // BS2 Pro RGB per-game settings
        public string Bs2ProRgbMode { get; set; } = "Static"; // "Off", "SmartTemp", "Static", "Flowing", "Breathing"
        public byte Bs2ProRgbR { get; set; } = 0;
        public byte Bs2ProRgbG { get; set; } = 0;
        public byte Bs2ProRgbB { get; set; } = 255;
        public byte Bs2ProBrightness { get; set; } = 100;
        public byte Bs2ProRgbSpeed { get; set; } = 5;

        // EC Fan Control per-game settings
        public bool EcFanEnabled { get; set; } = false;
        public bool EcFanUnifiedMode { get; set; } = false;
        public string EcFanPreset { get; set; } = "Balanced";
        public int[]? EcFanCustomDuties { get; set; }
        public string EcFanCpuPreset { get; set; } = "Balanced";
        public int[]? EcFanCpuCustomDuties { get; set; }
        public string EcFanGpuPreset { get; set; } = "Balanced";
        public int[]? EcFanGpuCustomDuties { get; set; }

        // Keyboard RGB per-game settings
        public bool KbEnabled { get; set; } = false;
        public bool KbPerKeyMode { get; set; } = false;
        public int KbBrightness { get; set; } = 5;
        public bool KbIdleTimerEnabled { get; set; } = false;
        public int KbIdleTimerMinutes { get; set; } = 10;
        public string KbEffectMode { get; set; } = "Static";
        public byte KbEffectSpeed { get; set; } = 5;
        public string KbDirection { get; set; } = "LeftRight";
        public byte KbColorR { get; set; } = 0;
        public byte KbColorG { get; set; } = 255;
        public byte KbColorB { get; set; } = 255;
        public string KbMultiColors { get; set; } = "";
        public string? KbPerKeyColors { get; set; }

        // BetterRGB streamer per-game settings - full mirror of the Keyboard page's
        // BetterRGB card (nested so the preset carries every per-effect parameter)
        public AdaptiveBetterRgbSettings KbBetterRgb { get; set; } = new();

        // CPU per-game settings (Presets-style)
        public bool CpuEnabled { get; set; } = false;
        // AMD SMU
        public int CpuSustained { get; set; } = 75;    // 5-255W (SPL)
        public int CpuBoost { get; set; } = 85;        // 5-255W (FPL)
        public int CpuTjMax { get; set; } = 95;        // 70-105°C (AMD only)
        public bool CpuCoUnified { get; set; } = true; // true = unified, false = per-CCD
        public int CpuCoUnifiedValue { get; set; } = 0; // unified offset (-30 to 0)
        public int CpuCoCcd0 { get; set; } = 0;        // CCD 0 offset (-30 to 0)
        public int CpuCoCcd1 { get; set; } = 0;        // CCD 1 offset (-30 to 0)
        // Intel MSR
        public int CpuPl1 { get; set; } = 45;          // 5-400W (Intel only)
        public int CpuPl2 { get; set; } = 60;          // 5-400W (Intel only)
        public int CpuUndervolt { get; set; } = 0;     // -150 to 0 (Intel only)

        // GPU per-game settings (Presets-style) ? mode-aware
        public bool GpuEnabled { get; set; } = false;
        public AdaptiveGpuSettings GpuNormal { get; set; } = new();
        public AdaptiveGpuSettings GpuBoost { get; set; } = new(boostDefaults: true);


        // Light Bar per-game settings
        public bool LbEnabled { get; set; } = false;
        public bool LbPower { get; set; } = true;
        public byte LbColorR { get; set; } = 0;
        public byte LbColorG { get; set; } = 255;
        public byte LbColorB { get; set; } = 255;
        public int LbBrightness { get; set; } = 100;   // 0-100
        public int LbEffect { get; set; } = 0;          // 0=Static, 1=Breathing, 2=Wave, 3=Loading, 4=Marquee, 5=Meteor
        public int LbSpeed { get; set; } = 5;           // 0-11

        [DefaultValue(true)]
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Populate)]
        public bool isAutoSwitch { get; set; }
    }

    /// <summary>
    /// GPU settings for a single mode (Normal or Boost) within an adaptive per-game profile.
    /// </summary>
    public class AdaptiveGpuSettings
    {
        public int Tgp { get; set; } = 150;            // 0-150W (Normal) / 0-250W (Boost)
        public bool DynamicBoost { get; set; } = false;
        public int DbOffset { get; set; } = 5;         // 0-25W
        public int CoreClock { get; set; } = 0;        // -1000 to 1000 MHz
        public int MemClock { get; set; } = 0;         // -1000 to 3000 MHz

        /// <summary>
        /// Per-game V-F curve offsets: pointIndex ? frequencyOffsetMHz.
        /// Stores true per-profile curve data (not a reference to a global preset).
        /// </summary>
        public Dictionary<int, int> VfCurveOffsets { get; set; } = new();

        /// <summary>
        /// Constructs a new instance. When <paramref name="boostDefaults"/> is true,
        /// the Tgp defaults to 200W (typical Boost-mode starting point).
        /// </summary>
        public AdaptiveGpuSettings(bool boostDefaults = false)
        {
            if (boostDefaults)
                Tgp = 200;
        }
    }

    /// <summary>
    /// BetterRGB streamer settings for a single adaptive per-game profile - mirrors
    /// every control on the Keyboard page's BetterRGB card so the override applies
    /// the full per-effect parameter set, not just effect/colors/FPS/brightness.
    /// </summary>
    public class AdaptiveBetterRgbSettings
    {
        public bool Enabled { get; set; } = false;
        public string Mode { get; set; } = "Breathing";       // display name (Keyboard page list)
        public int Fps { get; set; } = 30;                    // 15-60
        public int BrightnessPercent { get; set; } = 100;     // software 2-100

        // Breathing
        public int BreathSpeed { get; set; } = 5;            // 1-5
        public int BreathSmooth { get; set; } = 60;           // 2-150
        public bool BreathRandom { get; set; } = false;
        public string BreathColor { get; set; } = "#0078FF";

        // Wave
        public int WaveAngle { get; set; } = 0;               // 0-315, 45-degree steps
        public int WaveSpeed { get; set; } = 4;               // 1-5 (renderer clamps to 1-10)

        // Sparkle
        public int SparkleSpeed { get; set; } = 5;            // 1-5
        public int SparkleDensity { get; set; } = 8;          // 3-20

        // Reactive
        public bool ReactiveRandom { get; set; } = true;
        public int ReactiveDuration { get; set; } = 20;       // 10 / 20 / 45
        public string ReactiveColor { get; set; } = "#FFFFFF";

        // Wheel
        public bool WheelReverse { get; set; } = false;
        public int WheelSpeed { get; set; } = 4;              // 1-5 (renderer clamps to 1-10)

        // Lightning
        public int LightningSpeed { get; set; } = 5;          // 1-5
        public int LightningSmooth { get; set; } = 10;        // 1-20
        public int LightningWidth { get; set; } = 2;          // 1-4
        public int LightningConcurrent { get; set; } = 1;     // 1-4

        // Flame
        public int FlameSpeed { get; set; } = 5;              // 1-5
        public int FlameSmooth { get; set; } = 8;             // 1-20

        // Raindrop
        public int RainSpeed { get; set; } = 5;               // 1-5
        public int RainDensity { get; set; } = 3;             // 1-5
        public string RainColors { get; set; } = "";          // CSV, up to 5

        // Matrix
        public int MatrixStyle { get; set; } = 0;             // 0 Diagonal / 1 Columns / 2 Lasers / 3 Rings
        public int MatrixSpeed { get; set; } = 5;             // 1-5
        public int MatrixDensity { get; set; } = 2;           // 1-5
        public string MatrixColors { get; set; } = "";        // CSV, up to 3

        // Static
        public int StaticLayout { get; set; } = 0;            // 0 Horizontal / 1 Vertical
        public int StaticZones { get; set; } = 1;             // 1-7
        public string StaticZoneColors { get; set; } = "";    // CSV, up to 7

        // Ripple
        public bool RippleRandom { get; set; } = true;
        public int RippleSpeed { get; set; } = 5;             // 1-5
        public int RippleWidth { get; set; } = 2;             // 1-4
        public string RippleColor { get; set; } = "#00B4FF";

        // Rainbow
        public int RainbowSpeed { get; set; } = 5;            // 1-5

        // Quicksand
        public int QuicksandSpeed { get; set; } = 5;          // 1-5
        public int QuicksandScale { get; set; } = 5;          // 1-10
        public string QuicksandColors { get; set; } = "";     // CSV, up to 3

        // Current
        public int CurrentSpeed { get; set; } = 5;            // 1-5
        public int CurrentWidth { get; set; } = 4;            // 2-8
        public string CurrentColors { get; set; } = "";       // CSV, up to 3

    }

    public class AdaptivePresetManager
    {
        private readonly string _filePath;
        private Dictionary<string, AdaptivePreset> _presets;

        public AdaptivePresetManager(string filePath)
        {
            _filePath = filePath;
            _presets = new Dictionary<string, AdaptivePreset>();
            LoadPresets();
        }

        public IEnumerable<string> GetPresetNames()
        {
            return _presets.Keys;
        }

        public AdaptivePreset? GetPreset(string presetName)
        {
            return _presets.TryGetValue(presetName, out var preset) ? preset : null;
        }

        public void SavePreset(string name, AdaptivePreset preset)
        {
            _presets[name] = preset;
            SavePresets();
        }

        public void DeletePreset(string name)
        {
            _presets.Remove(name);
            SavePresets();
        }

        private void LoadPresets()
        {
            if (File.Exists(_filePath))
            {
                string json = File.ReadAllText(_filePath, Encoding.UTF8);
                string migrated = MigrateBetterRgbFieldsToJson(MigrateGpuFieldsToJson(json));
                _presets = JsonConvert.DeserializeObject<Dictionary<string, AdaptivePreset>>(migrated)
                    ?? new Dictionary<string, AdaptivePreset>();

                // If migration rewrites happened, persist the new format back to disk
                if (migrated != json)
                    SavePresets();
            }
            else
            {
                _presets = new Dictionary<string, AdaptivePreset>();
            }
        }

        /// <summary>
        /// Migrates old flat BetterRGB fields (KbBetterRgbEnabled, ...) to the nested
        /// KbBetterRgb object in the raw JSON before deserialization.
        /// </summary>
        private static string MigrateBetterRgbFieldsToJson(string json)
        {
            // Quick check: if the JSON already has the nested object, it's already migrated
            if (json.Contains("\"KbBetterRgb\":"))
                return json;

            // Check if there are any old-format BetterRGB fields to migrate
            if (!json.Contains("\"KbBetterRgbEnabled\""))
                return json;

            var tokens = JsonConvert.DeserializeObject<Newtonsoft.Json.Linq.JObject>(json);
            if (tokens == null)
                return json;

            bool changed = false;

            foreach (var property in tokens.Properties())
            {
                var preset = property.Value as Newtonsoft.Json.Linq.JObject;
                if (preset == null)
                    continue;

                if (preset["KbBetterRgbEnabled"] == null)
                    continue;

                // Carry the flat fields over; per-effect params start at defaults
                var br = new Newtonsoft.Json.Linq.JObject
                {
                    ["Enabled"] = preset["KbBetterRgbEnabled"],
                    ["Mode"] = preset["KbBetterRgbMode"] ?? "Breathing",
                    ["Fps"] = preset["KbBetterRgbFps"] ?? 30,
                    ["BrightnessPercent"] = preset["KbBetterRgbBrightness"] ?? 100,
                    ["BreathColor"] = preset["KbBetterRgbColor"] ?? "#FFFFFF",
                    ["ReactiveColor"] = preset["KbBetterRgbColor"] ?? "#FFFFFF",
                    ["RippleColor"] = preset["KbBetterRgbColor"] ?? "#FFFFFF",
                    ["RainColors"] = preset["KbBetterRgbMultiColors"] ?? "",
                    ["MatrixColors"] = preset["KbBetterRgbMultiColors"] ?? "",
                    ["StaticZoneColors"] = preset["KbBetterRgbMultiColors"] ?? "",
                    ["QuicksandColors"] = preset["KbBetterRgbMultiColors"] ?? "",
                    ["CurrentColors"] = preset["KbBetterRgbMultiColors"] ?? ""
                };

                preset.Remove("KbBetterRgbEnabled");
                preset.Remove("KbBetterRgbMode");
                preset.Remove("KbBetterRgbFps");
                preset.Remove("KbBetterRgbBrightness");
                preset.Remove("KbBetterRgbColor");
                preset.Remove("KbBetterRgbMultiColors");

                preset["KbBetterRgb"] = br;
                changed = true;
            }

            if (!changed)
                return json;

            return JsonConvert.SerializeObject(tokens, Newtonsoft.Json.Formatting.Indented);
        }

        /// <summary>
        /// Migrates old flat GPU fields (GpuTgp, GpuDynamicBoost, etc.) to the new
        /// mode-aware GpuNormal/GpuBoost structure in the raw JSON before deserialization.
        /// </summary>
        private static string MigrateGpuFieldsToJson(string json)
        {
            // Quick check: if the JSON already has GpuNormal, it's already migrated
            if (json.Contains("\"GpuNormal\""))
                return json;

            // Check if there are any old-format GPU fields to migrate
            if (!json.Contains("\"GpuTgp\""))
                return json;

            var tokens = JsonConvert.DeserializeObject<Newtonsoft.Json.Linq.JObject>(json);
            if (tokens == null)
                return json;

            bool changed = false;

            foreach (var property in tokens.Properties())
            {
                var preset = property.Value as Newtonsoft.Json.Linq.JObject;
                if (preset == null)
                    continue;

                var gpuTgp = preset["GpuTgp"];
                if (gpuTgp == null)
                    continue;

                // Build GpuNormal from old flat fields - migrate VfCurveName to empty VfCurveOffsets dict
                var gpuNormal = new Newtonsoft.Json.Linq.JObject
                {
                    ["Tgp"] = preset["GpuTgp"] ?? new Newtonsoft.Json.Linq.JValue(150),
                    ["DynamicBoost"] = preset["GpuDynamicBoost"] ?? false,
                    ["DbOffset"] = preset["GpuDbOffset"] ?? 5,
                    ["CoreClock"] = preset["GpuCoreClock"] ?? 0,
                    ["MemClock"] = preset["GpuMemClock"] ?? 0,
                    ["VfCurveOffsets"] = new Newtonsoft.Json.Linq.JObject()
                };

                // GpuBoost starts with defaults (Tgp=200, no DB)
                var gpuBoost = new Newtonsoft.Json.Linq.JObject
                {
                    ["Tgp"] = new Newtonsoft.Json.Linq.JValue(200),
                    ["DynamicBoost"] = false,
                    ["DbOffset"] = new Newtonsoft.Json.Linq.JValue(0),
                    ["CoreClock"] = new Newtonsoft.Json.Linq.JValue(0),
                    ["MemClock"] = new Newtonsoft.Json.Linq.JValue(0),
                    ["VfCurveOffsets"] = new Newtonsoft.Json.Linq.JObject()
                };

                // Remove old flat fields
                preset.Remove("GpuTgp");
                preset.Remove("GpuDynamicBoost");
                preset.Remove("GpuDbOffset");
                preset.Remove("GpuCoreClock");
                preset.Remove("GpuMemClock");
                preset.Remove("GpuVfCurveName");

                // Add new mode-aware fields
                preset["GpuNormal"] = gpuNormal;
                preset["GpuBoost"] = gpuBoost;
                changed = true;
            }

            if (!changed)
                return json;

            return JsonConvert.SerializeObject(tokens, Newtonsoft.Json.Formatting.Indented);
        }


        private void SavePresets()
        {
            string json = JsonConvert.SerializeObject(_presets, Newtonsoft.Json.Formatting.Indented);
            File.WriteAllText(_filePath, json, Encoding.UTF8);
        }
    }
}