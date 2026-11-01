using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Serilog;
using UCU.Contracts;
using UCU.Contracts.Capabilities;
using UCU.Core.Supervision;

namespace Uniwill_Control_Utility.Services
{
    /// <summary>What a shortcut can do (U-204). Deliberately the same action
    /// class the watcher hub executes - user wiring triggers vetted actions,
    /// never raw EC writes.</summary>
    public enum ShortcutAction
    {
        ApplyPreset,
        SetPerformanceMode,
        SetFanTurbo,
        PauseOemTakeover,
        ResumeOemTakeover,
    }

    /// <summary>One chord -> action binding (the shortcuts.json row).</summary>
    public sealed record ShortcutBinding
    {
        public string Chord { get; init; } = string.Empty;

        public ShortcutAction Action { get; init; }

        /// <summary>Preset name for <see cref="ShortcutAction.ApplyPreset"/>.</summary>
        public string? PresetName { get; init; }

        /// <summary>Q-mode number for <see cref="ShortcutAction.SetPerformanceMode"/>.</summary>
        public int? PerformanceMode { get; init; }
    }

    /// <summary>
    /// shortcuts.json (U-204): the user's chord -> action map, same shape and
    /// discipline as watchers.json - the record IS the file, invalid rows are
    /// dropped on load (a hand-edited file must not crash the hook), and a
    /// corrupt file is moved to .bak instead of silently rewritten (the user's
    /// bindings are user data).
    /// </summary>
    public sealed class ShortcutStore
    {
        /// <summary>A hook cannot sanely dispatch more than this; the cap also
        /// bounds the per-keydown scan.</summary>
        public const int MaxBindings = 32;

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        private readonly string _path;

        public ShortcutStore()
            : this(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "UCU", "shortcuts.json"))
        {
        }

        public ShortcutStore(string path) => _path = path;

        public string FilePath => _path;

        public List<ShortcutBinding> Load()
        {
            try
            {
                if (!File.Exists(_path))
                    return new List<ShortcutBinding>();

                string text = File.ReadAllText(_path);
                List<ShortcutBinding>? rows =
                    JsonSerializer.Deserialize<List<ShortcutBinding>>(text, JsonOptions);
                return Sanitise(rows);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[Shortcuts] shortcuts.json unreadable; starting with no shortcuts");
                QuarantineCorruptFile();
                return new List<ShortcutBinding>();
            }
        }

        public void Save(IReadOnlyList<ShortcutBinding> bindings)
        {
            string? directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            string json = JsonSerializer.Serialize(Sanitise(bindings), JsonOptions);
            string temp = _path + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, _path, overwrite: true);
        }

        /// <summary>The load/save gate: a binding without a parseable chord or
        /// without the payload its action needs is dropped, and a chord is used
        /// once (the first row wins - a duplicate would fire two actions).</summary>
        public static List<ShortcutBinding> Sanitise(IEnumerable<ShortcutBinding>? rows)
        {
            var valid = new List<ShortcutBinding>();
            if (rows is null)
                return valid;

            var usedChords = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (ShortcutBinding row in rows)
            {
                if (row is null || !Hotkey.TryParse(row.Chord, out _))
                    continue;

                string chord = row.Chord.Trim();
                if (!usedChords.Add(chord))
                    continue;

                if (row.Action == ShortcutAction.ApplyPreset && string.IsNullOrWhiteSpace(row.PresetName))
                    continue;

                if (row.Action == ShortcutAction.SetPerformanceMode &&
                    (row.PerformanceMode is null or < 0 or > 8))
                {
                    continue;
                }

                valid.Add(row with { Chord = chord });

                if (valid.Count >= MaxBindings)
                    break;
            }

            return valid;
        }

        private void QuarantineCorruptFile()
        {
            try
            {
                string backup = _path + ".bak";
                File.Move(_path, backup, overwrite: true);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[Shortcuts] shortcuts.json unreadable AND unmovable: {Path}", _path);
            }
        }
    }

    /// <summary>
    /// Dispatches the user's shortcut map (U-204). The matching runs on the
    /// existing global hook thread (one hook, one set of field lessons:
    /// pumped dedicated thread, 350 ms debounce); the action runs on the UI
    /// dispatcher because every capability call and every toast belongs to it.
    ///
    /// The action set is the watcher hub's class - preset through the
    /// <see cref="PresetArbiter"/> (so a shortcut yields to Adaptive and
    /// respects the boot grace exactly like a tray click), performance mode,
    /// fan turbo with set semantics (the engine verb is a toggle), and the OEM
    /// takeover pause/restore. No raw EC writes exist here.
    /// </summary>
    public sealed class ShortcutService
    {
        private readonly ShortcutStore _store;
        private readonly PresetArbiter _arbiter;
        private readonly IFanCapability _fan;
        private readonly IPowerCapability _power;
        private readonly OemTakeoverService? _oem;

        private List<ShortcutBinding> _bindings;

        public ShortcutService(
            ShortcutStore store,
            PresetArbiter arbiter,
            IFanCapability fan,
            IPowerCapability power,
            OemTakeoverService? oem = null)
        {
            _store = store;
            _arbiter = arbiter;
            _fan = fan;
            _power = power;
            _oem = oem;
            _bindings = _store.Load();
        }

        public IReadOnlyList<ShortcutBinding> Bindings => _bindings;

        /// <summary>Re-reads the file (the editor saves through the store).</summary>
        public void Reload() => _bindings = _store.Load();

        /// <summary>Called per hook keydown. True when a binding matched; the
        /// action is queued on the UI dispatcher and never runs inline on the
        /// hook thread.</summary>
        public bool TryHandle(System.Windows.Forms.KeyEventArgs e)
        {
            foreach (ShortcutBinding binding in _bindings)
            {
                if (!Hotkey.TryParse(binding.Chord, out Hotkey hotkey) || !hotkey.Matches(e))
                    continue;

                Dispatch(binding);
                return true;
            }

            return false;
        }

        private void Dispatch(ShortcutBinding binding)
        {
            System.Windows.Threading.Dispatcher? dispatcher =
                System.Windows.Application.Current?.Dispatcher;

            if (dispatcher is null)
            {
                _ = ExecuteAsync(binding);
                return;
            }

            dispatcher.BeginInvoke(new Action(() => _ = ExecuteAsync(binding)));
        }

        /// <summary>The whitelist executor. <paramref name="showToast"/> is the
        /// hermetic-test seam (the watcher hub's shape).</summary>
        internal async Task ExecuteAsync(
            ShortcutBinding binding,
            Action<string, string, string?>? showToast = null)
        {
            showToast ??= (title, body, accent) =>
                Scripts.Misc.ToastNotification.ShowToastNotification(title, body, accent: accent);

            switch (binding.Action)
            {
                case ShortcutAction.ApplyPreset:
                {
                    // UserInterface source: a hotkey press IS the human choosing
                    // a profile (highest priority, same as the tray menu), so it
                    // must not be suppressed by the automation rules.
                    var result = await _arbiter
                        .RequestAsync(PresetIntentSource.UserInterface, binding.PresetName!)
                        .ConfigureAwait(false);
                    if (result.Applied)
                        showToast(LocalizationService.Format("{0} Applied", binding.PresetName), string.Empty, binding.PresetName);
                    break;
                }

                case ShortcutAction.SetPerformanceMode:
                    await _power.SetPerformanceModeAsync(binding.PerformanceMode!.Value).ConfigureAwait(false);
                    break;

                case ShortcutAction.SetFanTurbo:
                {
                    FanStatusDto status = await _fan.GetStatusAsync().ConfigureAwait(false);
                    // Set semantics: the engine verb is a toggle, so a blind
                    // toggle on an already-on state would turn it OFF.
                    if (!status.TurboActive)
                        await _fan.ToggleTurboAsync().ConfigureAwait(false);
                    break;
                }

                case ShortcutAction.PauseOemTakeover:
                case ShortcutAction.ResumeOemTakeover:
                {
                    if (_oem is null)
                        return;

                    OemMergeReport report = binding.Action == ShortcutAction.PauseOemTakeover
                        ? await _oem.PauseAsync().ConfigureAwait(false)
                        : await _oem.ResumeAsync().ConfigureAwait(false);

                    string body = report.State == "Busy"
                        ? LocalizationService.Get("A takeover action is already running.")
                        : !report.EngineReachable
                            ? LocalizationService.Get("The engine service is not running; the machine scope (services, tasks, machine run keys) was not paused.")
                            : report.MutationsApplied == 0
                                ? LocalizationService.Get("Nothing to pause: the OEM Control Center stack is not installed on this machine.")
                                : LocalizationService.Format("Paused the OEM Control Center stack ({0} mutations applied).", report.MutationsApplied);

                    IReadOnlyList<string> failures = OemTakeoverService.Failures(report);
                    if (failures.Count > 0)
                        body += "\n" + string.Join("\n", failures);

                    showToast(LocalizationService.Get("OEM Control Center"), body, null);
                    break;
                }
            }
        }
    }
}
