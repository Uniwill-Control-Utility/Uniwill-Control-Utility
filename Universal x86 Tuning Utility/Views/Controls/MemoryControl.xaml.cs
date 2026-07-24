using System.Windows.Controls;
using Universal_x86_Tuning_Utility.Services;

namespace Universal_x86_Tuning_Utility.Views.Controls
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
        }
    }
}
