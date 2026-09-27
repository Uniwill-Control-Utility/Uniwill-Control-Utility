using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
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
        private bool _hasLastAppliedSettings;

        [ObservableProperty]
        private string _lastAppliedSettingsToolTip = string.Empty;

        private readonly UniwillECService? _uniwillEc;

        public MainWindowViewModel(INavigationService navigationService, UniwillECService? uniwillEc = null)
        {
            _uniwillEc = uniwillEc;
            RyzenAdj_To_UCU.EcService = uniwillEc;
            InitializeViewModel();
            LocalizationService.CultureChanged += OnCultureChanged;
            LastAppliedSettingsService.Changed += OnLastAppliedSettingsChanged;
        }

        

        private void InitializeViewModel()
        {
            ApplicationTitle = "Uniwill Control Utility";

            NavigationItems = new ObservableCollection<object> { };

            createNavItem("Home", "dashboard", SymbolRegular.Home24, typeof(Views.Pages.DashboardPage), false);

            createNavItem("Presets", "presets", SymbolRegular.Predictions24, typeof(Views.Pages.Presets), false);

            createNavItem("Adaptive", "adaptive", SymbolRegular.Radar20, typeof(Views.Pages.Adaptive), false);


            // Hydro UI in footer for supported watercooler hardware
            if (WaterCoolerHardwareDetector.IsSupportedHardware())
            {
                createNavItem("Hydro UI", "watercooler", SymbolRegular.Water24, typeof(Views.Pages.Watercooler), false);
            }

            // Flydigi cooler in footer (hidden if user has disabled it in Settings)
            if (Settings.Default.EnableFlydigi)
            {
                NavigationItems.Add(CreateFlydigiNavItem());
            }

            // Fan Control, Keyboard, Light Bar, Battery in nav for Uniwill EC hardware (XMG / TUXEDO).
            // EC initialization runs asynchronously in DashboardViewModel.OnNavigatedToAsync(),
            // so we don't block MainWindow construction waiting for the EC handshake.
            if (_uniwillEc is not null)
            {
                NavigationItems.Add(CreateFanControlNavItem());

                createNavItem("Keyboard", "keyboard", SymbolRegular.Keyboard24, typeof(Views.Pages.Keyboard), false);

                createNavItem("Light Bar", "lightbar", SymbolRegular.Lightbulb24, typeof(Views.Pages.LightBar), false);

                createNavItem("Battery", "battery", SymbolRegular.BatteryCharge24, typeof(Views.Pages.Battery), false);
            }

            createNavItem("Overlay", "overlay", SymbolRegular.DesktopPulse24, typeof(Views.Pages.OverlaySettingsPage), false);

            createNavItem("Auto", "auto", SymbolRegular.Transmission24, typeof(Views.Pages.Automations), false);

            // About in footer, above Settings
            createNavItem("About", "about", SymbolRegular.Info24, typeof(Views.Pages.AboutPage), true);

            // Settings at the bottom of footer
            createNavItem("Settings", "settings", SymbolRegular.Settings24, typeof(Views.Pages.SettingsPage), true);
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
                    "fancontrol" => SymbolRegular.Empty, // uses FanIconElement, not SymbolIcon
                    "keyboard" => SymbolRegular.Keyboard24,
                    "lightbar" => SymbolRegular.Lightbulb24,
                    "battery" => SymbolRegular.BatteryCharge24,
                    "watercooler" => SymbolRegular.Water24,
                    "about" => SymbolRegular.Info24,
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

        /// <summary>
        /// Creates the Fan Control navigation item with a custom fan icon.
        /// Uses a FanIconElement (a Path-based IconElement) whose Fill binds to the
        /// parent NavigationViewItem's Foreground, so it follows theme changes automatically.
        /// </summary>
        public static NavigationViewItem CreateFanControlNavItem()
        {
            return new NavigationViewItem("Fans", SymbolRegular.Empty, typeof(Views.Pages.FanControl))
            {
                TargetPageTag = "fancontrol",
                Icon = new Views.Controls.FanIconElement()
            };
        }

        private void createNavItem(String title, String tag, SymbolRegular icon, Type pageType, Boolean isFooterItem)
        {
            var navItem = CreateNavigationItem(title, tag, icon, pageType);
            if (isFooterItem)
            {
                NavigationFooter.Add(navItem);
            }
            else
            {
                NavigationItems.Add(navItem);
            }
        }
    }
}
