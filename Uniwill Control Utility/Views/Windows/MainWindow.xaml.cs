using Accord.Math.Distances;
using AutoOC.Controllers;
using AutoOC.Monitors;
using DuoVia.FuzzyStrings;
using GameLib.Plugin.RiotGames.Model;
using Gma.System.MouseKeyHook;
using HidSharp.Utility;
using Microsoft.Win32;
using RyzenSmu;
using Serilog;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using MenuItem = System.Windows.Controls.MenuItem;
using Uniwill_Control_Utility.Models;
using Uniwill_Control_Utility.Properties;
using Uniwill_Control_Utility.Scripts;
using Uniwill_Control_Utility.Scripts.Intel_Backend;
using Uniwill_Control_Utility.Scripts.Misc;
using Uniwill_Control_Utility.Scripts.UCU_Super_Resolution;
using Uniwill_Control_Utility.Services;
using Wpf.Ui.Controls;
using Wpf.Ui;
using Wpf.Ui.Abstractions;
using static Uniwill_Control_Utility.Scripts.Game_Manager;
using Application = System.Windows.Application;
using MessageBox = System.Windows.Forms.MessageBox;
using Settings = Uniwill_Control_Utility.Properties.Settings;

namespace Uniwill_Control_Utility.Views.Windows
{
    public partial class MainWindow : INavigationWindow
    {
        public ViewModels.MainWindowViewModel ViewModel { get; set; }

        DispatcherTimer Misc = new DispatcherTimer();
        public DispatcherTimer autoReapply = new DispatcherTimer();
        public DispatcherTimer autoRestore = new DispatcherTimer();

        public static bool isMini { get; private set; }
        public static NavigationView _mainWindowNav;
        public static MainWindow? Instance { get; private set; }
        private static INavigationService _navigationService;
        private bool _isTrayPresetApplying;
        public static bool IsPageSelected(Type pageType) =>
            _mainWindowNav?.SelectedItem is INavigationViewItem item && item.TargetPageType == pageType;

        public MainWindow(ViewModels.MainWindowViewModel viewModel, INavigationViewPageProvider pageProvider, INavigationService navigationService)
        {
            ViewModel = viewModel;
            DataContext = this;
            InitializeComponent();

            _navigationService = navigationService;
            _mainWindowNav = RootNavigation;

            Misc.Interval = TimeSpan.FromSeconds(1);
            Misc.Tick += Misc_Tick;
            Misc.Start();

            autoReapply.Interval = TimeSpan.FromSeconds(Settings.Default.AutoReapplyTime);
            autoReapply.Tick += AutoReapply_Tick;
            autoReapply.Start();

            autoRestore.Interval = TimeSpan.FromSeconds(1);
            autoRestore.Tick += Controller.AutoRestore_Tick;
            autoRestore.Start();

            SetupNavigationService(pageProvider);
            SetupUI();
            ApplyOnStart();

            SystemEvents.PowerModeChanged += HandlePowerModeChange;

            Instance = this;

            // Ensure tray icon is visible
            niTray.Visibility = Visibility.Visible;
        }

        /// <summary>Restores the window when the tray icon is left-clicked.</summary>
        private void NotifyIcon_LeftClick(Wpf.Ui.Tray.Controls.NotifyIcon sender, RoutedEventArgs e)
        {
            if (this.Visibility != Visibility.Visible || this.WindowState == WindowState.Minimized)
            {
                this.Show();
                this.WindowState = WindowState.Normal;
                this.Activate();
            }
        }

        private void TrayMenu_Opened(object sender, RoutedEventArgs e)
        {
            // Sync all tray sections with current page state
            RefreshTrayStockPresetChecks();
            PopulateTrayCustomPresets();
            SyncTrayHydroChecks();
            SyncTrayFlydigiChecks();
            SyncTrayFanControlChecks();
            SyncTrayFlydigiCurveChecks();
            UpdateTrayFanProfileVisibility();

            // Hide Flydigi section if disabled in Settings
            miSectionFlydigi.Visibility = Settings.Default.HideFlydigi ? Visibility.Collapsed : Visibility.Visible;

            // Disable/hide tray controls when Adaptive overrides are active
            UpdateTrayAdaptiveOverrideState();

            // Elevate tray menu above the auto-hiding taskbar.
            // WPF's ContextMenu uses a Popup that doesn't inherit WS_EX_TOPMOST,
            // so the menu can appear behind the taskbar when it auto-shows.
            // We find the menu's HWND and call SetWindowPos with HWND_TOPMOST.
            try
            {
                // Use a small delay to ensure the Popup's HWND is created
                Dispatcher.BeginInvoke(
                    DispatcherPriority.ContextIdle,
                    new Action(() =>
                    {
                        try
                        {
                            var hwnd = GetContextMenuHwnd(cmTrayMenu);
                            if (hwnd != IntPtr.Zero)
                            {
                                const int HWND_TOPMOST = -1;
                                const int SWP_NOMOVE = 0x0002;
                                const int SWP_NOSIZE = 0x0001;
                                const int SWP_NOACTIVATE = 0x0010;
                                NativeSetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
                            }
                        }
                        catch { /* Non-critical: SetWindowPos failure does not affect tray menu functionality */ }
                    }));
            }
            catch { /* Non-critical: BeginInvoke failure does not affect tray menu functionality */ }
        }

        /// <summary>
        /// Finds the HWND of a WPF ContextMenu by walking the visual tree for a Popup.
        /// </summary>
        private static IntPtr GetContextMenuHwnd(System.Windows.Controls.ContextMenu menu)
        {
            if (menu == null) return IntPtr.Zero;

            // The ContextMenu's Popup is created as a child of the menu in the logical tree.
            // We use HwndSource to find the window handle.
            var popup = menu.Template?.FindName("PART_Popup", menu) as System.Windows.Controls.Primitives.Popup;
            if (popup == null)
            {
                // Fallback: try to find the Popup via the visual tree
                for (int i = 0; i < VisualTreeHelper.GetChildrenCount(menu); i++)
                {
                    var child = VisualTreeHelper.GetChild(menu, i);
                    if (child is System.Windows.Controls.Primitives.Popup p)
                    {
                        popup = p;
                        break;
                    }
                }
            }

            if (popup != null && popup.Child != null)
            {
                var source = PresentationSource.FromVisual(popup.Child) as HwndSource;
                if (source != null)
                    return source.Handle;
            }

            // Alternative: the ContextMenu itself might have a PresentationSource
            var source2 = PresentationSource.FromVisual(menu) as HwndSource;
            return source2?.Handle ?? IntPtr.Zero;
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool NativeSetWindowPos(IntPtr hWnd, int hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        private void SetupNavigationService(INavigationViewPageProvider pageProvider)
        {
            _navigationService.SetNavigationControl(RootNavigation);
            RootNavigation.SetPageProviderService(pageProvider);
            RootNavigation.SelectionChanged += OnNavigationSelectionChanged;
        }

        private void OnNavigationSelectionChanged(INavigationView sender, RoutedEventArgs e)
        {
            bool isDashboard = IsPageSelected(typeof(Views.Pages.DashboardPage));
            ModelNameText.Visibility = isDashboard ? Visibility.Collapsed : Visibility.Visible;
        }

        private void SetupUI()
        {
            ModelNameText.Text = "Uniwill Control Utility";

            tbMain.Title = "Uniwill Control Utility";

            try
            {
                Controller.SetUpMagWindow(this);
            }
            catch (Exception ex)
            {
                Log.Logger.Error(ex, "Failed to setup MagWindow");
            }

             Wpf.Ui.Appearance.SystemThemeWatcher.Watch(this, WindowBackdropType.Mica, true);
        }

        private async void ApplyOnStart()
        {
            try
            {
                if (Settings.Default.ApplyOnStart)
                {
                    // If the last selected profile was Custom, apply from CustomPresetStore.
                    if (Settings.Default.presetProfile == "Custom")
                    {
                        await Task.Run(() => GetBatteryStatus());

                        var presets = Models.CustomPresetStore.Load();
                        // Find the first slot with saved data (slot 0 = Profile 1 is default)
                        int slot = 0;
                        for (int i = 0; i < presets.Length; i++)
                        {
                            if (presets[i].HasChanges)
                            {
                                slot = i;
                                break;
                            }
                        }

                        var p = presets[slot];
                        if (p.HasChanges)
                        {
                            // Build command string from the preset values
                            string command = BuildCustomCommandString(p);
                            if (!string.IsNullOrWhiteSpace(command))
                            {
                                await ApplyCustomOnStart(command, p);
                                ToastNotification.ShowToastNotification("Custom Preset Applied",
                                    $"Your Custom preset (Profile {slot + 1}) has been applied on startup!");
                            }
                        }
                        return;
                    }

                    // Legacy path: CommandString from Automations / tray menu
                    if (Settings.Default.CommandString != null && Settings.Default.CommandString != "")
                    {
                        await Task.Run(() => GetBatteryStatus());

                        if (statuscode == BatteryStatusAc || statuscode == BatteryStatusCritical
                            || statuscode == BatteryStatusCharging || statuscode == BatteryStatusNoSystemBattery)
                        {
                            if (Settings.Default.acPreset != "None" && !string.IsNullOrEmpty(Settings.Default.acPreset))
                            {
                                Settings.Default.acCommandString = PresetProfileApplier.BuildCommandString(
                                    ResolveAutomationProfile(Settings.Default.acPreset));
                                Settings.Default.CommandString = Settings.Default.acCommandString;
                                Settings.Default.Save();
                                await TranslatePresetAsync(Settings.Default.acCommandString, Settings.Default.acPreset);
                                ToastNotification.ShowToastNotification("Charge Preset Applied!", $"Your charge preset settings have been applied!");
                            }
                            else
                            {
                                await RyzenAdj_To_UCU.TranslateAsync(Settings.Default.CommandString);
                                ToastNotification.ShowToastNotification("Settings Reapplied!", $"Your last applied settings have been reapplied!");
                            }
                        }
                        else
                        {
                            if (Settings.Default.dcPreset != "None" && !string.IsNullOrEmpty(Settings.Default.dcPreset))
                            {
                                Settings.Default.dcCommandString = PresetProfileApplier.BuildCommandString(
                                    ResolveAutomationProfile(Settings.Default.dcPreset));
                                Settings.Default.CommandString = Settings.Default.dcCommandString;
                                Settings.Default.Save();
                                await TranslatePresetAsync(Settings.Default.dcCommandString, Settings.Default.dcPreset);
                                ToastNotification.ShowToastNotification("Discharge Preset Applied!", $"Your discharge preset settings have been applied!");
                            }
                            else
                            {
                                await RyzenAdj_To_UCU.TranslateAsync(Settings.Default.CommandString);
                                ToastNotification.ShowToastNotification("Settings Reapplied!", $"Your last applied settings have been reapplied!");
                            }
                        }
                    }
                }

                // Apply keyboard RGB settings on startup (runs on background thread)
                Task.Run(ApplyKeyboardOnStart);
            }
            catch (Exception ex)
            {
                Log.Logger.Error(ex, "Failed to apply settings on start");
            }
        }

        /// <summary>Re-applies keyboard RGB after system resume (HID device loses state after hibernate).</summary>
        private void ApplyKeyboardOnResume()
        {
            try
            {
                var hidService = new KeyboardHidService();
                if (!hidService.Open())
                {
                    hidService.Dispose();
                    return;
                }

                var settings = KeyboardSettingsService.Load();

                if (settings.PowerOn)
                {
                    if (settings.PerKeyMode)
                    {
                        int brightnessPercent = (settings.Brightness * 100) / 7;
                        hidService.TurnOn(settings.ColorR, settings.ColorG, settings.ColorB, brightnessPercent);
                        hidService.SetEffect(Uniwill_Control_Utility.Models.KeyboardEffect.Static);
                        var colors = settings.GetPerKeyColors();
                        hidService.SendAllPerKeyColorsFromDict(colors);
                        System.Diagnostics.Debug.WriteLine("[KBD] Resume apply: per-key mode");
                    }
                    else
                    {
                        int brightnessPercent = (settings.Brightness * 100) / 7;
                        byte speed = (byte)Math.Clamp((int)settings.Speed, 0, 11);
                        if (settings.Direction != KeyboardDirection.LeftRight)
                        {
                            System.Threading.Thread.Sleep(500);
                        }
                        hidService.TurnOnWithEffect(settings.ColorR, settings.ColorG, settings.ColorB,
                            brightnessPercent, settings.EffectMode, speed, settings.Direction);

                        if (Uniwill_Control_Utility.Views.Pages.Keyboard.IsMultiColor7Effect(settings.EffectMode))
                        {
                            var colors = ParseKeyboardColorString(settings.MultiColors, 7);
                            if (colors.Count > 0)
                            {
                                if (settings.EffectMode == Uniwill_Control_Utility.Models.KeyboardEffect.Marquee)
                                    colors = colors.AsEnumerable().Reverse().ToList();
                                hidService.SetMultiColor(colors);
                            }
                        }
                        else if (Uniwill_Control_Utility.Views.Pages.Keyboard.IsMultiColor4Effect(settings.EffectMode))
                        {
                            var colors = ParseKeyboardColorString(settings.MultiColors, 4);
                            if (colors.Count > 0)
                                hidService.SetMultiColor(colors);
                        }
                        else if (Uniwill_Control_Utility.Views.Pages.Keyboard.IsMultiColor4Plus1Effect(settings.EffectMode))
                        {
                            var colors = ParseKeyboardColorString(settings.MultiColors, 4);
                            if (colors.Count > 0)
                                hidService.SetMultiColor(colors);
                        }

                        System.Diagnostics.Debug.WriteLine($"[KBD] Resume apply: effect {settings.EffectMode}");
                    }
                }
                else
                {
                    hidService.TurnOff();
                }

                hidService.Dispose();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[KBD] Resume apply failed: {ex.Message}");
            }
        }

        /// <summary>Re-applies light bar settings after system resume (HID device loses state after hibernate).</summary>
        private void ApplyLightBarOnResume()
        {
            try
            {
                var hidService = new KeyboardHidService();
                if (!hidService.Open())
                {
                    hidService.Dispose();
                    return;
                }

                // Only apply if the device has a light bar zone
                if (!hidService.IsLightBarAvailable)
                {
                    hidService.Dispose();
                    return;
                }

                bool enabled = Settings.Default.LightBarEnabled;
                byte r = Settings.Default.LightBarR;
                byte g = Settings.Default.LightBarG;
                byte b = Settings.Default.LightBarB;
                int brightness = Settings.Default.LightBarBrightness;
                int effectIndex = Settings.Default.LightBarEffectIndex;
                int speed = Settings.Default.LightBarSpeed;

                if (enabled)
                {
                    // Light bar effects mapping (same as LightBar page's Effects array)
                    byte effect = effectIndex switch
                    {
                        0 => 0x01, // Static
                        1 => 0x02, // Breathing
                        2 => 0x03, // Wave
                        3 => 0x04, // Loading
                        4 => 0x05, // Marquee
                        5 => 0x06, // Meteor
                        _ => 0x01
                    };
                    // Light bar speed is inverted: UI 0-11 (slow?fast), HID 0-11 (fast?slow)
                    byte hidSpeed = (byte)(11 - Math.Clamp(speed, 0, 11));

                    System.Threading.Thread.Sleep(200);
                    hidService.LightBarTurnOn(r, g, b, brightness);
                    hidService.LightBarSetEffect(effect, hidSpeed, brightness);
                    System.Diagnostics.Debug.WriteLine($"[LB] Resume apply: enabled, color=({r},{g},{b}), brightness={brightness}, effect=0x{effect:X2}");
                }
                else
                {
                    hidService.LightBarTurnOff();
                    System.Diagnostics.Debug.WriteLine("[LB] Resume apply: disabled");
                }

                hidService.Dispose();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LB] Resume apply failed: {ex.Message}");
            }
        }

        /// <summary>Applies persisted keyboard RGB settings at app startup.</summary>
        private void ApplyKeyboardOnStart()
        {
            try
            {
                var hidService = new KeyboardHidService();
                if (!hidService.Open())
                {
                    hidService.Dispose();
                    return;
                }

                var settings = KeyboardSettingsService.Load();

                if (settings.PowerOn)
                {
                    if (settings.PerKeyMode)
                    {
                        // Per-key mode: apply saved per-key colors
                        int brightnessPercent = (settings.Brightness * 100) / 7;
                        hidService.TurnOn(settings.ColorR, settings.ColorG, settings.ColorB, brightnessPercent);
                        hidService.SetEffect(Uniwill_Control_Utility.Models.KeyboardEffect.Static);

                        var colors = settings.GetPerKeyColors();
                        hidService.SendAllPerKeyColorsFromDict(colors);

                        System.Diagnostics.Debug.WriteLine("[KBD] Startup apply: per-key mode");
                    }
                    else
                    {
                        // Effects mode: apply saved effect settings using TurnOnWithEffect
                        // to combine power-on and effect into one sequence, avoiding a
                        // visible flash of intermediate Static color.
                        // The ITE firmware needs ~500ms to settle after the HID handle
                        // is opened before it accepts direction bytes. Without this delay
                        // the direction silently falls back to Left?Right.
                        int brightnessPercent = (settings.Brightness * 100) / 7;
                        byte speed = (byte)Math.Clamp((int)settings.Speed, 0, 11);
                        if (settings.Direction != KeyboardDirection.LeftRight)
                        {
                            System.Threading.Thread.Sleep(500);
                        }
                        hidService.TurnOnWithEffect(settings.ColorR, settings.ColorG, settings.ColorB,
                            brightnessPercent, settings.EffectMode, speed, settings.Direction);

                        // Send multi-color palette for effects that need it
                        if (Uniwill_Control_Utility.Views.Pages.Keyboard.IsMultiColor7Effect(settings.EffectMode))
                        {
                            var colors = ParseKeyboardColorString(settings.MultiColors, 7);
                            if (colors.Count > 0)
                            {
                                // Marquee displays colors in reverse order on the HID controller
                                if (settings.EffectMode == Uniwill_Control_Utility.Models.KeyboardEffect.Marquee)
                                    colors = colors.AsEnumerable().Reverse().ToList();
                                hidService.SetMultiColor(colors);
                            }
                        }
                        else if (Uniwill_Control_Utility.Views.Pages.Keyboard.IsMultiColor4Effect(settings.EffectMode))
                        {
                            var colors = ParseKeyboardColorString(settings.MultiColors, 4);
                            if (colors.Count > 0)
                                hidService.SetMultiColor(colors);
                        }
                        else if (Uniwill_Control_Utility.Views.Pages.Keyboard.IsMultiColor4Plus1Effect(settings.EffectMode))
                        {
                            var colors = ParseKeyboardColorString(settings.MultiColors, 4);
                            if (colors.Count > 0)
                                hidService.SetMultiColor(colors);
                        }

                        System.Diagnostics.Debug.WriteLine($"[KBD] Startup apply: effect {settings.EffectMode}");
                    }
                }
                else
                {
                    hidService.TurnOff();
                }

                hidService.Dispose();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[KBD] Startup apply failed: {ex.Message}");
            }
        }

        /// <summary>Parses a comma-separated hex color string, padded to the requested count.</summary>
        private static List<System.Windows.Media.Color> ParseKeyboardColorString(string data, int count)
        {
            var defaults = new[]
            {
                System.Windows.Media.Color.FromRgb(255, 0, 0),
                System.Windows.Media.Color.FromRgb(255, 123, 0),
                System.Windows.Media.Color.FromRgb(255, 183, 0),
                System.Windows.Media.Color.FromRgb(0, 255, 0),
                System.Windows.Media.Color.FromRgb(0, 255, 255),
                System.Windows.Media.Color.FromRgb(0, 0, 255),
                System.Windows.Media.Color.FromRgb(139, 0, 255),
            };

            var result = new List<System.Windows.Media.Color>();
            if (!string.IsNullOrEmpty(data))
            {
                foreach (var part in data.Split(',', System.StringSplitOptions.RemoveEmptyEntries))
                {
                    try
                    {
                        var hex = part.Trim();
                        if (hex.StartsWith("#")) hex = hex.Substring(1);
                        if (hex.Length == 6)
                        {
                            var r = byte.Parse(hex.Substring(0, 2), System.Globalization.NumberStyles.HexNumber);
                            var g = byte.Parse(hex.Substring(2, 2), System.Globalization.NumberStyles.HexNumber);
                            var b = byte.Parse(hex.Substring(4, 2), System.Globalization.NumberStyles.HexNumber);
                            result.Add(System.Windows.Media.Color.FromRgb(r, g, b));
                        }
                    }
                    catch { /* Skip invalid color entries - malformed hex values are ignored */ }
                }
            }

            // Pad with defaults
            while (result.Count < count)
                result.Add(defaults[result.Count % defaults.Length]);

            return result.Take(count).ToList();
        }


        InstabilityMonitor monitor = null;

        AdaptiveUndervoltController iGpuController = null;

        int lastiGPUUVOffset = 0;
        private async void Misc_Tick(object sender, EventArgs e)
        {
            try
            {
                if (Settings.Default.isAutoUviGPU)
                {
                    if (monitor == null) monitor = new InstabilityMonitor();

                    if(iGpuController == null) iGpuController = new AdaptiveUndervoltController(
                        monitor,
                        minOffset: -50,
                        stepSize: 1,
                        stableThreshold: 8,
                        cooldownThreshold: 4,
                        isIgpu : true
                    );

                    var newOffset = iGpuController.UpdateOffset();
                    var commandValues = "";

                    if (newOffset >= 0) commandValues = commandValues + $"--set-cogfx={newOffset} ";
                    if (newOffset < 0) commandValues = commandValues + $"--set-cogfx={Convert.ToUInt32(0x100000 - (uint)(-1 * (int)newOffset))} ";

                    if (lastiGPUUVOffset != newOffset)
                    {
                        await RyzenAdj_To_UCU.TranslateAsync(commandValues, false, true);
                        lastiGPUUVOffset = newOffset;
                    }

                    iGpuController.RecordAppliedOffset(newOffset);
                }
                else if (!Settings.Default.isAutoUviGPU && iGpuController != null)
                {
                    iGpuController.Dispose();
                    iGpuController = null;
                }

                if (!Settings.Default.isAutoUvCPU && !Settings.Default.isAutoUviGPU && monitor != null)
                {
                    monitor.Stop();
                    monitor = null;
                }
            }
            catch (Exception ex)
            {
                DiagnosticLogger.LogError(ex, "Failed during adaptive undervolt monitoring tick");
            }
        }

        private static ushort statuscode;

        // Win32_Battery.BatteryStatus values that indicate AC-powered state
        private const ushort BatteryStatusAc = 2;
        private const ushort BatteryStatusCritical = 6;
        private const ushort BatteryStatusCharging = 7;
        private const ushort BatteryStatusNoSystemBattery = 8;

        public static void GetBatteryStatus()
        {
            try
            {
                var batteryClass = new ManagementClass("Win32_Battery");
                var batteries = batteryClass.GetInstances();

                foreach (var battery in batteries)
                {
                    statuscode = (ushort)battery["BatteryStatus"];
                }
            }
            catch (Exception ex)
            {
                // Handle exceptions if necessary
                DiagnosticLogger.LogError(ex, "Failed to get battery status");
            }
        }

        private async void AutoReapply_Tick(object sender, EventArgs e)
        {
            try
            {
                if (Settings.Default.AutoReapply == true && Settings.Default.isAdaptiveModeRunning == false)
                {
                    string? commands = Settings.Default.CommandString;
                    //Check if RyzenAdjArguments is populated
                    if (commands != null && commands != "")
                    {
                        await RyzenAdj_To_UCU.TranslateAsync(commands);
                    }

                    if (autoReapply.Interval != TimeSpan.FromSeconds(Settings.Default.AutoReapplyTime))
                    {
                        autoReapply.Stop();
                        autoReapply.Interval = TimeSpan.FromSeconds(Settings.Default.AutoReapplyTime);
                        autoReapply.Start();
                    }
                }
            }
            catch (Exception ex)
            {
                DiagnosticLogger.LogError(ex, "Failed during auto-reapply tick");
            }
        }

        static string lastAppliedState = "";
        private async void HandlePowerModeChange(object sender, PowerModeChangedEventArgs e)
        {
            try
            {
                if (Settings.Default.isAdaptiveModeRunning == false)
                {
                    if (e.Mode == PowerModes.StatusChange)
                    {
                        await Task.Run(() => GetBatteryStatus());

                        if (statuscode == BatteryStatusAc || statuscode == BatteryStatusCritical
                            || statuscode == BatteryStatusCharging || statuscode == BatteryStatusNoSystemBattery)
                        {
                            if (Settings.Default.acPreset != "None" && !string.IsNullOrEmpty(Settings.Default.acPreset))
                            {
                                Settings.Default.acCommandString = PresetProfileApplier.BuildCommandString(
                                    ResolveAutomationProfile(Settings.Default.acPreset));
                                Settings.Default.CommandString = Settings.Default.acCommandString;
                                Settings.Default.Save();
                                await TranslatePresetAsync(Settings.Default.acCommandString, Settings.Default.acPreset);

                                if (lastAppliedState != "ac") ToastNotification.ShowToastNotification("Charge Preset Applied!", $"Your charge preset settings have been applied!");
                                lastAppliedState = "ac";
                            }
                        }
                        else
                        {
                            if (Settings.Default.dcPreset != "None" && !string.IsNullOrEmpty(Settings.Default.dcPreset))
                            {
                                Settings.Default.dcCommandString = PresetProfileApplier.BuildCommandString(
                                    ResolveAutomationProfile(Settings.Default.dcPreset));
                                Settings.Default.CommandString = Settings.Default.dcCommandString;
                                Settings.Default.Save();
                                await TranslatePresetAsync(Settings.Default.dcCommandString, Settings.Default.dcPreset);

                                if (lastAppliedState != "dc") ToastNotification.ShowToastNotification("Discharge Preset Applied!", $"Your discharge preset settings have been applied!");
                                lastAppliedState = "dc";
                            }
                        }
                    }

                    if (e.Mode == PowerModes.Resume)
                    {
                        if (Settings.Default.resumePreset != "None" && !string.IsNullOrEmpty(Settings.Default.resumePreset))
                        {
                            Settings.Default.resumeCommandString = PresetProfileApplier.BuildCommandString(
                                ResolveAutomationProfile(Settings.Default.resumePreset));
                            Settings.Default.CommandString = Settings.Default.resumeCommandString;
                            Settings.Default.Save();
                            _ = TranslatePresetAsync(Settings.Default.resumeCommandString, Settings.Default.resumePreset);

                            if (lastAppliedState != "resume") ToastNotification.ShowToastNotification("Resume Preset Applied!", $"Your resume preset settings have been applied!");
                            lastAppliedState = "resume";
                        }

                        // Re-apply settings that are lost after hibernate/sleep:
                        // - Keyboard RGB (HID device loses state)
                        // - Light Bar (HID device loses state)
                        // - Battery charge limit (EC may revert)
                        // - Fan Control EC duties (EC may revert)
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                // Give the system a moment to reinitialize after resume
                                await Task.Delay(1500);

                                // Re-apply Keyboard RGB
                                try
                                {
                                    ApplyKeyboardOnResume();
                                }
                                catch (Exception ex)
                                {
                                    Debug.WriteLine($"[Resume] Keyboard re-apply failed: {ex.Message}");
                                }

                                // Re-apply Light Bar
                                try
                                {
                                    ApplyLightBarOnResume();
                                }
                                catch (Exception ex)
                                {
                                    Debug.WriteLine($"[Resume] Light Bar re-apply failed: {ex.Message}");
                                }

                                // Re-apply Battery charge limit
                                try
                                {
                                    var ecService = App.GetService<UniwillECService>();
                                    if (ecService != null && ecService.Initialize())
                                    {
                                        int savedMode = Settings.Default.BatteryMode;
                                        int currentMode = ecService.GetBatteryMode();
                                        if (savedMode != currentMode)
                                        {
                                            ecService.SetBatteryMode(savedMode);
                                            Debug.WriteLine($"[Resume] Battery mode reverted from {currentMode} to saved {savedMode}");
                                        }
                                    }
                                }
                                catch (Exception ex)
                                {
                                    Debug.WriteLine($"[Resume] Battery re-apply failed: {ex.Message}");
                                }

                                // Re-apply Fan Control EC duties
                                try
                                {
                                    var ecService = App.GetService<UniwillECService>();
                                    if (ecService != null)
                                    {
                                        var fanSettings = FanControlSettingsService.Load();
                                        EcFanCurve cpuCurve;
                                        EcFanCurve gpuCurve;

                                        if (fanSettings.UnifiedMode)
                                        {
                                            cpuCurve = GetFanPresetCurve(fanSettings.UnifiedPreset);
                                            gpuCurve = cpuCurve;
                                        }
                                        else
                                        {
                                            cpuCurve = GetFanPresetCurve(fanSettings.CpuPreset);
                                            gpuCurve = GetFanPresetCurve(fanSettings.GpuPreset);
                                        }

                                        ecService.ApplyFanCurve(cpuCurve, gpuCurve,
                                            fanSettings.CpuTempThresholds ?? EcFanCurve.CpuTemperatures,
                                            fanSettings.GpuTempThresholds ?? EcFanCurve.GpuTemperatures);
                                        Debug.WriteLine("[Resume] Fan Control EC duties re-applied");
                                    }
                                }
                                catch (Exception ex)
                                {
                                    Debug.WriteLine($"[Resume] Fan Control re-apply failed: {ex.Message}");
                                }

                                // Auto-reconnect Hydro UI watercooler.
                                var waterCoolerService = App.GetService<WaterCoolerService>();
                                if (waterCoolerService != null)
                                {
                                    // If the service still thinks it's connected, the BLE link is a
                                    // zombie - force a disconnect before attempting reconnect.
                                    if (waterCoolerService.IsConnected)
                                    {
                                        Debug.WriteLine("[WaterCooler] Resuming from sleep - disconnecting zombie BLE link");
                                        await waterCoolerService.DisconnectAsync();
                                        await Task.Delay(500);
                                    }

                                    bool connected = await waterCoolerService.TryAutoConnectAsync();
                                    if (connected)
                                        Debug.WriteLine("[WaterCooler] Auto-reconnected after system resume");
                                }
                            }
                            catch (Exception ex)
                            {
                                Debug.WriteLine($"[Resume] Auto-reapply failed: {ex.Message}");
                            }
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                DiagnosticLogger.LogError(ex, "Failed during power mode change handling");
            }
        }

        #region INavigationWindow Methods

        public INavigationView GetNavigation() => RootNavigation;

        public bool Navigate(Type pageType)
        {
            bool result = RootNavigation.Navigate(pageType);
            ModelNameText.Visibility = pageType == typeof(Views.Pages.DashboardPage)
                ? Visibility.Collapsed : Visibility.Visible;
            return result;
        }

        public void SetPageService(INavigationViewPageProvider pageProvider) => RootNavigation.SetPageProviderService(pageProvider);

        public void SetServiceProvider(IServiceProvider serviceProvider)
        {
            if (serviceProvider.GetService(typeof(INavigationViewPageProvider)) is INavigationViewPageProvider pageProvider)
                SetPageService(pageProvider);
        }

        public void ShowWindow() => Show();

        public void CloseWindow() => Close();

        #endregion

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            Application.Current.Shutdown();
        }

        private void UiWindow_StateChanged(object sender, EventArgs e)
        {
            if (this.WindowState == WindowState.Minimized)
            {
                isMini = true;
                this.WindowStyle = WindowStyle.ToolWindow;
                this.ShowInTaskbar = false;
            }
            else
            {
                isMini = false;
                this.WindowStyle = WindowStyle.SingleBorderWindow;
                this.ShowInTaskbar = true;
            }

        }

        private async void TrayStockPreset_Click(object? sender, RoutedEventArgs e)
        {
            if (_isTrayPresetApplying || sender is not MenuItem menuItem)
                return;

            string? profileName = menuItem.Tag?.ToString();
            if (string.IsNullOrEmpty(profileName))
                return;

            _isTrayPresetApplying = true;
            try
            {
                string command = PresetProfileApplier.BuildCommandString(profileName);
                string displayName = PresetProfileApplier.DisplayName(profileName);

                await PresetProfileApplier.ApplyProfileAsync(profileName, displayName);

                Settings.Default.presetProfile = profileName;
                Settings.Default.CommandString = command;
                Settings.Default.Save();

                ToastNotification.ShowToastNotification($"{displayName} Applied!",
                    $"{displayName} has been applied!");
            }
            catch (Exception ex)
            {
                DiagnosticLogger.LogError(ex, "Failed to apply a stock preset from the tray menu");
            }
            finally
            {
                RefreshTrayStockPresetChecks();
                _isTrayPresetApplying = false;
            }
        }

        private void PopulateTrayCustomPresets()
        {
            // Remove any existing custom preset items from miSectionPresets
            // (items after the Separator, which is at index 3)
            while (miSectionPresets.Items.Count > 4)
            {
                miSectionPresets.Items.RemoveAt(miSectionPresets.Items.Count - 1);
            }
            try
            {
                var presets = Models.CustomPresetStore.Load();
                int count = 0;
                int activeSlot = Settings.Default.customPresetSlot;
                bool isCustomActive = string.Equals(Settings.Default.presetProfile, "Custom", StringComparison.OrdinalIgnoreCase);

                for (int i = 0; i < presets.Length; i++)
                {
                    if (presets[i].HasChanges)
                    {
                        var item = new MenuItem
                        {
                            Header = $"Custom {i + 1}",
                            Tag = i,
                            IsCheckable = true,
                            IsChecked = isCustomActive && i == activeSlot
                        };
                        item.Click += TrayCustomPreset_Click;
                        miSectionPresets.Items.Add(item);
                        count++;
                    }
                }

                if (count == 0)
                {
                    miSectionPresets.Items.Add(new MenuItem
                    {
                        Header = "No saved custom profiles",
                        IsEnabled = false
                    });
                }
            }
            catch (Exception ex)
            {
                DiagnosticLogger.LogError(ex, "Failed to populate custom presets in the tray menu");
                miSectionPresets.Items.Add(new MenuItem
                {
                    Header = "No saved custom profiles",
                    IsEnabled = false
                });
            }
        }

        private async void TrayCustomPreset_Click(object? sender, RoutedEventArgs e)
        {
            if (_isTrayPresetApplying || sender is not MenuItem menuItem || menuItem.Tag is not int slot)
                return;
            _isTrayPresetApplying = true;
            try
            {
                var presets = Models.CustomPresetStore.Load();
                if (slot >= presets.Length)
                    return;

                var p = presets[slot];
                string command = BuildCustomCommandString(p);
                if (!string.IsNullOrWhiteSpace(command))
                {
                    await ApplyCustomOnStart(command, p);
                    ToastNotification.ShowToastNotification($"Custom {slot + 1} Applied!",
                        $"Custom preset (Profile {slot + 1}) has been applied!");
                }

                Settings.Default.presetProfile = "Custom";
                Settings.Default.customPresetSlot = slot;
                Settings.Default.CommandString = command;
                Settings.Default.Save();
            }
            catch (Exception ex)
            {
                DiagnosticLogger.LogError(ex, "Failed to apply a custom preset from the tray menu");
            }
            finally
            {
                RefreshTrayStockPresetChecks();
                PopulateTrayCustomPresets();
                _isTrayPresetApplying = false;
            }
        }

        private static async Task TranslatePresetAsync(string commands, string? configuredName)
        {
            string profileName = ResolveAutomationProfile(configuredName);
            if (string.IsNullOrEmpty(profileName))
            {
                // Unknown profile — fall back to raw command execution
                await RyzenAdj_To_UCU.TranslateAsync(commands, appliedName: configuredName, localizeAppliedName: false);
                return;
            }

            // Full apply: RyzenAdj + V-F curve (Custom) + EC limits (AMD)
            await PresetProfileApplier.ApplyProfileAsync(profileName, PresetProfileApplier.DisplayName(profileName));
        }

        /// <summary>
        /// Maps a saved preset display name (e.g. "Office Preset") back to the profile key ("Office").
        /// </summary>
        private static string ResolveAutomationProfile(string? presetName)
        {
            if (string.IsNullOrEmpty(presetName))
                return string.Empty;

            return presetName switch
            {
                string s when s.Contains("Office", StringComparison.OrdinalIgnoreCase) => "Office",
                string s when s.Contains("Gaming", StringComparison.OrdinalIgnoreCase) => "Gaming",
                string s when s.Contains("Turbo", StringComparison.OrdinalIgnoreCase) => "Turbo",
                string s when s.Contains("Custom", StringComparison.OrdinalIgnoreCase) => "Custom",
                _ => string.Empty
            };
        }

        private void RefreshTrayStockPresetChecks()
        {
            string? profile = Settings.Default.presetProfile;
            miTrayOfficePreset.IsChecked = string.Equals(profile, "Office", StringComparison.OrdinalIgnoreCase);
            miTrayGamingPreset.IsChecked = string.Equals(profile, "Gaming", StringComparison.OrdinalIgnoreCase);
            miTrayTurboPreset.IsChecked = string.Equals(profile, "Turbo", StringComparison.OrdinalIgnoreCase);

            // Clear custom preset checkmarks when a stock preset is active
            if (!string.Equals(profile, "Custom", StringComparison.OrdinalIgnoreCase))
            {
                for (int i = 4; i < miSectionPresets.Items.Count; i++)
                {
                    if (miSectionPresets.Items[i] is MenuItem item && item.IsCheckable)
                        item.IsChecked = false;
                }
            }
        }

        private void miClose_Click(object? sender, RoutedEventArgs e)
        {
            Settings.Default.isAdaptiveModeRunning = false;
            Settings.Default.Save();
            Controller.magWindow?.Dispose();
            Application.Current.Shutdown();
        }

        private void UiWindow_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (Settings.Default.MinimizeClose)
            {
                WindowState = WindowState.Minimized;
                e.Cancel = true;
                return;
            }
            
            Settings.Default.isAdaptiveModeRunning = false;
            Settings.Default.Save();
            Controller.magWindow?.Dispose();
            //Fan_Control.disableFanControl();
        }

        private void mainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            Dispatcher.BeginInvoke(
                DispatcherPriority.ApplicationIdle,
                new Action(() =>
                {
                    Controller.SetUpMagWindow(this);
                }));

            if (Settings.Default.StartMini)
            {
                this.WindowState = WindowState.Minimized;
            }
            else
            {
                if (GetSystemInfo.Manufacturer.ToUpper().Contains("AYANEO") || GetSystemInfo.Manufacturer.ToUpper().Contains("GPD") || GetSystemInfo.Product.ToUpper().Contains("ONEXPLAYER"))
                {
                    int displayCount = Screen.AllScreens.Length;
                    if (displayCount < 2)
                    {
                        this.MaxHeight = SystemParameters.MaximizedPrimaryScreenHeight;
                        this.WindowState = WindowState.Maximized;
                    }
                }
            }

            _ = Task.Run(PremadePresets.SetPremadePresets);

            // Eagerly instantiate the Adaptive page so its constructor runs (starts the
            // sensor polling timer and auto-starts Adaptive Mode if isStartAdpative is set).
            // Without this, the page is lazily created on first navigation, meaning the
            // timer never starts and Adaptive Mode doesn't activate on app launch.
            // Set isAdaptiveModeRunning synchronously so other pages (Flydigi) can detect
            // the override state before ToggleAdaptiveMode() completes its async work.
            if (Settings.Default.isStartAdpative)
            {
                Settings.Default.isAdaptiveModeRunning = true;
                Settings.Default.Save();
                var adaptivePage = App.GetService<Views.Pages.Adaptive>();
            }

            // Eagerly instantiate Hydro UI and Flydigi cooler pages to avoid a 1-2 second
            // delay on first navigation. These pages do heavy work in Loaded (service
            // resolution, event subscriptions, settings loading) which is noticeable
            // when the page is created lazily on first visit.
            _ = App.GetService<Views.Pages.Watercooler>();
            _ = App.GetService<Views.Pages.FlydigiCooler>();

            Task.Run(Garbage.Garbage_Collect);
        }

        #region Tray Menu Handlers

        #region Hydro UI Tray Handlers

        private async void TrayHydroPump_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem item || item.Tag is not string tag)
                return;

            var wc = App.GetService<WaterCoolerService>();
            if (wc == null || !wc.IsConnected)
            {
                ToastNotification.ShowToastNotification("Hydro UI", "Watercooler not connected");
                return;
            }

            var voltage = tag switch
            {
                "Off" => PumpVoltage.Off,
                "V7" => PumpVoltage.V7,
                "V8" => PumpVoltage.V8,
                "V11" => PumpVoltage.V11,
                _ => PumpVoltage.V7
            };

            // Update checkmarks
            foreach (MenuItem mi in miHydroPump.Items)
                mi.IsChecked = mi == item;

            await wc.WritePumpModeAsync(voltage);
            wc.SaveSettings();
        }

        private async void TrayHydroFan_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem item || item.Tag is not string tag)
                return;

            var wc = App.GetService<WaterCoolerService>();
            if (wc == null || !wc.IsConnected)
            {
                ToastNotification.ShowToastNotification("Hydro UI", "Watercooler not connected");
                return;
            }

            var speed = tag switch
            {
                "Off" => FanSpeed.Off,
                "Percent25" => FanSpeed.Percent25,
                "Percent50" => FanSpeed.Percent50,
                "Percent75" => FanSpeed.Percent75,
                "Percent90" => FanSpeed.Percent90,
                "Percent95" => FanSpeed.Percent95,
                "Percent100" => FanSpeed.Percent100,
                _ => FanSpeed.Percent50
            };

            foreach (MenuItem mi in miHydroFan.Items)
                mi.IsChecked = mi == item;

            await wc.WriteFanModeAsync(speed);
            wc.SaveSettings();
        }

        private async void TrayHydroRgb_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem item || item.Tag is not string tag)
                return;

            var wc = App.GetService<WaterCoolerService>();
            if (wc == null || !wc.IsConnected)
            {
                ToastNotification.ShowToastNotification("Hydro UI", "Watercooler not connected");
                return;
            }

            var mode = tag switch
            {
                "Off" => RgbState.Off,
                "Static" => RgbState.Static,
                "Breathe" => RgbState.Breathe,
                "Colorful" => RgbState.Colorful,
                "BreatheColor" => RgbState.BreatheColor,
                _ => RgbState.Static
            };

            foreach (MenuItem mi in miHydroRgb.Items)
                mi.IsChecked = mi == item;

            await wc.WriteRgbModeAsync(mode);
            wc.UpdateRgbMode(mode);
            wc.SaveSettings();
        }

        #endregion

        #region Flydigi Tray Handlers

        private async void TrayFlydigiFan_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem item || item.Tag is not string tag)
                return;

            var fc = App.GetService<FlydigiCoolerService>();
            if (fc == null || !fc.IsConnected)
            {
                ToastNotification.ShowToastNotification("Flydigi", "Cooler not connected");
                return;
            }

            // Update checkmarks - clear all Fan Mode items (Gears and Auto(Curve) children)
            foreach (MenuItem mi in miFlydigiFan.Items)
            {
                if (mi == miFlydigiGears)
                {
                    foreach (MenuItem child in mi.Items)
                        child.IsChecked = child == item;
                }
                else if (mi == miFlydigiAutoCurve)
                {
                    foreach (MenuItem child in mi.Items)
                        child.IsChecked = false;
                }
                else
                {
                    mi.IsChecked = mi == item;
                }
            }

            if (tag == "Off")
            {
                await fc.WriteRealtimeRpmAsync(0);
            }
            else
            {
                byte gear = tag switch
                {
                    "Gear1" => 1,
                    "Gear2" => 2,
                    "Gear3" => 3,
                    "Gear4" => 4,
                    _ => 1
                };
                var settings = fc.GetSettings();
                settings.FanMode = 1; // Gear Presets
                settings.ManualGear = gear;
                await fc.WriteGearAsync(gear);
                fc.PersistSettings();
            }
        }

        private void TrayFlydigiCurve_SubmenuOpened(object sender, RoutedEventArgs e)
        {
            // Show/hide Custom item based on whether a custom curve exists
            try
            {
                var fc = App.GetService<FlydigiCoolerService>();
                bool hasCustom = false;
                if (fc != null)
                {
                    var settings = fc.GetSettings();
                    hasCustom = !string.IsNullOrEmpty(settings.CustomCurveJson);
                }
                miFlydigiCurveCustom.Visibility = hasCustom ? Visibility.Visible : Visibility.Collapsed;
            }
            catch { /* Non-critical: failing to read custom curve settings only affects the tray menu Custom item visibility */ }
        }

        private async void TrayFlydigiCurve_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem item)
                return;

            var fc = App.GetService<FlydigiCoolerService>();
            if (fc == null || !fc.IsConnected)
            {
                ToastNotification.ShowToastNotification("Flydigi", "Cooler not connected");
                return;
            }

            // Update checkmarks - clear all Auto(Curve) children
            foreach (MenuItem mi in miFlydigiAutoCurve.Items)
                mi.IsChecked = mi == item;

            // Also clear Off and Gears checkmarks since we're switching to Auto(Curve)
            foreach (MenuItem mi in miFlydigiFan.Items)
            {
                if (mi == miFlydigiAutoCurve) continue;
                if (mi == miFlydigiGears)
                {
                    foreach (MenuItem child in mi.Items)
                        child.IsChecked = false;
                }
                else
                {
                    mi.IsChecked = false;
                }
            }

            try
            {
                string? tag = item.Tag?.ToString();
                FlydigiFanCurveProfile profile;

                if (tag == "Silent") profile = FlydigiFanCurveProfile.CreateSilent();
                else if (tag == "Balanced") profile = FlydigiFanCurveProfile.CreateBalanced();
                else if (tag == "Performance") profile = FlydigiFanCurveProfile.CreatePerformance();
                else if (tag == "Custom")
                {
                    profile = FlydigiFanCurveProfile.FromJSON(fc.GetSettings().CustomCurveJson);
                }
                else profile = FlydigiFanCurveProfile.CreateBalanced();

                // Save the selected curve profile
                var fcSettings = fc.GetSettings();
                fcSettings.FanMode = 2; // Auto Curve
                fcSettings.SelectedCurveProfile = profile.Name;
                fc.PersistSettings();

                var sc = App.GetService<FlydigiSmartControl>();
                if (sc != null && sc.IsActive)
                {
                    sc.ActiveProfile = profile;
                }

                ToastNotification.ShowToastNotification("Flydigi", $"Curve profile \"{profile.Name}\" applied");
            }
            catch (Exception ex)
            {
                DiagnosticLogger.LogError(ex, "Failed to apply Flydigi curve profile from tray");
            }
        }

        private async void TrayFlydigiRgb_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem item || item.Tag is not string tag)
                return;

            var fc = App.GetService<FlydigiCoolerService>();
            if (fc == null || !fc.IsConnected)
            {
                ToastNotification.ShowToastNotification("Flydigi", "Cooler not connected");
                return;
            }

            foreach (MenuItem mi in miFlydigiRgb.Items)
                mi.IsChecked = mi == item;

            var settings = fc.GetSettings();
            switch (tag)
            {
                case "Off":
                    await fc.WriteRgbOffAsync();
                    break;
                case "SmartTemp":
                    await fc.WriteRgbSmartTempAsync();
                    break;
                case "Static":
                    await fc.WriteRgbStaticAsync(settings.R, settings.G, settings.B, settings.Brightness);
                    break;
                case "Flowing":
                    await fc.WriteRgbFlowingAsync();
                    break;
                case "Breathing":
                    await fc.WriteRgbBreathingAsync(settings.R, settings.G, settings.B, settings.Brightness);
                    break;
            }

            fc.PersistSettings();
        }

        #endregion

        #region Fan Control Tray Handlers

        private void TrayFanUnified_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem item)
                return;

            bool enabled = item.IsChecked is true;
            var settings = FanControlSettingsService.Load();
            settings.UnifiedMode = enabled;
            FanControlSettingsService.Save(settings);

            // Apply the curve
            var ec = App.GetService<UniwillECService>();
            if (ec != null)
            {
                var curve = GetFanPresetCurve(settings.UnifiedPreset);
                if (enabled)
                {
                    ec.ApplyFanCurve(curve, curve, EcFanCurve.CpuTemperatures, EcFanCurve.CpuTemperatures);
                }
                else
                {
                    var cpuCurve = GetFanPresetCurve(settings.CpuPreset);
                    var gpuCurve = GetFanPresetCurve(settings.GpuPreset);
                    ec.ApplyFanCurve(cpuCurve, gpuCurve, EcFanCurve.CpuTemperatures, EcFanCurve.GpuTemperatures);
                }
            }
        }

        private void TrayFanProfile_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem item || item.Tag is not string tag)
                return;

            string presetName = tag switch
            {
                "FullSpeed" => "Full Speed",
                _ => tag
            };

            foreach (MenuItem mi in miFanCurveProfile.Items)
                mi.IsChecked = mi.Header.ToString() == (tag == "FullSpeed" ? "Full Speed" : tag);

            var settings = FanControlSettingsService.Load();
            settings.UnifiedPreset = presetName;
            if (!settings.UnifiedMode)
            {
                settings.CpuPreset = presetName;
                settings.GpuPreset = presetName;
            }
            FanControlSettingsService.Save(settings);

            var ec = App.GetService<UniwillECService>();
            if (ec != null)
            {
                var curve = GetFanPresetCurve(presetName);
                if (settings.UnifiedMode)
                {
                    ec.ApplyFanCurve(curve, curve, EcFanCurve.CpuTemperatures, EcFanCurve.CpuTemperatures);
                }
                else
                {
                    ec.ApplyFanCurve(curve, curve, EcFanCurve.CpuTemperatures, EcFanCurve.GpuTemperatures);
                }
            }
        }

        private void TrayFanCpuProfile_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem item || item.Tag is not string tag)
                return;

            string presetName = tag switch
            {
                "FullSpeed" => "Full Speed",
                _ => tag
            };

            foreach (MenuItem mi in miFanCpuProfile.Items)
                mi.IsChecked = mi.Header?.ToString() == presetName;

            var settings = FanControlSettingsService.Load();
            settings.CpuPreset = presetName;
            FanControlSettingsService.Save(settings);

            var ec = App.GetService<UniwillECService>();
            if (ec != null)
            {
                var cpuCurve = GetFanPresetCurve(presetName);
                var gpuCurve = GetFanPresetCurve(settings.GpuPreset);
                ec.ApplyFanCurve(cpuCurve, gpuCurve, EcFanCurve.CpuTemperatures, EcFanCurve.GpuTemperatures);
            }
        }

        private void TrayFanGpuProfile_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem item || item.Tag is not string tag)
                return;

            string presetName = tag switch
            {
                "FullSpeed" => "Full Speed",
                _ => tag
            };

            foreach (MenuItem mi in miFanGpuProfile.Items)
                mi.IsChecked = mi.Header?.ToString() == presetName;

            var settings = FanControlSettingsService.Load();
            settings.GpuPreset = presetName;
            FanControlSettingsService.Save(settings);

            var ec = App.GetService<UniwillECService>();
            if (ec != null)
            {
                var cpuCurve = GetFanPresetCurve(settings.CpuPreset);
                var gpuCurve = GetFanPresetCurve(presetName);
                ec.ApplyFanCurve(cpuCurve, gpuCurve, EcFanCurve.CpuTemperatures, EcFanCurve.GpuTemperatures);
            }
        }

        /// <summary>
        /// Returns a built-in EC fan curve preset by name.
        /// </summary>
        private static EcFanCurve GetFanPresetCurve(string? name) => name switch
        {
            "Silent" => EcFanCurve.CreateSilent(),
            "Balanced" => EcFanCurve.CreateBalanced(),
            "Performance" => EcFanCurve.CreatePerformance(),
            "Full Speed" => EcFanCurve.CreateFullSpeed(),
            "Off" => EcFanCurve.CreateOff(),
            _ => EcFanCurve.CreateBalanced()
        };

        #endregion

        #endregion

        #region Tray Sync Helpers

        /// <summary>
        /// Syncs Hydro UI tray checkmarks with persisted WaterCoolerSettings.
        /// </summary>
        private void SyncTrayHydroChecks()
        {
            try
            {
                var wc = App.GetService<WaterCoolerService>();
                if (wc == null) return;

                var settings = wc.GetSettings();

                // Pump
                foreach (MenuItem mi in miHydroPump.Items)
                {
                    mi.IsChecked = mi.Tag?.ToString() == settings.PumpVoltage;
                }

                // Fan
                foreach (MenuItem mi in miHydroFan.Items)
                {
                    mi.IsChecked = mi.Tag?.ToString() == settings.FanSpeed;
                }

                // RGB
                foreach (MenuItem mi in miHydroRgb.Items)
                {
                    mi.IsChecked = mi.Tag?.ToString() == settings.RgbMode;
                }
            }
            catch { /* Non-critical: failing to sync Hydro tray checkmarks only affects visual state */ }
        }

        /// <summary>
        /// Syncs Flydigi tray checkmarks with persisted Bs2ProSettings.
        /// </summary>
        private void SyncTrayFlydigiChecks()
        {
            try
            {
                var fc = App.GetService<FlydigiCoolerService>();
                if (fc == null) return;

                var settings = fc.GetSettings();

                // Fan Mode - FanMode: 0=Manual, 1=Gear Presets, 2=Auto Curve
                // Clear all Fan Mode items first
                foreach (MenuItem mi in miFlydigiFan.Items)
                {
                    if (mi == miFlydigiGears || mi == miFlydigiAutoCurve)
                    {
                        foreach (MenuItem child in mi.Items)
                            child.IsChecked = false;
                    }
                    else
                    {
                        mi.IsChecked = false;
                    }
                }

                if (settings.FanMode == 1)
                {
                    // Gear Presets mode - check matching gear in Gears submenu
                    string? gearTag = settings.ManualGear switch
                    {
                        1 => "Gear1",
                        2 => "Gear2",
                        3 => "Gear3",
                        4 => "Gear4",
                        _ => null
                    };
                    foreach (MenuItem mi in miFlydigiGears.Items)
                    {
                        if (mi.Tag?.ToString() == gearTag)
                            mi.IsChecked = true;
                    }
                }
                else if (settings.FanMode == 2)
                {
                    // Auto Curve mode - check matching curve profile
                    string? curveName = settings.SelectedCurveProfile;
                    foreach (MenuItem mi in miFlydigiAutoCurve.Items)
                    {
                        if (string.Equals(mi.Tag?.ToString(), curveName, StringComparison.OrdinalIgnoreCase))
                            mi.IsChecked = true;
                    }
                }
                else if (settings.FanMode == 0)
                {
                    // Manual mode - check Off if RPM is 0
                    if (settings.ManualRpm == 0)
                    {
                        foreach (MenuItem mi in miFlydigiFan.Items)
                        {
                            if (mi == miFlydigiGears || mi == miFlydigiAutoCurve) continue;
                            if (mi.Tag?.ToString() == "Off")
                                mi.IsChecked = true;
                        }
                    }
                }

                // RGB
                foreach (MenuItem mi in miFlydigiRgb.Items)
                {
                    string? tag = mi.Tag?.ToString();
                    string? mode = settings.RgbMode;
                    if (tag == "Off" && string.Equals(mode, "Off", StringComparison.OrdinalIgnoreCase))
                        mi.IsChecked = true;
                    else if (tag == "SmartTemp" && string.Equals(mode, "SmartTemp", StringComparison.OrdinalIgnoreCase))
                        mi.IsChecked = true;
                    else if (tag == "Static" && string.Equals(mode, "Static", StringComparison.OrdinalIgnoreCase))
                        mi.IsChecked = true;
                    else if (tag == "Flowing" && string.Equals(mode, "Flowing", StringComparison.OrdinalIgnoreCase))
                        mi.IsChecked = true;
                    else if (tag == "Breathing" && string.Equals(mode, "Breathing", StringComparison.OrdinalIgnoreCase))
                        mi.IsChecked = true;
                }
            }
            catch { /* Non-critical: failing to sync Flydigi tray checkmarks only affects visual state */ }
        }

        /// <summary>
        /// Syncs Flydigi Auto(Curve) checkmarks. Called from TrayMenu_Opened.
        /// </summary>
        private void SyncTrayFlydigiCurveChecks()
        {
            // Handled in SyncTrayFlydigiChecks now
        }

        /// <summary>
        /// Syncs Fan Control tray checkmarks with persisted FanControlSettings.
        /// </summary>
        private void SyncTrayFanControlChecks()
        {
            try
            {
                var settings = FanControlSettingsService.Load();

                // Unified Curve toggle
                miFanUnified.IsChecked = settings.UnifiedMode;

                if (settings.UnifiedMode)
                {
                    // Unified mode: show Profile, hide CPU/GPU Profile
                    string presetName = settings.UnifiedPreset switch
                    {
                        "Full Speed" => "FullSpeed",
                        _ => settings.UnifiedPreset
                    };
                    foreach (MenuItem mi in miFanCurveProfile.Items)
                        mi.IsChecked = mi.Tag?.ToString() == presetName;
                }
                else
                {
                    // Separate mode: check CPU and GPU profiles
                    string cpuPreset = settings.CpuPreset switch
                    {
                        "Full Speed" => "FullSpeed",
                        _ => settings.CpuPreset
                    };
                    string gpuPreset = settings.GpuPreset switch
                    {
                        "Full Speed" => "FullSpeed",
                        _ => settings.GpuPreset
                    };
                    foreach (MenuItem mi in miFanCpuProfile.Items)
                        mi.IsChecked = mi.Tag?.ToString() == cpuPreset;
                    foreach (MenuItem mi in miFanGpuProfile.Items)
                        mi.IsChecked = mi.Tag?.ToString() == gpuPreset;
                }
            }
            catch { /* Non-critical: failing to sync Fan Control tray checkmarks only affects visual state */ }
        }

        /// <summary>
        /// Toggles visibility of Profile vs CPU/GPU Profile based on Unified mode.
        /// </summary>
        private void UpdateTrayFanProfileVisibility()
        {
            try
            {
                var settings = FanControlSettingsService.Load();
                if (settings.UnifiedMode)
                {
                    miFanCurveProfile.Visibility = Visibility.Visible;
                    miFanCpuProfile.Visibility = Visibility.Collapsed;
                    miFanGpuProfile.Visibility = Visibility.Collapsed;
                }
                else
                {
                    miFanCurveProfile.Visibility = Visibility.Collapsed;
                    miFanCpuProfile.Visibility = Visibility.Visible;
                    miFanGpuProfile.Visibility = Visibility.Visible;
                }
            }
            catch { /* Non-critical: failing to update tray fan profile visibility only affects visual state */ }
        }

        /// <summary>
        /// Disables or enables tray controls based on Adaptive Mode override state.
        /// Only applies when Adaptive Mode is actually running (isAdaptiveModeRunning = true).
        /// </summary>
        private void UpdateTrayAdaptiveOverrideState()
        {
            // Only apply override restrictions when Adaptive Mode is running
            if (!Settings.Default.isAdaptiveModeRunning)
            {
                // All controls enabled when Adaptive Mode is off
                miSectionPresets.IsEnabled = true;
                miSectionHydro.IsEnabled = true;
                miSectionFanControl.IsEnabled = true;
                return;
            }

            var deviceApplier = App.GetService<DeviceApplier>();

            // Presets: disable if CPU or GPU tuning override is active
            // CPU/GPU overrides are tracked in Settings (AdaptiveCpuEnabled/AdaptiveGpuEnabled)
            bool cpuOrGpuOverridden = Settings.Default.AdaptiveCpuEnabled || Settings.Default.AdaptiveGpuEnabled;
            miSectionPresets.IsEnabled = !cpuOrGpuOverridden;

            // Hydro UI: disable if watercooler override is active
            bool wcOverridden = deviceApplier != null && deviceApplier.IsWatercoolerOverridden;
            miSectionHydro.IsEnabled = !wcOverridden;

            // Fan Control: disable if EC fan override is active
            bool ecFanOverridden = deviceApplier != null && deviceApplier.IsEcFanOverridden;
            miSectionFanControl.IsEnabled = !ecFanOverridden;
        }

        #endregion

        #region Custom Preset Boot Apply

        /// <summary>
        /// Builds a RyzenAdj command string from a CustomPreset, mirroring Presets.buildCommandString().
        /// </summary>
        private string BuildCustomCommandString(Models.CustomPreset p)
        {
            var parts = new List<string>();

            if (Family.TYPE == Family.ProcessorType.Amd_Apu)
            {
                int slowLimit = (int)p.CpuSustained * 1000;
                int fastLimit = (int)p.CpuBoost * 1000;
                parts.Add($"--slow-limit={slowLimit}");
                parts.Add($"--fast-limit={fastLimit}");
                parts.Add($"--tctl-temp={p.CpuTjMax}");
                parts.Add($"--cHTC-temp={p.CpuTjMax}");
            }
            else if (Family.TYPE == Family.ProcessorType.Intel)
            {
                parts.Add($"--intel-pl={p.IntelPl1:F0},{p.IntelPl2:F0}");
                if (p.IntelVolt < 0)
                    parts.Add($"--intel-volt-cpu={p.IntelVolt:F0}");
            }

            // NVIDIA GPU clocks
            int core = (int)p.GpuCoreOffset;
            int mem = (int)p.GpuMemOffset;
            int tgp = (int)p.GpuTgp;
            int dbOffset = p.GpuDynamicBoost ? (int)p.GpuDbOffset : 0;
            int totalWatts = tgp + dbOffset;
            parts.Add($"--NVIDIA-Clocks=0-{core}-{mem}-{totalWatts}-{dbOffset}");

            return string.Join(" ", parts) + " ";
        }

        /// <summary>
        /// Applies a Custom preset on startup: SMU commands, V-F curve, and EC writes.
        /// </summary>
        private async Task ApplyCustomOnStart(string command, Models.CustomPreset p)
        {
            await Scripts.RyzenAdj_To_UCU.TranslateAsync(command, appliedName: "Custom Preset", localizeAppliedName: false);

            // Apply V-F curve offsets if the preset has them
            if (p.VfCurveOffsets.Count > 0)
            {
                int tgp = (int)p.GpuTgp;
                int dbOffset = p.GpuDynamicBoost ? (int)p.GpuDbOffset : 0;
                Scripts.GPUs.NVIDIA.NvTuning.SetPowerLimit(tgp + dbOffset, dbOffset);
                Scripts.GPUs.NVIDIA.NvTuning.ApplyVfCurveOffsets_ClockBoostTable(p.VfCurveOffsets);
                Scripts.GPUs.NVIDIA.NvTuning.SetPowerLimit(tgp + dbOffset, dbOffset);
            }

            // Apply EC CPU power limits (AMD)
            if (Family.TYPE == Family.ProcessorType.Amd_Apu)
            {
                var ec = Scripts.RyzenAdj_To_UCU.EcService;
                ec?.ApplyCpuPowerLimits((int)p.CpuSustained, (int)p.CpuBoost);
            }
        }

        #endregion
    }
}
