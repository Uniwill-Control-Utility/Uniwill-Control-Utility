using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Threading.Tasks;
using Serilog;
using Uniwill_Control_Utility.Models;
using Uniwill_Control_Utility.Properties;
using Uniwill_Control_Utility.Scripts;
using Uniwill_Control_Utility.Scripts.GPUs.NVIDIA;
using Uniwill_Control_Utility.Services;
using Uniwill_Control_Utility.Views.Windows;
using UCU.Contracts.Capabilities;
using UCU.Core.Ec;
using UCU.Core.Supervision;

namespace Uniwill_Control_Utility.Views.Pages
{
    public partial class Automations : Page
    {
        bool setup = false;
        private readonly IGpuMuxCapability? _mux;

        public Automations()
        {
            InitializeComponent();
            _ = Tablet.TabletDevices;

            _mux = App.GetService<IGpuMuxCapability>();

            PopulateComboBox(cbxCharge, ActivePresetMode);
            PopulateComboBox(cbxDischarge, ActivePresetMode);
            PopulateComboBox(cbxResume, ActivePresetMode);

            // Restore saved selections — resets to None if the saved preset is from the wrong mode pool.
            getAcPreset(Settings.Default.acPreset);
            getDcPreset(Settings.Default.dcPreset);
            getResumePreset(Settings.Default.resumePreset);

            // Populate GPU Power Saving ComboBoxes
            PopulateGpuPowerSavingComboBoxes();

            // Restore GPU Power Saving selections from settings
            setGpuPowerSavingBattery(Settings.Default.GpuPowerSavingBattery);
            setGpuPowerSavingPluggedIn(Settings.Default.GpuPowerSavingPluggedIn);

            // Populate internal-panel refresh-rate ComboBoxes and restore selections
            PopulateRefreshRateComboBoxes();
            setRefreshRateBattery(Settings.Default.RefreshRateBattery);
            setRefreshRatePluggedIn(Settings.Default.RefreshRatePluggedIn);
            UpdateRefreshRateVisibility();

            // Power Guardian toggles (U-107): persisted rows, defaults OFF.
            cbPowerGuardGpu.IsChecked = Settings.Default.PowerGuardGpu;
            cbPowerGuardCpu.IsChecked = Settings.Default.PowerGuardCpu;

            // Watcher Rules card (U-201): rows from watchers.json; edits save
            // through the store, which reloads the hosted hub.
            InitWatcherRules();
            InitShortcuts();

            setup = true;
        }

        /// <summary>
        /// Returns the active preset mode based on the Power boost mode setting.
        /// </summary>
        private static PresetMode ActivePresetMode =>
            Settings.Default.EnablePowerBoostMode
                ? PresetMode.Boost
                : PresetMode.Normal;

        #region ComboBox Population

        /// <summary>
        /// Populates a ComboBox with "None" plus all available preset display names
        /// filtered to only include custom presets from the currently active preset mode.
        /// Built-in profiles (Office, Gaming, Turbo) are always included.
        /// </summary>
        private static void PopulateComboBox(ComboBox comboBox, PresetMode mode)
        {
            comboBox.Items.Add("None");

            // Always show built-in profiles
            foreach (string name in PresetProfileApplier.BuiltInProfileNames)
            {
                comboBox.Items.Add(PresetProfileApplier.DisplayName(name));
            }

            // Only show custom presets from the active mode pool
            var presets = CustomPresetStore.Load(mode);
            for (int i = 0; i < presets.Length; i++)
            {
                if (presets[i].HasChanges)
                {
                    comboBox.Items.Add(PresetProfileApplier.CustomProfileKey(i, mode));
                }
            }
        }

        #endregion

        #region Load Buttons

        private void btnChargeLoad_Click(object sender, RoutedEventArgs e)
        {
            setup = false;
            string oldPreset = Settings.Default.acPreset;
            cbxCharge.Items.Clear();
            PopulateComboBox(cbxCharge, ActivePresetMode);
            getAcPreset(oldPreset);
            setup = true;
        }

        private void btnDischargeLoad_Click(object sender, RoutedEventArgs e)
        {
            setup = false;
            string oldPreset = Settings.Default.dcPreset;
            cbxDischarge.Items.Clear();
            PopulateComboBox(cbxDischarge, ActivePresetMode);
            getDcPreset(oldPreset);
            setup = true;
        }

        private void btnResume_Click(object sender, RoutedEventArgs e)
        {
            setup = false;
            string oldPreset = Settings.Default.resumePreset;
            cbxResume.Items.Clear();
            PopulateComboBox(cbxResume, ActivePresetMode);
            getResumePreset(oldPreset);
            setup = true;
        }

        #endregion

        /// <summary>
        /// Prevents the CardExpander's ToggleButton from intercepting clicks on ComboBoxes
        /// nested inside its content. Uses the bubbling MouseLeftButtonDown so the ComboBox
        /// processes the click first, then we block it from reaching the ToggleButton above.
        /// </summary>
        private void ComboBoxInCardExpander_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
        }

        #region Selection Changed Handlers

        private void cbxCharge_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            try
            {
                if (!setup) return;

                string? selectedText = (sender as ComboBox)?.SelectedItem?.ToString();
                string profileName = ResolveProfileName(selectedText);

                Settings.Default.acPreset = selectedText ?? "None";
                Settings.Default.acCommandString = PresetProfileApplier.BuildCommandString(profileName);
                Settings.Default.Save();
            }
            catch (Exception ex) { MessageBox.Show(ex.ToString()); }
        }

        private void cbxDischarge_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!setup) return;

            string? selectedText = (sender as ComboBox)?.SelectedItem?.ToString();
            string profileName = ResolveProfileName(selectedText);

            Settings.Default.dcPreset = selectedText ?? "None";
            Settings.Default.dcCommandString = PresetProfileApplier.BuildCommandString(profileName);
            Settings.Default.Save();
        }

        private void cbxResume_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            try
            {
                if (!setup) return;

                string? selectedText = (sender as ComboBox)?.SelectedItem?.ToString();
                string profileName = ResolveProfileName(selectedText);

                Settings.Default.resumePreset = selectedText ?? "None";
                Settings.Default.resumeCommandString = PresetProfileApplier.BuildCommandString(profileName);
                Settings.Default.Save();
            }
            catch (Exception ex) { MessageBox.Show(ex.ToString()); }
        }

        #endregion

        #region Helpers

        /// <summary>
        /// Maps a display name ("Office", "Custom 3") back to the profile key ("Office", "Custom 3").
        /// Returns empty string for "None" or unrecognized values. Internal: the watcher rule
        /// dialog resolves its preset selection through the same mapping.
        /// </summary>
        internal static string ResolveProfileName(string? displayName)
        {
            if (string.IsNullOrEmpty(displayName) || displayName == "None")
                return string.Empty;

            // Check for custom preset display names first (e.g. "Custom 3") -- display name equals profile key.
            if (PresetProfileApplier.TryParseCustomSlot(displayName) >= 0)
            {
                return displayName;
            }

            return displayName switch
            {
                string s when s.Equals("Office", StringComparison.OrdinalIgnoreCase) => "Office",
                string s when s.Equals("Gaming", StringComparison.OrdinalIgnoreCase) => "Gaming",
                string s when s.Equals("Turbo", StringComparison.OrdinalIgnoreCase) => "Turbo",
                _ => string.Empty
            };
        }

        private void getAcPreset(string searchName)
        {
            setPresetFromSaved(cbxCharge, searchName, (val) => Settings.Default.acPreset = val);
        }

        private void getDcPreset(string searchName)
        {
            setPresetFromSaved(cbxDischarge, searchName, (val) => Settings.Default.dcPreset = val);
        }

        private void getResumePreset(string searchName)
        {
            setPresetFromSaved(cbxResume, searchName, (val) => Settings.Default.resumePreset = val);
        }

        /// <summary>
        /// Selects a combobox item matching the saved display name.
        /// Falls back to resolving old display name format (e.g. "Office Preset" → "Office") and saves the corrected value.
        /// If the saved preset is not found (e.g. it belongs to a different power mode pool), resets to "None".
        /// </summary>
        private static void setPresetFromSaved(ComboBox comboBox, string searchName, Action<string?> saveCallback)
        {
            if (string.IsNullOrEmpty(searchName))
            {
                comboBox.SelectedIndex = 0;
                return;
            }

            // Exact match first
            foreach (var item in comboBox.Items)
            {
                if (item.ToString() == searchName)
                {
                    comboBox.SelectedItem = item;
                    return;
                }
            }

            // Fallback: resolve old display name to profile key, then find new display name
            string profileKey = ResolveProfileName(searchName);
            if (!string.IsNullOrEmpty(profileKey))
            {
                string newDisplayName = PresetProfileApplier.DisplayName(profileKey);
                foreach (var item in comboBox.Items)
                {
                    if (item.ToString() == newDisplayName)
                    {
                        comboBox.SelectedItem = item;
                        saveCallback(newDisplayName);
                        Settings.Default.Save();
                        return;
                    }
                }
            }

            // Preset not found — likely from a different power mode pool. Reset to None.
            comboBox.SelectedIndex = 0;
            saveCallback(null);
            Settings.Default.Save();
        }

        #endregion

        /// <summary>
        /// Intentionally left empty; page initialization is handled in the constructor.
        /// </summary>
        private void Page_Loaded(object sender, RoutedEventArgs e)
        {
            // Intentionally empty: page initialization is handled in the constructor.
        }

        /// <summary>
        /// Intentionally left empty; checkbox state is managed by data binding.
        /// </summary>
        private void CheckBox_Click(object sender, RoutedEventArgs e)
        {
            // Intentionally empty: checkbox state is managed by data binding.
        }

        #region GPU Power Saving

        /// <summary>
        /// Fills a ComboBox with the GPU preference options. The Tag carries the
        /// GpuPowerSavingMode value so selection handlers never depend on item order —
        /// "No change" leads as the default (the engine leaves the GPU untouched).
        /// </summary>
        private static void PopulateGpuPowerSavingComboBox(ComboBox comboBox)
        {
            comboBox.Items.Clear();
            comboBox.Items.Add(new ComboBoxItem { Content = "No change", Tag = (int)GpuPowerSavingMode.NoChange });
            comboBox.Items.Add(new ComboBoxItem { Content = "iGPU", Tag = (int)GpuPowerSavingMode.Enable });
            comboBox.Items.Add(new ComboBoxItem { Content = "dGPU", Tag = (int)GpuPowerSavingMode.Disable });
            comboBox.Items.Add(new ComboBoxItem { Content = "Auto", Tag = (int)GpuPowerSavingMode.Auto });
        }

        /// <summary>Reads the GpuPowerSavingMode value carried by the selected item's Tag.</summary>
        private static int SelectedGpuPowerSavingMode(ComboBox comboBox)
        {
            return comboBox.SelectedItem is ComboBoxItem item && item.Tag is int mode
                ? mode
                : (int)GpuPowerSavingMode.NoChange;
        }

        /// <summary>Index of the item whose Tag carries the given mode, or -1 when absent.</summary>
        private static int IndexOfGpuPowerSavingMode(ComboBox comboBox, int mode)
        {
            for (int i = 0; i < comboBox.Items.Count; i++)
            {
                if (comboBox.Items[i] is ComboBoxItem item && item.Tag is int tag && tag == mode)
                    return i;
            }
            return -1;
        }

        private void PopulateGpuPowerSavingComboBoxes()
        {
            PopulateGpuPowerSavingComboBox(cbxGpuPowerSavingBattery);
            PopulateGpuPowerSavingComboBox(cbxGpuPowerSavingPluggedIn);

            // Check if we're in Hybrid mode and show/hide cards accordingly
            UpdateGpuPowerSavingVisibility();
        }

        private void UpdateGpuPowerSavingVisibility()
        {
            bool isHybrid = false;

            try
            {
                if (_mux is not null)
                {
                    var mode = _mux.GetModeAsync().GetAwaiter().GetResult();
                    isHybrid = mode == GpuMuxMode.Hybrid;
                }
            }
            catch (Exception ex)
            {
                Log.Information("[Automations] Failed to read GPU MUX mode: {Message}", ex.Message);
            }

            Visibility visibility = isHybrid ? Visibility.Visible : Visibility.Collapsed;
            SeparatorGpuBattery.Visibility = visibility;
            RowGpuPowerSavingBattery.Visibility = visibility;
            SeparatorGpuPluggedIn.Visibility = visibility;
            RowGpuPowerSavingPluggedIn.Visibility = visibility;
        }

        private void cbxGpuPowerSavingBattery_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!setup) return;

            int mode = SelectedGpuPowerSavingMode(cbxGpuPowerSavingBattery);
            Settings.Default.GpuPowerSavingBattery = mode;
            Settings.Default.Save();

            // Apply immediately if currently on battery ("No change" never applies)
            var powerStatus = System.Windows.Forms.SystemInformation.PowerStatus;
            if (mode != (int)GpuPowerSavingMode.NoChange
                && powerStatus.PowerLineStatus == System.Windows.Forms.PowerLineStatus.Offline)
            {
                _ = GpuPowerSaving.ApplyAsync((GpuPowerSavingMode)mode);
            }
        }

        private void cbxGpuPowerSavingPluggedIn_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!setup) return;

            int mode = SelectedGpuPowerSavingMode(cbxGpuPowerSavingPluggedIn);
            Settings.Default.GpuPowerSavingPluggedIn = mode;
            Settings.Default.Save();

            // Apply immediately if currently plugged in ("No change" never applies)
            var powerStatus = System.Windows.Forms.SystemInformation.PowerStatus;
            if (mode != (int)GpuPowerSavingMode.NoChange
                && powerStatus.PowerLineStatus != System.Windows.Forms.PowerLineStatus.Offline)
            {
                _ = GpuPowerSaving.ApplyAsync((GpuPowerSavingMode)mode);
            }
        }

        private void setGpuPowerSavingBattery(int mode)
        {
            int index = IndexOfGpuPowerSavingMode(cbxGpuPowerSavingBattery, mode);
            cbxGpuPowerSavingBattery.SelectedIndex = index >= 0 ? index : 0; // Fall back to No change
        }

        private void setGpuPowerSavingPluggedIn(int mode)
        {
            int index = IndexOfGpuPowerSavingMode(cbxGpuPowerSavingPluggedIn, mode);
            cbxGpuPowerSavingPluggedIn.SelectedIndex = index >= 0 ? index : 0; // Fall back to No change
        }

        #endregion

        #region Internal Panel Refresh Rate

        private void PopulateRefreshRateComboBoxes()
        {
            PopulateRefreshRateComboBox(cbxRefreshRateBattery);
            PopulateRefreshRateComboBox(cbxRefreshRatePluggedIn);
        }

        /// <summary>True while the runtime mux mode is dGPU-only (unlocks the 300 Hz mode).</summary>
        private bool IsDgpuOnlyMode()
        {
            try
            {
                return _mux is not null
                    && _mux.GetModeAsync().GetAwaiter().GetResult() == GpuMuxMode.DgpuOnly;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Fills a ComboBox with the internal panel's offered rates (Tag carries the Hz
        /// value so selection handlers never re-parse the label). 300 Hz appears only
        /// while dGPU-only mux is active — Windows does not enumerate it otherwise.
        /// </summary>
        private void PopulateRefreshRateComboBox(ComboBox comboBox)
        {
            comboBox.Items.Clear();
            // "No change" (Tag 0) leads as the default — the engine treats hz <= 0 as "don't apply".
            comboBox.Items.Add(new ComboBoxItem { Content = "No change", Tag = 0 });
            foreach (int hz in Uniwill_Control_Utility.Scripts.Misc.Display.GetInternalRefreshRates(IsDgpuOnlyMode()))
            {
                comboBox.Items.Add(new ComboBoxItem { Content = $"{hz} Hz", Tag = hz });
            }
        }

        // These target the internal panel only — always available, regardless of
        // which display is in use (an external monitor never affects them).
        private void UpdateRefreshRateVisibility()
        {
            SeparatorRefreshRateBattery.Visibility = Visibility.Visible;
            RowRefreshRateBattery.Visibility = Visibility.Visible;
            SeparatorRefreshRatePluggedIn.Visibility = Visibility.Visible;
            RowRefreshRatePluggedIn.Visibility = Visibility.Visible;
        }

        private static int SelectedRefreshRateHz(ComboBox comboBox)
        {
            return comboBox.SelectedItem is ComboBoxItem item && item.Tag is int hz ? hz : 0;
        }

        private static int IndexOfRefreshRateHz(ComboBox comboBox, int hz)
        {
            for (int i = 0; i < comboBox.Items.Count; i++)
            {
                if (comboBox.Items[i] is ComboBoxItem item && item.Tag is int tag && tag == hz)
                    return i;
            }
            return -1;
        }

        private void setRefreshRateBattery(int hz)
        {
            int index = IndexOfRefreshRateHz(cbxRefreshRateBattery, hz);
            cbxRefreshRateBattery.SelectedIndex = index >= 0 ? index : 0; // Fall back to No change
        }

        private void setRefreshRatePluggedIn(int hz)
        {
            int index = IndexOfRefreshRateHz(cbxRefreshRatePluggedIn, hz);
            cbxRefreshRatePluggedIn.SelectedIndex = index >= 0 ? index : 0; // Fall back to No change
        }

        private void cbxRefreshRateBattery_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!setup) return;

            int hz = SelectedRefreshRateHz(cbxRefreshRateBattery);
            Settings.Default.RefreshRateBattery = hz;
            Settings.Default.Save();

            // Apply immediately if currently on battery
            var powerStatus = System.Windows.Forms.SystemInformation.PowerStatus;
            if (powerStatus.PowerLineStatus == System.Windows.Forms.PowerLineStatus.Offline && hz > 0)
            {
                _ = Task.Run(() => Uniwill_Control_Utility.Scripts.Misc.Display.ApplyInternalRefreshRate(hz));
            }
        }

        private void cbxRefreshRatePluggedIn_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!setup) return;

            int hz = SelectedRefreshRateHz(cbxRefreshRatePluggedIn);
            Settings.Default.RefreshRatePluggedIn = hz;
            Settings.Default.Save();

            // Apply immediately if currently plugged in
            var powerStatus = System.Windows.Forms.SystemInformation.PowerStatus;
            if (powerStatus.PowerLineStatus != System.Windows.Forms.PowerLineStatus.Offline && hz > 0)
            {
                _ = Task.Run(() => Uniwill_Control_Utility.Scripts.Misc.Display.ApplyInternalRefreshRate(hz));
            }
        }

        #endregion

        #region Power Tuning Guardian

        // Persist-only rows (U-107): the constructor reflects the saved state
        // before `setup` is armed, so the events that write raises are ignored;
        // a handler past the guard is user intent, and the row actuates nothing
        // on reflection — the guardian loop reads these Settings itself.

        private void cbPowerGuardGpu_Checked(object sender, RoutedEventArgs e)
        {
            if (!setup) return;
            Settings.Default.PowerGuardGpu = true;
            Settings.Default.Save();
        }

        private void cbPowerGuardGpu_Unchecked(object sender, RoutedEventArgs e)
        {
            if (!setup) return;
            Settings.Default.PowerGuardGpu = false;
            Settings.Default.Save();
        }

        private void cbPowerGuardCpu_Checked(object sender, RoutedEventArgs e)
        {
            if (!setup) return;
            Settings.Default.PowerGuardCpu = true;
            Settings.Default.Save();
        }

        private void cbPowerGuardCpu_Unchecked(object sender, RoutedEventArgs e)
        {
            if (!setup) return;
            Settings.Default.PowerGuardCpu = false;
            Settings.Default.Save();
        }

        #endregion

        #region Watcher Rules (U-201)

        // The card edits watchers.json through the WatcherStore: every change
        // goes Save -> the store raises Changed -> the hosted hub reloads its
        // live rule set. The rows carry the rule records; Enabled lives on the
        // row and is folded back into the record at save time.

        private readonly WatcherStore? _watcherStore = App.GetService<WatcherStore>();
        private readonly ObservableCollection<WatcherRuleRow> _watcherRows = new();
        private bool _watcherLoading;

        /// <summary>Loads the store, binds the rows, and arms culture-aware
        /// summary refresh. Called from the constructor.</summary>
        private void InitWatcherRules()
        {
            try
            {
                _watcherStore?.Load();
            }
            catch (Exception ex)
            {
                Log.Warning("[Automations] watchers.json load failed: {Message}", ex.Message);
            }

            icWatcherRules.ItemsSource = _watcherRows;
            RefreshWatcherRows();

            LocalizationService.CultureChanged += OnWatcherCultureChanged;
        }

        private void OnWatcherCultureChanged(object? sender, EventArgs e)
        {
            // Summaries are localized fragments; rebuild the rows so they
            // re-render in the new culture.
            Dispatcher.BeginInvoke(new Action(RefreshWatcherRows));
        }

        private void RefreshWatcherRows()
        {
            _watcherLoading = true;

            foreach (WatcherRuleRow row in _watcherRows)
                row.PropertyChanged -= WatcherRowChanged;
            _watcherRows.Clear();

            if (_watcherStore is not null)
            {
                foreach (WatcherRule rule in _watcherStore.Rules)
                {
                    WatcherRuleRow row = new(rule);
                    row.PropertyChanged += WatcherRowChanged;
                    _watcherRows.Add(row);
                }
            }

            _watcherLoading = false;

            txtWatcherRulesEmpty.Visibility = _watcherRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void WatcherRowChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (_watcherLoading || e.PropertyName != nameof(WatcherRuleRow.Enabled))
                return;

            SaveWatcherRules(_watcherRows.Select(r => r.ToRule()).ToList());
        }

        private void SaveWatcherRules(List<WatcherRule> rules)        {
            if (_watcherStore is null)
                return;

            try
            {
                // The store validates every rule and raises Changed (the hub
                // reloads): the page never touches the hub directly.
                _watcherStore.Save(rules);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message,
                    LocalizationService.Get("Watcher Rules"),
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            RefreshWatcherRows();
        }

        /// <summary>The preset display names for the dialog's combo — the
        /// same pool the AC/DC/resume rows offer (built-ins + the changed
        /// custom slots of the active mode), minus "None": a watcher rule's
        /// apply-preset action requires a profile.</summary>
        private static List<string> WatcherPresetDisplayNames()
        {
            List<string> names = new();
            foreach (string name in PresetProfileApplier.BuiltInProfileNames)
                names.Add(PresetProfileApplier.DisplayName(name));

            PresetMode mode = ActivePresetMode;
            var presets = CustomPresetStore.Load(mode);
            for (int i = 0; i < presets.Length; i++)
            {
                if (presets[i].HasChanges)
                    names.Add(PresetProfileApplier.CustomProfileKey(i, mode));
            }

            return names;
        }

        private void btnAddWatcherRule_Click(object sender, RoutedEventArgs e)
        {
            if (_watcherStore is null)
                return;

            if (_watcherRows.Count >= WatcherPolicy.MaxRules)
            {
                MessageBox.Show(
                    LocalizationService.Format("At most {0} watcher rules.", WatcherPolicy.MaxRules),
                    LocalizationService.Get("Watcher Rules"),
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            WatcherRuleDialog dialog = new(null, WatcherPresetDisplayNames())
            {
                Owner = Window.GetWindow(this),
            };

            if (dialog.ShowDialog() == true && dialog.Rule is WatcherRule rule)
            {
                List<WatcherRule> updated = _watcherRows.Select(r => r.ToRule()).ToList();
                updated.Add(rule);
                SaveWatcherRules(updated);
            }
        }

        private void btnEditWatcherRule_Click(object sender, RoutedEventArgs e)
        {
            if (_watcherStore is null || sender is not FrameworkElement { DataContext: WatcherRuleRow row })
                return;

            int index = _watcherRows.IndexOf(row);
            if (index < 0)
                return;

            WatcherRuleDialog dialog = new(row.ToRule(), WatcherPresetDisplayNames())
            {
                Owner = Window.GetWindow(this),
            };

            if (dialog.ShowDialog() == true && dialog.Rule is WatcherRule edited)
            {
                List<WatcherRule> updated = _watcherRows.Select(r => r.ToRule()).ToList();
                updated[index] = edited;
                SaveWatcherRules(updated);
            }
        }

        private void btnDeleteWatcherRule_Click(object sender, RoutedEventArgs e)
        {
            if (_watcherStore is null || sender is not FrameworkElement { DataContext: WatcherRuleRow row })
                return;

            List<WatcherRule> updated = _watcherRows
                .Where(r => !ReferenceEquals(r, row))
                .Select(r => r.ToRule())
                .ToList();

            SaveWatcherRules(updated);
        }

        /// <summary>One card row: the rule plus the live Enabled toggle and
        /// the localized one-line summary.</summary>
        #region Keyboard shortcuts (U-204)

        private readonly ObservableCollection<ShortcutRow> _shortcutRows = new();
        private ShortcutService? _shortcutService;

        private sealed class ShortcutRow
        {
            public string Chord { get; init; } = string.Empty;

            public string Summary { get; init; } = string.Empty;

            public ShortcutBinding Binding { get; init; } = new();
        }

        /// <summary>Binds the shortcut map (U-204). The dispatcher lives in the
        /// overlay's global hook; this page only edits and saves the file.</summary>
        private void InitShortcuts()
        {
            _shortcutService = App.GetService<ShortcutService>();

            icShortcuts.ItemsSource = _shortcutRows;
            RefreshShortcutRows();

            // Summaries are localized fragments: rebuild on culture change.
            LocalizationService.CultureChanged += (_, _) =>
                Dispatcher.BeginInvoke(new Action(RefreshShortcutRows));
        }

        private void RefreshShortcutRows()
        {
            _shortcutRows.Clear();

            if (_shortcutService is not null)
            {
                foreach (ShortcutBinding binding in _shortcutService.Bindings)
                {
                    _shortcutRows.Add(new ShortcutRow
                    {
                        Chord = binding.Chord,
                        Summary = ShortcutSummary(binding),
                        Binding = binding,
                    });
                }
            }

            txtShortcutsEmpty.Visibility = _shortcutRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private static string ShortcutSummary(ShortcutBinding binding) => binding.Action switch
        {
            ShortcutAction.ApplyPreset =>
                LocalizationService.Get("Apply preset") + ": " + PresetProfileApplier.DisplayName(binding.PresetName ?? string.Empty),
            ShortcutAction.SetPerformanceMode =>
                LocalizationService.Get("Set performance mode") + ": " + binding.PerformanceMode,
            ShortcutAction.SetFanTurbo => LocalizationService.Get("Enable fan turbo"),
            ShortcutAction.PauseOemTakeover => LocalizationService.Get("Pause OEM Control Center"),
            ShortcutAction.ResumeOemTakeover => LocalizationService.Get("Restore OEM Control Center"),
            _ => binding.Action.ToString(),
        };

        private IReadOnlyList<string> ShortcutPresetChoices() =>
            PresetProfileApplier.GetProfileNames()
                .Select(PresetProfileApplier.DisplayName)
                .ToList();

        private void SaveShortcuts(List<ShortcutBinding> bindings)
        {
            if (_shortcutService is null)
                return;

            ShortcutStore? store = App.GetService<ShortcutStore>();
            if (store is null)
                return;

            try
            {
                store.Save(bindings);
                _shortcutService.Reload();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message,
                    LocalizationService.Get("Keyboard shortcuts"),
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            RefreshShortcutRows();
        }

        private void btnAddShortcut_Click(object sender, RoutedEventArgs e)
        {
            if (_shortcutService is null || _shortcutService.Bindings.Count >= ShortcutStore.MaxBindings)
                return;

            var dialog = new ShortcutDialog(ShortcutPresetChoices())
            {
                Owner = Window.GetWindow(this),
            };

            if (dialog.ShowDialog() != true || dialog.Result is null)
                return;

            List<ShortcutBinding> updated = _shortcutService.Bindings.ToList();
            updated.Add(dialog.Result);
            SaveShortcuts(updated);
        }

        private void btnEditShortcut_Click(object sender, RoutedEventArgs e)
        {
            if (_shortcutService is null ||
                (sender as FrameworkElement)?.DataContext is not ShortcutRow row)
            {
                return;
            }

            var dialog = new ShortcutDialog(ShortcutPresetChoices(), row.Binding)
            {
                Owner = Window.GetWindow(this),
            };

            if (dialog.ShowDialog() != true || dialog.Result is null)
                return;

            List<ShortcutBinding> updated = _shortcutService.Bindings.ToList();
            int index = updated.FindIndex(b => string.Equals(b.Chord, row.Binding.Chord, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
                return;

            updated[index] = dialog.Result;
            SaveShortcuts(updated);
        }

        private void btnDeleteShortcut_Click(object sender, RoutedEventArgs e)
        {
            if (_shortcutService is null ||
                (sender as FrameworkElement)?.DataContext is not ShortcutRow row)
            {
                return;
            }

            List<ShortcutBinding> updated = _shortcutService.Bindings
                .Where(b => !string.Equals(b.Chord, row.Binding.Chord, StringComparison.OrdinalIgnoreCase))
                .ToList();

            SaveShortcuts(updated);
        }

        #endregion

        public sealed class WatcherRuleRow : INotifyPropertyChanged
        {
            private readonly WatcherRule _rule;
            private bool _enabled;

            public WatcherRuleRow(WatcherRule rule)
            {
                _rule = rule;
                _enabled = rule.Enabled;
            }

            public string Name => _rule.Name;

            public string Summary => WatcherVocabulary.Describe(_rule);

            public bool Enabled
            {
                get => _enabled;
                set
                {
                    if (_enabled == value)
                        return;
                    _enabled = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Enabled)));
                }
            }

            public WatcherRule ToRule() => _rule with { Enabled = _enabled };

            public event PropertyChangedEventHandler? PropertyChanged;
        }

        #endregion
    }
}
