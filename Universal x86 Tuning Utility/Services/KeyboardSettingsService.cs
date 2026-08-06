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

        /// <summary>
        /// Separate file that survives app restarts when Adaptive Mode keyboard override is active.
        /// Stores the user's original keyboard settings (captured when override was enabled)
        /// so they can be restored when override is lifted, even after a restart.
        /// </summary>
        private static readonly string SavedSettingsFile = Path.Combine(SettingsFolder, "keyboard_saved_settings.json");

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
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[KBD-SETTINGS] Failed to save keyboard settings: {ex.Message}");
            }
        }

        /// <summary>
        /// Saves the user's keyboard settings to the override backup file.
        /// Called when Adaptive Mode keyboard override is enabled to preserve
        /// the original settings across app restarts.
        /// </summary>
        public static void SaveForOverride(KeyboardSettings settings)
        {
            try
            {
                Directory.CreateDirectory(SettingsFolder);
                var json = JsonConvert.SerializeObject(settings, Formatting.Indented);
                File.WriteAllText(SavedSettingsFile, json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[KBD-SETTINGS] Failed to save override backup: {ex.Message}");
            }
        }

        /// <summary>
        /// Loads the user's keyboard settings from the override backup file.
        /// Returns null if the file doesn't exist.
        /// </summary>
        public static KeyboardSettings? LoadForOverride()
        {
            try
            {
                if (File.Exists(SavedSettingsFile))
                {
                    var json = File.ReadAllText(SavedSettingsFile);
                    return JsonConvert.DeserializeObject<KeyboardSettings>(json);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[KBD-SETTINGS] Failed to load override backup: {ex.Message}");
            }
            return null;
        }

        /// <summary>
        /// Deletes the override backup file. Called when override is lifted
        /// so the backup doesn't persist after it's no longer needed.
        /// </summary>
        public static void DeleteSavedSettings()
        {
            try
            {
                if (File.Exists(SavedSettingsFile))
                    File.Delete(SavedSettingsFile);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[KBD-SETTINGS] Failed to delete override backup: {ex.Message}");
            }
        }
    }
}
