using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace Uniwill_Control_Utility.Services
{
    /// <summary>
    /// Carries user settings forward across an app update without trusting
    /// <c>Settings.Default.Upgrade()</c>'s choice of predecessor.
    ///
    /// Upgrade() copies from the nearest lower version directory, and that is wrong
    /// whenever a stale directory sits between the version the user actually ran last
    /// and the one starting now (field, 0.9.11-0.9.13: a 0.9.10.0 config written days
    /// earlier shadowed the 0.9.9.0 one that held EnablePowerBoostMode, so the toggle
    /// reverted on every update). This pass fills only the settings the current
    /// version's config does not carry at all - presence in the XML is the test, never
    /// a comparison against the designer default - each from the newest older config
    /// that carries it. A value the current config already holds always wins.
    ///
    /// Layout addressed: %LOCALAPPDATA%|%APPDATA%\&lt;company&gt;\&lt;app&gt;_Url_&lt;hash&gt;\&lt;version&gt;\user.config
    /// </summary>
    internal static class SettingsCarryOver
    {
        /// <summary>Cap on version directories examined per startup.</summary>
        internal const int MaxFiles = 24;

        /// <summary>
        /// The setting values carried by one user.config, keyed by setting name, or
        /// <see langword="null"/> when the file is missing, unreadable, or not a
        /// client-settings file. Never throws.
        /// </summary>
        internal static Dictionary<string, string>? Read(string path)
        {
            try
            {
                XElement? userSettings = XDocument.Load(path).Root?.Element("userSettings");
                if (userSettings is null)
                    return null;

                var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (XElement setting in userSettings.Descendants("setting"))
                {
                    string? name = setting.Attribute("name")?.Value;
                    if (string.IsNullOrEmpty(name))
                        continue;
                    // <value /> means an empty string, which is still a present value.
                    values[name] = setting.Element("value")?.Value ?? "";
                }
                return values;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// Earlier configs for this application, newest version first, current version
        /// (and any version above it - side-by-side dev builds are not history) excluded.
        /// Never throws; an unreadable directory ends the walk with what was found.
        /// </summary>
        internal static List<(Version Version, string Path)> History(string currentConfigPath)
        {
            var found = new List<(Version Version, string Path)>();
            try
            {
                string versionDirectory = Path.GetDirectoryName(currentConfigPath) ?? "";
                string hashDirectory = Path.GetDirectoryName(versionDirectory) ?? "";
                string companyDirectory = Path.GetDirectoryName(hashDirectory) ?? "";
                string hashName = Path.GetFileName(hashDirectory);

                int marker = hashName.IndexOf("_Url_", StringComparison.Ordinal);
                if (marker < 0 || !Version.TryParse(Path.GetFileName(versionDirectory), out Version? current))
                    return found;

                string appPrefix = hashName.Substring(0, marker + "_Url_".Length);
                string companyName = Path.GetFileName(companyDirectory);
                string[] roots =
                {
                    companyDirectory,
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), companyName),
                };

                foreach (string root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (!Directory.Exists(root))
                        continue;

                    foreach (string identityDirectory in Directory.EnumerateDirectories(root, appPrefix + "*"))
                    {
                        foreach (string versionPath in Directory.EnumerateDirectories(identityDirectory))
                        {
                            if (!Version.TryParse(Path.GetFileName(versionPath), out Version? version) || version >= current)
                                continue;

                            string file = Path.Combine(versionPath, "user.config");
                            if (File.Exists(file))
                                found.Add((version, file));
                        }
                    }
                }
            }
            catch (Exception)
            {
                // A locked or vanished directory just shrinks the history set.
            }

            return found
                .OrderByDescending(candidate => candidate.Version)
                .ThenBy(candidate => candidate.Path, StringComparer.OrdinalIgnoreCase)
                .Take(MaxFiles)
                .ToList();
        }

        /// <summary>
        /// For every setting the current config does not carry, the value from the newest
        /// earlier config that does. A file that cannot be read is skipped (reported
        /// through <paramref name="onSkipped"/>) and the next one is tried.
        /// </summary>
        internal static List<(string Setting, string Version, string Value)> Plan(
            IEnumerable<(Version Version, string Path)> newestFirst,
            IEnumerable<string> presentInCurrent,
            IEnumerable<string> userSettings,
            Action<string>? onSkipped = null)
        {
            var claimed = new HashSet<string>(presentInCurrent, StringComparer.OrdinalIgnoreCase);
            string[] pending = userSettings.ToArray();
            var adopted = new List<(string Setting, string Version, string Value)>();

            foreach ((Version version, string path) in newestFirst)
            {
                Dictionary<string, string>? values = Read(path);
                if (values is null)
                {
                    onSkipped?.Invoke($"settings carry-over: skipping unreadable {path}");
                    continue;
                }

                foreach (string setting in pending)
                {
                    if (claimed.Contains(setting) || !values.TryGetValue(setting, out string? value))
                        continue;
                    adopted.Add((setting, version.ToString(), value));
                    claimed.Add(setting);
                }
            }

            return adopted;
        }

        /// <summary>
        /// The pass: adopt every declared setting the current config does not carry,
        /// newest older config first. App runs this on every start - the once-per-version
        /// <c>Upgrade()</c> gate is not a substitute (a leftover config for the running
        /// version carries the flag false: field, 0.9.15, EnablePowerBoostMode came back
        /// False because the pass never ran).
        ///
        /// The short-circuit is a presence comparison, never a version flag: when the
        /// current config already carries every declared setting no config file is
        /// opened at all. <paramref name="tryApply"/> returns null on success, else the
        /// reason the value could not be stored. Returns the lines to log.
        /// </summary>
        internal static List<string> Run(
            string currentConfigPath,
            IEnumerable<string>? presentInCurrent,
            IEnumerable<string> userSettings,
            Func<string, string, string?> tryApply,
            out int adopted)
        {
            adopted = 0;
            var lines = new List<string>();
            var present = new HashSet<string>(
                presentInCurrent ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            string[] pending = userSettings.Where(setting => !present.Contains(setting)).ToArray();

            // History() only enumerates version directories; it stays outside the
            // short-circuit so the closing line can still report how many exist.
            List<(Version Version, string Path)> history = History(currentConfigPath);
            if (pending.Length == 0)
            {
                lines.Add(Summary(0, history.Count));
                return lines;
            }

            if (history.Count == 0)
                lines.Add("settings carry-over: no earlier user.config found");

            foreach ((string setting, string version, string value) in Plan(history, present, pending, lines.Add))
            {
                string? reason = tryApply(setting, value);
                if (reason is not null)
                {
                    lines.Add($"settings carry-over: {setting} not adopted ({reason})");
                    continue;
                }

                lines.Add($"settings carry-over: {setting} = {value} adopted from {version}");
                adopted++;
            }

            lines.Add(Summary(adopted, history.Count));
            return lines;
        }

        /// <summary>The single closing line: what the pass did, or that there was nothing to do.</summary>
        internal static string Summary(int adopted, int configsExamined) => adopted == 0
            ? $"settings carry-over: nothing to adopt from {configsExamined} earlier config(s)"
            : $"settings carry-over: {adopted} setting(s) adopted from earlier versions";
    }
}
