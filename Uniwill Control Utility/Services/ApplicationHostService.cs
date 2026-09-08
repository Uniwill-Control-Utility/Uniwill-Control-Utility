using Microsoft.Extensions.Hosting;
using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Uniwill_Control_Utility.Properties;
using Wpf.Ui;

namespace Uniwill_Control_Utility.Services
{
    public class ApplicationHostService : IHostedService
    {
        private readonly IServiceProvider _serviceProvider;
        private INavigationWindow? _navigationWindow;

        public ApplicationHostService(IServiceProvider serviceProvider)
        {
            _serviceProvider = serviceProvider;
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            await HandleActivationAsync();
            ApplyLightBarBootSettings();
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
        }

        private async Task HandleActivationAsync()
        {
            await Task.CompletedTask;

            if (!Application.Current.Windows.OfType<Views.Windows.MainWindow>().Any())
            {
                _navigationWindow = (_serviceProvider.GetService(typeof(INavigationWindow)) as INavigationWindow)!;
                _navigationWindow!.ShowWindow();
                _navigationWindow!.Navigate(typeof(Views.Pages.DashboardPage));
            }

            await Task.CompletedTask;
        }

        /// <summary>
        /// Applies LightBar saved settings on app startup so the light bar
        /// reflects the user's last configuration without requiring navigation
        /// to the LightBar page.
        /// </summary>
        private void ApplyLightBarBootSettings()
        {
            _ = Task.Run(() =>
            {
                try
                {
                    // Give the HID subsystem a moment to initialize
                    Thread.Sleep(500);

                    bool enabled = Settings.Default.LightBarEnabled;
                    if (!enabled)
                    {
                        Debug.WriteLine("[LB-boot] LightBar disabled, skipping boot apply");
                        return;
                    }

                    byte r = (byte)Settings.Default.LightBarR;
                    byte g = (byte)Settings.Default.LightBarG;
                    byte b = (byte)Settings.Default.LightBarB;
                    int brightness = (int)Settings.Default.LightBarBrightness;
                    int effectIndex = (int)Settings.Default.LightBarEffectIndex;
                    int speed = (int)Settings.Default.LightBarSpeed;

                    var hid = new KeyboardHidService();
                    try
                    {
                        bool opened = hid.Open();
                        if (!opened || !hid.IsLightBarAvailable)
                        {
                            Debug.WriteLine("[LB-boot] HID light bar not available");
                            return;
                        }

                        // Clamp values to valid ranges
                        effectIndex = Math.Clamp(effectIndex, 0, 5);
                        speed = Math.Clamp(speed, 0, 11);

                        // Effect mapping: (display name, effect byte, requires speed, color count)
                        byte[] effectValues = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06 };
                        byte effect = effectValues[effectIndex];
                        byte hidSpeed = (byte)(11 - speed);

                        hid.LightBarTurnOn(r, g, b, brightness);
                        hid.LightBarSetEffect(effect, hidSpeed, brightness);

                        Debug.WriteLine($"[LB-boot] Applied: enabled={enabled}, color=({r},{g},{b}), brightness={brightness}, effect=0x{effect:X2}, hidSpeed={hidSpeed}");
                    }
                    finally
                    {
                        hid.Dispose();
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[LB-boot] Apply failed: {ex.Message}");
                }
            });
        }
    }
}
