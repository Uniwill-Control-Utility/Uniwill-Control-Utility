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

        public void UpdateModuleInfo(string manufacturer, string model, int speed)
        {
            string moduleIdentity = DashboardMemoryInfoFormatter.FormatModel(manufacturer, model);
            _moduleModel.Text = moduleIdentity;
            _moduleModel.ToolTip = moduleIdentity;
            _moduleSpeed.Text = DashboardMemoryInfoFormatter.FormatSpeed(speed);
        }

        public void UpdateMetrics(HardwareMetricsSnapshot snapshot)
        {
            double totalMem = snapshot.SystemMemoryTotalGb;
            if (totalMem > 0)
            {
                double percent = (snapshot.SystemMemoryUsedGb / totalMem) * 100;
                _memoryBar.Value = percent;
                _memoryUsedHero.Text = $"{snapshot.SystemMemoryUsedGb:F1}";
                _memoryTotalHero.Text = $"/ {totalMem:F1} GB";
                _memoryPercent.Text = $"{percent:F0}%";
            }
            else
            {
                _memoryBar.Value = 0;
                _memoryUsedHero.Text = "--";
                _memoryTotalHero.Text = string.Empty;
                _memoryPercent.Text = "--";
            }

            // DIMM #1 temperature
            if (snapshot.MemoryTemperature0 > 0)
            {
                _memoryTemp1Bar.Value = snapshot.MemoryTemperature0;
                _memoryTemp1Label.Text = $"{snapshot.MemoryTemperature0}\u00B0C";
            }
            else
            {
                _memoryTemp1Bar.Value = 0;
                _memoryTemp1Label.Text = "--";
            }

            // DIMM #2 temperature
            if (snapshot.MemoryTemperature1 > 0)
            {
                _memoryTemp2Bar.Value = snapshot.MemoryTemperature1;
                _memoryTemp2Label.Text = $"{snapshot.MemoryTemperature1}\u00B0C";
            }
            else
            {
                _memoryTemp2Bar.Value = 0;
                _memoryTemp2Label.Text = "--";
            }
        }
    }
}
