using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Uniwill_Control_Utility.Properties;
using Uniwill_Control_Utility.Scripts;
using Uniwill_Control_Utility.Services;
using Uniwill_Control_Utility.Views.Controls;
using Wpf.Ui;
using Wpf.Ui.Controls;

namespace Uniwill_Control_Utility.ViewModels
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

        [ObservableProperty]
        private bool _hasLastAppliedSettings;

        [ObservableProperty]
        private string _lastAppliedSettingsToolTip = string.Empty;

        private ICommand? _navigateCommand;
        private readonly UniwillECService? _uniwillEc;

        public MainWindowViewModel(INavigationService navigationService, UniwillECService? uniwillEc = null)
        {
            _uniwillEc = uniwillEc;
            RyzenAdj_To_UCU.EcService = uniwillEc;
            InitializeViewModel();
            LocalizationService.CultureChanged += OnCultureChanged;
            LastAppliedSettingsService.Changed += OnLastAppliedSettingsChanged;
        }

        public ICommand NavigateCommand => _navigateCommand ??= new RelayCommand<string>(OnNavigate);

        private void InitializeViewModel()
        {
            ApplicationTitle = "Uniwill Control Utility";

            NavigationItems = new ObservableCollection<object>
            {
                CreateNavigationItem("Home", "dashboard", SymbolRegular.Home24, typeof(Views.Pages.DashboardPage))
            };

            NavigationItems.Add(CreateNavigationItem("Presets", "presets", SymbolRegular.Predictions24, typeof(Views.Pages.Presets)));
            NavigationItems.Add(CreateNavigationItem("Adaptive", "adaptive", SymbolRegular.Radar20, typeof(Views.Pages.Adaptive)));
            


            // Hydro UI in footer for supported watercooler hardware
            if (WaterCoolerHardwareDetector.IsSupportedHardware())
            {
                var hydroItem = CreateNavigationItem("Hydro UI", "watercooler", SymbolRegular.Empty, typeof(Views.Pages.Watercooler));
                hydroItem.Icon = new Wpf.Ui.Controls.FontIcon { FontFamily = new System.Windows.Media.FontFamily("Segoe Fluent Icons"), Glyph = "\uEB42", FontSize = 20 };
                NavigationItems.Add(hydroItem);
            }

            // Flydigi cooler in footer (hidden if user has disabled it in Settings)
            if (!Settings.Default.HideFlydigi)
            {
                NavigationFooter.Add(CreateFlydigiNavItem());
            }

            // Fan Control, Keyboard, Light Bar, Battery, CPU Temp Limit in footer for Uniwill EC hardware (XMG / TUXEDO)
            if (_uniwillEc is not null && _uniwillEc.Initialize())
            {
                NavigationItems.Add(CreateNavigationItem("Fan Control", "fancontrol", SymbolRegular.Gauge24, typeof(Views.Pages.FanControl)));
                NavigationItems.Add(CreateNavigationItem("Keyboard", "keyboard", SymbolRegular.Keyboard24, typeof(Views.Pages.Keyboard)));

                // Light Bar (HID lighting zone)
                var lightBarItem = CreateNavigationItem("Light Bar", "lightbar", SymbolRegular.Empty, typeof(Views.Pages.LightBar));
                lightBarItem.Icon = new Wpf.Ui.Controls.FontIcon { FontFamily = new System.Windows.Media.FontFamily("Segoe Fluent Icons"), Glyph = "\uED39", FontSize = 20 };
                NavigationItems.Add(lightBarItem);

                // Battery (charging mode + telemetry)
                var batteryItem = CreateNavigationItem("Battery", "battery", SymbolRegular.Empty, typeof(Views.Pages.Battery));
                batteryItem.Icon = new Wpf.Ui.Controls.FontIcon { FontFamily = new System.Windows.Media.FontFamily("Segoe Fluent Icons"), Glyph = "\uEBC0", FontSize = 20 };
                NavigationItems.Add(batteryItem);
            }

            NavigationItems.Add(CreateNavigationItem("Overlay", "overlay", SymbolRegular.DesktopPulse24, typeof(Views.Pages.OverlaySettingsPage)));
            NavigationItems.Add(CreateNavigationItem("Auto", "auto", SymbolRegular.Transmission24, typeof(Views.Pages.Automations)));

            // Settings at the bottom of footer
            NavigationFooter.Add(CreateNavigationItem("Settings", "settings", SymbolRegular.Settings24, typeof(Views.Pages.SettingsPage)));

            TrayMenuItems = new ObservableCollection<MenuItem>
            {
                new() { Header = "Home", Tag = "tray_home" }
            };
        }

        private static NavigationViewItem CreateNavigationItem(string content, string tag, SymbolRegular icon, Type pageType) =>
            new(content, icon, pageType) { TargetPageTag = tag };

        private void OnCultureChanged(object? sender, EventArgs e)
        {
            foreach (var item in NavigationItems.Concat(NavigationFooter).OfType<NavigationViewItem>())
            {
                var symbol = item.TargetPageTag switch
                {
                    "dashboard" => SymbolRegular.Home24,
                    "presets" => SymbolRegular.Predictions24,
                    "adaptive" => SymbolRegular.Radar20,
                    "overlay" => SymbolRegular.DesktopPulse24,
                    "auto" => SymbolRegular.Transmission24,
                    "fancontrol" => SymbolRegular.Gauge24,
                    "keyboard" => SymbolRegular.Keyboard24,
                    "battery" => SymbolRegular.Empty, // uses FontIcon, not SymbolIcon
                    "settings" => SymbolRegular.Settings24,
                    _ => SymbolRegular.Empty
                };

                if (symbol != SymbolRegular.Empty)
                {
                    item.Icon = new SymbolIcon { Symbol = symbol };
                }
            }

            RefreshLastAppliedSettings();
        }

        private void OnLastAppliedSettingsChanged(object? sender, EventArgs e)
        {
            RefreshLastAppliedSettings();
        }

        private void RefreshLastAppliedSettings()
        {
            var current = LastAppliedSettingsService.Current;
            HasLastAppliedSettings = current != null;
            if (current == null)
            {
                LastAppliedSettingsToolTip = string.Empty;
                return;
            }

            var lines = new List<string>
            {
                LocalizationService.Get("Last applied settings")
            };

            if (!string.IsNullOrWhiteSpace(current.PresetName))
            {
                var name = current.LocalizePresetName
                    ? LocalizationService.Get(current.PresetName)
                    : current.PresetName;
                lines.Add(LocalizationService.Format("Preset: {0}", name));
            }

            lines.Add(LocalizationService.Format("Arguments: {0}", current.Arguments));
            LastAppliedSettingsToolTip = string.Join(Environment.NewLine, lines);
        }

        private void OnNavigate(string? parameter)
        {
            switch (parameter)
            {
                case "download":
                    OpenUrl("https://github.com/jgudec/Uniwill-Control-Utility/releases");
                    break;
            }
        }

        /// <summary>
        /// Creates the Flydigi navigation item with the logo icon.
        /// Uses a FlydigiIconElement (a Path-based IconElement) whose Fill binds to the
        /// parent NavigationViewItem's Foreground, so it follows theme changes automatically.
        /// </summary>
        public static NavigationViewItem CreateFlydigiNavItem()
        {
            return new NavigationViewItem("Flydigi", SymbolRegular.Empty, typeof(Views.Pages.FlydigiCooler))
            {
                TargetPageTag = "flydigicooler",
                Icon = new FlydigiIconElement()
            };
        }

        private static void OpenUrl(string url) =>
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }
}
