using System;
using System.Windows;
using System.Windows.Threading;
using Uniwill_Control_Utility.Services;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace Uniwill_Control_Utility.Views.Windows
{
    public partial class TrayMetricsWindow : FluentWindow
    {
        private readonly TrayQuickControlService _quickControl;
        private readonly DispatcherTimer _refreshTimer;

        public TrayMetricsWindow(TrayQuickControlService quickControl)
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

        public void ShowAt(double x, double y)
        {
            Left = x;
            Top = y;
            Show();
            StartRefresh();
        }

        /// <summary>
        /// Starts the periodic refresh timer and performs an immediate update.
        /// Call this after <c>Show()</c> to begin populating metrics.
        /// </summary>
        public void StartRefresh()
        {
            UpdateMetrics();
            _refreshTimer.Start();
        }

        public void HideAndStop()
        {
            _refreshTimer.Stop();
            Hide();
        }

        public void UpdateMetrics()
        {
            try
            {
                var snapshot = _quickControl.GetTelemetrySnapshot();
                ApplySnapshot(snapshot);
            }
            catch
            {
                // Silently fail — metrics are best-effort
            }
        }

        private void ApplySnapshot(TrayQuickTelemetrySnapshot s)
        {
            // CPU hero values
            CpuTempText.Text = $"{s.CpuTemperature}°";
            CpuUsageText.Text = $"{s.CpuUsage}%";

            // CPU segmented bars
            CpuPowerBar.Value = s.CpuPowerWatts;
            CpuPowerText.Text = $"{s.CpuPowerWatts} W";
            CpuClockBar.Value = s.CpuClockMhz;
            CpuClockText.Text = $"{s.CpuClockMhz / 1000.0:F1} GHz";
            CpuFanBar.Value = s.CpuFanPercent;
            CpuFanText.Text = $"{s.CpuFanPercent}%";

            // GPU hero values
            GpuTempText.Text = $"{s.GpuTemperature}°";
            GpuUsageText.Text = $"{(int)s.GpuUsage}%";

            // GPU segmented bars
            GpuPowerBar.Value = s.GpuPowerWatts;
            GpuPowerText.Text = $"{s.GpuPowerWatts} W";
            GpuClockBar.Value = s.GpuClockMhz;
            GpuClockText.Text = $"{s.GpuClockMhz / 1000.0:F1} GHz";
            GpuFanBar.Value = s.GpuFanPercent;
            GpuFanText.Text = $"{s.GpuFanPercent}%";
        }

        protected override void OnClosed(EventArgs e)
        {
            _refreshTimer.Stop();
            base.OnClosed(e);
        }
    }
}
