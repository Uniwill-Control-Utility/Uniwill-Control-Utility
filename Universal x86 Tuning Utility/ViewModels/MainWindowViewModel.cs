using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Universal_x86_Tuning_Utility.Scripts;
using Universal_x86_Tuning_Utility.Services;
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

        [ObservableProperty]
        private bool _hasLastAppliedSettings;

        [ObservableProperty]
        private string _lastAppliedSettingsToolTip = string.Empty;

        private ICommand? _navigateCommand;

        public MainWindowViewModel(INavigationService navigationService)
        {
            InitializeViewModel();
            LocalizationService.CultureChanged += OnCultureChanged;
            LastAppliedSettingsService.Changed += OnLastAppliedSettingsChanged;
        }

        public ICommand NavigateCommand => _navigateCommand ??= new RelayCommand<string>(OnNavigate);

        private void InitializeViewModel()
        {
            ApplicationTitle = "Universal x86 Tuning Utility";

            NavigationItems = new ObservableCollection<object>
            {
                CreateNavigationItem("Home", "dashboard", SymbolRegular.Home24, typeof(Views.Pages.DashboardPage))
            };

            NavigationItems.Add(CreateNavigationItem("Premade", "premade", SymbolRegular.Predictions24, typeof(Views.Pages.Premade)));

            NavigationItems.Add(CreateNavigationItem("Custom", "custom", SymbolRegular.Book24, typeof(Views.Pages.CustomPresets)));
            NavigationItems.Add(CreateNavigationItem("Adaptive", "adaptive", SymbolRegular.Radar20, typeof(Views.Pages.Adaptive)));
            NavigationItems.Add(CreateNavigationItem("Games", "games", SymbolRegular.Games24, typeof(Views.Pages.Games)));
            NavigationItems.Add(CreateNavigationItem("Overlay", "overlay", SymbolRegular.DesktopPulse24, typeof(Views.Pages.OverlaySettingsPage)));
            NavigationItems.Add(CreateNavigationItem("Auto", "auto", SymbolRegular.Transmission24, typeof(Views.Pages.Automations)));

            NavigationFooter = new ObservableCollection<object>();

            // Hydro UI in footer for supported watercooler hardware
            if (WaterCoolerHardwareDetector.IsSupportedHardware())
            {
                NavigationFooter.Add(CreateNavigationItem("Hydro UI", "watercooler", SymbolRegular.Water24, typeof(Views.Pages.Watercooler)));
            }

            // Flydigi cooler in footer
            NavigationFooter.Add(CreateFlydigiNavItem());

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
                    "premade" => SymbolRegular.Predictions24,
                    "custom" => SymbolRegular.Book24,
                    "adaptive" => SymbolRegular.Radar20,
                    "games" => SymbolRegular.Games24,
                    "overlay" => SymbolRegular.DesktopPulse24,
                    "auto" => SymbolRegular.Transmission24,
                    "watercooler" => SymbolRegular.Water24,
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
                    OpenUrl("https://github.com/JamesCJ60/Universal-x86-Tuning-Utility/releases");
                    break;
            }
        }

        /// <summary>
        /// Creates the Flydigi navigation item with the logo icon.
        /// Uses a FlydigiIconElement (a Path-based IconElement) whose Fill binds to the
        /// parent NavigationViewItem's Foreground, so it follows theme changes automatically.
        /// </summary>
        private static NavigationViewItem CreateFlydigiNavItem()
        {
            return new NavigationViewItem("Flydigi", SymbolRegular.Empty, typeof(Views.Pages.FlydigiCooler))
            {
                TargetPageTag = "flydigicooler",
                Icon = new FlydigiIconElement()
            };
        }

        /// <summary>
        /// A Path-based IconElement for the Flydigi logo. The Fill binds to the inherited
        /// Foreground from the parent NavigationViewItem, so it follows theme changes.
        /// </summary>
        private sealed class FlydigiIconElement : IconElement
        {
            private readonly Path _path;

            public FlydigiIconElement()
            {
                var geometry = Geometry.Parse(
                    "M19.015,7.83 L0,0 L15.659,23.488 L16.777,21.251 L10.066,10.066 L15.659,11.185 " +
                    "L20.287,18.706 L16.777,25.726 L22.369,40.267 L27.962,25.726 L25.609,21.02 " +
                    "L23.645,24.163 L24.607,25.726 L22.369,40.267 L20.132,25.726 L29.079,11.185 " +
                    "L34.673,10.066 L27.962,21.251 L29.08,23.488 L45,0 L25.985,7.829 " +
                    "L22.63,14.54 L19.274,7.83 Z");

                _path = new Path
                {
                    Data = geometry,
                    Stretch = Stretch.Uniform,
                    Width = 24,
                    Height = 24,
                    SnapsToDevicePixels = true
                };
            }

            protected override UIElement InitializeChildren()
            {
                _path.SetBinding(Path.FillProperty, new Binding("Foreground")
                {
                    RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(NavigationViewItem), 1),
                    Mode = BindingMode.OneWay
                });
                return _path;
            }
        }

        private static void OpenUrl(string url) =>
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }
}
