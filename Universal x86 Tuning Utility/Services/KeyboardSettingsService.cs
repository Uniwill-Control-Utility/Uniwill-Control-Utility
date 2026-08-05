using System;
using System.IO;
using Newtonsoft.Json;
using Universal_x86_Tuning_Utility.Models;

namespace Universal_x86_Tuning_Utility.Services
{
    /// <summary>
    /// Loads and saves Keyboard RGB page settings to disk.
    /// </summary>
    public static class KeyboardSettingsService
    {
        private static readonly string SettingsFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UXTU");

        private static readonly string SettingsFile = Path.Combine(SettingsFolder, "keyboard_settings.json");

        public static KeyboardSettings Load()
        {
            try
            {
                if (File.Exists(SettingsFile))
                {
                    var json = File.ReadAllText(SettingsFile);
                    return JsonConvert.DeserializeObject<KeyboardSettings>(json) ?? new KeyboardSettings();
                }
            }
            catch
            {
                // If loading fails, return defaults
            }
            return new KeyboardSettings();
        }

        public static void Save(KeyboardSettings settings)
        {
            try
            {
                Directory.CreateDirectory(SettingsFolder);
                var json = JsonConvert.SerializeObject(settings, Formatting.Indented);
                File.WriteAllText(SettingsFile, json);
                System.Diagnostics.Debug.WriteLine($"[KBD-SETTINGS] Saved to {SettingsFile} (PerKeyMode={settings.PerKeyMode})");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[KBD-SETTINGS] Failed to save keyboard settings: {ex.Message}");
            }
        }
    }
}
