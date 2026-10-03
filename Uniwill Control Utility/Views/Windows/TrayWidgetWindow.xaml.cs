using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Uniwill_Control_Utility.Services;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using Screen = System.Windows.Forms.Screen;

namespace Uniwill_Control_Utility.Views.Windows
{
    public partial class TrayWidgetWindow : ITrayWidget
    {
        private readonly TrayQuickControlService _quickControl;
        private readonly DispatcherTimer _refreshTimer;
        private bool _loading;
        private bool _applying;
        private bool _showing;
        private System.Drawing.Point _placementAnchor;
        private string? _placementScreenDeviceName;

        public TrayWidgetWindow(TrayQuickControlService quickControl)
        {
            _quickControl = quickControl;
            InitializeComponent();
            SystemThemeWatcher.Watch(this, Wpf.Ui.Controls.WindowBackdropType.Mica, true);

            _refreshTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(2)
            };
            _refreshTimer.Tick += (s, e) => UpdateMetrics();
        }

        public async Task ToggleAsync()
        {
            if (IsVisible)
            {
                HideWidget();
                return;
            }

            CapturePlacementAnchor();
            _loading = true;
            _showing = true;

            Show();
            UpdateLayout();
            PositionNearTray();
            Activate();
            _refreshTimer.Start();

            try
            {
                TrayQuickControlSnapshot snapshot = await Task.Run(_quickControl.GetSnapshot);
                ApplySnapshot(snapshot);
                UpdateMetrics();
                UpdateLayout();
                PositionNearTray();
            }
            catch
            {
                // Silently fail — no status text to show
            }
            finally
            {
                _loading = false;
            }

            // Small delay before enabling deactivate-to-hide, so the window
            // doesn't immediately close on the initial activation/deactivation flicker.
            await Task.Delay(200);
            _showing = false;
        }

        private void ApplySnapshot(TrayQuickControlSnapshot snapshot)
        {
            var policy = snapshot.Policy;

            ApplyToggle(SlotAdaptiveMode, BtnAdaptiveMode, policy.ShowAdaptiveMode, snapshot.AdaptiveModeRunning, false);
            ApplyToggle(SlotHydroSmartMode, BtnHydroSmartMode, policy.ShowHydroSmartMode, snapshot.HydroSmartMode, policy.DisableHydroSmartMode);
            ApplyToggle(SlotHydroRgb, BtnHydroRgb, policy.ShowHydroRgb, snapshot.HydroRgbOn, policy.DisableHydroRgb);
            ApplyToggle(SlotKeyboardBacklight, BtnKeyboardBacklight, policy.ShowKeyboardBacklight, snapshot.KeyboardRgbOn, policy.DisableKeyboardBacklight);
            ApplyToggle(SlotLightBar, BtnLightBar, policy.ShowLightBar, snapshot.LightBarOn, policy.DisableLightBar);
            ApplyToggle(SlotFanSmartMode, BtnFanSmartMode, policy.ShowFanSmartMode, snapshot.FanSmartMode, policy.DisableFanSmartMode);
            ApplyToggle(SlotLocalDimming, BtnLocalDimming, policy.ShowLocalDimming, snapshot.LocalDimmingOn, false);
            ApplyToggle(SlotOverlay, BtnOverlay, policy.ShowOverlay, snapshot.OverlayOn, false);
            ApplyToggle(SlotFlydigiRgb, BtnFlydigiRgb, policy.ShowFlydigiRgb, snapshot.FlydigiRgbOn, policy.DisableFlydigiRgb);

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

            // Hide any collapsed children by clearing their row (they stay in Children but invisible).
            foreach (FrameworkElement child in ButtonsGrid.Children)
            {
                if (child.Visibility != Visibility.Visible)
                {
                    System.Windows.Controls.Grid.SetRow(child, -1);
                }
            }
        }

        #region Toggle Handlers

        private async void BtnAdaptiveMode_Click(object sender, RoutedEventArgs e)
        {
            if (_loading || _applying || sender is not System.Windows.Controls.Primitives.ToggleButton btn)
                return;

            bool enabled = btn.IsChecked == true;
            await ApplyAsync(async () =>
            {
                await _quickControl.SetAdaptiveModeAsync(enabled);
            });
        }

        private async void BtnHydroSmartMode_Click(object sender, RoutedEventArgs e)
        {
            if (_loading || _applying || sender is not System.Windows.Controls.Primitives.ToggleButton btn)
                return;

            bool enabled = btn.IsChecked == true;
            await ApplyAsync(async () =>
            {
                await _quickControl.SetHydroSmartModeAsync(enabled);
            });
        }

        private async void BtnHydroRgb_Click(object sender, RoutedEventArgs e)
        {
            if (_loading || _applying || sender is not System.Windows.Controls.Primitives.ToggleButton btn)
                return;

            bool enabled = btn.IsChecked == true;
            await ApplyAsync(async () =>
            {
                await _quickControl.SetHydroRgbAsync(enabled);
            });
        }

        private async void BtnKeyboardBacklight_Click(object sender, RoutedEventArgs e)
        {
            if (_loading || _applying || sender is not System.Windows.Controls.Primitives.ToggleButton btn)
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
            if (_loading || _applying || sender is not System.Windows.Controls.Primitives.ToggleButton btn)
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
            if (_loading || _applying || sender is not System.Windows.Controls.Primitives.ToggleButton btn)
                return;

            bool enabled = btn.IsChecked == true;
            await ApplyAsync(async () =>
            {
                _quickControl.SetFanSmartMode(enabled);
                await Task.CompletedTask;
            });
        }

        private async void BtnLocalDimming_Click(object sender, RoutedEventArgs e)
        {
            if (_loading || _applying || sender is not System.Windows.Controls.Primitives.ToggleButton btn)
                return;

            bool enabled = btn.IsChecked == true;
            await ApplyAsync(async () =>
            {
                bool success = await _quickControl.SetLocalDimmingAsync(enabled);
                if (!success)
                {
                    // Revert toggle on failure
                    Application.Current.Dispatcher.Invoke(() => btn.IsChecked = !enabled);
                }
            });
        }

        private async void BtnOverlay_Click(object sender, RoutedEventArgs e)
        {
            if (_loading || _applying || sender is not System.Windows.Controls.Primitives.ToggleButton btn)
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
            if (_loading || _applying || sender is not System.Windows.Controls.Primitives.ToggleButton btn)
                return;

            bool enabled = btn.IsChecked == true;
            await ApplyAsync(async () =>
            {
                await _quickControl.SetFlydigiRgbAsync(enabled);
            });
        }

        #endregion

        private async Task ApplyAsync(Func<Task> action)
        {
            _applying = true;
            try
            {
                await action();

                // Re-fetch snapshot to update button enabled states after any change.
                try
                {
                    var snapshot = await Task.Run(_quickControl.GetSnapshot);
                    ApplySnapshot(snapshot);
                }
                catch
                {
                    // Ignore — snapshot refresh is best-effort
                }
            }
            catch
            {
                // Silently fail
            }
            finally
            {
                _applying = false;
            }
        }

        private void UpdateMetrics()
        {
            try
            {
                var snapshot = _quickControl.GetTelemetrySnapshot();
                ApplyMetrics(snapshot);
            }
            catch
            {
                // Silently fail — metrics are best-effort
            }
        }

        private void ApplyMetrics(TrayQuickTelemetrySnapshot s)
        {
            // CPU hero values
            CpuTempText.Text = $"{s.CpuTemperature}°C";
            CpuUsageText.Text = $"{s.CpuUsage}%";

            // CPU segmented bars
            CpuPowerBar.Value = s.CpuPowerWatts;
            CpuPowerText.Text = $"{s.CpuPowerWatts} W";
            CpuClockBar.Value = s.CpuClockMhz;
            CpuClockText.Text = $"{s.CpuClockMhz / 1000.0:F1} GHz";
            CpuFanBar.Value = s.CpuFanPercent;
            CpuFanText.Text = $"{s.CpuFanPercent}%";

            // GPU hero values
            GpuTempText.Text = $"{s.GpuTemperature}°C";
            GpuUsageText.Text = $"{(int)s.GpuUsage}%";

            // GPU segmented bars
            GpuPowerBar.Value = s.GpuPowerWatts;
            GpuPowerText.Text = $"{s.GpuPowerWatts} W";
            GpuClockBar.Value = s.GpuClockMhz;
            GpuClockText.Text = $"{s.GpuClockMhz / 1000.0:F1} GHz";
            GpuFanBar.Value = s.GpuFanPercent;
            GpuFanText.Text = $"{s.GpuFanPercent}%";
        }

        private void PositionNearTray()
        {
            IntPtr handle = new WindowInteropHelper(this).Handle;
            if (handle == IntPtr.Zero || !GetWindowRect(handle, out NativeRect windowRect))
                return;

            var windowSize = new System.Drawing.Size(
                windowRect.Right - windowRect.Left,
                windowRect.Bottom - windowRect.Top);

            Screen screen = Screen.AllScreens.FirstOrDefault(candidate =>
                string.Equals(
                    candidate.DeviceName,
                    _placementScreenDeviceName,
                    StringComparison.OrdinalIgnoreCase)) ?? Screen.FromHandle(handle);

            TrayWidgetPlacementResult placement = TrayWidgetPlacement.Calculate(
                screen.Bounds,
                screen.WorkingArea,
                _placementAnchor,
                windowSize,
                gap: 10,
                taskbarBounds: FindTaskbarBounds(screen.Bounds));

            SetWindowPos(
                handle,
                IntPtr.Zero,
                placement.Location.X,
                placement.Location.Y,
                0,
                0,
                SetWindowPosFlags.NoSize |
                SetWindowPosFlags.NoZOrder |
                SetWindowPosFlags.NoActivate);
        }

        private void CapturePlacementAnchor()
        {
            _placementAnchor = System.Windows.Forms.Cursor.Position;
            _placementScreenDeviceName = Screen.FromPoint(_placementAnchor).DeviceName;
        }

        private static System.Drawing.Rectangle? FindTaskbarBounds(
            System.Drawing.Rectangle monitorBounds)
        {
            foreach (TrayEdge edge in Enum.GetValues<TrayEdge>())
            {
                var appBarData = new AppBarData
                {
                    Size = (uint)Marshal.SizeOf<AppBarData>(),
                    Edge = (uint)edge,
                    Bounds = new NativeRect
                    {
                        Left = monitorBounds.Left,
                        Top = monitorBounds.Top,
                        Right = monitorBounds.Right,
                        Bottom = monitorBounds.Bottom
                    }
                };
                IntPtr autoHideTaskbar = SHAppBarMessage(GetAutoHideBarEx, ref appBarData);
                if (TryGetTaskbarBounds(
                        autoHideTaskbar,
                        monitorBounds,
                        edge,
                        requireIntersection: false,
                        out System.Drawing.Rectangle autoHideBounds))
                {
                    return autoHideBounds;
                }
            }

            IntPtr primaryTaskbar = FindWindow("Shell_TrayWnd", null);
            if (TryGetTaskbarBounds(
                    primaryTaskbar,
                    monitorBounds,
                    edge: null,
                    requireIntersection: true,
                    out System.Drawing.Rectangle bounds))
                return bounds;

            IntPtr secondaryTaskbar = IntPtr.Zero;
            while ((secondaryTaskbar = FindWindowEx(
                       IntPtr.Zero,
                       secondaryTaskbar,
                       "Shell_SecondaryTrayWnd",
                       null)) != IntPtr.Zero)
            {
                if (TryGetTaskbarBounds(
                        secondaryTaskbar,
                        monitorBounds,
                        edge: null,
                        requireIntersection: true,
                        out bounds))
                    return bounds;
            }

            return null;
        }

        private static bool TryGetTaskbarBounds(
            IntPtr taskbar,
            System.Drawing.Rectangle monitorBounds,
            TrayEdge? edge,
            bool requireIntersection,
            out System.Drawing.Rectangle bounds)
        {
            bounds = default;
            if (taskbar == IntPtr.Zero || !GetWindowRect(taskbar, out NativeRect rect))
                return false;

            bounds = System.Drawing.Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
            System.Drawing.Rectangle intersection = System.Drawing.Rectangle.Intersect(bounds, monitorBounds);
            if (requireIntersection && (intersection.Width <= 0 || intersection.Height <= 0))
                return false;

            bounds = edge is TrayEdge knownEdge
                ? TrayWidgetPlacement.NormalizeTaskbarBounds(monitorBounds, bounds, knownEdge)
                : TrayWidgetPlacement.NormalizeTaskbarBounds(monitorBounds, bounds);
            return true;
        }

        private void Window_DpiChanged(object sender, DpiChangedEventArgs e) =>
            Dispatcher.BeginInvoke(PositionNearTray, DispatcherPriority.Loaded);

        private void HideWidget()
        {
            _refreshTimer.Stop();
            Hide();
        }

        private void Window_Deactivated(object sender, EventArgs e)
        {
            if (_showing || _applying)
                return;

            HideWidget();
        }

        private void Window_Closed(object? sender, EventArgs e) { }

        private void OpenUcuButton_Click(object sender, RoutedEventArgs e)
        {
            HideWidget();
            MainWindow? mainWindow = MainWindow.Instance;
            if (mainWindow is null)
                return;

            mainWindow.Show();
            mainWindow.WindowState = WindowState.Normal;
            mainWindow.Activate();
        }

        private void NavigateContextMenu_Opened(object sender, RoutedEventArgs e)
        {
            // Store the source button's Tag in the context menu so the click handler
            // knows which page to navigate to.
            if (sender is System.Windows.Controls.ContextMenu menu && menu.PlacementTarget is System.Windows.Controls.Primitives.ToggleButton btn)
                menu.Tag = btn.Tag;
        }

        private void GoToUcuMenuItem_Click(object sender, RoutedEventArgs e)
        {
            HideWidget();

            if (sender is not System.Windows.Controls.MenuItem item || item.Parent is not System.Windows.Controls.ContextMenu menu)
                return;
            if (menu.Tag is not string tagName)
                return;

            MainWindow? mainWindow = MainWindow.Instance;
            if (mainWindow is null)
                return;

            mainWindow.Show();
            mainWindow.WindowState = WindowState.Normal;
            mainWindow.Activate();

            // Navigate to the page matching the button's Tag.
            Type? pageType = tagName switch
            {
                "Adaptive" => typeof(Views.Pages.Adaptive),
                "Watercooler" => typeof(Views.Pages.Watercooler),
                "Keyboard" => typeof(Views.Pages.Keyboard),
                "LightBar" => typeof(Views.Pages.LightBar),
                "FanControl" => typeof(Views.Pages.FanControl),
                "SettingsPage" => typeof(Views.Pages.SettingsPage),
                "OverlaySettingsPage" => typeof(Views.Pages.OverlaySettingsPage),
                "FlydigiCooler" => typeof(Views.Pages.FlydigiCooler),
                _ => null
            };

            if (pageType is not null)
                mainWindow.Navigate(pageType);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect
        {
            internal int Left;
            internal int Top;
            internal int Right;
            internal int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct AppBarData
        {
            internal uint Size;
            internal IntPtr Window;
            internal uint CallbackMessage;
            internal uint Edge;
            internal NativeRect Bounds;
            internal IntPtr Parameter;
        }

        private const uint GetAutoHideBarEx = 0x0000000B;

        [Flags]
        private enum SetWindowPosFlags : uint
        {
            NoSize = 0x0001,
            NoZOrder = 0x0004,
            NoActivate = 0x0010
        }

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindow(string className, string? windowName);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindowEx(
            IntPtr parent,
            IntPtr childAfter,
            string className,
            string? windowName);

        [DllImport("shell32.dll")]
        private static extern IntPtr SHAppBarMessage(uint message, ref AppBarData data);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWindowPos(
            IntPtr window,
            IntPtr insertAfter,
            int x,
            int y,
            int width,
            int height,
            SetWindowPosFlags flags);
    }
}
