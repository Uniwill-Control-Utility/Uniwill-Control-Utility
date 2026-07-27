using System;
using System.IO;
using Newtonsoft.Json;
using Universal_x86_Tuning_Utility.Models;

namespace Universal_x86_Tuning_Utility.Services
{
    /// <summary>
    /// Loads and saves Fan Control page settings to disk.
    /// </summary>
    public static class FanControlSettingsService
    {
        private static readonly string SettingsFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UXTU");

        private static readonly string SettingsFile = Path.Combine(SettingsFolder, "fancontrol_settings.json");

        public static FanControlSettings Load()
        {
            try
            {
                if (File.Exists(SettingsFile))
                {
                    var json = File.ReadAllText(SettingsFile);
                    return JsonConvert.DeserializeObject<FanControlSettings>(json) ?? new FanControlSettings();
                }
            }
            catch
            {
                // If loading fails, return defaults
            }
            return new FanControlSettings();
        }

        public static void Save(FanControlSettings settings)
        {
            try
            {
                Directory.CreateDirectory(SettingsFolder);
                var json = JsonConvert.SerializeObject(settings, Formatting.Indented);
                File.WriteAllText(SettingsFile, json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to save fan control settings: {ex.Message}");
            }
        }
    }
}
