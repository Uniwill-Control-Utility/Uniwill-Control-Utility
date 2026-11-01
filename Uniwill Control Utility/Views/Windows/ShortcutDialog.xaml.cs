using System;
using System.Collections.Generic;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Uniwill_Control_Utility.Services;

namespace Uniwill_Control_Utility.Views.Windows
{
    /// <summary>
    /// The shortcut editor (U-204): chord capture plus the action whitelist,
    /// with the payload field the chosen action needs. The chord box borrows
    /// the overlay page's capture behaviour verbatim (modifiers + primary key,
    /// modifier-only presses ignored), so both recorders speak one format.
    /// </summary>
    public partial class ShortcutDialog : Wpf.Ui.Controls.FluentWindow
    {
        private static readonly ShortcutAction[] Actions =
        {
            ShortcutAction.ApplyPreset,
            ShortcutAction.SetPerformanceMode,
            ShortcutAction.SetFanTurbo,
            ShortcutAction.PauseOemTakeover,
            ShortcutAction.ResumeOemTakeover,
        };

        private readonly IReadOnlyList<string> _presetNames;

        public ShortcutBinding? Result { get; private set; }

        public ShortcutDialog(IReadOnlyList<string> presetDisplayNames, ShortcutBinding? existing = null)
        {
            _presetNames = presetDisplayNames;
            InitializeComponent();

            TitleBar.Title = LocalizationService.Get("Keyboard Shortcut");
            lblChord.Text = LocalizationService.Get("Shortcut");
            lblChordHint.Text = LocalizationService.Get("Click the box and press the shortcut you want");
            lblAction.Text = LocalizationService.Get("Action");
            lblPreset.Text = LocalizationService.Get("Preset");
            lblMode.Text = LocalizationService.Get("Performance mode");
            btnCancel.Content = LocalizationService.Get("Cancel");
            btnSave.Content = LocalizationService.Get("Save");

            cbxAction.ItemsSource = ActionNames();
            cbxPreset.ItemsSource = _presetNames;

            if (existing is null)
            {
                cbxAction.SelectedIndex = 0;
                if (_presetNames.Count > 0)
                    cbxPreset.SelectedIndex = 0;
            }
            else
            {
                txtChord.Text = existing.Chord;
                cbxAction.SelectedIndex = Array.IndexOf(Actions, existing.Action);

                int presetIndex = IndexOfDisplayName(existing.PresetName);
                if (presetIndex >= 0)
                    cbxPreset.SelectedIndex = presetIndex;

                nbMode.Value = existing.PerformanceMode ?? 0;
            }

            UpdatePayloadPanels();
        }

        private static string[] ActionNames() => new[]
        {
            LocalizationService.Get("Apply preset"),
            LocalizationService.Get("Set performance mode"),
            LocalizationService.Get("Enable fan turbo"),
            LocalizationService.Get("Pause OEM Control Center"),
            LocalizationService.Get("Restore OEM Control Center"),
        };

        private int IndexOfDisplayName(string? profileName)
        {
            if (string.IsNullOrEmpty(profileName))
                return -1;

            string display = PresetProfileApplier.DisplayName(profileName);
            for (int i = 0; i < _presetNames.Count; i++)
                if (string.Equals(_presetNames[i], display, StringComparison.Ordinal))
                    return i;

            return -1;
        }

        private void ChordTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            e.Handled = true;

            if (sender is not TextBox textBox)
                return;

            Key key = e.Key == Key.System ? e.SystemKey : e.Key;

            if (key is
                Key.LeftCtrl or Key.RightCtrl or
                Key.LeftAlt or Key.RightAlt or
                Key.LeftShift or Key.RightShift or
                Key.LWin or Key.RWin)
            {
                return;
            }

            ModifierKeys modifiers = Keyboard.Modifiers;
            var value = new StringBuilder();

            if (modifiers.HasFlag(ModifierKeys.Control))
                value.Append("Ctrl+");

            if (modifiers.HasFlag(ModifierKeys.Alt))
                value.Append("Alt+");

            if (modifiers.HasFlag(ModifierKeys.Shift))
                value.Append("Shift+");

            value.Append(key);

            textBox.Text = value.ToString();
            textBox.CaretIndex = textBox.Text.Length;
        }

        private void Action_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdatePayloadPanels();

        private void UpdatePayloadPanels()
        {
            ShortcutAction action = Actions[Math.Clamp(cbxAction.SelectedIndex, 0, Actions.Length - 1)];
            panelPreset.Visibility = action == ShortcutAction.ApplyPreset ? Visibility.Visible : Visibility.Collapsed;
            panelMode.Visibility = action == ShortcutAction.SetPerformanceMode ? Visibility.Visible : Visibility.Collapsed;
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            string chord = txtChord.Text?.Trim() ?? string.Empty;
            if (!Hotkey.TryParse(chord, out _))
            {
                txtChord.Focus();
                return;
            }

            ShortcutAction action = Actions[Math.Clamp(cbxAction.SelectedIndex, 0, Actions.Length - 1)];

            string? presetName = null;
            if (action == ShortcutAction.ApplyPreset)
            {
                if (cbxPreset.SelectedIndex < 0)
                    return;

                // Persist the RAW profile name (the display name is a localized
                // label; the arbiter needs the identity) - the same resolver the
                // watcher editor uses.
                presetName = Views.Pages.Automations.ResolveProfileName(
                    _presetNames[cbxPreset.SelectedIndex]);
            }

            Result = new ShortcutBinding
            {
                Chord = chord,
                Action = action,
                PresetName = presetName,
                PerformanceMode = action == ShortcutAction.SetPerformanceMode
                    ? (int)(nbMode.Value ?? 0)
                    : null,
            };

            DialogResult = true;
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
