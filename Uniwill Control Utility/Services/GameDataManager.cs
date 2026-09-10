using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml;

namespace Uniwill_Control_Utility.Services
{
    public class GameData
    {
        public string fpsData { get; set; } = "No Data";
        public string fpsAvData { get; set; } = "0,0,0,0,0,0,0,0,0,0";
        public string msData { get; set; } = "No Data";
        public string msAvData { get; set; } = "0,0,0,0,0";
    }

    public class GameDataManager
    {
        private string _filePath;
        private Dictionary<string, GameData> _presets;

        public GameDataManager(string filePath)
        {
            _filePath = filePath;
            _presets = new Dictionary<string, GameData>();
            LoadPresets();
        }

        public IEnumerable<string> GetPresetNames()
        {
            return _presets.Keys;
        }

        public GameData? GetPreset(string presetName)
        {
            return _presets.TryGetValue(presetName, out var preset) ? preset : null;
        }

        public void SavePreset(string name, GameData preset)
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
                var loaded = JsonConvert.DeserializeObject<Dictionary<string, GameData>>(json);
                if (loaded != null)
                {
                    _presets = loaded;
                }
            }
        }


        private void SavePresets()
        {
            string json = JsonConvert.SerializeObject(_presets, Newtonsoft.Json.Formatting.Indented);
            File.WriteAllText(_filePath, json);
        }
    }
}
