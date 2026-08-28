using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using Universal_x86_Tuning_Utility.Services;

namespace Universal_x86_Tuning_Utility.Views.Pages
{
    /// <summary>
    /// CPU temperature limit (TjMax) control page.
    /// Writes to EC register 0x0786 and reads back from 0x0463.
    /// </summary>
    public partial class CpuTempLimit : Page
    {
        private readonly UniwillECService? _ecService;
        private bool _isEcAvailable;
        private bool _isLoaded;

        public CpuTempLimit()
        {
            InitializeComponent();

            _ecService = App.GetService<UniwillECService>();
            if (_ecService is not null)
            {
                try
                {
                    _isEcAvailable = _ecService.Initialize();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[CpuTempLimit] EC initialization failed: {ex.Message}");
                    _isEcAvailable = false;
                }
            }
        }

        private void Page_Loaded(object sender, RoutedEventArgs e)
        {
            if (!_isEcAvailable)
            {
                CpuTempAvailable.Visibility = Visibility.Collapsed;
                CpuTempUnavailable.Visibility = Visibility.Visible;
                return;
            }

            CpuTempAvailable.Visibility = Visibility.Visible;
            CpuTempUnavailable.Visibility = Visibility.Collapsed;

            // Read current value from EC and populate UI
            ReadAndUpdateFromEC();
            _isLoaded = true;
        }

        private void ReadAndUpdateFromEC()
        {
            if (_ecService is null)
            {
                tbStatus.Text = "EC service unavailable";
                return;
            }

            try
            {
                int currentTemp = _ecService.GetCpuTempLimit();
                byte rawValue = _ecService.ReadECRegister(0x0463);

                // Clamp to slider range in case EC returns an unexpected value
                currentTemp = Math.Clamp(currentTemp, 80, 95);

                sliderTempLimit.Value = currentTemp;
                tbTempValue.Text = $"{currentTemp} °C";
                tbRawValue.Text = $"0x{rawValue:X2}";
                tbDecodedTemp.Text = $"{currentTemp} °C";

                tbStatus.Text = $"Current limit: {currentTemp} °C";
                Debug.WriteLine($"[CpuTempLimit] Read from EC: raw=0x{rawValue:X2}, decoded={currentTemp} °C");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CpuTempLimit] Failed to read from EC: {ex.Message}");
                tbStatus.Text = "Failed to read from EC";
            }
        }

        private void sliderTempLimit_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_isLoaded)
                return;

            int temp = (int)Math.Round(sliderTempLimit.Value);
            tbTempValue.Text = $"{temp} °C";
            tbStatus.Text = $"Selected: {temp} °C — click Apply to write to EC";
        }

        private void btnApply_Click(object sender, RoutedEventArgs e)
        {
            if (_ecService is null)
            {
                tbStatus.Text = "EC service unavailable";
                return;
            }

            int temp = (int)Math.Round(sliderTempLimit.Value);

            try
            {
                _ecService.SetCpuTempLimit(temp);
                Debug.WriteLine($"[CpuTempLimit] Applied temp limit: {temp} °C (encoded=0x{UniwillECService.EncodeCpuTempLimit(temp):X2})");

                // Re-read from EC to confirm write
                ReadAndUpdateFromEC();

                tbStatus.Text = $"Applied: {temp} °C";
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CpuTempLimit] Failed to apply temp limit: {ex.Message}");
                tbStatus.Text = $"Error: {ex.Message}";
                MessageBox.Show(
                    $"Failed to write CPU temperature limit:\n\n{ex.Message}",
                    "EC Write Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }
    }
}
