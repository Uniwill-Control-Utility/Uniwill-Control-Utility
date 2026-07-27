using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Universal_x86_Tuning_Utility.Scripts.Misc;
using Universal_x86_Tuning_Utility.Services;

namespace Universal_x86_Tuning_Utility.Views.Pages
{
    public partial class FanControl : Page
    {
        private readonly DispatcherTimer _timer;
        private readonly IHardwareMonitoringService _hardwareMonitoring;
        private readonly UniwillECService? _uniwillEc;
        private IDisposable? _cpuMonitoringLease;
        private bool _isFanCurveActive;
        private bool _isManualModeActive;

        public FanControl(
            IHardwareMonitoringService hardwareMonitoring,
            UniwillECService? uniwillEc = null)
        {
            _hardwareMonitoring = hardwareMonitoring;
            _uniwillEc = uniwillEc;
            InitializeComponent();

            _ = Tablet.TabletDevices;

            _timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(2.5)
            };
            _timer.Tick += Timer_Tick;

            // Try to initialize Uniwill EC if injected
            bool uniwillAvailable = false;
            if (_uniwillEc is not null)
            {
                try
                {
                    uniwillAvailable = _uniwillEc.Initialize();
                }
                catch
                {
                    uniwillAvailable = false;
                }
            }

            // Update UI based on whether Uniwill EC is available
            if (uniwillAvailable)
            {
                string fanConfig = $"{GetSystemInfo.Manufacturer.ToUpper()}_{GetSystemInfo.Product.ToUpper()}.json";
                tbConfigName.Text = fanConfig;
                tbFanSpeed.Text = LocalizationService.Get("Ready");
                UniwillAvailable.Visibility = Visibility.Visible;
                UniwillUnavailable.Visibility = Visibility.Collapsed;

                // Read current fan RPM on load
                ReadFanRpm();
            }
            else
            {
                tbConfigName.Text = LocalizationService.Get("Not detected");
                tbFanSpeed.Text = LocalizationService.Get("EC hardware not available");
                UniwillAvailable.Visibility = Visibility.Collapsed;
                UniwillUnavailable.Visibility = Visibility.Visible;
            }
        }

        private void ReadFanRpm()
        {
            try
            {
                if (_uniwillEc is null) return;

                int rpm = _uniwillEc.GetMainFanRpm();
                if (rpm > 0)
                {
                    tbFanSpeed.Text = $"{rpm} RPM";
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to read fan RPM: {ex.Message}");
            }
        }

        private void Timer_Tick(object? sender, EventArgs e)
        {
            try
            {
                if (!_isFanCurveActive || _uniwillEc is null) return;

                int[] temps = { 25, 35, 45, 55, 65, 75, 85, 95 };
                int[] speeds = { 0, 5, 15, 25, 40, 55, 70, 100 };

                int cpuTemperature = GetCpuTemperature();
                int fanSpeed = Interpolate(speeds, temps, cpuTemperature);

                _uniwillEc.SetFanSpeedPercent(fanSpeed);

                tbFanSpeed.Text = LocalizationService.Format("Fan Curve: {0}% @ {1}°C", fanSpeed, cpuTemperature);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Fan curve timer error: {ex.Message}");
            }
        }

        private int GetCpuTemperature()
        {
            try
            {
                var snapshot = _hardwareMonitoring.ReadSnapshot();
                return snapshot.CpuTemperature;
            }
            catch
            {
                return 0;
            }
        }

        private static int Interpolate(int[] yValues, int[] xValues, int x)
        {
            int i = Array.FindIndex(xValues, t => t >= x);

            if (i == -1)
                return yValues[0];
            else if (i == 0)
                return yValues[0];
            else if (i >= xValues.Length)
                return yValues[xValues.Length - 1];
            else
                return (yValues[i - 1] * (xValues[i] - x) + yValues[i] * (x - xValues[i - 1])) / (xValues[i] - xValues[i - 1]);
        }

        private void btnFanSpeed_Click(object sender, RoutedEventArgs e)
        {
            if (_uniwillEc is null) return;

            try
            {
                int speed = (int)nudFanSpeed.Value;
                _uniwillEc.SetFanSpeedPercent(speed);
                _isManualModeActive = true;
                tbFanSpeed.Text = LocalizationService.Format("Manual: {0}%", speed);
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(
                    $"Failed to set fan speed: {ex.Message}",
                    "Error",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Error);
            }
        }

        private void btnFanCurve_Click(object sender, RoutedEventArgs e)
        {
            if (_uniwillEc is null) return;

            if (_isFanCurveActive)
            {
                StopFanCurve();
            }
            else
            {
                StartFanCurve();
            }
        }

        private void StartFanCurve()
        {
            _isFanCurveActive = true;
            _isManualModeActive = false;
            _cpuMonitoringLease ??= _hardwareMonitoring.Acquire(HardwareMonitoringCategory.Cpu);
            _timer.Start();

            var stackPanel = (StackPanel)btnFanCurve.Content!;
            var textBlock = (System.Windows.Controls.TextBlock)stackPanel.Children[1];
            textBlock.Text = LocalizationService.Get("Stop Fan Curve");
            nudFanSpeed.IsEnabled = false;
        }

        private void StopFanCurve()
        {
            _isFanCurveActive = false;
            _timer.Stop();
            _cpuMonitoringLease?.Dispose();
            _cpuMonitoringLease = null;

            var stackPanel = (StackPanel)btnFanCurve.Content!;
            var textBlock = (System.Windows.Controls.TextBlock)stackPanel.Children[1];
            textBlock.Text = LocalizationService.Get("Test Fan Curve");
            nudFanSpeed.IsEnabled = true;

            if (_uniwillEc is not null)
            {
                tbFanSpeed.Text = LocalizationService.Get("Stopped");
                ReadFanRpm();
            }
        }

        private void btnRestore_Click(object sender, RoutedEventArgs e)
        {
            if (_uniwillEc is null) return;

            try
            {
                StopFanCurve();
                _isManualModeActive = false;
                _uniwillEc.RestoreAutoFanControl();

                tbFanSpeed.Text = LocalizationService.Get("Auto mode restored");
                ReadFanRpm();
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(
                    $"Failed to restore auto mode: {ex.Message}",
                    "Error",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Error);
            }
        }

        private void btnCopy_Click(object sender, RoutedEventArgs e)
        {
            string fanConfig = $"{GetSystemInfo.Manufacturer.ToUpper()}_{GetSystemInfo.Product.ToUpper()}.json";
            Clipboard.SetText(fanConfig);
        }

        private void btnReload_Click(object sender, RoutedEventArgs e)
        {
            if (_uniwillEc is not null)
            {
                ReadFanRpm();
            }
        }

        private void Page_Loaded(object sender, RoutedEventArgs e)
        {
        }

        private void Page_Unloaded(object? sender, EventArgs e)
        {
            StopFanCurve();

            // Restore auto fan control on navigation away
            if (_uniwillEc is not null && (_isManualModeActive || _isFanCurveActive))
            {
                try
                {
                    _uniwillEc.RestoreAutoFanControl();
                }
                catch
                {
                    // Silent fail on unload
                }
            }
        }
    }
}
