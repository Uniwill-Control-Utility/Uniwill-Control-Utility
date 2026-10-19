using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Wpf.Ui.Controls;

namespace Uniwill_Control_Utility.Views.Windows
{
    public partial class WaterFillDialog : FluentWindow
    {
        private readonly Services.HydroUIService _service;
        private CancellationTokenSource? _primingCts;

        public WaterFillDialog(Services.HydroUIService service)
        {
            InitializeComponent();
            _service = service;
            Owner = Application.Current.MainWindow;
        }

        private async void btnStart_Click(object sender, RoutedEventArgs e)
        {
            // Hide start button, center cancel button
            btnStart.Visibility = Visibility.Collapsed;
            Grid.SetColumn(btnCancel, 2);
            btnCancel.Content = "Cancel";
            btnCancel.IsEnabled = true;
            ProgressPanel.Visibility = Visibility.Visible;

            _primingCts = new CancellationTokenSource();

            try
            {
                await _service.RunPrimingCycleAsync(
                    _primingCts.Token,
                    cycle => Dispatcher.Invoke(() =>
                    {
                        PrimingProgressBar.Value = cycle;
                        CycleText.Text = $"Cycle {cycle}/8";
                        StatusText.Text = cycle == 8
                            ? "Filling complete — restoring settings..."
                            : "Running filling cycle...";
                    }));

                // Completed normally
                Dispatcher.Invoke(() =>
                {
                    StatusText.Text = "Water filling complete.";
                    btnCancel.Content = "Close";
                    btnCancel.IsEnabled = true;
                });
            }
            catch (OperationCanceledException)
            {
                Dispatcher.Invoke(() =>
                {
                    StatusText.Text = "Filling cycle cancelled.";
                    btnCancel.Content = "Close";
                    btnCancel.IsEnabled = true;
                });
            }
            finally
            {
                _primingCts = null;
            }
        }

        private void btnCancel_Click(object sender, RoutedEventArgs e)
        {
            if (_primingCts != null)
            {
                btnCancel.IsEnabled = false;
                StatusText.Text = "Cancelling...";
                _primingCts.Cancel();
            }
            else
            {
                // Not running — just close
                Close();
            }
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            // If the user tries to close the window while priming, cancel first
            if (_primingCts != null)
            {
                e.Cancel = true;
                btnCancel_Click(btnCancel, new RoutedEventArgs());
            }
        }
    }
}
