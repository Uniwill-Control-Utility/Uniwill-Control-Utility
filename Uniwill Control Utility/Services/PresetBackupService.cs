using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Uniwill_Control_Utility.Services
{
    public sealed record PresetBackupResult(
        int CustomPresetCount,
        int AdaptivePresetCount,
        int VfCurveCount,
        int SettingsCount);

    public static class PresetBackupService
    {
        private const string BackupFormat = "UCU Preset Backup";
        private const int BackupVersion = 2;
        private const long MaximumBackupSize = 64 * 1024 * 1024;

        // Legacy preset files in Settings.Default.Path (from dead-code PresetManager).
        private static readonly IReadOnlyDictionary<string, string> CustomPresetFiles = new Dictionary<string, string>
        {
            ["amdApu"] = "apuPresets.json",
            ["amdDesktop"] = "amdDtCpuPresets.json",
            ["intel"] = "intelPresets.json"
        };

        // File-based stores in AppData / LocalAppData that are not in the preset directory.
        private static readonly string UcuAppDataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UCU");

        private static readonly string UcuLocalAppDataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UCU");

        public static async Task<PresetBackupResult> ExportAsync(string presetDirectory, string destinationPath)
        {
            // --- Custom preset files (legacy, from PresetManager) ---
            var customPresets = new JObject();
            var customCount = 0;

            foreach (var entry in CustomPresetFiles)
            {
                var presets = await ReadPresetObjectAsync(Path.Combine(presetDirectory, entry.Value));
                customPresets[entry.Key] = presets;
                customCount += presets.Count;
            }

            // --- Adaptive presets ---
            var adaptivePresets = await ReadPresetObjectAsync(Path.Combine(presetDirectory, "adaptivePresets.json"));

            // --- BS2 Pro settings in preset directory (Adaptive per-game profiles) ---
            var bs2proPresets = await ReadFileAsJTokenAsync(Path.Combine(presetDirectory, "bs2pro_settings.json"));

            // --- CustomPresetStore (5 slots, LocalAppData) ---
            var customPresetStore = await ReadFileAsJTokenAsync(Models.CustomPresetStore.FilePath);

            // --- VfCurvePresetStore (5 slots, LocalAppData) ---
            var vfCurvePresets = await ReadFileAsJTokenAsync(Models.VfCurvePresetStore.FilePath);
            int vfCurveCount = 0;
            if (vfCurvePresets is JArray vfArray)
            {
                vfCurveCount = vfArray.Children().Count();
            }

            // --- VfStockCurveStore (LocalAppData) ---
            var vfStockCurve = await ReadFileAsJTokenAsync(Models.VfStockCurveStore.FilePath);

            // --- Overlay settings (LocalAppData) ---
            var overlaySettings = await ReadFileAsJTokenAsync(Path.Combine(UcuLocalAppDataFolder, "overlay.json"));

            // --- Fan Control settings (AppData) ---
            var fanControlSettings = await ReadFileAsJTokenAsync(Path.Combine(UcuAppDataFolder, "fancontrol_settings.json"));

            // --- Keyboard settings (AppData) ---
            var keyboardSettings = await ReadFileAsJTokenAsync(Path.Combine(UcuAppDataFolder, "keyboard_settings.json"));

            // --- BS2 Pro settings (AppData) ---
            var bs2proSettings = await ReadFileAsJTokenAsync(Path.Combine(UcuAppDataFolder, "bs2pro_settings.json"));

            // --- BS1 settings (AppData) ---
            var bs1Settings = await ReadFileAsJTokenAsync(Path.Combine(UcuAppDataFolder, "bs1_settings.json"));

            // --- HydroUI settings (AppData) ---
            var watercoolerSettings = await ReadFileAsJTokenAsync(Path.Combine(UcuAppDataFolder, "watercooler_settings.json"));

            // --- .NET Application Settings ---
            var appSettings = ExportDotNetSettings();

            var backup = new JObject
            {
                ["format"] = BackupFormat,
                ["version"] = BackupVersion,
                ["createdUtc"] = DateTime.UtcNow.ToString("O"),
                ["customPresets"] = customPresets,
                ["adaptivePresets"] = adaptivePresets,
                ["bs2proPresets"] = bs2proPresets,
                ["customPresetStore"] = customPresetStore,
                ["vfCurvePresets"] = vfCurvePresets,
                ["vfStockCurve"] = vfStockCurve,
                ["overlay"] = overlaySettings,
                ["fanControl"] = fanControlSettings,
                ["keyboard"] = keyboardSettings,
                ["bs2pro"] = bs2proSettings,
                ["bs1"] = bs1Settings,
                ["watercooler"] = watercoolerSettings,
                ["appSettings"] = appSettings
            };

            await WriteAtomicAsync(destinationPath, backup.ToString(Formatting.Indented));
            return new PresetBackupResult(customCount, adaptivePresets.Count, vfCurveCount, appSettings != null ? appSettings.Properties().Count() : 0);
        }

        public static async Task<PresetBackupResult> ImportAsync(string presetDirectory, string sourcePath)
        {
            var file = new FileInfo(sourcePath);
            if (!file.Exists || file.Length == 0 || file.Length > MaximumBackupSize)
            {
                throw new InvalidDataException();
            }

            JObject backup;
            try
            {
                backup = JObject.Parse(await File.ReadAllTextAsync(sourcePath));
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException(null, exception);
            }

            int version = backup.Value<int?>("version") ?? 0;

            // --- v1 legacy import (only customPresets + adaptivePresets) ---
            if (version == 1)
            {
                return await ImportV1Async(presetDirectory, backup);
            }

            // --- v2 full import ---
            if (!string.Equals(backup.Value<string>("format"), BackupFormat, StringComparison.Ordinal) ||
                version != BackupVersion ||
                backup["customPresets"] is not JObject customPresets ||
                backup["adaptivePresets"] is not JObject adaptivePresets)
            {
                throw new InvalidDataException();
            }

            var preparedPresetFiles = new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);
            var preparedAppDataFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var preparedLocalAppDataFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var customCount = 0;

            // --- Custom preset files (legacy) ---
            foreach (var entry in CustomPresetFiles)
            {
                if (customPresets[entry.Key] is not JObject imported)
                {
                    throw new InvalidDataException();
                }

                ValidatePresets(imported);
                var path = Path.Combine(presetDirectory, entry.Value);
                var merged = await ReadPresetObjectAsync(path);
                MergePresets(merged, imported);
                preparedPresetFiles[path] = merged;
                customCount += imported.Count;
            }

            // --- Adaptive presets ---
            ValidatePresets(adaptivePresets);
            var adaptivePath = Path.Combine(presetDirectory, "adaptivePresets.json");
            var mergedAdaptive = await ReadPresetObjectAsync(adaptivePath);
            MergePresets(mergedAdaptive, adaptivePresets);
            preparedPresetFiles[adaptivePath] = mergedAdaptive;

            // --- BS2 Pro presets in preset directory ---
            if (backup["bs2proPresets"] is JToken bs2proPresetsToken && bs2proPresetsToken.Type != JTokenType.Null)
            {
                preparedPresetFiles[Path.Combine(presetDirectory, "bs2pro_settings.json")] =
                    bs2proPresetsToken as JObject ?? new JObject();
            }

            // --- CustomPresetStore (LocalAppData) ---
            if (backup["customPresetStore"] is JToken cpsToken && cpsToken.Type != JTokenType.Null)
            {
                preparedLocalAppDataFiles[Models.CustomPresetStore.FilePath] =
                    cpsToken.ToString(Formatting.Indented);
            }

            // --- VfCurvePresetStore (LocalAppData) ---
            var vfpToken = backup["vfCurvePresets"];
            if (vfpToken != null && vfpToken.Type != JTokenType.Null)
            {
                preparedLocalAppDataFiles[Models.VfCurvePresetStore.FilePath] =
                    vfpToken.ToString(Formatting.Indented);
            }
            int vfCurveCount = 0;
            if (vfpToken is JArray vfArr)
            {
                vfCurveCount = vfArr.Children().Count();
            }

            // --- VfStockCurveStore (LocalAppData) ---
            if (backup["vfStockCurve"] is JToken vscToken && vscToken.Type != JTokenType.Null)
            {
                preparedLocalAppDataFiles[Models.VfStockCurveStore.FilePath] =
                    vscToken.ToString(Formatting.Indented);
            }

            // --- Overlay settings (LocalAppData) ---
            if (backup["overlay"] is JToken overlayToken && overlayToken.Type != JTokenType.Null)
            {
                preparedLocalAppDataFiles[Path.Combine(UcuLocalAppDataFolder, "overlay.json")] =
                    overlayToken.ToString(Formatting.Indented);
            }

            // --- Fan Control settings (AppData) ---
            if (backup["fanControl"] is JToken fcToken && fcToken.Type != JTokenType.Null)
            {
                preparedAppDataFiles[Path.Combine(UcuAppDataFolder, "fancontrol_settings.json")] =
                    fcToken.ToString(Formatting.Indented);
            }

            // --- Keyboard settings (AppData) ---
            if (backup["keyboard"] is JToken kbToken && kbToken.Type != JTokenType.Null)
            {
                preparedAppDataFiles[Path.Combine(UcuAppDataFolder, "keyboard_settings.json")] =
                    kbToken.ToString(Formatting.Indented);
            }

            // --- BS2 Pro settings (AppData) ---
            if (backup["bs2pro"] is JToken bs2Token && bs2Token.Type != JTokenType.Null)
            {
                preparedAppDataFiles[Path.Combine(UcuAppDataFolder, "bs2pro_settings.json")] =
                    bs2Token.ToString(Formatting.Indented);
            }

            // --- BS1 settings (AppData) ---
            if (backup["bs1"] is JToken bs1Token && bs1Token.Type != JTokenType.Null)
            {
                preparedAppDataFiles[Path.Combine(UcuAppDataFolder, "bs1_settings.json")] =
                    bs1Token.ToString(Formatting.Indented);
            }

            // --- HydroUI settings (AppData) ---
            if (backup["watercooler"] is JToken wcToken && wcToken.Type != JTokenType.Null)
            {
                preparedAppDataFiles[Path.Combine(UcuAppDataFolder, "watercooler_settings.json")] =
                    wcToken.ToString(Formatting.Indented);
            }

            // --- .NET Application Settings ---
            var appSettingsToken = backup["appSettings"] as JObject;
            if (appSettingsToken != null)
            {
                ImportDotNetSettings(appSettingsToken);
            }
            int settingsCount = appSettingsToken?.Properties().Count() ?? 0;

            // Write all preset directory files atomically
            await WritePresetFilesAsync(preparedPresetFiles);

            // Write AppData files
            await WriteTextFilesAsync(preparedAppDataFiles);

            // Write LocalAppData files
            await WriteTextFilesAsync(preparedLocalAppDataFiles);

            return new PresetBackupResult(customCount, adaptivePresets.Count, vfCurveCount, settingsCount);
        }

        #region Legacy v1 import

        private static async Task<PresetBackupResult> ImportV1Async(string presetDirectory, JObject backup)
        {
            if (backup["customPresets"] is not JObject customPresets ||
                backup["adaptivePresets"] is not JObject adaptivePresets)
            {
                throw new InvalidDataException();
            }

            var preparedFiles = new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);
            var customCount = 0;

            foreach (var entry in CustomPresetFiles)
            {
                if (customPresets[entry.Key] is not JObject imported)
                {
                    throw new InvalidDataException();
                }

                ValidatePresets(imported);
                var path = Path.Combine(presetDirectory, entry.Value);
                var merged = await ReadPresetObjectAsync(path);
                MergePresets(merged, imported);
                preparedFiles[path] = merged;
                customCount += imported.Count;
            }

            ValidatePresets(adaptivePresets);
            var adaptivePath = Path.Combine(presetDirectory, "adaptivePresets.json");
            var mergedAdaptive = await ReadPresetObjectAsync(adaptivePath);
            MergePresets(mergedAdaptive, adaptivePresets);
            preparedFiles[adaptivePath] = mergedAdaptive;

            await WritePresetFilesAsync(preparedFiles);
            return new PresetBackupResult(customCount, adaptivePresets.Count, 0, 0);
        }

        #endregion

        #region .NET Settings export/import

        private static JObject ExportDotNetSettings()
        {
            var settings = new JObject();
            var props = global::Uniwill_Control_Utility.Properties.Settings.Default;

            // Export all user-scoped settings.
            // Skip machine-scoped and internal state properties.
            var skipNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                // State flags that should not be restored (they are computed at runtime)
                "isAdaptiveModeRunning",
                "FirstBoot",
                "SettingsUpgradeRequired",
                "ShowPrerequisiteDialog",
                "isTrack"
            };

            foreach (var setting in props.Properties.OfType<SettingsProperty>()
                         .Where(s => s.Attributes.Contains(typeof(UserScopedSettingAttribute)) && !skipNames.Contains(s.Name)))
            {
                var value = props[setting.Name];
                if (value == null) continue;

                // Convert to JSON-compatible types
                JToken token = value switch
                {
                    bool b => b,
                    int i => i,
                    byte b => (int)b,
                    double d => d,
                    string s => s,
                    _ => value.ToString()
                };

                settings[setting.Name] = token;
            }

            return settings;
        }

        private static void ImportDotNetSettings(JObject settings)
        {
            var props = global::Uniwill_Control_Utility.Properties.Settings.Default;

            foreach (var prop in settings.Properties())
            {
                var settingProp = props.Properties[prop.Name] as SettingsProperty;
                if (settingProp == null || !settingProp.Attributes.Contains(typeof(UserScopedSettingAttribute)))
                    continue;

                try
                {
                    var targetTypeName = settingProp.PropertyType.FullName;
                    var converted = ConvertSettingValue(prop.Value, settingProp.PropertyType);
                    if (converted != null)
                    {
                        props[prop.Name] = converted;
                    }
                }
                catch
                {
                    // Skip settings that can't be converted
                }
            }

            props.Save();
        }

        private static object? ConvertSettingValue(JToken token, System.Type targetType)
        {
            return targetType.FullName switch
            {
                "System.Boolean" => token.Value<bool>(),
                "System.Int32" => token.Value<int>(),
                "System.Byte" => (byte)token.Value<byte>(),
                "System.Double" => token.Value<double>(),
                "System.String" => token.Value<string>(),
                _ => token.ToString()
            };
        }

        #endregion

        #region File I/O helpers

        private static async Task<JObject> ReadPresetObjectAsync(string path)
        {
            if (!File.Exists(path))
            {
                return new JObject();
            }

            var json = await File.ReadAllTextAsync(path);
            if (string.IsNullOrWhiteSpace(json))
            {
                return new JObject();
            }

            try
            {
                var presets = JObject.Parse(json);
                ValidatePresets(presets);
                return presets;
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException(null, exception);
            }
        }

        private static async Task<JToken> ReadFileAsJTokenAsync(string path)
        {
            if (!File.Exists(path))
            {
                return null;
            }

            try
            {
                var json = await File.ReadAllTextAsync(path);
                return JToken.Parse(json);
            }
            catch
            {
                return null;
            }
        }

        private static void ValidatePresets(JObject presets)
        {
            if (presets.Properties().Any(property => string.IsNullOrWhiteSpace(property.Name) || property.Value.Type != JTokenType.Object))
            {
                throw new InvalidDataException();
            }
        }

        private static void MergePresets(JObject destination, JObject source)
        {
            foreach (var property in source.Properties())
            {
                destination[property.Name] = property.Value.DeepClone();
            }
        }

        private static async Task WritePresetFilesAsync(IReadOnlyDictionary<string, JObject> files)
        {
            var originals = files.Keys.ToDictionary(
                path => path,
                path => File.Exists(path) ? File.ReadAllText(path) : null,
                StringComparer.OrdinalIgnoreCase);

            try
            {
                foreach (var file in files)
                {
                    await WriteAtomicAsync(file.Key, file.Value.ToString(Formatting.Indented));
                }
            }
            catch
            {
                foreach (var original in originals)
                {
                    if (original.Value == null)
                    {
                        if (File.Exists(original.Key))
                        {
                            File.Delete(original.Key);
                        }
                    }
                    else
                    {
                        await WriteAtomicAsync(original.Key, original.Value);
                    }
                }

                throw;
            }
        }

        private static async Task WriteTextFilesAsync(IReadOnlyDictionary<string, string> files)
        {
            foreach (var file in files)
            {
                await WriteAtomicAsync(file.Key, file.Value);
            }
        }

        private static async Task WriteAtomicAsync(string path, string contents)
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(path)) ?? throw new InvalidOperationException();
            Directory.CreateDirectory(directory);
            var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");

            try
            {
                await File.WriteAllTextAsync(temporaryPath, contents);
                File.Move(temporaryPath, path, true);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
        }

        #endregion
    }
}
