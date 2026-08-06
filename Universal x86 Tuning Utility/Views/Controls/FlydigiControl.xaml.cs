using System.Windows;
using System.Windows.Controls;
using Universal_x86_Tuning_Utility.Services;

namespace Universal_x86_Tuning_Utility.Views.Controls
{
    public partial class FlydigiControl : UserControl
    {
        public FlydigiControl()
        {
            InitializeComponent();
        }

        public void UpdateMetrics(DeviceMetricsSnapshot snapshot)
        {
            if (snapshot.FlydigiConnected)
            {
                this.Visibility = Visibility.Visible;

                // Update title with model name
                if (!string.IsNullOrEmpty(snapshot.FlydigiModelName))
                {
                    _titleText.Text = $"Flydigi {snapshot.FlydigiModelName}";
                }

                // BS1 has max 3000 RPM; BS2/BS2 Pro/BS3/BS3 Pro have max 4000 RPM
                int maxRpm = snapshot.FlydigiIsBs1 ? 3000 : 4000;
                _fanBar.Maximum = maxRpm;

                if (snapshot.FlydigiFanRpm > 0)
                {
                    _fanLabel.Content = $"{snapshot.FlydigiFanRpm} RPM";
                    _fanBar.Value = snapshot.FlydigiFanRpm;
                }
                else
                {
                    _fanLabel.Content = "Off";
                    _fanBar.Value = _fanBar.Minimum;
                }

                _rgbLabel.Content = string.IsNullOrEmpty(snapshot.FlydigiRgbMode)
                    ? "--"
                    : snapshot.FlydigiRgbMode;
            }
            else
            {
                this.Visibility = Visibility.Collapsed;
            }
        }
    }
}
