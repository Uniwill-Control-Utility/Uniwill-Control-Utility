using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Wpf.Ui;

namespace Uniwill_Control_Utility.Services
{
    internal class ApplicationHostService : IHostedService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly StartupProgressReporter _startupProgressReporter;

        public ApplicationHostService(
            IServiceProvider serviceProvider,
            StartupProgressReporter startupProgressReporter)
        {
            _serviceProvider = serviceProvider;
            _startupProgressReporter = startupProgressReporter;
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            await _startupProgressReporter.ReportAsync(StartupStage.StartingServices);
            await HandleActivationAsync();
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
        }

        private async Task HandleActivationAsync()
        {
            if (!Application.Current.Windows.OfType<Views.Windows.MainWindow>().Any())
            {
                await _startupProgressReporter.ReportAsync(StartupStage.PreparingInterface);
                var window = _serviceProvider.GetRequiredService<INavigationWindow>();

                if (window is Window applicationWindow)
                {
                    Application.Current.MainWindow = applicationWindow;
                }

                await _startupProgressReporter.ReportAsync(StartupStage.OpeningDashboard);
                window.ShowWindow();
                window.Navigate(typeof(Views.Pages.DashboardPage));
            }
        }
    }
}
