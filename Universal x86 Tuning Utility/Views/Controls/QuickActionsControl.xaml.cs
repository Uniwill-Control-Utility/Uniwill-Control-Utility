using System;
using System.Windows;
using System.Windows.Controls;
using Universal_x86_Tuning_Utility.Services;

namespace Universal_x86_Tuning_Utility.Views.Controls
{
    public partial class QuickActionsControl : UserControl
    {
        private bool _isChanging = false;

        public QuickActionsControl()
        {
            InitializeComponent();
            Loaded += QuickActionsControl_Loaded;
        }

        private void QuickActionsControl_Loaded(object sender, RoutedEventArgs e)
        {
            LoadRefreshRates();
            LoadResolutions();
            LoadHdrState();
            LoadNightLightState();
        }

        #region Refresh Rate

        private void LoadRefreshRates()
        {
            try
            {
                var rates = QuickActionsService.GetAvailableRefreshRates();
                var currentHz = QuickActionsService.GetCurrentDisplayMode().RefreshRate;
                _cmbRefreshRate.ItemsSource = rates;

                // Select the matching rate
                foreach (string item in rates)
                {
                    if (item == $"{currentHz} Hz")
                    {
                        _cmbRefreshRate.SelectedItem = item;
                        break;
                    }
                }
            }
            catch { /* not available */ }
        }

        private void _cmbRefreshRate_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isChanging || _cmbRefreshRate.SelectedItem is not string selected)
                return;

            // Parse "XX Hz" to int
            if (!int.TryParse(selected.Replace(" Hz", ""), out int newRate) || newRate <= 0)
                return;

            var current = QuickActionsService.GetCurrentDisplayMode();
            _isChanging = true;
            try
            {
                QuickActionsService.SetDisplayMode(current.Width, current.Height, newRate);
            }
            finally
            {
                _isChanging = false;
            }
        }

        #endregion

        #region Resolution

        private void LoadResolutions()
        {
            try
            {
                var modes = QuickActionsService.GetSupportedDisplayModes();
                var current = QuickActionsService.GetCurrentDisplayMode();

                _cmbResolution.ItemsSource = modes;

                // Select the current resolution
                string currentText = $"{current.Width} × {current.Height}";
                foreach (string item in modes)
                {
                    if (item == currentText)
                    {
                        _cmbResolution.SelectedItem = item;
                        break;
                    }
                }
            }
            catch { /* not available */ }
        }

        private void _cmbResolution_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isChanging || _cmbResolution.SelectedItem is not string selected)
                return;

            // Parse "XXXX × YYYY" to (width, height)
            var parts = selected.Split(new[] { '×' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2
                || !int.TryParse(parts[0].Trim(), out int width)
                || !int.TryParse(parts[1].Trim(), out int height)
                || width <= 0 || height <= 0)
                return;

            // Find the best refresh rate for this resolution
            var current = QuickActionsService.GetCurrentDisplayMode();
            int newRate = current.RefreshRate;

            _isChanging = true;
            try
            {
                QuickActionsService.SetDisplayMode(width, height, newRate);
                // Reload refresh rates for the new resolution
                LoadRefreshRates();
            }
            finally
            {
                _isChanging = false;
            }
        }

        #endregion

        #region HDR

        private void LoadHdrState()
        {
            try
            {
                bool supported = QuickActionsService.IsHdrSupported();
                if (!supported)
                {
                    _toggleHdr.IsEnabled = false;
                }
                else
                {
                    _toggleHdr.IsChecked = QuickActionsService.GetHdrState();
                }
            }
            catch { /* not available */ }
        }

        private void _toggleHdr_Checked(object sender, RoutedEventArgs e)
        {
            QuickActionsService.SetHdrState(true);
        }

        private void _toggleHdr_Unchecked(object sender, RoutedEventArgs e)
        {
            QuickActionsService.SetHdrState(false);
        }

        #endregion

        #region Night Light

        private void LoadNightLightState()
        {
            try
            {
                _toggleNightLight.IsChecked = QuickActionsService.GetNightLightState();
            }
            catch { /* not available */ }
        }

        private void _toggleNightLight_Checked(object sender, RoutedEventArgs e)
        {
            QuickActionsService.SetNightLightState(true);
        }

        private void _toggleNightLight_Unchecked(object sender, RoutedEventArgs e)
        {
            QuickActionsService.SetNightLightState(false);
        }

        #endregion

        #region Buttons (open Settings pages)

        private void _btnMicMute_Click(object sender, RoutedEventArgs e)
        {
            OpenSettings("ms-settings:privacy-microphone");
        }

        private void _btnTouchpad_Click(object sender, RoutedEventArgs e)
        {
            OpenSettings("ms-settings:devices-touchpad");
        }

        private void _btnNightCharge_Click(object sender, RoutedEventArgs e)
        {
            OpenSettings("ms-settings:battery-saver");
        }

        private void _btnDisplay_Click(object sender, RoutedEventArgs e)
        {
            OpenSettings("ms-settings:display");
        }

        private static void OpenSettings(string uri)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = uri,
                    UseShellExecute = true
                });
            }
            catch { /* not available */ }
        }

        #endregion
    }
}
