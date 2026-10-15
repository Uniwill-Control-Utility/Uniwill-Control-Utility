using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Serilog;
using Wpf.Ui.Controls;
using Wpf.Ui.Abstractions.Controls;
using Uniwill_Control_Utility.Services;
using Uniwill_Control_Utility.Scripts.Misc;

namespace Uniwill_Control_Utility.Views.Windows.Tray
{
    /// <summary>
    /// The mini-tray's Display page: refresh rate, color calibration, Nvidia Optimus
    /// and MiniLED local dimming sections. Hosted by the tray window's NavigationView
    /// when the quick-settings page's Display button navigates here; the back button
    /// navigates to the quick-settings page. The header row stays fixed while the
    /// sections scroll inside DisplayScroll.
    /// </summary>
    public partial class TrayDisplayPage : Page, INavigationAware
    {
        // The NavigationView instantiates this page by reflection, so the tray
        // window reaches the live instance through this static.
        public static TrayDisplayPage? Instance;

        private TrayQuickControlService _quickControl;

        // Fences programmatic IsChecked writes so the Checked/Unchecked handlers
        // do not re-apply the setting.
        private bool _syncingToggle;

        /// <inheritdoc />
        public async Task OnNavigatedToAsync() { }

        /// <inheritdoc />
        public async Task OnNavigatedFromAsync() { }

        public TrayDisplayPage()
        {
            InitializeComponent();
            Instance = this;
            _quickControl = TrayWidgetWindow.Instance?.QuickControl;
        }

        /// <summary>
        /// Applies the display half of a snapshot: section visibility, active
        /// button styles and the reflowed rate/profile grids.
        /// </summary>
        public void ApplySnapshot(TrayQuickControlSnapshot snapshot)
        {
            var policy = snapshot.Policy;

            OptimusSection.Visibility = snapshot.GpuModeHybrid ? Visibility.Visible : Visibility.Collapsed;
            MiniLedSection.Visibility = policy.ShowLocalDimming ? Visibility.Visible : Visibility.Collapsed;

            // Sync the local dimming switch state; the flag fences the Checked/Unchecked
            // event handlers so this programmatic write does not re-apply the setting.
            _syncingToggle = true;
            TrayTsLocalDimming.IsChecked = snapshot.LocalDimmingOn;
            _syncingToggle = false;

            UpdateTrayPowerSavingButtons(snapshot.GpuPowerSavingMode);

            // Fenced: a refresh-rate sync failure must never skip the placement above.
            try
            {
                RefreshRateSection.Visibility = policy.ShowRefreshRate ? Visibility.Visible : Visibility.Collapsed;
                UpdateTrayRefreshRateButtons(snapshot.RefreshRateHz, snapshot.GpuModeDgpuOnly);

                ColorCalSection.Visibility = snapshot.ColorCalAvailable ? Visibility.Visible : Visibility.Collapsed;
                UpdateTrayColorCalButtons(snapshot.ColorProfileFile);
            }
            catch (Exception ex)
            {
                Log.Warning("[Tray] Refresh rate sync failed: {Message}", ex.Message);
            }
        }

        /// <summary>
        /// Swaps the active/inactive styles of the tray's iGPU/Auto/dGPU power saving
        /// buttons, mirroring the Display page's UpdatePowerSavingButtons.
        /// </summary>
        private void UpdateTrayPowerSavingButtons(int mode)
        {
            Style? inactiveStyle = (Style)Resources["TrayModeButtonStyle"];
            Style? activeStyle = (Style)Resources["TrayModeButtonActiveStyle"];

            BtnTrayPowerSavingOn.Style = mode == 0 ? activeStyle : inactiveStyle;
            BtnTrayPowerSavingAuto.Style = mode == 2 ? activeStyle : inactiveStyle;
            BtnTrayPowerSavingOff.Style = mode == 1 ? activeStyle : inactiveStyle;
        }

        /// <summary>
        /// Shows exactly the rates the display in use offers (collapsed otherwise),
        /// styles the active one, and reflows the visible buttons into the 5-column
        /// grid — mirroring the Display page's UpdateRefreshRateButtons.
        /// </summary>
        private void UpdateTrayRefreshRateButtons(int currentHz, bool dGpuOnly)
        {
            Style? inactiveStyle = (Style)Resources["TrayModeButtonStyle"];
            Style? activeStyle = (Style)Resources["TrayModeButtonActiveStyle"];

            System.Windows.Controls.StackPanel[] slots =
                new System.Windows.Controls.StackPanel[]
                {
                    SlotTrayRefreshRate60, SlotTrayRefreshRate75, SlotTrayRefreshRate90,
                    SlotTrayRefreshRate100, SlotTrayRefreshRate120, SlotTrayRefreshRate144,
                    SlotTrayRefreshRate165, SlotTrayRefreshRate240, SlotTrayRefreshRate300
                };
            System.Windows.Controls.Button[] buttons =
                new System.Windows.Controls.Button[]
                {
                    BtnTrayRefreshRate60, BtnTrayRefreshRate75, BtnTrayRefreshRate90,
                    BtnTrayRefreshRate100, BtnTrayRefreshRate120, BtnTrayRefreshRate144,
                    BtnTrayRefreshRate165, BtnTrayRefreshRate240, BtnTrayRefreshRate300
                };
            int[] hzValues = new int[] { 60, 75, 90, 100, 120, 144, 165, 240, 300 };

            for (int i = 0; i < slots.Length; i++)
            {
                buttons[i].Style = currentHz == hzValues[i] ? activeStyle : inactiveStyle;
                slots[i].Visibility = TrayRefreshRateAvailable(hzValues[i], dGpuOnly) ? Visibility.Visible : Visibility.Collapsed;
            }

            // Reflow visible slots left-to-right, wrapping at 5 per row (the Grid's
            // children are the StackPanel wrappers, not the buttons). Collapsed slots
            // are left untouched — they render nothing, and -1 indices are rejected
            // by this WPF's grid placement validation.
            const int Columns = 5;
            int slot = 0;
            foreach (System.Windows.Controls.StackPanel panel in slots)
            {
                if (panel.Visibility != Visibility.Visible)
                    continue;

                System.Windows.Controls.Grid.SetRow(panel, slot / Columns);
                System.Windows.Controls.Grid.SetColumn(panel, slot % Columns);
                slot++;
            }
        }

        private static bool TrayRefreshRateAvailable(int hz, bool dGpuOnly)
        {
            try
            {
                var rates = Uniwill_Control_Utility.Scripts.Misc.Display.GetOfferedRefreshRates(
                    Uniwill_Control_Utility.Scripts.Misc.Display.CurrentDisplayName(), dGpuOnly);
                return rates.Contains(hz);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Styles the active ICC profile and shows exactly the profiles Windows offers
        /// (spool-folder presence), reflowing visible slots into the 4-column grid.
        /// </summary>
        private void UpdateTrayColorCalButtons(string? currentFile)
        {
            Style? inactiveStyle = (Style)Resources["TrayModeButtonStyle"];
            Style? activeStyle = (Style)Resources["TrayModeButtonActiveStyle"];

            System.Windows.Controls.StackPanel[] slots =
                new System.Windows.Controls.StackPanel[] { SlotTrayColorDefault, SlotTrayColorSrgb, SlotTrayColorP3, SlotTrayColorAdobe };
            System.Windows.Controls.Button[] buttons =
                new System.Windows.Controls.Button[] { BtnTrayColorDefault, BtnTrayColorSrgb, BtnTrayColorP3, BtnTrayColorAdobe };
            string[] files = new string[] { "Default.icm", "sRGB.icm", "DCIP3.icm", "AdobeRGB.icm" };

            for (int i = 0; i < slots.Length; i++)
            {
                buttons[i].Style = currentFile == files[i] ? activeStyle : inactiveStyle;
                slots[i].Visibility = TrayColorProfileAvailable(files[i]) ? Visibility.Visible : Visibility.Collapsed;
            }

            const int Columns = 4;
            int slot = 0;
            foreach (System.Windows.Controls.StackPanel panel in slots)
            {
                if (panel.Visibility != Visibility.Visible)
                    continue;

                System.Windows.Controls.Grid.SetRow(panel, slot / Columns);
                System.Windows.Controls.Grid.SetColumn(panel, slot % Columns);
                slot++;
            }
        }

        /// <summary>
        /// Optimistically paints the clicked profile button active (others inactive)
        /// at click time, so the visual feedback is immediate rather than waiting for
        /// the slow apply's snapshot re-fetch. The re-fetch re-styles from live state
        /// and restores the previous selection if the apply failed.
        /// </summary>
        private void SelectColorCalButton(System.Windows.Controls.Button clicked)
        {
            Style? activeStyle = (Style)Resources["TrayModeButtonActiveStyle"];
            Style? inactiveStyle = (Style)Resources["TrayModeButtonStyle"];

            BtnTrayColorDefault.Style = BtnTrayColorDefault == clicked ? activeStyle : inactiveStyle;
            BtnTrayColorSrgb.Style = BtnTrayColorSrgb == clicked ? activeStyle : inactiveStyle;
            BtnTrayColorP3.Style = BtnTrayColorP3 == clicked ? activeStyle : inactiveStyle;
            BtnTrayColorAdobe.Style = BtnTrayColorAdobe == clicked ? activeStyle : inactiveStyle;
        }

        private static bool TrayColorProfileAvailable(string file)
        {
            if (file == Uniwill_Control_Utility.Scripts.Misc.Display.DefaultColorProfileFile)
                return true;

            try
            {
                return System.IO.File.Exists(
                    Uniwill_Control_Utility.Scripts.Misc.Display.IcmProfilesFolder + @"\" + file);
            }
            catch
            {
                return false;
            }
        }

        #region Action Handlers

        private void BtnTrayBack_Click(object sender, RoutedEventArgs e)
        {
            if (IsBusy || sender is not System.Windows.Controls.Button btn || btn != BtnTrayBack)
                return;

            _ = TrayWidgetWindow.Instance?.NavigateTo(typeof(TrayQuickSettingsPage));
            TrayWidgetWindow.Instance?.RefreshSnapshot();
        }

        private async void BtnTrayPowerSaving_Click(object sender, RoutedEventArgs e)
        {
            if (IsBusy || sender is not System.Windows.Controls.Button btn)
                return;
            if (btn.Tag is not string tag || !int.TryParse(tag, out int mode))
                return;

            await ApplyAsync(async () =>
            {
                // On failure the persisted mode is untouched, so the snapshot re-fetch
                // inside ApplyAsync restores the previous active style.
                _ = await _quickControl.SetGpuPowerSavingAsync(mode);
            });
        }

        private async void BtnTrayRefreshRate_Click(object sender, RoutedEventArgs e)
        {
            if (IsBusy || sender is not System.Windows.Controls.Button btn)
                return;
            if (btn.Tag is not string tag || !int.TryParse(tag, out int hz))
                return;

            await ApplyAsync(async () =>
            {
                // On failure the live rate is unchanged, so the snapshot re-fetch
                // inside ApplyAsync restores the previous active style.
                _ = await _quickControl.SetRefreshRateAsync(hz);
            });
        }

        private async void BtnTrayColorCal_Click(object sender, RoutedEventArgs e)
        {
            if (IsBusy || sender is not System.Windows.Controls.Button btn)
                return;
            if (btn.Tag is not string file)
                return;

            // Paint the selection immediately — the apply is slow (WCS install +
            // reg write + readback), so waiting for the snapshot re-fetch to
            // restyle would lag the click. The re-fetch corrects on failure.
            SelectColorCalButton(btn);

            await ApplyAsync(async () =>
            {
                // On failure the live association is unchanged, so the snapshot re-fetch
                // inside ApplyAsync restores the previous active style.
                _ = await _quickControl.SetColorProfileAsync(file);
            });
        }

        private async void TrayTsLocalDimming_Toggled(object sender, RoutedEventArgs e)
        {
            if (IsBusy || _syncingToggle || sender is not Wpf.Ui.Controls.ToggleSwitch sw)
                return;

            bool enabled = sw.IsChecked == true;
            await ApplyAsync(async () =>
            {
                bool success = await _quickControl.SetLocalDimmingAsync(enabled);
                if (!success)
                {
                    // Revert the toggle on failure. The revert re-fires this handler,
                    // so fence it with _syncingToggle.
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        _syncingToggle = true;
                        sw.IsChecked = !enabled;
                        _syncingToggle = false;
                    });
                }
            });
        }

        #endregion

        private static bool IsBusy => TrayWidgetWindow.Instance?.IsBusy ?? true;

        private static Task ApplyAsync(Func<Task> action)
        {
            return TrayWidgetWindow.Instance?.ApplyAsync(action) ?? Task.CompletedTask;
        }

        #region Context Menu

        private void NavigateContextMenu_Opened(object sender, RoutedEventArgs e)
        {
            // Store the source button's Tag in the context menu so the click handler
            // knows which page to navigate to.
            if (sender is System.Windows.Controls.ContextMenu menu)
            {
                if (menu.PlacementTarget is System.Windows.Controls.Primitives.ToggleButton btn)
                    menu.Tag = btn.Tag;
                else if (menu.PlacementTarget is System.Windows.Controls.Button btn2)
                    menu.Tag = btn2.Tag;
            }
        }

        private void GoToUcuMenuItem_Click(object sender, RoutedEventArgs e)
        {
            TrayWidgetWindow.Instance?.GoToUcuMenuItem_Click(sender, e);
        }

        #endregion
    }
}
