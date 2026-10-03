using System;
using System.IO;
using System.Text.Json;
using Uniwill_Control_Utility.Models;

namespace Uniwill_Control_Utility.Services
{
    public sealed class OverlaySettingsStore
    {
        private readonly object _sync = new();
        private readonly string _settingsPath;
        private static readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };
        private OverlayOptions _current;

        public OverlaySettingsStore()
        {
            string directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "UCU");
            Directory.CreateDirectory(directory);
            _settingsPath = Path.Combine(directory, "overlay.json");
            _current = Load();
        }

        public OverlayOptions Current
        {
            get
            {
                lock (_sync)
                    return _current with { };
            }
        }

        public void Save(OverlayOptions options)
        {
            OverlayOptions validated = options with
            {
                RefreshIntervalMs = Math.Clamp(options.RefreshIntervalMs, 100, 5000),
                Scale = Math.Clamp(options.Scale, 0.5, 2),
                Opacity = Math.Clamp(options.Opacity, 0.25, 1)
            };

            lock (_sync)
            {
                string temporaryPath = _settingsPath + ".tmp";
                File.WriteAllText(temporaryPath, JsonSerializer.Serialize(validated, _jsonOptions));
                File.Move(temporaryPath, _settingsPath, true);
                _current = validated;
            }
        }

        private OverlayOptions Load()
        {
            try
            {
                if (File.Exists(_settingsPath))
                {
                    var loaded = JsonSerializer.Deserialize<OverlayOptions>(File.ReadAllText(_settingsPath));
                    if (loaded is not null)
                        return ApplyDefaults(loaded);
                }
            }
            catch
            {
                // Corrupted or unreadable settings file — fall back to defaults.
            }

            return new OverlayOptions();
        }

        /// <summary>Applies default values for properties that may be missing from older saved files.</summary>
        private static OverlayOptions ApplyDefaults(OverlayOptions options)
        {
            return options with
            {
                BackgroundOpacity = IsFinite(options.BackgroundOpacity)
                    ? Math.Clamp(options.BackgroundOpacity, 0.0, 1.0)
                    : 0.95,
                ContentOpacity = IsFinite(options.ContentOpacity)
                    ? Math.Clamp(options.ContentOpacity, 0.0, 1.0)
                    : 1.0
            };
        }

        private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    }
}
