using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Serilog;
using Uniwill_Control_Utility.Services;
using Wpf.Ui.Abstractions.Controls;

namespace Uniwill_Control_Utility.Views.Windows.Tray
{
    /// <summary>
    /// The mini-tray's default page: the quick-settings button grid. Hosted by the
    /// tray window's NavigationView; the Display button navigates to TrayDisplayPage.
    /// The tray window owns the NavigationView, the quick-control service and the
    /// shared apply/snapshot orchestration.
    /// </summary>
    public partial class TrayQuickSettingsPage : Page, INavigationAware
    {
        // The NavigationView instantiates this page by reflection, so the tray
        // window reaches the live instance through this static.
        public static TrayQuickSettingsPage? Instance;

        private TrayQuickControlService _quickControl;

        /// <inheritdoc />
        public async Task OnNavigatedToAsync() { }

        /// <inheritdoc />
        public async Task OnNavigatedFromAsync() { }

        public TrayQuickSettingsPage()
        {
            InitializeComponent();
            Instance = this;
            _quickControl = TrayWidgetWindow.Instance?.QuickControl;
        }

        /// <summary>
        /// Applies the quick-settings half of a snapshot: slot visibility, toggle
        /// states and the reflowed button grid.
        /// </summary>
        public void ApplySnapshot(TrayQuickControlSnapshot snapshot)
        {
            var policy = snapshot.Policy;

            ApplyToggle(SlotAdaptiveMode, BtnAdaptiveMode, policy.ShowAdaptiveMode, snapshot.AdaptiveModeRunning, false);
            ApplyToggle(SlotHydroSmartMode, BtnHydroSmartMode, policy.ShowHydroSmartMode, snapshot.HydroSmartMode, policy.DisableHydroSmartMode);
            ApplyToggle(SlotHydroRgb, BtnHydroRgb, policy.ShowHydroRgb, snapshot.HydroRgbOn, policy.DisableHydroRgb);
            ApplyToggle(SlotKeyboardBacklight, BtnKeyboardBacklight, policy.ShowKeyboardBacklight, snapshot.KeyboardRgbOn, policy.DisableKeyboardBacklight);
            ApplyToggle(SlotLightBar, BtnLightBar, policy.ShowLightBar, snapshot.LightBarOn, policy.DisableLightBar);
            ApplyToggle(SlotFanSmartMode, BtnFanSmartMode, policy.ShowFanSmartMode, snapshot.FanSmartMode, policy.DisableFanSmartMode);
            ApplyToggle(SlotOverlay, BtnOverlay, policy.ShowOverlay, snapshot.OverlayOn, false);
            ApplyToggle(SlotFlydigiRgb, BtnFlydigiRgb, policy.ShowFlydigiRgb, snapshot.FlydigiRgbOn, policy.DisableFlydigiRgb);

            // Display navigation slot — always visible.
            SlotDisplay.Visibility = Visibility.Visible;

            // Recalculate UniformGrid rows based on visible children so collapsed
            // items don't leave gaps and buttons flow left-to-right, top-to-bottom.
            UpdateButtonGridRows();
        }

        private static void ApplyToggle(
            System.Windows.Controls.Panel panel,
            System.Windows.Controls.Primitives.ToggleButton button,
            bool visible,
            bool isChecked,
            bool isDisabled)
        {
            panel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            button.IsChecked = isChecked;
            button.IsEnabled = !isDisabled;
        }

        private void UpdateButtonGridRows()
        {
            const int Columns = 3;
            const int RowGap = 24;

            // Collect visible children in order.
            var visible = new List<FrameworkElement>();
            foreach (FrameworkElement child in ButtonsGrid.Children)
            {
                if (child.Visibility == Visibility.Visible)
                    visible.Add(child);
            }

            int visibleCount = visible.Count;
            int rowCount = visibleCount > 0 ? (visibleCount + Columns - 1) / Columns : 0;

            // Build row definitions: one Auto row per content row, plus a spacer row between each.
            ButtonsGrid.RowDefinitions.Clear();
            for (int r = 0; r < rowCount; r++)
            {
                ButtonsGrid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = new GridLength(0, GridUnitType.Auto) });
                if (r < rowCount - 1)
                {
                    ButtonsGrid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = new GridLength(RowGap) });
                }
            }

            // Assign each visible slot to the correct Grid.Row and column, clear margins.
            for (int i = 0; i < visibleCount; i++)
            {
                int col = i % Columns;
                int contentRow = (i / Columns) * 2; // 0, 2, 4, ... (spacer rows are 1, 3, ...)
                System.Windows.Controls.Grid.SetRow(visible[i], contentRow);
                System.Windows.Controls.Grid.SetColumn(visible[i], col);
                visible[i].Margin = new System.Windows.Thickness(0);
            }

            // Collapsed children are left untouched — they render nothing regardless of
            // their stale row index (implicit tracks absorb out-of-range rows), and this
            // WPF rejects -1 for Grid.Row ("'-1' is not a valid value for property 'Row'"),
            // which threw here and skipped the Display page's snapshot half — leaving its
            // Collapsed-by-default sections un-applied (an empty tray Display page) on
            // laptops with any collapsed quick-settings slot.

            // The card (and the tray window) are sized for the full three-row grid;
            // shrink both by exactly the space the hidden rows would occupy — each
            // row is its content height (button + label margin + label) plus the
            // spacer row that separated it. The slot is measured explicitly because
            // on the first show the freshly instantiated page has not been through
            // a layout pass yet, so its DesiredSize is still zero.
            int designRows = (ButtonsGrid.Children.Count + Columns - 1) / Columns;
            if (visibleCount > 0)
            {
                visible[0].Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                double freedHeight = (designRows - rowCount) * (visible[0].DesiredSize.Height + RowGap);
                TrayWidgetWindow.Instance?.ResizeQuickSettingsCard(freedHeight);
            }
        }

        #region Toggle Handlers

        private async void BtnAdaptiveMode_Click(object sender, RoutedEventArgs e)
        {
            if (IsBusy || sender is not System.Windows.Controls.Primitives.ToggleButton btn)
                return;

            bool enabled = btn.IsChecked == true;
            await ApplyAsync(async () =>
            {
                await _quickControl.SetAdaptiveModeAsync(enabled);
            });
        }

        private async void BtnHydroSmartMode_Click(object sender, RoutedEventArgs e)
        {
            if (IsBusy || sender is not System.Windows.Controls.Primitives.ToggleButton btn)
                return;

            bool enabled = btn.IsChecked == true;
            await ApplyAsync(async () =>
            {
                await _quickControl.SetHydroSmartModeAsync(enabled);
            });
        }

        private async void BtnHydroRgb_Click(object sender, RoutedEventArgs e)
        {
            if (IsBusy || sender is not System.Windows.Controls.Primitives.ToggleButton btn)
                return;

            bool enabled = btn.IsChecked == true;
            await ApplyAsync(async () =>
            {
                await _quickControl.SetHydroRgbAsync(enabled);
            });
        }

        private async void BtnKeyboardBacklight_Click(object sender, RoutedEventArgs e)
        {
            if (IsBusy || sender is not System.Windows.Controls.Primitives.ToggleButton btn)
                return;

            bool enabled = btn.IsChecked == true;
            await ApplyAsync(async () =>
            {
                _quickControl.SetKeyboardBacklight(enabled);
                await Task.CompletedTask;
            });
        }

        private async void BtnLightBar_Click(object sender, RoutedEventArgs e)
        {
            if (IsBusy || sender is not System.Windows.Controls.Primitives.ToggleButton btn)
                return;

            bool enabled = btn.IsChecked == true;
            await ApplyAsync(async () =>
            {
                _quickControl.SetLightBar(enabled);
                await Task.CompletedTask;
            });
        }

        private async void BtnFanSmartMode_Click(object sender, RoutedEventArgs e)
        {
            if (IsBusy || sender is not System.Windows.Controls.Primitives.ToggleButton btn)
                return;

            bool enabled = btn.IsChecked == true;
            await ApplyAsync(async () =>
            {
                _quickControl.SetFanSmartMode(enabled);
                await Task.CompletedTask;
            });
        }

        private async void BtnOverlay_Click(object sender, RoutedEventArgs e)
        {
            if (IsBusy || sender is not System.Windows.Controls.Primitives.ToggleButton btn)
                return;

            bool enabled = btn.IsChecked == true;
            await ApplyAsync(async () =>
            {
                _quickControl.SetOverlay(enabled);
                await Task.CompletedTask;
            });
        }

        private async void BtnFlydigiRgb_Click(object sender, RoutedEventArgs e)
        {
            if (IsBusy || sender is not System.Windows.Controls.Primitives.ToggleButton btn)
                return;

            bool enabled = btn.IsChecked == true;
            await ApplyAsync(async () =>
            {
                await _quickControl.SetFlydigiRgbAsync(enabled);
            });
        }

        #endregion

        /// <summary>
        /// Navigates the tray's NavigationView to the Display page. The navigated
        /// page is a fresh instance, so its sections are re-synced from live state.
        /// </summary>
        private void BtnDisplay_Click(object sender, RoutedEventArgs e)
        {
            if (IsBusy || sender is not System.Windows.Controls.Button btn || btn != BtnDisplay)
                return;

            _ = TrayWidgetWindow.Instance?.NavigateTo(typeof(TrayDisplayPage));
            TrayWidgetWindow.Instance?.RefreshSnapshot();
        }

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
