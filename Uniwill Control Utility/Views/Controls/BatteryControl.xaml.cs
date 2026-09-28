using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Uniwill_Control_Utility.Services;

namespace Uniwill_Control_Utility.Views.Controls
{
    public partial class BatteryControl : UserControl
    {
        private static readonly Brush HealthyBorderBrush = CreateBrush(0x55, 0xD7, 0x7D);
        private static readonly Brush HealthyBackgroundBrush = CreateBrush(0x1B, 0x7B, 0x3B, 0x26);
        private static readonly Brush HealthyIconBrush = CreateBrush(0x65, 0xDE, 0x8A);
        private static readonly Brush HealthyTextBrush = CreateBrush(0x7C, 0xE4, 0x9A);
        private static readonly Brush ReducedBorderBrush = CreateBrush(0xE5, 0xA9, 0x3D);
        private static readonly Brush ReducedBackgroundBrush = CreateBrush(0x7A, 0x55, 0x18, 0x26);
        private static readonly Brush ReducedIconBrush = CreateBrush(0xFF, 0xCA, 0x64);
        private static readonly Brush ReducedTextBrush = CreateBrush(0xFF, 0xD8, 0x8A);

        public BatteryControl()
        {
            InitializeComponent();
        }

        public void UpdateMetrics(HardwareMetricsSnapshot snapshot)
        {
            if (!snapshot.HasBattery)
            {
                this.Visibility = Visibility.Collapsed;
                return;
            }

            this.Visibility = Visibility.Visible;

            // Health
            DashboardBatteryHealthState healthState =
                DashboardBatteryHealthClassifier.Classify(snapshot.BatteryHealthPercent);

            if (healthState != DashboardBatteryHealthState.Unavailable)
            {
                _batteryHealthHero.Text = $"{snapshot.BatteryHealthPercent}%";
                ApplyHealthPresentation(healthState);
                _healthPill.Visibility = Visibility.Visible;
            }
            else
            {
                _batteryHealthHero.Text = "--";
                _healthPill.Visibility = Visibility.Collapsed;
            }

            // Temperature
            if (snapshot.BatteryTemperatureC >= 0)
            {
                _batteryTempBar.Value = snapshot.BatteryTemperatureC;
                _batteryTempLabel.Text = $"{snapshot.BatteryTemperatureC}\u00B0C";
            }
            else
            {
                _batteryTempBar.Value = 0;
                _batteryTempLabel.Text = "--";
            }

            // Charge limit
            int chargeLimitValue = snapshot.BatteryChargeLimit switch
            {
                0 => 100,
                1 => 80,
                2 => 60,
                _ => -1
            };

            if (chargeLimitValue >= 0)
            {
                _chargeLimitHero.Text = $"{chargeLimitValue}%";
            }
            else
            {
                _chargeLimitHero.Text = "--";
            }
        }

        private void ApplyHealthPresentation(DashboardBatteryHealthState state)
        {
            bool isHealthy = state == DashboardBatteryHealthState.Healthy;
            _healthState.Text = isHealthy ? "Healthy" : "Reduced";
            _healthPill.BorderBrush = isHealthy ? HealthyBorderBrush : ReducedBorderBrush;
            _healthPill.Background = isHealthy ? HealthyBackgroundBrush : ReducedBackgroundBrush;
            _healthIcon.Foreground = isHealthy ? HealthyIconBrush : ReducedIconBrush;
            _healthState.Foreground = isHealthy ? HealthyTextBrush : ReducedTextBrush;
        }

        private static Brush CreateBrush(byte red, byte green, byte blue, byte alpha = 0xFF)
        {
            var brush = new SolidColorBrush(Color.FromArgb(alpha, red, green, blue));
            brush.Freeze();
            return brush;
        }
    }
}
