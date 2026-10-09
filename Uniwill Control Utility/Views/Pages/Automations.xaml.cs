using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Serilog;
using Uniwill_Control_Utility.Models;
using Uniwill_Control_Utility.Properties;
using Uniwill_Control_Utility.Scripts;
using Uniwill_Control_Utility.Scripts.GPUs.NVIDIA;
using Uniwill_Control_Utility.Services;

namespace Uniwill_Control_Utility.Views.Pages
{
    public partial class Automations : Page
    {
        bool setup = false;
        private readonly UniwillECService? _ecService;

        public Automations()
        {
            InitializeComponent();
            _ = Tablet.TabletDevices;

            _ecService = App.GetService<UniwillECService>();

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
        /// Returns empty string for "None" or unrecognized values.
        /// </summary>
        private static string ResolveProfileName(string? displayName)
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

        private static void PopulateGpuPowerSavingComboBox(ComboBox comboBox)
        {
            comboBox.Items.Add("iGPU");
            comboBox.Items.Add("dGPU");
            comboBox.Items.Add("Auto");
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
                if (_ecService is not null)
                {
                    var mode = _ecService.GetCurrentGpuMuxMode();
                    isHybrid = mode == UniwillECService.GpuMuxMode.Hybrid;
                }
            }
            catch (Exception ex)
            {
                Log.Information("[Automations] Failed to read GPU MUX mode: {Message}", ex.Message);
            }

            Visibility visibility = isHybrid ? Visibility.Visible : Visibility.Collapsed;
            CardGpuPowerSavingBattery.Visibility = visibility;
            CardGpuPowerSavingPluggedIn.Visibility = visibility;
        }

        private void cbxGpuPowerSavingBattery_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!setup) return;

            int mode = cbxGpuPowerSavingBattery.SelectedIndex;
            Settings.Default.GpuPowerSavingBattery = mode;
            Settings.Default.Save();

            // Apply immediately if currently on battery
            var powerStatus = System.Windows.Forms.SystemInformation.PowerStatus;
            if (powerStatus.PowerLineStatus == System.Windows.Forms.PowerLineStatus.Offline)
            {
                _ = GpuPowerSaving.ApplyAsync((GpuPowerSavingMode)mode);
            }
        }

        private void cbxGpuPowerSavingPluggedIn_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!setup) return;

            int mode = cbxGpuPowerSavingPluggedIn.SelectedIndex;
            Settings.Default.GpuPowerSavingPluggedIn = mode;
            Settings.Default.Save();

            // Apply immediately if currently plugged in
            var powerStatus = System.Windows.Forms.SystemInformation.PowerStatus;
            if (powerStatus.PowerLineStatus != System.Windows.Forms.PowerLineStatus.Offline)
            {
                _ = GpuPowerSaving.ApplyAsync((GpuPowerSavingMode)mode);
            }
        }

        private void setGpuPowerSavingBattery(int mode)
        {
            if (mode >= 0 && mode <= 2)
            {
                cbxGpuPowerSavingBattery.SelectedIndex = mode;
            }
            else
            {
                cbxGpuPowerSavingBattery.SelectedIndex = 2; // Default to Auto
            }
        }

        private void setGpuPowerSavingPluggedIn(int mode)
        {
            if (mode >= 0 && mode <= 2)
            {
                cbxGpuPowerSavingPluggedIn.SelectedIndex = mode;
            }
            else
            {
                cbxGpuPowerSavingPluggedIn.SelectedIndex = 2; // Default to Auto
            }
        }

        #endregion
    }
}
