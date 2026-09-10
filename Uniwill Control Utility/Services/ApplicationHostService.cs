using Microsoft.Extensions.DependencyInjection;
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
        private const string LightBarBootTag = "[LB-boot]";

        private readonly IServiceProvider _serviceProvider;

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
            if (!Application.Current.Windows.OfType<Views.Windows.MainWindow>().Any())
            {
                var window = _serviceProvider.GetRequiredService<INavigationWindow>();
                window.ShowWindow();
                window.Navigate(typeof(Views.Pages.DashboardPage));
            }
        }

        /// <summary>
        /// Applies LightBar saved settings on app startup so the light bar
        /// reflects the user's last configuration without requiring navigation
        /// to the LightBar page.
        /// </summary>
        private static void ApplyLightBarBootSettings()
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
                        Debug.WriteLine($"{LightBarBootTag} LightBar disabled, skipping boot apply");
                        return;
                    }

                    byte r = Settings.Default.LightBarR;
                    byte g = Settings.Default.LightBarG;
                    byte b = Settings.Default.LightBarB;
                    int brightness = Settings.Default.LightBarBrightness;
                    int effectIndex = Settings.Default.LightBarEffectIndex;
                    int speed = Settings.Default.LightBarSpeed;

                    var hid = new KeyboardHidService();
                    try
                    {
                        bool opened = hid.Open();
                        if (!opened || !hid.IsLightBarAvailable)
                        {
                            Debug.WriteLine($"{LightBarBootTag} HID light bar not available");
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

                        Debug.WriteLine($"{LightBarBootTag} Applied: enabled={enabled}, color=({r},{g},{b}), brightness={brightness}, effect=0x{effect:X2}, hidSpeed={hidSpeed}");
                    }
                    finally
                    {
                        hid.Dispose();
                    }
                }
                catch (Exception ex)
                {
                    // Logged for diagnostics; boot settings are non-critical so we swallow the error
                    Debug.WriteLine($"{LightBarBootTag} Apply failed: {ex.Message}");
                }
            });
        }
    }
}
