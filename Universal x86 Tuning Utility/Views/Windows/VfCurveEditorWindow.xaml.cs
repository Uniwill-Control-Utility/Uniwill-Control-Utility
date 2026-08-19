using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using Universal_x86_Tuning_Utility.Models;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace Universal_x86_Tuning_Utility.Views.Windows
{
    /// <summary>
    /// Dedicated popup window for editing GPU V-F curves.
    /// Gives the editor full keyboard focus so Tab, arrows, etc. work without parent conflicts.
    /// </summary>
    public partial class VfCurveEditorWindow : FluentWindow
    {
        /// <summary> Fired when the curve changes (auto-save). Allows the owner page to refresh labels. </summary>
        public event EventHandler? CurveChanged;

        private VfCurvePreset[] _presets = Array.Empty<VfCurvePreset>();
        private int _activePreset = 0;

        public VfCurveEditorWindow(Dictionary<int, int>? initialOffsets = null)
        {
            InitializeComponent();
            _presets = VfCurvePresetStore.Load();
            _editor.CurveChanged += Editor_CurveChanged;
            _editor.LoadCurve(initialOffsets);
            updatePresetButtons();

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
            updatePresetButtons();
        }

        private void Editor_CurveChanged(object? sender, EventArgs e)
        {
            // Auto-save to active preset slot
            if (_activePreset < _presets.Length)
            {
                _presets[_activePreset].Offsets = _editor.GetOffsets();
                VfCurvePresetStore.Save(_presets);
            }
            updatePresetButtons();
            CurveChanged?.Invoke(this, e);
        }

        private void btnPreset_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not string tag) return;
            int slot = int.Parse(tag);
            if (_activePreset == slot) return;

            // Save current preset state first
            if (_activePreset < _presets.Length)
                _presets[_activePreset].Offsets = _editor.GetOffsets();

            _activePreset = slot;
            var offsets = _presets[slot].HasChanges ? _presets[slot].Offsets : null;
            _editor.LoadCurve(offsets);
            updatePresetButtons();
            VfCurvePresetStore.Save(_presets);
        }

        private void btnReset_Click(object sender, RoutedEventArgs e)
        {
            _editor.ResetOffsets();
            if (_activePreset < _presets.Length)
            {
                _presets[_activePreset].Offsets = new Dictionary<int, int>();
                VfCurvePresetStore.Save(_presets);
            }
            updatePresetButtons();
        }

        private void updatePresetButtons()
        {
            var buttons = new[] { btnPreset1, btnPreset2, btnPreset3, btnPreset4, btnPreset5 };
            for (int i = 0; i < buttons.Length; i++)
            {
                var hasData = i < _presets.Length && _presets[i].HasChanges;
                buttons[i].Background = hasData
                    ? (Brush?)FindResource("AccentFillColorDefaultBrush")
                    : null;
                buttons[i].Foreground = hasData
                    ? (Brush?)FindResource("TextOnAccentFillColorPrimaryBrush")
                    : null;

                if (i == _activePreset)
                {
                    buttons[i].BorderThickness = new Thickness(2);
                    buttons[i].BorderBrush = (Brush?)FindResource("AccentFillColorDefaultBrush");
                }
                else
                {
                    buttons[i].BorderThickness = new Thickness(0);
                }
            }
        }
    }
}
