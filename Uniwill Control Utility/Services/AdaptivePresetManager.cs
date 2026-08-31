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
        // Watercooler (Hydro UI) per-game settings
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
        public byte KbRestColorR { get; set; } = 255;
        public byte KbRestColorG { get; set; } = 255;
        public byte KbRestColorB { get; set; } = 255;
        public string KbMultiColors { get; set; } = "";
        public string? KbPerKeyColors { get; set; }

        // CPU per-game settings (Presets-style)
        public bool CpuEnabled { get; set; } = false;
        // AMD SMU
        public int CpuSustained { get; set; } = 75;    // 5-255W (SPL)
        public int CpuBoost { get; set; } = 85;        // 5-255W (FPL)
        public int CpuTjMax { get; set; } = 95;        // 70-105°C (AMD only)
        public int CpuCo { get; set; } = 0;            // -50 to 0 (AMD only, Curve Optimizer)
        // Intel MSR
        public int CpuPl1 { get; set; } = 45;          // 5-400W (Intel only)
        public int CpuPl2 { get; set; } = 60;          // 5-400W (Intel only)
        public int CpuUndervolt { get; set; } = 0;     // -150 to 0 (Intel only)

        // GPU per-game settings (Presets-style)
        public bool GpuEnabled { get; set; } = false;
        public int GpuTgp { get; set; } = 150;         // 0-150W
        public bool GpuDynamicBoost { get; set; } = false;
        public int GpuDbOffset { get; set; } = 5;      // 0-25W
        public int GpuCoreClock { get; set; } = 0;     // -1000 to 1000 MHz
        public int GpuMemClock { get; set; } = 0;      // -1000 to 2000 MHz
        public string GpuVfCurveName { get; set; } = string.Empty; // Active V-F curve preset name (slot 1-5)


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

    public class AdaptivePresetManager
    {
        private string _filePath;
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

        public AdaptivePreset GetPreset(string presetName)
        {
            if (_presets.ContainsKey(presetName))
            {
                return _presets[presetName];
            }
            else
            {
                return null;
            }
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
                string json = File.ReadAllText(_filePath);
                _presets = JsonConvert.DeserializeObject<Dictionary<string, AdaptivePreset>>(json);
            }
            else
            {
                _presets = new Dictionary<string, AdaptivePreset>();
            }
        }


        private void SavePresets()
        {
            string json = JsonConvert.SerializeObject(_presets, Newtonsoft.Json.Formatting.Indented);
            File.WriteAllText(_filePath, json);
        }
    }
}
