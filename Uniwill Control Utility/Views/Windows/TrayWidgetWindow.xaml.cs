using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
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
        private bool _loading;
        private bool _applying;
        private string _selectedPreset = "Gaming";
        private bool _isPowerBoostMode;
        private System.Drawing.Point _placementAnchor;
        private string? _placementScreenDeviceName;

        public TrayWidgetWindow(TrayQuickControlService quickControl)
        {
            _quickControl = quickControl;
            InitializeComponent();
            SystemThemeWatcher.Watch(this, Wpf.Ui.Controls.WindowBackdropType.Mica, true);
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
            StatusText.Text = "Loading…";
            Show();
            UpdateLayout();
            PositionNearTray();
            Activate();

            try
            {
                TrayQuickControlSnapshot snapshot = await Task.Run(_quickControl.GetSnapshot);
                ApplySnapshot(snapshot);
                UpdateLayout();
                PositionNearTray();
                StatusText.Text = "Ready";
            }
            catch (Exception ex)
            {
                StatusText.Text = ex.Message;
            }
            finally
            {
                _loading = false;
            }
        }

        private void ApplySnapshot(TrayQuickControlSnapshot snapshot)
        {
            _selectedPreset = snapshot.SelectedPreset;
            _isPowerBoostMode = snapshot.IsPowerBoostMode;

            // Enable/disable preset controls based on policy
            bool presetEnabled = snapshot.Policy.PresetEnabled;
            PresetOfficeButton.IsEnabled = presetEnabled;
            PresetGamingButton.IsEnabled = presetEnabled;
            PresetTurboButton.IsEnabled = presetEnabled;

            // Populate custom preset buttons first (so they exist when UpdatePresetButtonStates runs)
            UpdateCustomPresetButtons(snapshot.CustomPresetNames, presetEnabled);

            // Set checked states after buttons are created
            UpdatePresetButtonStates();

            UpdateHeaderContext();
        }

        private void UpdatePresetButtonStates()
        {
            PresetOfficeButton.IsChecked = _selectedPreset.Equals("Office", StringComparison.OrdinalIgnoreCase);
            PresetGamingButton.IsChecked = _selectedPreset.Equals("Gaming", StringComparison.OrdinalIgnoreCase);
            PresetTurboButton.IsChecked = _selectedPreset.Equals("Turbo", StringComparison.OrdinalIgnoreCase);

            // Update custom preset checked states (buttons are inside UniformGrid rows inside CustomPresetsPanel)
            foreach (UniformGrid row in CustomPresetsPanel.Children)
            {
                foreach (ToggleButton btn in row.Children)
                {
                    string? name = (string?)btn.Tag;
                    btn.IsChecked = !string.IsNullOrEmpty(name) && _selectedPreset.Equals(name, StringComparison.OrdinalIgnoreCase);
                }
            }
        }

        private void UpdateCustomPresetButtons(string[] customNames, bool enabled)
        {
            CustomPresetsPanel.Children.Clear();

            if (customNames.Length == 0)
            {
                CustomPresetsPanel.Visibility = Visibility.Collapsed;
                return;
            }

            CustomPresetsPanel.Visibility = Visibility.Visible;

            // Fit up to 3 buttons per row to avoid clipping (~390px available, buttons are 100px + 24px margin)
            int maxColumns = 3;
            int index = 0;

            while (index < customNames.Length)
            {
                int columns = Math.Min(maxColumns, customNames.Length - index);
                var row = new UniformGrid
                {
                    Columns = columns,
                    Rows = 1,
                    HorizontalAlignment = HorizontalAlignment.Center
                };

                for (int j = 0; j < columns && index < customNames.Length; j++, index++)
                {
                    string name = customNames[index];
                    var button = new ToggleButton
                    {
                        Height = 36,
                        Width = 100,
                        Margin = new Thickness(12),
                        HorizontalContentAlignment = HorizontalAlignment.Center,
                        Style = this.FindResource("PresetButtonStyle") as Style,
                        Tag = name,
                        IsEnabled = enabled
                    };

                    var text = new Wpf.Ui.Controls.TextBlock
                    {
                        VerticalAlignment = VerticalAlignment.Center,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        FontSize = 13,
                        Text = name
                    };

                    button.Content = text;
                    button.Click += PresetButton_Click;
                    row.Children.Add(button);
                }

                CustomPresetsPanel.Children.Add(row);
            }
        }

        private async void PresetButton_Click(object sender, RoutedEventArgs e)
        {
            if (_loading || _applying || sender is not ToggleButton button)
                return;

            string presetName = ExtractButtonText(button) ?? "";

            // Update checked state immediately so the previous button deselects right away
            _selectedPreset = presetName;
            UpdatePresetButtonStates();
            UpdateHeaderContext();

            bool applied = await ApplyAsync(() => _quickControl.ApplyPresetAsync(presetName), $"Preset: {presetName}");
            if (!applied)
            {
                // Re-apply checked state in case snapshot refresh overwrites it
                UpdatePresetButtonStates();
            }
        }

        /// <summary>Extracts the preset name from a button (Tag for custom, TextBlock content for stock).</summary>
        private static string? ExtractButtonText(ToggleButton button)
        {
            // Custom presets store the name in Tag
            if (button.Tag is string tag && !string.IsNullOrEmpty(tag))
                return tag;

            // Stock presets have a ui:TextBlock inside a StackPanel
            if (button.Content is System.Windows.Controls.StackPanel sp)
            {
                for (int i = 0; i < sp.Children.Count; i++)
                {
                    if (sp.Children[i] is Wpf.Ui.Controls.TextBlock childTb)
                        return childTb.Text;
                }
            }

            return null;
        }

        private async Task<bool> ApplyAsync(Func<Task> action, string successStatus)
        {
            _applying = true;
            StatusText.Text = "Applying…";
            try
            {
                await action();
                StatusText.Text = successStatus;
                return true;
            }
            catch (Exception ex)
            {
                StatusText.Text = ex.Message;
                return false;
            }
            finally
            {
                _applying = false;
            }
        }

        private void UpdateHeaderContext()
        {
            HeaderContextText.Text = _selectedPreset;
        }

        private void PositionNearTray()
        {
            IntPtr handle = new WindowInteropHelper(this).Handle;
            if (handle == IntPtr.Zero || !GetWindowRect(handle, out NativeRect windowRect))
                return;

            Screen screen = Screen.AllScreens.FirstOrDefault(candidate =>
                string.Equals(
                    candidate.DeviceName,
                    _placementScreenDeviceName,
                    StringComparison.OrdinalIgnoreCase)) ?? Screen.FromHandle(handle);
            var windowSize = new System.Drawing.Size(
                windowRect.Right - windowRect.Left,
                windowRect.Bottom - windowRect.Top);
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

        private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
                DragMove();
        }

        private void HideWidget() => Hide();

        private void Window_Deactivated(object sender, EventArgs e)
        {
            if (!_applying)
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
