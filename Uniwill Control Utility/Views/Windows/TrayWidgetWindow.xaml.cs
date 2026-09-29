using System;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Uniwill_Control_Utility.Models;
using Uniwill_Control_Utility.Services;
using Screen = System.Windows.Forms.Screen;

namespace Uniwill_Control_Utility.Views.Windows
{
    public partial class TrayWidgetWindow : ITrayWidget
    {
        private readonly TrayQuickControlService _quickControl;
        private readonly DispatcherTimer _telemetryTimer;
        private bool _loading;
        private bool _applying;
        private string[] _presetNames = Array.Empty<string>();
        private string _selectedPreset = "Gaming";
        private int _refreshRate;

        public TrayWidgetWindow(TrayQuickControlService quickControl)
        {
            _quickControl = quickControl;
            InitializeComponent();
            PopulateHydroOptions();

            _telemetryTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1),
                IsEnabled = false
            };
            _telemetryTimer.Tick += TelemetryTimer_Tick;
        }

        public async Task ToggleAsync()
        {
            if (IsVisible)
            {
                HideWidget();
                return;
            }

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
                ApplyTelemetry(_quickControl.GetTelemetrySnapshot());
                UpdateLayout();
                PositionNearTray();
                _telemetryTimer.Start();
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
            _presetNames = snapshot.PresetNames;
            _selectedPreset = snapshot.SelectedPreset;
            _refreshRate = snapshot.RefreshRate;
            PresetButtons.ItemsSource = TrayPresetOptions.Build(_presetNames, _selectedPreset);
            PresetButtons.IsEnabled = snapshot.Policy.PresetEnabled;

            FullFanButton.IsChecked = snapshot.FullFanEnabled;
            FullFanButton.IsEnabled = snapshot.Policy.FullFanEnabled;

            RefreshRateComboBox.ItemsSource = snapshot.RefreshRates.Select(rate => $"{rate} Hz").ToArray();
            RefreshRateComboBox.SelectedItem = $"{snapshot.RefreshRate} Hz";
            DisplayRow.Visibility = snapshot.RefreshRates.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

            HydroRow.Visibility = snapshot.Policy.ShowHydro ? Visibility.Visible : Visibility.Collapsed;
            PumpComboBox.IsEnabled = snapshot.Policy.HydroEnabled;
            HydroFanComboBox.IsEnabled = snapshot.Policy.HydroEnabled;
            PumpComboBox.SelectedValue = snapshot.PumpVoltage;
            HydroFanComboBox.SelectedValue = snapshot.FanSpeed;

            UpdateHeaderContext();
        }

        private void TelemetryTimer_Tick(object? sender, EventArgs e)
        {
            if (!IsVisible)
                return;

            try
            {
                ApplyTelemetry(_quickControl.GetTelemetrySnapshot());
            }
            catch
            {
                // Keep the last cached sample when a transient read is unavailable.
            }
        }

        private void ApplyTelemetry(TrayQuickTelemetrySnapshot telemetry)
        {
            CpuTemperatureText.Text = telemetry.CpuTemperature > 0 ? $"{telemetry.CpuTemperature}°" : "--°";
            CpuFanText.Text = $"{telemetry.CpuFanPercent}% fan";
            CpuPowerText.Text = telemetry.CpuPowerWatts > 0 ? $"{telemetry.CpuPowerWatts} W" : "-- W";
            CpuClockText.Text = FormatClock(telemetry.CpuClockMhz);
            CpuUsageText.Text = $"{telemetry.CpuUsage}%";
            CpuUsageBar.Value = telemetry.CpuUsage;
            SetTemperatureArc(CpuTemperatureArc, telemetry.CpuTemperature);

            GpuTemperatureText.Text = telemetry.GpuTemperature > 0 ? $"{telemetry.GpuTemperature}°" : "--°";
            GpuFanText.Text = $"{telemetry.GpuFanPercent}% fan";
            GpuPowerText.Text = telemetry.GpuPowerWatts > 0 ? $"{telemetry.GpuPowerWatts} W" : "-- W";
            GpuClockText.Text = FormatClock(telemetry.GpuClockMhz);
            GpuUsageText.Text = $"{telemetry.GpuUsage:F0}%";
            GpuUsageBar.Value = telemetry.GpuUsage;
            SetTemperatureArc(GpuTemperatureArc, telemetry.GpuTemperature);

            TrayPowerConsumptionDisplay power = TrayPowerConsumptionDisplay.Create(
                telemetry.CpuPowerWatts,
                telemetry.GpuPowerWatts);
            CpuTdpValueText.Text = power.CpuTdp;
            GpuTgpValueText.Text = power.GpuTgp;
        }

        private static string FormatClock(int clockMhz) =>
            clockMhz > 0 ? $"{clockMhz / 1000.0:F1} GHz" : "-- GHz";

        private static void SetTemperatureArc(ArcSegment arc, int temperature)
        {
            const double center = 30;
            const double radius = 27;
            double progress = Math.Clamp(temperature, 0, 100) / 100.0;
            double angle = progress * 359.9;
            double radians = (angle - 90) * Math.PI / 180.0;

            arc.Point = new Point(
                center + radius * Math.Cos(radians),
                center + radius * Math.Sin(radians));
            arc.IsLargeArc = angle > 180;
        }

        private async void PresetButton_Click(object sender, RoutedEventArgs e)
        {
            if (_loading || _applying || sender is not RadioButton { DataContext: TrayPresetOption option })
                return;

            bool applied = await ApplyAsync(() => _quickControl.ApplyPresetAsync(option.Name), $"Preset: {option.Name}");
            if (applied)
                _selectedPreset = option.Name;

            PresetButtons.ItemsSource = TrayPresetOptions.Build(_presetNames, _selectedPreset);
            UpdateHeaderContext();
        }

        private async void FullFanButton_Click(object sender, RoutedEventArgs e)
        {
            if (_loading || _applying)
                return;

            bool enabled = FullFanButton.IsChecked == true;
            await SetFullFanStateAsync(enabled);
        }

        private async Task SetFullFanStateAsync(bool enabled)
        {
            _applying = true;
            FullFanButton.IsEnabled = false;
            StatusText.Text = enabled ? "Enabling full fan…" : "Restoring fan control…";
            try
            {
                await _quickControl.SetFullFanAsync(enabled);
                FullFanButton.IsChecked = _quickControl.IsFullFanActive;
                StatusText.Text = enabled ? "Full fan enabled" : "Fan control restored";
            }
            catch (Exception ex)
            {
                FullFanButton.IsChecked = _quickControl.IsFullFanActive;
                StatusText.Text = ex.Message;
            }
            finally
            {
                FullFanButton.IsEnabled = true;
                _applying = false;
            }
        }

        private async void RefreshRateComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || _applying || RefreshRateComboBox.SelectedItem is not string text || !TryReadLeadingInt(text, out int hz))
                return;

            bool applied = await ApplyAsync(() => _quickControl.SetRefreshRateAsync(hz), $"Display: {hz} Hz");
            if (applied)
            {
                _refreshRate = hz;
                UpdateHeaderContext();
            }
        }

        private async void PumpComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || _applying || PumpComboBox.SelectedValue is not PumpVoltage voltage)
                return;

            await ApplyAsync(() => _quickControl.SetHydroPumpAsync(voltage), $"Hydro pump: {PumpComboBox.Text}");
        }

        private async void HydroFanComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || _applying || HydroFanComboBox.SelectedValue is not FanSpeed speed)
                return;

            await ApplyAsync(() => _quickControl.SetHydroFanAsync(speed), $"Hydro fan: {HydroFanComboBox.Text}");
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
            HeaderContextText.Text = _refreshRate > 0
                ? $"{_selectedPreset} · {_refreshRate} Hz"
                : _selectedPreset;
        }

        private void PopulateHydroOptions()
        {
            PumpComboBox.DisplayMemberPath = nameof(Option<PumpVoltage>.Label);
            PumpComboBox.SelectedValuePath = nameof(Option<PumpVoltage>.Value);
            PumpComboBox.ItemsSource = new[]
            {
                new Option<PumpVoltage>("Pump · Off", PumpVoltage.Off), new Option<PumpVoltage>("Pump · 7 V", PumpVoltage.V7),
                new Option<PumpVoltage>("Pump · 8 V", PumpVoltage.V8), new Option<PumpVoltage>("Pump · 11 V", PumpVoltage.V11)
            };

            HydroFanComboBox.DisplayMemberPath = nameof(Option<FanSpeed>.Label);
            HydroFanComboBox.SelectedValuePath = nameof(Option<FanSpeed>.Value);
            HydroFanComboBox.ItemsSource = new[]
            {
                new Option<FanSpeed>("Fan · Off", FanSpeed.Off), new Option<FanSpeed>("Fan · 25%", FanSpeed.Percent25),
                new Option<FanSpeed>("Fan · 50%", FanSpeed.Percent50), new Option<FanSpeed>("Fan · 75%", FanSpeed.Percent75),
                new Option<FanSpeed>("Fan · 90%", FanSpeed.Percent90), new Option<FanSpeed>("Fan · 95%", FanSpeed.Percent95),
                new Option<FanSpeed>("Fan · 100%", FanSpeed.Percent100)
            };
        }

        private void PositionNearTray()
        {
            var cursor = System.Windows.Forms.Cursor.Position;
            Screen screen = Screen.FromPoint(cursor);
            IntPtr handle = new WindowInteropHelper(this).Handle;
            if (handle == IntPtr.Zero || !GetWindowRect(handle, out NativeRect windowRect))
                return;

            var windowSize = new System.Drawing.Size(
                windowRect.Right - windowRect.Left,
                windowRect.Bottom - windowRect.Top);
            TrayWidgetPlacementResult placement = TrayWidgetPlacement.Calculate(
                screen.Bounds,
                screen.WorkingArea,
                cursor,
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

        private static System.Drawing.Rectangle? FindTaskbarBounds(
            System.Drawing.Rectangle monitorBounds)
        {
            IntPtr primaryTaskbar = FindWindow("Shell_TrayWnd", null);
            if (TryGetTaskbarBounds(primaryTaskbar, monitorBounds, out System.Drawing.Rectangle bounds))
                return bounds;

            IntPtr secondaryTaskbar = IntPtr.Zero;
            while ((secondaryTaskbar = FindWindowEx(
                       IntPtr.Zero,
                       secondaryTaskbar,
                       "Shell_SecondaryTrayWnd",
                       null)) != IntPtr.Zero)
            {
                if (TryGetTaskbarBounds(secondaryTaskbar, monitorBounds, out bounds))
                    return bounds;
            }

            return null;
        }

        private static bool TryGetTaskbarBounds(
            IntPtr taskbar,
            System.Drawing.Rectangle monitorBounds,
            out System.Drawing.Rectangle bounds)
        {
            bounds = default;
            if (taskbar == IntPtr.Zero || !GetWindowRect(taskbar, out NativeRect rect))
                return false;

            bounds = System.Drawing.Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
            System.Drawing.Rectangle intersection = System.Drawing.Rectangle.Intersect(bounds, monitorBounds);
            return intersection.Width > 0 && intersection.Height > 0;
        }

        private void Window_DpiChanged(object sender, DpiChangedEventArgs e) =>
            Dispatcher.BeginInvoke(PositionNearTray, DispatcherPriority.Loaded);

        private static bool TryReadLeadingInt(string text, out int value)
        {
            string token = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
            return int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        }

        private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
                DragMove();
        }

        private void HideWidget()
        {
            _telemetryTimer.Stop();
            Hide();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e) => HideWidget();

        private void Window_Deactivated(object sender, EventArgs e)
        {
            if (!_applying)
                HideWidget();
        }

        private void Window_Closed(object? sender, EventArgs e) => _telemetryTimer.Stop();

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

        private sealed record Option<T>(string Label, T Value);
    }
}
