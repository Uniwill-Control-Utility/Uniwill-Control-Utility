using System.Windows;
using System.Windows.Controls;
using Uniwill_Control_Utility.Services;

namespace Uniwill_Control_Utility.Views.Controls
{
    public partial class BatteryControl : UserControl
    {
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
            if (snapshot.BatteryHealthPercent > 0)
            {
                _batteryHealthHero.Text = $"{snapshot.BatteryHealthPercent}%";
                _healthState.Text = snapshot.BatteryHealthPercent >= 80 ? "Healthy" : "Reduced";
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
    }
}
