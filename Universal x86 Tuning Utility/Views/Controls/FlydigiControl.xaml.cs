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

                if (snapshot.FlydigiFanRpm > 0)
                    _fanLabel.Content = $"{snapshot.FlydigiFanRpm} RPM";
                else
                    _fanLabel.Content = "Off";

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
