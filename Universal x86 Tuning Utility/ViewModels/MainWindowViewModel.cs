using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Universal_x86_Tuning_Utility.Scripts;
using Wpf.Ui;
using Wpf.Ui.Controls;

namespace Universal_x86_Tuning_Utility.ViewModels
{
    public partial class MainWindowViewModel : ObservableObject
    {
        [ObservableProperty]
        private string _applicationTitle = string.Empty;

        [ObservableProperty]
        private ObservableCollection<object> _navigationItems = new();

        [ObservableProperty]
        private ObservableCollection<object> _navigationFooter = new();

        [ObservableProperty]
        private ObservableCollection<MenuItem> _trayMenuItems = new();

        [ObservableProperty]
        private string _downloads = "Downloads: ";

        [ObservableProperty]
        private bool _isDownloads;

        private ICommand? _navigateCommand;

        public MainWindowViewModel(INavigationService navigationService)
        {
            InitializeViewModel();
        }

        public ICommand NavigateCommand => _navigateCommand ??= new RelayCommand<string>(OnNavigate);

        private void InitializeViewModel()
        {
            ApplicationTitle = "Universal x86 Tuning Utility";

            NavigationItems = new ObservableCollection<object>
            {
                CreateNavigationItem("Home", "dashboard", SymbolRegular.Home24, typeof(Views.Pages.DashboardPage))
            };

            if (Family.TYPE != Family.ProcessorType.Intel)
                NavigationItems.Add(CreateNavigationItem("Premade", "premade", SymbolRegular.Predictions24, typeof(Views.Pages.Premade)));

            NavigationItems.Add(CreateNavigationItem("Custom", "custom", SymbolRegular.Book24, typeof(Views.Pages.CustomPresets)));
            NavigationItems.Add(CreateNavigationItem("Adaptive", "adaptive", SymbolRegular.Radar20, typeof(Views.Pages.Adaptive)));
            NavigationItems.Add(CreateNavigationItem("Games", "games", SymbolRegular.Games24, typeof(Views.Pages.Games)));
            NavigationItems.Add(CreateNavigationItem("Overlay", "overlay", SymbolRegular.DesktopPulse24, typeof(Views.Pages.OverlaySettingsPage)));
            NavigationItems.Add(CreateNavigationItem("Auto", "auto", SymbolRegular.Transmission24, typeof(Views.Pages.Automations)));

            // Conditionally insert Hydro UI before Info for supported watercooler hardware
            if (WaterCoolerHardwareDetector.IsSupportedHardware())
            {
                NavigationItems.Add(CreateNavigationItem("Hydro UI", "watercooler", SymbolRegular.Water24, typeof(Views.Pages.Watercooler)));
            }

            // Conditionally insert Flydigi cooler before Info for connected cooling pads
            if (FlydigiHardwareDetector.IsDeviceAvailable())
            {
                NavigationItems.Add(CreateNavigationItem(FlydigiHardwareDetector.GetDetectedModelName(), "flydigicooler", SymbolRegular.WeatherDuststorm24, typeof(Views.Pages.FlydigiCooler)));
            }

            NavigationItems.Add(CreateNavigationItem("Info", "info", SymbolRegular.Info24, typeof(Views.Pages.SystemInfo)));

            NavigationFooter = new ObservableCollection<object>
            {
                CreateNavigationItem("Settings", "settings", SymbolRegular.Settings24, typeof(Views.Pages.SettingsPage))
            };

            TrayMenuItems = new ObservableCollection<MenuItem>
            {
                new() { Header = "Home", Tag = "tray_home" }
            };
        }

        private static NavigationViewItem CreateNavigationItem(string content, string tag, SymbolRegular icon, Type pageType) =>
            new(content, icon, pageType) { TargetPageTag = tag };

        private void OnNavigate(string? parameter)
        {
            switch (parameter)
            {
                case "download":
                    OpenUrl("https://github.com/JamesCJ60/Universal-x86-Tuning-Utility/releases");
                    break;
                case "discord":
                    OpenUrl("http://www.discord.gg/3EkYMZGJwq");
                    break;
                case "support":
                    OpenUrl("https://www.paypal.com/paypalme/JamesCJ60");
                    OpenUrl("https://patreon.com/uxtusoftware");
                    break;
            }
        }

        /// <summary>
        /// Creates a Flydigi logo bitmap from the SVG path data converted to a WPF DrawingImage,
        /// then rendered to a BitmapSource for use in the NavigationItem.Image property.
        /// </summary>
        private static BitmapSource CreateFlydigiLogoImageSource()
        {
            var drawing = new DrawingGroup
            {
                Children =
                {
                    new GeometryDrawing(
                        Brushes.White,
                        new Pen(Brushes.White, 1),
                        Geometry.Parse(
                            "M19.015,7.83 L0,0 L15.659,23.488 L16.777,21.251 L10.066,10.066 L15.659,11.185 " +
                            "L20.287,18.706 L16.777,25.726 L22.369,40.267 L27.962,25.726 L25.609,21.02 " +
                            "L23.645,24.163 L24.607,25.726 L22.369,40.267 L20.132,25.726 L29.079,11.185 " +
                            "L34.673,10.066 L27.962,21.251 L29.08,23.488 L45,0 L25.985,7.829 " +
                            "L22.63,14.54 L19.274,7.83 Z"))
                }
            };

            var drawingImage = new DrawingImage(drawing);
            drawingImage.Freeze();

            // Render to a 32x32 bitmap to match the SVG viewBox (45x41) at nav icon size
            var bitmap = new RenderTargetBitmap(32, 32, 96, 96, PixelFormats.Pbgra32);
            var visual = new System.Windows.Controls.Image { Source = drawingImage, Width = 32, Height = 32 };
            visual.Arrange(new Rect(0, 0, 32, 32));
            bitmap.Render(visual);
            bitmap.Freeze();

            return bitmap;
        }

        private static void OpenUrl(string url) =>
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }
}
