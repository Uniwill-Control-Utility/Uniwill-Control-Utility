using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml;

namespace Universal_x86_Tuning_Utility.Services
{
    public class AdaptivePreset
    {
        public int Temp { get; set; }
        public int Power { get; set; }
        public int CO { get; set; }
        public int minGFX { get; set; }
        public int MaxGFX { get; set; }
        public int minCPU { get; set; }
        public bool isCO { get; set; }
        public bool isGFX { get; set; }

        public int rsr { get; set; }
        public int boost { get; set; }
        public int imageSharp { get; set; }

        public bool isRadeonGraphics { get; set; }
        public bool isAntiLag { get; set; }
        public bool isRSR { get; set; }
        public bool isBoost { get; set; }
        public bool isImageSharp { get; set; }
        public bool isSync { get; set; }

        public bool isNVIDIA { get; set; }
        public int nvMaxCoreClk { get; set; } = 4000;
        public int nvCoreClk { get; set; }
        public int nvMemClk { get; set; }

        public int asusPowerProfile { get; set; }

        public int windowsBoostMode { get; set; }
        public bool isWindowsMinState { get; set; }
        public int windowsMinState { get; set; } = 5;
        public bool isWindowsMaxState { get; set; }
        public int windowsMaxState { get; set; } = 100;
        public bool isWindowsMaxFrequency { get; set; }
        public int windowsMaxFrequency { get; set; } = 5000;
        public bool isWindowsEpp { get; set; }
        public int windowsEpp { get; set; } = 50;
        public bool isWindowsCoreParking { get; set; }
        public int windowsCoreParking { get; set; } = 100;
        public bool isWindowsMaxUnparkedCores { get; set; }
        public int windowsMaxUnparkedCores { get; set; } = 100;

        public bool isMag { get; set; }
        public bool isVsync { get; set; }
        public bool isRecap { get; set; }
        public int Sharpness { get; set; }
        public int ResScaleIndex { get; set; }

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

        // BS2 Pro RGB per-game settings
        public string Bs2ProRgbMode { get; set; } = "Static"; // "Off", "SmartTemp", "Static", "Flowing", "Breathing"
        public byte Bs2ProRgbR { get; set; } = 0;
        public byte Bs2ProRgbG { get; set; } = 0;
        public byte Bs2ProRgbB { get; set; } = 255;
        public byte Bs2ProBrightness { get; set; } = 100;

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
