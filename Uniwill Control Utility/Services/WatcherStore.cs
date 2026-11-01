using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Serilog;
using UCU.Core.Supervision;

namespace Uniwill_Control_Utility.Services
{
    /// <summary>
    /// watchers.json: the watcher hub's rule store (story U-201, the plan's
    /// "per-feature JSON in %LOCALAPPDATA%\UCU\watchers.json"). The WatcherRule
    /// record IS the file shape - no second schema.
    ///
    /// The rules reference profiles and exe names, not Settings-style slot
    /// indices, so the file is machine-portable; corrupt content is not
    /// silently rewritten: it is renamed to .bak (the user's rules are user
    /// data) and the hub starts with an empty, valid set.
    /// </summary>
    public sealed class WatcherStore
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        private readonly object _gate = new();
        private readonly string _path;
        private List<WatcherRule> _rules = new();

        /// <summary>Raised after Save (the hub reloads its live set and
        /// re-decides the fps lease; the page raises it by saving).</summary>
        public event Action? Changed;

        public WatcherStore()
            : this(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "UCU", "watchers.json"))
        {
        }

        public WatcherStore(string path) => _path = path;

        public string FilePath => _path;

        /// <summary>The current rules (snapshot copy; mutate by saving).</summary>
        public IReadOnlyList<WatcherRule> Rules
        {
            get { lock (_gate) return new List<WatcherRule>(_rules); }
        }

        /// <summary>Load from disk (called once at hub start).</summary>
        public void Load()
        {
            List<WatcherRule> loaded = new();
            try
            {
                if (File.Exists(_path))
                {
                    string text = File.ReadAllText(_path);
                    List<WatcherRule>? parsed =
                        JsonSerializer.Deserialize<List<WatcherRule>>(text, JsonOptions);

                    if (parsed is null)
                        throw new InvalidDataException("watchers.json deserialized to null");

                    foreach (WatcherRule rule in parsed)
                    {
                        string? why = WatcherPolicy.Validate(rule);
                        if (why is not null)
                        {
                            Log.Warning("[Watcher] dropped rule '{Name}': {Why}", rule.Name, why);
                            continue;
                        }
                        loaded.Add(rule with { CooldownSeconds = Math.Max(1, rule.CooldownSeconds) });
                    }

                    if (loaded.Count > WatcherPolicy.MaxRules)
                        loaded = loaded.GetRange(0, WatcherPolicy.MaxRules);
                }
            }
            catch (Exception ex)
            {
                // User data: keep the broken file, start clean, say so loudly.
                string bak = _path + ".bak";
                try
                {
                    File.Move(_path, bak, overwrite: true);
                    Log.Warning(ex, "[Watcher] watchers.json unreadable - moved to {Bak}, starting empty", bak);
                }
                catch (Exception moveEx)
                {
                    Log.Error(moveEx, "[Watcher] watchers.json unreadable AND unmovable: {Path}", _path);
                }
                loaded.Clear();
            }

            lock (_gate) _rules = loaded;
        }

        /// <summary>Persist the full set (the page saves the edited list).
        /// Every rule goes through the policy's validation; a rule that does
        /// not validate throws with the reason (the page surfaces it).</summary>
        public void Save(IReadOnlyList<WatcherRule> rules)
        {
            if (rules.Count > WatcherPolicy.MaxRules)
                throw new InvalidOperationException(
                    LocalizationService.Format("At most {0} watcher rules.", WatcherPolicy.MaxRules));

            List<WatcherRule> sanitized = new(rules.Count);
            foreach (WatcherRule rule in rules)
            {
                string? why = WatcherPolicy.Validate(rule);
                if (why is not null)
                    throw new InvalidOperationException(
                        LocalizationService.Format("Rule '{0}': {1}", rule.Name, LocalizationService.Get(why)));

                sanitized.Add(rule with { CooldownSeconds = Math.Max(1, rule.CooldownSeconds) });
            }

            string directory = Path.GetDirectoryName(_path)!;
            Directory.CreateDirectory(directory);

            string json = JsonSerializer.Serialize(sanitized, JsonOptions);
            string temp = _path + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, _path, overwrite: true);

            lock (_gate) _rules = sanitized;

            Log.Information("[Watcher] saved {Count} watcher rule(s)", sanitized.Count);
            Changed?.Invoke();
        }
    }
}
