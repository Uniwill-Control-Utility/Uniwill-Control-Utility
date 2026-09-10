using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Uniwill_Control_Utility.Properties;
using Uniwill_Control_Utility.Scripts;
using Uniwill_Control_Utility.Services;

namespace Uniwill_Control_Utility.Views.Pages
{
    public partial class Automations : Page
    {
        bool setup = false;

        public Automations()
        {
            InitializeComponent();
            _ = Tablet.TabletDevices;

            PopulateComboBox(cbxCharge);
            PopulateComboBox(cbxDischarge);
            PopulateComboBox(cbxResume);

            // Restore saved selections
            getAcPreset(Settings.Default.acPreset);
            getDcPreset(Settings.Default.dcPreset);
            getResumePreset(Settings.Default.resumePreset);

            setup = true;
        }

        #region ComboBox Population

        private static void PopulateComboBox(ComboBox comboBox)
        {
            comboBox.Items.Add("None");
            foreach (string name in PresetProfileApplier.ProfileNames)
            {
                comboBox.Items.Add(PresetProfileApplier.DisplayName(name));
            }
        }

        #endregion

        #region Load Buttons

        private void btnChargeLoad_Click(object sender, RoutedEventArgs e)
        {
            setup = false;
            string oldPreset = Settings.Default.acPreset;
            cbxCharge.Items.Clear();
            PopulateComboBox(cbxCharge);
            getAcPreset(oldPreset);
            setup = true;
        }

        private void btnDischargeLoad_Click(object sender, RoutedEventArgs e)
        {
            setup = false;
            string oldPreset = Settings.Default.dcPreset;
            cbxDischarge.Items.Clear();
            PopulateComboBox(cbxDischarge);
            getDcPreset(oldPreset);
            setup = true;
        }

        private void btnResume_Click(object sender, RoutedEventArgs e)
        {
            setup = false;
            string oldPreset = Settings.Default.resumePreset;
            cbxResume.Items.Clear();
            PopulateComboBox(cbxResume);
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
        /// Maps a display name ("Office Preset") back to the profile key ("Office").
        /// Returns empty string for "None" or unrecognized values.
        /// </summary>
        private static string ResolveProfileName(string? displayName)
        {
            if (string.IsNullOrEmpty(displayName) || displayName == "None")
                return string.Empty;

            return displayName switch
            {
                string s when s.Contains("Office", StringComparison.OrdinalIgnoreCase) => "Office",
                string s when s.Contains("Gaming", StringComparison.OrdinalIgnoreCase) => "Gaming",
                string s when s.Contains("Turbo", StringComparison.OrdinalIgnoreCase) => "Turbo",
                string s when s.Contains("Custom", StringComparison.OrdinalIgnoreCase) => "Custom",
                _ => string.Empty
            };
        }

        private void getAcPreset(string searchName)
        {
            if (string.IsNullOrEmpty(searchName))
            {
                cbxCharge.SelectedIndex = 0;
                return;
            }

            foreach (var item in cbxCharge.Items)
            {
                if (item.ToString() == searchName)
                {
                    cbxCharge.SelectedItem = item;
                    return;
                }
            }

            cbxCharge.SelectedIndex = 0;
        }

        private void getDcPreset(string searchName)
        {
            if (string.IsNullOrEmpty(searchName))
            {
                cbxDischarge.SelectedIndex = 0;
                return;
            }

            foreach (var item in cbxDischarge.Items)
            {
                if (item.ToString() == searchName)
                {
                    cbxDischarge.SelectedItem = item;
                    return;
                }
            }

            cbxDischarge.SelectedIndex = 0;
        }

        private void getResumePreset(string searchName)
        {
            if (string.IsNullOrEmpty(searchName))
            {
                cbxResume.SelectedIndex = 0;
                return;
            }

            foreach (var item in cbxResume.Items)
            {
                if (item.ToString() == searchName)
                {
                    cbxResume.SelectedItem = item;
                    return;
                }
            }

            cbxResume.SelectedIndex = 0;
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
    }
}
