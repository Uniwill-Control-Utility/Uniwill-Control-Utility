using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using Uniwill_Control_Utility.Scripts.GPUs.NVIDIA;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using Microsoft.Win32;

namespace Uniwill_Control_Utility.Views.Windows
{
    /// <summary>
    /// Dedicated popup window for editing GPU V-F curves.
    /// Gives the editor full keyboard focus so Tab, arrows, etc. work without parent conflicts.
    /// Auto-saves to the active V-F curve preset slot (managed by the Presets page).
    /// </summary>
    public partial class VfCurveEditorWindow : FluentWindow
    {
        /// <summary> Fired when the curve changes (auto-save). Allows the owner page to refresh labels. </summary>
        public event EventHandler? CurveChanged;

        /// <summary> Returns the current V-F curve offsets from the editor. </summary>
        public Dictionary<int, int> GetOffsets() => _editor.GetOffsets();

        private readonly int _activePresetSlot;
        private readonly Models.PresetMode _presetMode;

        public VfCurveEditorWindow(Dictionary<int, int>? initialOffsets = null, int activePresetSlot = 0, Models.PresetMode presetMode = Models.PresetMode.Normal)
        {
            InitializeComponent();
            _activePresetSlot = activePresetSlot;
            _presetMode = presetMode;
            _editor.CurveChanged += Editor_CurveChanged;
            _editor.LoadCurve(initialOffsets);

            // Focus the editor canvas so keyboard shortcuts (Ctrl+A, Ctrl+D) work immediately.
            Loaded += (_, _) => _editor.FocusCanvas();

            // Listen for system theme/accent color changes via WPF-UI's event-driven watcher.
            SystemThemeWatcher.Watch(this, updateAccents: true);
            ApplicationThemeManager.Changed += OnThemeChanged;

            Closing += (_, _) =>
            {
                ApplicationThemeManager.Changed -= OnThemeChanged;
                SystemThemeWatcher.UnWatch(this);
            };
        }

        private void OnThemeChanged(ApplicationTheme theme, Color accent)
        {
            // Theme changed — no preset buttons to update anymore.
        }

        private void Editor_CurveChanged(object? sender, EventArgs e)
        {
            // Auto-save V-F curve offsets to the active preset slot in CustomPresetStore.
            var customPresets = Models.CustomPresetStore.Load(_presetMode);
            if (_activePresetSlot < customPresets.Length)
            {
                customPresets[_activePresetSlot].VfCurveOffsets = _editor.GetOffsets();
                Models.CustomPresetStore.Save(customPresets, _presetMode);
            }

            CurveChanged?.Invoke(this, e);
        }

        private void btnReset_Click(object sender, RoutedEventArgs e)
        {
            // Reset the live GPU curve to stock (zero offsets on all points).
            NvTuning.ResetVfCurveOffsets();

            // Clear UI offsets and save empty curve to active slot.
            _editor.ResetOffsets();
            var customPresets = Models.CustomPresetStore.Load(_presetMode);
            if (_activePresetSlot < customPresets.Length)
            {
                customPresets[_activePresetSlot].VfCurveOffsets = new Dictionary<int, int>();
                Models.CustomPresetStore.Save(customPresets, _presetMode);
            }

            // Notify parent page so labels refresh and slider flag resets.
            CurveChanged?.Invoke(this, EventArgs.Empty);
        }

        private void btnExport_Click(object sender, RoutedEventArgs e)
        {
            var offsets = _editor.GetAllOffsets();
            if (offsets.Count == 0)
            {
                System.Windows.MessageBox.Show("No V-F curve data to export.", "Export", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
                return;
            }

            var dialog = new SaveFileDialog
            {
                AddExtension = true,
                DefaultExt = ".json",
                FileName = "UCU-VF-Curve.json",
                Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*"
            };

            if (dialog.ShowDialog() != true)
                return;

            try
            {
                var json = JsonSerializer.Serialize(offsets, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(dialog.FileName, json);
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"Failed to export V-F curve:\n{ex.Message}", "Export Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            }
        }

        private void btnImport_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                CheckFileExists = true,
                DefaultExt = ".json",
                Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*"
            };

            if (dialog.ShowDialog() != true)
                return;

            try
            {
                var json = File.ReadAllText(dialog.FileName);
                var offsets = JsonSerializer.Deserialize<Dictionary<int, int>>(json);

                if (offsets == null)
                {
                    System.Windows.MessageBox.Show("The selected file does not contain valid V-F curve data.", "Import Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
                    return;
                }

                _editor.LoadCurve(offsets);

                // Save imported curve to the active preset slot.
                var customPresets = Models.CustomPresetStore.Load(_presetMode);
                if (_activePresetSlot < customPresets.Length)
                {
                    customPresets[_activePresetSlot].VfCurveOffsets = _editor.GetOffsets();
                    Models.CustomPresetStore.Save(customPresets, _presetMode);
                }

                CurveChanged?.Invoke(this, EventArgs.Empty);
            }
            catch (JsonException)
            {
                System.Windows.MessageBox.Show("The selected file is not a valid V-F curve JSON file.", "Import Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"Failed to import V-F curve:\n{ex.Message}", "Import Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            }
        }
    }
}
