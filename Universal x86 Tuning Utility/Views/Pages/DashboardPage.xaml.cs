using System;
using System.Windows.Controls;
using Universal_x86_Tuning_Utility.Services;
using Universal_x86_Tuning_Utility.ViewModels;
using Wpf.Ui.Abstractions.Controls;

namespace Universal_x86_Tuning_Utility.Views.Pages
{
    public partial class DashboardPage :
        Page,
        INavigableView<DashboardViewModel>
    {
        public DashboardViewModel ViewModel { get; }

        public DashboardPage(DashboardViewModel viewModel)
        {
            ViewModel = viewModel
                ?? throw new ArgumentNullException(nameof(viewModel));

            DataContext = this;

            InitializeComponent();

            ViewModel.MetricsUpdated += OnMetricsUpdated;
            ViewModel.DeviceMetricsUpdated += OnDeviceMetricsUpdated;
        }

        private void OnMetricsUpdated(object? sender, HardwareMetricsSnapshot snapshot)
        {
            _cpu.UpdateMetrics(snapshot);
            _gpu.UpdateMetrics(snapshot);
            _system.UpdateMetrics(snapshot);
        }

        private void OnDeviceMetricsUpdated(object? sender, DeviceMetricsSnapshot snapshot)
        {
            _devices.UpdateMetrics(snapshot);
        }
    }
}
