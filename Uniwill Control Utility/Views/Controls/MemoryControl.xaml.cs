using System.Windows.Controls;
using Uniwill_Control_Utility.Services;

namespace Uniwill_Control_Utility.Views.Controls
{
    public partial class MemoryControl : UserControl
    {
        public MemoryControl()
        {
            InitializeComponent();
        }

        public void UpdateMetrics(HardwareMetricsSnapshot snapshot)
        {
            double totalMem = snapshot.SystemMemoryTotalGb;
            if (totalMem > 0)
            {
                double percent = (snapshot.SystemMemoryUsedGb / totalMem) * 100;
                _memoryBar.Value = percent;
                _memoryLabel.Content = $"{snapshot.SystemMemoryUsedGb:F1}/{totalMem:F1} GB";
            }
            else
            {
                _memoryBar.Value = 0;
                _memoryLabel.Content = "--";
            }

            // DIMM #1 temperature
            if (snapshot.MemoryTemperature0 > 0)
            {
                _memoryTemp1Bar.Value = snapshot.MemoryTemperature0;
                _memoryTemp1Label.Content = $"{snapshot.MemoryTemperature0}\u00B0C";
            }
            else
            {
                _memoryTemp1Bar.Value = 0;
                _memoryTemp1Label.Content = "--";
            }

            // DIMM #2 temperature
            if (snapshot.MemoryTemperature1 > 0)
            {
                _memoryTemp2Bar.Value = snapshot.MemoryTemperature1;
                _memoryTemp2Label.Content = $"{snapshot.MemoryTemperature1}\u00B0C";
            }
            else
            {
                _memoryTemp2Bar.Value = 0;
                _memoryTemp2Label.Content = "--";
            }
        }
    }
}
