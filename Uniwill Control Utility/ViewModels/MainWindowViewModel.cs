using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
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

            RefreshNavigationItems();

            // About in footer, above Settings
            createNavItem("About", "about", "\uE946", typeof(Views.Pages.AboutPage), true);

            // Settings at the bottom of footer
            createNavItem("Settings", "settings", "\uE713", typeof(Views.Pages.SettingsPage), true);
        }

        /// <summary>
        /// Restores service-level state after a feature's navigation toggle is switched ON.
        /// Call this from SettingsPage when a feature toggle is switched ON so that
        /// internal service state (e.g. keyboard hooks) is re-enabled.
        /// </summary>
        internal async Task EnableFeatureAsync(OptionalNavigationFeature feature)
        {
            switch (feature)
            {
                case OptionalNavigationFeature.Overlay:
                    // Re-enable the overlay: reinstalls the keyboard hook.
                    var overlayService = App.GetService<OverlayManagerService>();
                    if (overlayService != null)
                    {
                        var options = overlayService.CurrentOptions with { Enabled = true };
                        overlayService.ApplyOptions(options);
                    }
                    break;

                case OptionalNavigationFeature.HydroUi:
                    // Attempt to reconnect the watercooler when the feature is re-enabled.
                    var hydroUIService = App.GetService<HydroUIService>();
                    if (hydroUIService != null)
                    {
                        _ = hydroUIService.TryReconnectOnFeatureEnableAsync();
                    }
                    break;

                case OptionalNavigationFeature.Adaptive:
                case OptionalNavigationFeature.LightBar:
                case OptionalNavigationFeature.Auto:
                    // No re-enable action needed — these features don't hold
                    // internal "enabled" state that gets cleared on disable.
                    break;
            }
        }

        /// <summary>
        /// Performs service-level cleanup before hiding a feature's navigation item.
        /// Call this from SettingsPage when a feature toggle is switched OFF.
        /// </summary>
        internal async Task DisableFeature(OptionalNavigationFeature feature)
        {
            switch (feature)
            {
                case OptionalNavigationFeature.Adaptive:
                    // Lift all DeviceApplier overrides so the device pages regain control.
                    // This also restores user settings to hardware immediately.
                    var deviceApplier = App.GetService<DeviceApplier>();
                    if (deviceApplier != null)
                    {
                        if (deviceApplier.IsFlydigiOverridden)
                            _ = deviceApplier.DisableFlydigiOverrideAsync();
                        if (deviceApplier.IsWatercoolerOverridden)
                            _ = deviceApplier.DisableWatercoolerOverrideAsync();
                        if (deviceApplier.IsEcFanOverridden)
                            deviceApplier.DisableEcFanOverride();
                        if (deviceApplier.IsKeyboardOverridden)
                            deviceApplier.DisableKeyboardOverride();
                        if (deviceApplier.IsLightBarOverridden)
                            deviceApplier.DisableLightBarOverride();
                        // CPU/GPU override has no IsOverridden flag; Disable is idempotent
                        // (re-applies the last active preset profile from Settings).
                        _ = deviceApplier.DisableCpuGpuOverrideAsync();
                    }
                    break;

                case OptionalNavigationFeature.HydroUi:
                    // Disconnect the watercooler if connected.
                    var hydroUIService = App.GetService<HydroUIService>();
                    if (hydroUIService?.IsConnected == true)
                    {
                        hydroUIService.SetUserDisconnected();
                        await hydroUIService.DisconnectAsync();
                    }
                    break;

                case OptionalNavigationFeature.Overlay:
                    // Disable the overlay: removes keyboard hook, stops monitoring, hides window.
                    var overlayService = App.GetService<OverlayManagerService>();
                    if (overlayService != null)
                    {
                        var options = overlayService.CurrentOptions with { Enabled = false };
                        overlayService.ApplyOptions(options);
                    }
                    break;

                case OptionalNavigationFeature.LightBar:
                case OptionalNavigationFeature.Auto:
                    // Light Bar: HID handle is intentionally kept open; no cleanup needed.
                    // Auto: pure config page; guard is in MainWindow automation triggers.
                    break;
            }
        }

        public void RefreshNavigationItems()
        {
            var preferences = new FeatureNavigationPreferences(
                Settings.Default.EnableAdaptiveNavigation,
                Settings.Default.EnableHydroUiNavigation,
                Settings.Default.EnableLightBarNavigation,
                Settings.Default.EnableOverlayNavigation,
                Settings.Default.EnableAutoNavigation);
            bool hydroUiAvailable = WaterCoolerHardwareDetector.IsSupportedHardware();
            bool lightBarAvailable = _uniwillEc is not null;

            NavigationItems.Clear();

            createNavItem("Home", "dashboard", "\uE80F", typeof(Views.Pages.DashboardPage), false);

            createNavItem("Presets", "presets", "\uEF58", typeof(Views.Pages.Presets), false);

            if (FeatureNavigationPolicy.IsVisible(OptionalNavigationFeature.Adaptive, preferences, hydroUiAvailable, lightBarAvailable))
            {
                createNavItem("Adaptive", "adaptive", "\uEB44", typeof(Views.Pages.Adaptive), false);
            }

            if (FeatureNavigationPolicy.IsVisible(OptionalNavigationFeature.HydroUi, preferences, hydroUiAvailable, lightBarAvailable))
            {
                createNavItem("Hydro UI", "watercooler", "\uEB42", typeof(Views.Pages.HydroUI), false);
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

                createNavItem("Keyboard", "keyboard", "\uE765", typeof(Views.Pages.Keyboard), false);

                if (FeatureNavigationPolicy.IsVisible(OptionalNavigationFeature.LightBar, preferences, hydroUiAvailable, lightBarAvailable))
                {
                    createNavItem("Light Bar", "lightbar", "\uED39", typeof(Views.Pages.LightBar), false);
                }

                createNavItem("Battery", "battery", "\uEBC0", typeof(Views.Pages.Battery), false);
                createNavItem("Display", "display", "\uE7F8", typeof(Views.Pages.Display), false);
            }

            if (FeatureNavigationPolicy.IsVisible(OptionalNavigationFeature.Overlay, preferences, hydroUiAvailable, lightBarAvailable))
            {
                createNavItem("Overlay", "overlay", "\uE81E", typeof(Views.Pages.OverlaySettingsPage), false);
            }

            if (FeatureNavigationPolicy.IsVisible(OptionalNavigationFeature.Auto, preferences, hydroUiAvailable, lightBarAvailable))
            {
                createNavItem("Auto", "auto", "\uF277", typeof(Views.Pages.Automations), false);
            }
        }

        private static NavigationViewItem CreateNavigationItem(string content, string tag, SymbolRegular icon, Type pageType) =>
            new(content, icon, pageType) { TargetPageTag = tag };

        private void OnCultureChanged(object? sender, EventArgs e)
        {
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

        private void createNavItem(String title, String tag, String iconGlyph, Type pageType, Boolean isFooterItem)
        {
            var navItem = CreateNavigationItem(title, tag, SymbolRegular.Empty, pageType);
            navItem.Icon = new Wpf.Ui.Controls.FontIcon { FontFamily = new System.Windows.Media.FontFamily("Segoe Fluent Icons"), Glyph = iconGlyph, FontSize = 20 };
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
