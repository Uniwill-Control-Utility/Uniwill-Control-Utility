using Accord.Math.Distances;
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
using System.Reflection;
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
using Uniwill_Control_Utility.Scripts.GPUs.NVIDIA;
using Uniwill_Control_Utility.Scripts.Intel_Backend;
using Uniwill_Control_Utility.Scripts.Misc;
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

        public static bool isMini { get; private set; }
        public static NavigationView _mainWindowNav;
        public static MainWindow? Instance { get; private set; }
        private static INavigationService _navigationService;
        private bool _isTrayPresetApplying;
        private readonly TrayWidgetController _trayWidget;
        // Debounce for the tray-icon left click — the shell can send the LBUTTONDOWN
        // message twice per click (press + release).
        private DateTime _lastTrayIconClick;
        public static bool IsPageSelected(Type pageType) =>
            _mainWindowNav?.SelectedItem is INavigationViewItem item && item.TargetPageType == pageType;

        public MainWindow(ViewModels.MainWindowViewModel viewModel, INavigationViewPageProvider pageProvider, INavigationService navigationService, TrayWidgetController trayWidget)
        {
            ViewModel = viewModel;
            _trayWidget = trayWidget;
            DataContext = this;
            InitializeComponent();

            _navigationService = navigationService;
            _mainWindowNav = RootNavigation;

            Misc.Interval = TimeSpan.FromSeconds(1);
            Misc.Tick += Misc_Tick;
            Misc.Start();

            SetupNavigationService(pageProvider);
            SetupUI();

            SystemEvents.PowerModeChanged += HandlePowerModeChange;

            // Subscribe to window messages for display change detection.
            // We reset LHM Computer proactively when the display topology changes
            // (monitor hotplug) to prevent ExecutionEngineException from stale NVML handles.
            this.SourceInitialized += OnSourceInitialized;

            Instance = this;

            // Ensure tray icon is visible
            niTray.Visibility = Visibility.Visible;

        }

        /// <summary>Toggles the compact quick-control widget when the tray icon is left-clicked.</summary>
        private void NotifyIcon_LeftClick(Wpf.Ui.Tray.Controls.NotifyIcon sender, RoutedEventArgs e)
        {
            // The shell can deliver the tray LBUTTONDOWN message twice (press and release);
            // debounce so the second arrival doesn't re-show a tray we just hid.
            var now = DateTime.Now;
            if ((now - _lastTrayIconClick).TotalMilliseconds < 300)
                return;
            _lastTrayIconClick = now;

            // If the main window is open on the desktop, just hide the tray widget
            // instead of toggling it open (main window takes priority).
            // WindowState, not IsActive: a window minimized right after startup keeps a
            // stale active flag (minimizing sends no WM_CLEARFOCUS), which used to swallow
            // the first left-click until a right-click forced the focus change.
            if (this.WindowState != WindowState.Minimized && this.IsVisible)
            {
                // Open but not in focus (another window owns the desktop) — the click
                // brings the main window back to the front instead of doing nothing.
                if (!this.IsActive)
                {
                    this.Activate();
                    _ = this.Focus();
                }

                _trayWidget.HideIfVisible();
                return;
            }

            _ = _trayWidget.ToggleAsync();
        }

        /// <summary>When the main window is activated, hide the tray widget (mutual exclusivity).</summary>
        private void MainWindow_Activated(object? sender, EventArgs e)
        {
            _trayWidget.HideIfVisible();
        }

        /// <summary>Title-bar hand-over: minimize the main window and open the mini-tray widget.</summary>
        private void BtnMinimizeToTray_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Wpf.Ui.Controls.Button)
                return;

            // Minimize first so the tray-icon left-click guard sees a minimized window,
            // then show the mini-tray near the tray icon — the cursor sits on the title
            // bar here, so anchoring on it would drop the tray at the button's axis.
            this.WindowState = WindowState.Minimized;
            _ = _trayWidget.ToggleAsync();
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
            miSectionFlydigi.Visibility = Settings.Default.EnableFlydigi ? Visibility.Visible : Visibility.Collapsed;

            // Hide Hydro UI section if no supported hardware or no cooler connected
            UpdateHydroTrayVisibility();

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
            Visibility visibility = isDashboard ? Visibility.Collapsed : Visibility.Visible;
            ModelNameText.Visibility = visibility;
            VersionCard.Visibility = visibility;
            LaptopNameCard.Visibility = visibility;
        }

        private void SetupUI()
        {
            ModelNameText.Text = "Uniwill Control Utility";
            LaptopNameText.Text = LaptopDetector.GetDisplayName().ToUpperInvariant();
            VersionText.Text = GetVersionString();

            tbMain.Title = "Uniwill Control Utility";

            // Migrate legacy custom_presets.json to the new dual-mode file structure.
            Models.CustomPresetStore.MigrateLegacyFile();

             Wpf.Ui.Appearance.SystemThemeWatcher.Watch(this, WindowBackdropType.Mica, true);
        }

        private static string GetVersionString()
        {
            var version = Assembly.GetEntryAssembly()?.GetName().Version;
            return version != null ? $"v{version}" : string.Empty;
        }

        internal static async Task ApplyOnStart()
        {
            try
            {
                // Stock presets (Office/Gaming/Turbo) always apply on startup regardless of the checkbox.
                if (PresetProfileApplier.IsBuiltInProfile(Settings.Default.presetProfile))
                {
                    await Task.Run(() => GetBatteryStatus());

                    string profileName = Settings.Default.presetProfile;
                    await PresetProfileApplier.ApplyProfileAsync(profileName, PresetProfileApplier.DisplayName(profileName));
                    ToastNotification.ShowToastNotification($"{profileName} Preset Applied",
                        $"Your {profileName} preset has been applied on startup!");
                    return;
                }

                if (Settings.Default.ApplyOnStart)
                {
                    // If the last selected profile was Custom, apply from CustomPresetStore.
                    if (Settings.Default.presetProfile == "Custom")
                    {
                        await Task.Run(() => GetBatteryStatus());

                        // Determine active preset mode based on current Power boost mode setting
                        Models.PresetMode mode = Settings.Default.EnablePowerBoostMode
                            ? Models.PresetMode.Boost
                            : Models.PresetMode.Normal;
                        var presets = Models.CustomPresetStore.Load(mode);
                        // Use the saved slot for the active mode, fall back to first slot with data.
                        int slot = mode == Models.PresetMode.Boost
                            ? Settings.Default.customPresetSlotBoost
                            : Settings.Default.customPresetSlot;
                        if (slot < 0 || slot >= presets.Length || !presets[slot].HasChanges)
                        {
                            // Fallback: find first slot with saved data.
                            slot = 0;
                            for (int i = 0; i < presets.Length; i++)
                            {
                                if (presets[i].HasChanges)
                                {
                                    slot = i;
                                    break;
                                }
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
                            if (Settings.Default.EnableAutoNavigation &&
                                Settings.Default.acPreset != "None" && !string.IsNullOrEmpty(Settings.Default.acPreset))
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
                            if (Settings.Default.EnableAutoNavigation &&
                                Settings.Default.dcPreset != "None" && !string.IsNullOrEmpty(Settings.Default.dcPreset))
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

                // Keyboard RGB startup apply moved to mainWindow_Loaded (AsyncApplyKeyboardOnStart)
                // so it fires regardless of whether the app starts minimized to tray.

                // Apply GPU Power Saving based on current power state
                try
                {
                    var powerStatus = System.Windows.Forms.SystemInformation.PowerStatus;
                    int mode = (powerStatus.PowerLineStatus == System.Windows.Forms.PowerLineStatus.Offline)
                        ? Settings.Default.GpuPowerSavingBattery
                        : Settings.Default.GpuPowerSavingPluggedIn;
                    await GpuPowerSaving.ApplyAsync((GpuPowerSavingMode)mode);
                    Log.Information("[Startup] GPU Power Saving applied: {Mode}", (GpuPowerSavingMode)mode);

                    // Reset LHM so it re-probes GPU availability after the PnP hotswap.
                    // This ensures the dashboard shows the correct active GPU (iGPU fallback).
                    try
                    {
                        var monitoring = App.GetService<IHardwareMonitoringService>();
                        monitoring?.ResetComputer();
                    }
                    catch { /* non-critical */ }
                }
                catch (Exception ex)
                {
                    Log.Information("[Startup] GPU Power Saving apply failed: {Message}", ex.Message);
                }
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
                var settings = KeyboardSettingsService.Load();

                // BetterRGB: the streamer owns the keyboard handle — resume it instead
                // of opening a fresh one and re-sending firmware reports.
                if (settings.Engine == KeyboardEngine.BetterRGB)
                {
                    App.GetService<BetterRgbStreamService>()?.ResumeAfterWake();
                    return;
                }

                var hidService = new KeyboardHidService();
                if (!hidService.Open())
                {
                    hidService.Dispose();
                    return;
                }

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

                        if (settings.EffectMode == Uniwill_Control_Utility.Models.KeyboardEffect.Rainbow)
                        {
                            hidService.SendRainbowEffect(brightnessPercent);
                            System.Diagnostics.Debug.WriteLine("[KBD] Resume apply: Rainbow (software gradient)");
                        }
                        else
                        {
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
                            System.Diagnostics.Debug.WriteLine($"[KBD] Resume apply: effect {settings.EffectMode}");
                        }
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

        /// <summary>
        /// Applies keyboard RGB settings on startup with a delay and retries.
        /// The ITE HID controller may not be discoverable immediately on cold boot,
        /// and the app may start minimized to tray (before Dashboard navigation fires).
        /// </summary>
        internal static async Task AsyncApplyKeyboardOnStart()
        {
            // Wait for the system to settle after boot. The HID controller needs
            // time to become discoverable — especially on cold startup.
            await Task.Delay(1000);

            KeyboardHidService? hidService = null;

            try
            {
                // Retry HID open up to 10 times with 500ms delays.
                for (int attempt = 0; attempt < 10; attempt++)
                {
                    if (attempt > 0)
                        await Task.Delay(500);

                    hidService = new KeyboardHidService();
                    if (hidService.Open())
                        break;

                    hidService.Dispose();
                    hidService = null;
                }

                if (hidService == null || !hidService.IsAvailable)
                {
                    System.Diagnostics.Debug.WriteLine("[KBD] Startup apply: HID keyboard not available after retries");
                    return;
                }

                var settings = KeyboardSettingsService.Load();
                if (settings.Engine == KeyboardEngine.BetterRGB)
                {
                    // BetterRGB: the streamer owns the keyboard-zone handle — skip the
                    // firmware keyboard apply. The light bar rides the usage-2 device
                    // and is unaffected by the engine choice.
                    var streamer = App.GetService<BetterRgbStreamService>();
                    if (streamer != null)
                    {
                        streamer.SetParams(settings);
                        streamer.StartEngine("startup");
                    }
                    Log.Information("[BetterRGB] Startup lighting presets successfully restored to hardware & BetterRGB Streamer");
                    ApplyLightBarOnStartWithService(hidService);
                }
                else
                {
                    ApplyKeyboardOnStartWithService(hidService);
                    ApplyLightBarOnStartWithService(hidService);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[KBD] Startup apply failed: {ex.Message}");
            }
            finally
            {
                hidService?.Dispose();
            }
        }

        /// <summary>Applies persisted keyboard RGB settings at app startup using a pre-opened HID service.</summary>
        private static void ApplyKeyboardOnStartWithService(KeyboardHidService hidService)
        {
            var settings = KeyboardSettingsService.Load();

            if (settings.PowerOn)
            {
                if (settings.PerKeyMode)
                {
                    System.Threading.Thread.Sleep(200);
                    int brightnessPercent = (settings.Brightness * 100) / 7;
                    hidService.TurnOn(settings.ColorR, settings.ColorG, settings.ColorB, brightnessPercent);
                    hidService.SetEffect(Uniwill_Control_Utility.Models.KeyboardEffect.Static);
                    var colors = settings.GetPerKeyColors();
                    hidService.SendAllPerKeyColorsFromDict(colors);
                    System.Diagnostics.Debug.WriteLine("[KBD] Startup apply: per-key mode");
                }
                else
                {
                    int brightnessPercent = (settings.Brightness * 100) / 7;
                    byte speed = (byte)Math.Clamp((int)settings.Speed, 0, 11);

                    if (settings.EffectMode == Uniwill_Control_Utility.Models.KeyboardEffect.Rainbow)
                    {
                        // Rainbow: use software-driven per-key gradient.
                        // The ITE firmware's built-in Rainbow (0x05) leaves the
                        // leftmost columns dark due to a hardcoded LED layout mismatch.
                        hidService.SendRainbowEffect(brightnessPercent);
                        System.Diagnostics.Debug.WriteLine("[KBD] Startup apply: Rainbow (software gradient)");
                    }
                    else
                    {
                        if (settings.Direction != KeyboardDirection.LeftRight)
                            System.Threading.Thread.Sleep(500);

                        hidService.TurnOnWithEffect(settings.ColorR, settings.ColorG, settings.ColorB,
                            brightnessPercent, settings.EffectMode, speed, settings.Direction, settings.TouchReactive);

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
                        System.Diagnostics.Debug.WriteLine($"[KBD] Startup apply: effect {settings.EffectMode}");
                    }
                }
            }
            else
            {
                hidService.TurnOff();
                System.Diagnostics.Debug.WriteLine("[KBD] Startup apply: keyboard OFF");
            }
        }

        /// <summary>Applies persisted LightBar settings at app startup using a pre-opened HID service.</summary>
        private static void ApplyLightBarOnStartWithService(KeyboardHidService hidService)
        {
            try
            {
                if (!Settings.Default.LightBarEnabled)
                {
                    System.Diagnostics.Debug.WriteLine("[LB] Startup apply: LightBar disabled, skipping");
                    return;
                }

                if (!hidService.IsLightBarAvailable)
                {
                    System.Diagnostics.Debug.WriteLine("[LB] Startup apply: LightBar not available on this device");
                    return;
                }

                byte r = Settings.Default.LightBarR;
                byte g = Settings.Default.LightBarG;
                byte b = Settings.Default.LightBarB;
                int brightness = Settings.Default.LightBarBrightness;
                int effectIndex = Math.Clamp(Settings.Default.LightBarEffectIndex, 0, 5);
                int speed = Math.Clamp(Settings.Default.LightBarSpeed, 0, 11);

                byte[] effectValues = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06 };
                byte effect = effectValues[effectIndex];
                byte hidSpeed = (byte)(11 - speed);

                hidService.LightBarTurnOn(r, g, b, brightness);
                hidService.LightBarSetEffect(effect, hidSpeed, brightness);

                System.Diagnostics.Debug.WriteLine($"[LB] Startup apply: color=({r},{g},{b}), brightness={brightness}, effect=0x{effect:X2}, hidSpeed={hidSpeed}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LB] Startup apply failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Applies EC fan curve and battery charge limit at app startup.
        /// Runs once from mainWindow_Loaded — not on every Dashboard navigation.
        /// </summary>
        internal static void ApplyEcSettingsOnStart()
        {
            var ec = App.GetService<UniwillECService>();
            if (ec is null)
                return;

            try
            {
                ec.Initialize();
            }
            catch { /* EC not available on this hardware */ }

            // Auto-apply the saved fan curve on startup.
            try
            {
                var settings = FanControlSettingsService.Load();
                Models.EcFanCurve cpuCurve;
                Models.EcFanCurve gpuCurve;

                if (settings.UnifiedMode)
                {
                    var unifiedCurve = settings.UnifiedPreset == "Custom" && settings.UnifiedDuties?.Length == 11
                        ? BuildCustomEcCurve(settings.UnifiedDuties)
                        : GetPresetEcCurve(settings.UnifiedPreset);
                    cpuCurve = unifiedCurve;
                    gpuCurve = unifiedCurve;
                }
                else
                {
                    cpuCurve = settings.CpuPreset == "Custom" && settings.CpuDuties?.Length == 11
                        ? BuildCustomEcCurve(settings.CpuDuties)
                        : GetPresetEcCurve(settings.CpuPreset);
                    gpuCurve = settings.GpuPreset == "Custom" && settings.GpuDuties?.Length == 11
                        ? BuildCustomEcCurve(settings.GpuDuties)
                        : GetPresetEcCurve(settings.GpuPreset);
                }

                var cpuTemps = settings.UnifiedMode
                    ? (settings.UnifiedTempThresholds ?? Models.EcFanCurve.CpuTemperatures)
                    : (settings.CpuTempThresholds ?? Models.EcFanCurve.CpuTemperatures);
                var gpuTemps = settings.UnifiedMode
                    ? cpuTemps
                    : (settings.GpuTempThresholds ?? Models.EcFanCurve.GpuTemperatures);

                // If Smart Fan Mode is enabled, start it instead of applying
                // the normal curve.
                if (settings.EcSmartFanMode)
                {
                    try
                    {
                        var tempProvider = new Models.FlydigiTemperatureProvider();
                        var smartFan = Uniwill_Control_Utility.Services.EcSmartFanControl.GetOrCreate(
                            ec,
                            tempProvider,
                            settings,
                            cpuCurve,
                            cpuTemps);
                        smartFan.Start();
                        System.Diagnostics.Debug.WriteLine("INFO | MainWindow: Smart Fan Mode auto-started on startup");
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"WARN | MainWindow: Smart Fan Mode auto-start failed: {ex.Message}");
                        ec.ApplyFanCurve(cpuCurve, gpuCurve, cpuTemps, gpuTemps);
                    }
                }
                else
                {
                    ec.ApplyFanCurve(cpuCurve, gpuCurve, cpuTemps, gpuTemps);
                    System.Diagnostics.Debug.WriteLine("INFO | MainWindow: Fan curve auto-applied on startup");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"WARN | MainWindow: Fan curve auto-apply failed: {ex.Message}");
            }

            // Re-apply the saved battery charge limit on startup. The EC resets
            // to BIOS default (100%) on every cold boot.
            try
            {
                int savedMode = Properties.Settings.Default.BatteryMode;
                int currentMode = ec.GetBatteryMode();
                if (savedMode != currentMode)
                {
                    ec.SetBatteryMode(savedMode);
                    System.Diagnostics.Debug.WriteLine($"INFO | MainWindow: Restored battery charge limit to mode {savedMode} (was {currentMode})");
                }
            }
            catch { /* EC read failed */ }
        }

        private static Models.EcFanCurve GetPresetEcCurve(string? name) => name switch
        {
            "Silent" => Models.EcFanCurve.CreateSilent(),
            "Balanced" => Models.EcFanCurve.CreateBalanced(),
            "Performance" => Models.EcFanCurve.CreatePerformance(),
            "Full Speed" => Models.EcFanCurve.CreateFullSpeed(),
            "Off" => Models.EcFanCurve.CreateOff(),
            _ => Models.EcFanCurve.CreateBalanced()
        };

        private static Models.EcFanCurve BuildCustomEcCurve(int[] duties)
        {
            var curve = new Models.EcFanCurve { Name = "Custom" };
            curve.Duties.Clear();
            foreach (var d in duties)
                curve.Duties.Add(Math.Clamp(d, 0, 100));
            return curve;
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


        private async void Misc_Tick(object sender, EventArgs e)
        {
            try
            {
            }
            catch (Exception ex)
            {
                DiagnosticLogger.LogError(ex, "Failed during misc monitoring tick");
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

        static string lastAppliedState = "";
      private async void HandlePowerModeChange(object sender, PowerModeChangedEventArgs e)
        {
            try
            {
                if (e.Mode == PowerModes.StatusChange && Settings.Default.isAdaptiveModeRunning == false)
                {
                    await Task.Run(() => GetBatteryStatus());

                    if (statuscode == BatteryStatusAc || statuscode == BatteryStatusCritical
                        || statuscode == BatteryStatusCharging || statuscode == BatteryStatusNoSystemBattery)
                    {
                        if (Settings.Default.EnableAutoNavigation &&
                            Settings.Default.acPreset != "None" && !string.IsNullOrEmpty(Settings.Default.acPreset))
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
                        if (Settings.Default.EnableAutoNavigation &&
                            Settings.Default.dcPreset != "None" && !string.IsNullOrEmpty(Settings.Default.dcPreset))
                        {
                            Settings.Default.dcCommandString = PresetProfileApplier.BuildCommandString(
                                ResolveAutomationProfile(Settings.Default.dcPreset));
                            Settings.Default.CommandString = Settings.Default.dcCommandString;
                            Settings.Default.Save();
                            await TranslatePresetAsync(Settings.Default.dcCommandString, Settings.Default.dcPreset);

                            if (lastAppliedState != "dc") ToastNotification.ShowToastNotification("Discharge Preset Applied!", $"Your discharge preset settings have been applied!");
                            lastAppliedState = "dc";
                        }

                        // Apply GPU Power Saving setting for battery
                        try
                        {
                            int mode = Settings.Default.GpuPowerSavingBattery;
                            await GpuPowerSaving.ApplyAsync((GpuPowerSavingMode)mode);
                            Log.Information("[Automations] GPU Power Saving applied for battery: {Mode}", (GpuPowerSavingMode)mode);
                        }
                        catch (Exception ex)
                        {
                            Log.Information("[Automations] GPU Power Saving battery apply failed: {Message}", ex.Message);
                        }
                    }

                    // Apply GPU Power Saving setting for AC (plugged in)
                    if (statuscode == BatteryStatusAc || statuscode == BatteryStatusCritical
                        || statuscode == BatteryStatusCharging || statuscode == BatteryStatusNoSystemBattery)
                    {
                        try
                        {
                            int mode = Settings.Default.GpuPowerSavingPluggedIn;
                            await GpuPowerSaving.ApplyAsync((GpuPowerSavingMode)mode);
                            Log.Information("[Automations] GPU Power Saving applied for plugged in: {Mode}", (GpuPowerSavingMode)mode);
                        }
                        catch (Exception ex)
                        {
                            Log.Information("[Automations] GPU Power Saving plugged-in apply failed: {Message}", ex.Message);
                        }
                    }

                    // BetterRGB streamer AC policy (OpenRevo's auto-policy):
                    // plugged in → restore the 60FPS streamer; battery → hardware
                    // firmware effects at the same params (0% CPU).
                    try
                    {
                        bool acPlugged = statuscode == BatteryStatusAc || statuscode == BatteryStatusCritical
                            || statuscode == BatteryStatusCharging || statuscode == BatteryStatusNoSystemBattery;
                        App.GetService<BetterRgbStreamService>()?.ApplyAcPolicy(acPlugged);
                    }
                    catch (Exception ex)
                    {
                        Log.Information("[BetterRGB] AC policy apply failed: {Message}", ex.Message);
                    }
                }

                if (e.Mode == PowerModes.Resume)
                {
                    // Always re-apply the current active preset after sleep/hibernation.
                    // The EC and GPU revert to BIOS defaults (low power state) on suspend.
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            // Give the system a moment to reinitialize after resume
                            await Task.Delay(1500);

                            // Reset LibreHardwareMonitor Computer to get fresh NVML/ADL handles.
                            // GPU driver state is stale after hibernation/sleep resume, causing
                            // ExecutionEngineException when LHM tries to use old handles.
                            try
                            {
                                var monitoring = App.GetService<IHardwareMonitoringService>();
                                monitoring?.ResetComputer();
                                Debug.WriteLine("[Resume] LHM Computer reset with fresh handles");
                            }
                            catch (Exception ex)
                            {
                                Debug.WriteLine($"[Resume] LHM reset failed: {ex.Message}");
                            }

                            // Also reset the NvTuning NVML state — it uses a separate NVML
                            // context from LHM and has its own stale handles after resume.
                            try
                            {
                                Scripts.GPUs.NVIDIA.Nvml.Reset();
                                Debug.WriteLine("[Resume] NVML reset for NvTuning");
                            }
                            catch (Exception ex)
                            {
                                Debug.WriteLine($"[Resume] NVML reset failed: {ex.Message}");
                            }

                            // Invalidate Adaptive page diff-tracking so next timer tick
                            // force-applies everything (hardware reverted to BIOS defaults).
                            try
                            {
                                var adaptivePage = App.GetService<Views.Pages.Adaptive>();
                                adaptivePage.InvalidateAllLastValues();
                                Debug.WriteLine("[Resume] Adaptive page diff-tracking invalidated");
                            }
                            catch (Exception ex)
                            {
                                Debug.WriteLine($"[Resume] Adaptive invalidate failed: {ex.Message}");
                            }

                            // Re-apply the current active preset (power limits, V-F curve, etc.)
                            // This covers regular preset mode (non-Adaptive).
                            string activeProfile = Settings.Default.presetProfile;
                            if (!string.IsNullOrEmpty(activeProfile))
                            {
                                try
                                {
                                    await PresetProfileApplier.ApplyProfileAsync(activeProfile, PresetProfileApplier.DisplayName(activeProfile));
                                    Debug.WriteLine($"[Resume] Preset '{activeProfile}' re-applied");
                                    ToastNotification.ShowToastNotification("Resume Preset Applied!", $"Your preset settings have been re-applied after resume.");
                                }
                                catch (Exception ex)
                                {
                                    Debug.WriteLine($"[Resume] Preset re-apply failed: {ex.Message}");
                                }
                            }

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

                            // Re-apply GPU Power Saving based on current power state
                            try
                            {
                                var powerStatus = System.Windows.Forms.SystemInformation.PowerStatus;
                                int mode = (powerStatus.PowerLineStatus == System.Windows.Forms.PowerLineStatus.Offline)
                                    ? Settings.Default.GpuPowerSavingBattery
                                    : Settings.Default.GpuPowerSavingPluggedIn;
                                await GpuPowerSaving.ApplyAsync((GpuPowerSavingMode)mode);
                                Log.Information("[Resume] GPU Power Saving re-applied: {Mode}", (GpuPowerSavingMode)mode);
                            }
                            catch (Exception ex)
                            {
                                Debug.WriteLine($"[Resume] GPU Power Saving re-apply failed: {ex.Message}");
                            }

                            // Re-apply Fan Control EC duties or restart Smart Fan Mode.
                            // Stock presets own their fans in firmware, so re-writing a
                            // custom EC curve after resume would undo the OEM mode engine.
                            try
                            {
                                var ecService = App.GetService<UniwillECService>();
                                if (ecService != null && !IsFanCustomizationAllowed())
                                {
                                    Debug.WriteLine("[Resume] Stock preset active — EC fan duties not re-applied");
                                }
                                else if (ecService != null)
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

                                    var cpuTemps = fanSettings.UnifiedMode
                                        ? (fanSettings.UnifiedTempThresholds ?? EcFanCurve.CpuTemperatures)
                                        : (fanSettings.CpuTempThresholds ?? EcFanCurve.CpuTemperatures);
                                    var gpuTemps = fanSettings.UnifiedMode
                                        ? cpuTemps
                                        : (fanSettings.GpuTempThresholds ?? EcFanCurve.GpuTemperatures);

                                    if (fanSettings.EcSmartFanMode)
                                    {
                                        // Restart Smart Fan Mode after resume
                                        var tempProvider = new Models.FlydigiTemperatureProvider();
                                        var smartFan = Uniwill_Control_Utility.Services.EcSmartFanControl.Recreate(
                                            ecService,
                                            tempProvider,
                                            fanSettings,
                                            cpuCurve,
                                            cpuTemps);
                                        smartFan.Start();
                                        Debug.WriteLine("[Resume] Smart Fan Mode re-started");
                                    }
                                    else
                                    {
                                        ecService.ApplyFanCurve(cpuCurve, gpuCurve, cpuTemps, gpuTemps);
                                        Debug.WriteLine("[Resume] Fan Control EC duties re-applied");
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                Debug.WriteLine($"[Resume] Fan Control re-apply failed: {ex.Message}");
                            }

                            // Auto-reconnect Hydro UI watercooler.
                            if (Properties.Settings.Default.EnableHydroUiNavigation)
                            {
                                var hydroUIService = App.GetService<HydroUIService>();
                                if (hydroUIService != null)
                                {
                                    // If the service still thinks it's connected, the BLE link is a
                                    // zombie - force a disconnect before attempting reconnect.
                                    if (hydroUIService.IsConnected)
                                    {
                                        Debug.WriteLine("[WaterCooler] Resuming from sleep - disconnecting zombie BLE link");
                                        await hydroUIService.DisconnectAsync();
                                        await Task.Delay(500);
                                    }

                                    bool connected = await hydroUIService.TryAutoConnectAsync();
                                    if (connected)
                                        Debug.WriteLine("[WaterCooler] Auto-reconnected after system resume");
                                }
                            }

                            // Resume Flydigi cooler (HID) background services.
                            if (Properties.Settings.Default.EnableFlydigi)
                            {
                                var flydigiService = App.GetService<FlydigiCoolerHidService>();
                                if (flydigiService != null)
                                {
                                    try
                                    {
                                        // If the service still thinks it's connected, the HID link
                                        // may be stale after hibernate — reconnect.
                                        if (flydigiService.IsConnected)
                                        {
                                            Debug.WriteLine("[Flydigi] Resuming from sleep - disconnecting stale HID link");
                                            flydigiService.DisconnectAsync();
                                            Task.Delay(500).Wait();
                                        }

                                        flydigiService.ResumeBackgroundServices();
                                        Debug.WriteLine("[Flydigi] Background services resumed after system resume");
                                    }
                                    catch (Exception ex)
                                    {
                                        Debug.WriteLine($"[Flydigi] Resume failed: {ex.Message}");
                                    }
                                }
                            }

                            // Resume BS1 cooler (BLE) background services.
                            if (Properties.Settings.Default.EnableFlydigi)
                            {
                                var bs1Service = App.GetService<FlydigiCoolerBleService>();
                                if (bs1Service != null)
                                {
                                    try
                                    {
                                        if (bs1Service.IsConnected)
                                        {
                                            Debug.WriteLine("[BS1] Resuming from sleep - disconnecting zombie BLE link");
                                            bs1Service.DisconnectAsync();
                                            await Task.Delay(500);
                                        }

                                        bs1Service.ResumeBackgroundServices();
                                        Debug.WriteLine("[BS1] Background services resumed after system resume");
                                    }
                                    catch (Exception ex)
                                    {
                                        Debug.WriteLine($"[BS1] Resume failed: {ex.Message}");
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"[Resume] Auto-reapply failed: {ex.Message}");
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                DiagnosticLogger.LogError(ex, "Failed during power mode change handling");
            }
        }

        #region Display Change Detection

        private HwndSourceHook? _hwndHook;

        private void OnSourceInitialized(object? sender, EventArgs e)
        {
            SourceInitialized -= OnSourceInitialized;

            var source = PresentationSource.FromVisual(this) as HwndSource;
            if (source is null)
                return;

            _hwndHook = OnWindowMessage;
            source.AddHook(_hwndHook);
        }

        /// <summary>
        /// WM_DISPLAYCHANGE (0x007E) fires when the display topology changes
        /// (monitor added/removed, resolution change, orientation change).
        /// We use this to proactively reset LHM Computer before stale NVML handles cause crashes.
        /// </summary>
        private const int WM_DISPLAYCHANGE = 0x007E;

        // SM_CMONITORS for GetSystemMetrics — tracks how many displays are active
        // so we only reset GPU driver state on real topology changes, not fullscreen alt-tabs.
        private const int SM_CMONITORS = 80;
        private int _lastMonitorCount = -1;

        [System.Runtime.InteropServices.LibraryImport("user32.dll")]
        private static partial int GetSystemMetrics(int nIndex);

        private IntPtr OnWindowMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_DISPLAYCHANGE)
            {
                int currentMonitorCount = GetSystemMetrics(SM_CMONITORS);
                bool topologyChanged = _lastMonitorCount < 0 || currentMonitorCount != _lastMonitorCount;
                _lastMonitorCount = currentMonitorCount;

                // Don't mark as handled — other WPF code may need this message.
                // Only reset GPU driver state when the monitor count actually changes.
                // Fullscreen alt-tabs fire WM_DISPLAYCHANGE but don't change the
                // monitor count — resetting on those causes a 15-second NVML
                // blackout (GpuInteropGuard cooldown) and metrics show zeros.
                if (topologyChanged)
                {
                    try
                    {
                        var monitoring = App.GetService<IHardwareMonitoringService>();
                        monitoring?.ResetComputer();
                        Debug.WriteLine($"[Display] LHM Computer reset after display topology change ({_lastMonitorCount} monitors)");
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[Display] LHM reset failed: {ex.Message}");
                    }

                    // Also reset NvTuning's NVML context — separate from LHM.
                    try
                    {
                        Scripts.GPUs.NVIDIA.Nvml.Reset();
                        Debug.WriteLine("[Display] NVML reset for NvTuning");
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[Display] NVML reset failed: {ex.Message}");
                    }

                    // Re-assert AppUserModelID to restore taskbar icon after display
                    // topology change (dGPU disable/enable invalidates DWM composition).
                    try
                    {
                        App.ReassertAppUserModelID();
                        Debug.WriteLine("[Display] AppUserModelID re-asserted after display change");
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[Display] AppUserModelID reassert failed: {ex.Message}");
                    }
                }
            }

            return IntPtr.Zero;
        }

        #endregion

        #region INavigationWindow Methods

        public INavigationView GetNavigation() => RootNavigation;

        public bool Navigate(Type pageType)
        {
            bool result = RootNavigation.Navigate(pageType);
            Visibility visibility = pageType == typeof(Views.Pages.DashboardPage)
                ? Visibility.Collapsed : Visibility.Visible;
            ModelNameText.Visibility = visibility;
            LaptopNameCard.Visibility = visibility;
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
            // Remove the window hook to avoid keeping the HwndSource alive
            var source = PresentationSource.FromVisual(this) as HwndSource;
            source?.RemoveHook(_hwndHook);

            SystemEvents.PowerModeChanged -= HandlePowerModeChange;

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
                // Determine active preset mode for the "checked" indicator
                Models.PresetMode activeMode = Settings.Default.EnablePowerBoostMode
                    ? Models.PresetMode.Boost
                    : Models.PresetMode.Normal;
                int activeSlot = activeMode == Models.PresetMode.Boost
                    ? Settings.Default.customPresetSlotBoost
                    : Settings.Default.customPresetSlot;
                bool isCustomActive = string.Equals(Settings.Default.presetProfile, "Custom", StringComparison.OrdinalIgnoreCase);

                int count = 0;

                // Only show presets from the currently active mode
                var presets = Models.CustomPresetStore.Load(activeMode);
                for (int i = 0; i < presets.Length; i++)
                {
                    if (presets[i].HasChanges)
                    {
                        string modeSuffix = activeMode == Models.PresetMode.Boost ? " (Boost)" : "";
                        var item = new MenuItem
                        {
                            Header = $"Custom {i + 1}{modeSuffix}",
                            Tag = (i, activeMode),
                            IsCheckable = true,
                            IsChecked = isCustomActive && i == activeSlot
                        };
                        item.Click += TrayCustomPreset_ClickExtended;
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
            // Legacy handler – delegates to extended handler with Normal mode.
            if (sender is not MenuItem menuItem || menuItem.Tag is not int slot)
                return;
            await TrayCustomPresetApplyAsync(slot, Models.PresetMode.Normal);
        }

        private async void TrayCustomPreset_ClickExtended(object? sender, RoutedEventArgs e)
        {
            if (_isTrayPresetApplying || sender is not MenuItem menuItem || menuItem.Tag is not ValueTuple<int, Models.PresetMode> tuple)
                return;
            await TrayCustomPresetApplyAsync(tuple.Item1, tuple.Item2);
        }

        private async Task TrayCustomPresetApplyAsync(int slot, Models.PresetMode mode)
        {
            if (_isTrayPresetApplying)
                return;
            _isTrayPresetApplying = true;
            try
            {
                var presets = Models.CustomPresetStore.Load(mode);
                if (slot >= presets.Length)
                    return;

                var p = presets[slot];
                string command = BuildCustomCommandString(p);
                if (string.IsNullOrWhiteSpace(command))
                    return;

                await ApplyCustomOnStart(command, p);
                string modeSuffix = mode == Models.PresetMode.Boost ? " (Boost)" : "";
                ToastNotification.ShowToastNotification($"Custom {slot + 1}{modeSuffix} Applied!",
                    $"Custom preset (Profile {slot + 1}) has been applied!");

                Settings.Default.presetProfile = "Custom";
                if (mode == Models.PresetMode.Boost)
                    Settings.Default.customPresetSlotBoost = slot;
                else
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
                // Unknown profile ? fall back to raw command execution
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
        }

        private void mainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            if (Settings.Default.StartMini)
            {
                this.WindowState = WindowState.Minimized;
            }

            // Adjust window height based on feature toggles.
            // When both Hydro UI and Flydigi are disabled, use a smaller height.
            // Runs here (Loaded) instead of the constructor so the window is already
            // shown and the layout is finalized, avoiding a visible flash at 1080.
            bool hydroDisabled = !Settings.Default.EnableHydroUiNavigation;
            bool flydigiDisabled = !Settings.Default.EnableFlydigi;
            if (hydroDisabled && flydigiDisabled)
            {
                this.Height = 907;
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
            _ = App.GetService<Views.Pages.HydroUI>();
            _ = App.GetService<Views.Pages.FlydigiCooler>();

            Task.Run(Garbage.Garbage_Collect);
        }

        #region Tray Menu Handlers

        #region Hydro UI Tray Handlers

        private async void TrayHydroPump_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem item || item.Tag is not string tag)
                return;

            var wc = App.GetService<HydroUIService>();
            if (wc == null || !wc.IsConnected)
            {
                ToastNotification.ShowToastNotification("Hydro UI", "HydroUI not connected");
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

            var wc = App.GetService<HydroUIService>();
            if (wc == null || !wc.IsConnected)
            {
                ToastNotification.ShowToastNotification("Hydro UI", "HydroUI not connected");
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

            var wc = App.GetService<HydroUIService>();
            if (wc == null || !wc.IsConnected)
            {
                ToastNotification.ShowToastNotification("Hydro UI", "HydroUI not connected");
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

        private void TrayHydroSmartMode_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem item)
                return;

            bool enabled = item.IsChecked == true;
            var wc = App.GetService<HydroUIService>();
            if (wc == null)
                return;

            var settings = wc.GetSettings();
            settings.SmartModeEnabled = enabled;
            wc.SaveSettings();

            if (enabled)
            {
                if (wc.IsConnected)
                    wc.StartSmartMode();
            }
            else
            {
                wc.StopSmartMode();
            }
        }

        #region Flydigi Tray Handlers

        private async void TrayFlydigiFan_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem item || item.Tag is not string tag)
                return;

            var fc = App.GetService<FlydigiCoolerHidService>();
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
                var fc = App.GetService<FlydigiCoolerHidService>();
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

            var fc = App.GetService<FlydigiCoolerHidService>();
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
                else if (tag == "Quiet") profile = FlydigiFanCurveProfile.CreateQuiet();
                else if (tag == "Balanced") profile = FlydigiFanCurveProfile.CreateBalanced();
                else if (tag == "Performance") profile = FlydigiFanCurveProfile.CreatePerformance();
                else if (tag == "Freezy") profile = FlydigiFanCurveProfile.CreateFreezy();
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

            var fc = App.GetService<FlydigiCoolerHidService>();
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
                    settings.RgbMode = "Off";
                    await fc.WriteRgbOffAsync();
                    break;
                case "SmartTemp":
                    settings.RgbMode = "SmartTemp";
                    await fc.WriteRgbSmartTempAsync();
                    break;
                case "Static":
                    settings.RgbMode = "Static";
                    await fc.WriteRgbStaticAsync(settings.R, settings.G, settings.B, settings.Brightness);
                    break;
                case "Flowing":
                    settings.RgbMode = "Flowing";
                    await fc.WriteRgbFlowingAsync();
                    break;
                case "Breathing":
                    settings.RgbMode = "Breathing";
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
            if (ec != null && IsFanCustomizationAllowed())
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

            // Update visibility of Smart Mode + Profile/CPU/GPU submenus
            UpdateTrayFanProfileVisibility();
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
                mi.IsChecked = mi.Header?.ToString() == presetName;

            var settings = FanControlSettingsService.Load();
            settings.UnifiedPreset = presetName;
            if (!settings.UnifiedMode)
            {
                settings.CpuPreset = presetName;
                settings.GpuPreset = presetName;
            }
            FanControlSettingsService.Save(settings);

            var ec = App.GetService<UniwillECService>();
            if (ec != null && IsFanCustomizationAllowed())
            {
                Models.EcFanCurve curve = presetName == "Custom"
                    ? GetFanCustomCurve(settings.UnifiedDuties)
                    : GetFanPresetCurve(presetName);
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
            if (ec != null && IsFanCustomizationAllowed())
            {
                Models.EcFanCurve cpuCurve = presetName == "Custom"
                    ? GetFanCustomCurve(settings.CpuDuties)
                    : GetFanPresetCurve(presetName);
                Models.EcFanCurve gpuCurve = settings.GpuPreset == "Custom"
                    ? GetFanCustomCurve(settings.GpuDuties)
                    : GetFanPresetCurve(settings.GpuPreset);
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
            if (ec != null && IsFanCustomizationAllowed())
            {
                Models.EcFanCurve cpuCurve = settings.CpuPreset == "Custom"
                    ? GetFanCustomCurve(settings.CpuDuties)
                    : GetFanPresetCurve(settings.CpuPreset);
                Models.EcFanCurve gpuCurve = presetName == "Custom"
                    ? GetFanCustomCurve(settings.GpuDuties)
                    : GetFanPresetCurve(presetName);
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

        /// <summary>
        /// Builds a Custom fan curve from saved duty values, or falls back to Performance.
        /// </summary>
        private static EcFanCurve GetFanCustomCurve(int[]? duties)
        {
            if (duties != null && duties.Length == 11)
            {
                var curve = new EcFanCurve { Name = "Custom" };
                curve.Duties.Clear();
                foreach (var d in duties)
                    curve.Duties.Add(Math.Clamp(d, 0, 100));
                return curve;
            }
            return EcFanCurve.CreatePerformance();
        }

        /// Fan Control customization (page, tray, resume) is Custom-preset-only:
        /// stock presets leave fans to the firmware's per-mode table.
        private static bool IsFanCustomizationAllowed() =>
            !PresetProfileApplier.IsBuiltInProfile(Settings.Default.presetProfile);

        private void TrayFanSmartMode_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem item)
                return;

            bool enabled = item.IsChecked == true;
            var settings = FanControlSettingsService.Load();
            settings.EcSmartFanMode = enabled;
            FanControlSettingsService.Save(settings);

            var ec = App.GetService<UniwillECService>();
            if (ec == null || !IsFanCustomizationAllowed())
                return;

            if (enabled)
            {
                // Start Smart Fan Mode
                var cpuCurve = settings.UnifiedMode
                    ? (settings.UnifiedPreset == "Custom" ? GetFanCustomCurve(settings.UnifiedDuties) : GetFanPresetCurve(settings.UnifiedPreset))
                    : (settings.CpuPreset == "Custom" ? GetFanCustomCurve(settings.CpuDuties) : GetFanPresetCurve(settings.CpuPreset));
                var temps = settings.UnifiedMode
                    ? (settings.UnifiedTempThresholds ?? EcFanCurve.CpuTemperatures)
                    : (settings.CpuTempThresholds ?? EcFanCurve.CpuTemperatures);

                var tempProvider = new Models.FlydigiTemperatureProvider();
                var smartFan = Services.EcSmartFanControl.GetOrCreate(ec, tempProvider, settings, cpuCurve, temps);
                smartFan.Start();
            }
            else
            {
                // Stop Smart Fan Mode and re-apply the normal fan curve
                Services.EcSmartFanControl.ClearInstance();

                var cpuCurve = settings.UnifiedMode
                    ? (settings.UnifiedPreset == "Custom" ? GetFanCustomCurve(settings.UnifiedDuties) : GetFanPresetCurve(settings.UnifiedPreset))
                    : (settings.CpuPreset == "Custom" ? GetFanCustomCurve(settings.CpuDuties) : GetFanPresetCurve(settings.CpuPreset));
                var gpuCurve = settings.UnifiedMode
                    ? cpuCurve
                    : (settings.GpuPreset == "Custom" ? GetFanCustomCurve(settings.GpuDuties) : GetFanPresetCurve(settings.GpuPreset));
                var cpuTemps = settings.UnifiedMode
                    ? (settings.UnifiedTempThresholds ?? EcFanCurve.CpuTemperatures)
                    : (settings.CpuTempThresholds ?? EcFanCurve.CpuTemperatures);
                var gpuTemps = settings.UnifiedMode
                    ? cpuTemps
                    : (settings.GpuTempThresholds ?? EcFanCurve.GpuTemperatures);

                ec.ApplyFanCurve(cpuCurve, gpuCurve, cpuTemps, gpuTemps);
            }
        }

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
                var wc = App.GetService<HydroUIService>();
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

                // Smart Mode toggle
                miHydroSmartMode.IsChecked = settings.SmartModeEnabled;
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
                var fc = App.GetService<FlydigiCoolerHidService>();
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

                // Smart Fan Mode toggle (only visible in unified mode)
                miFanSmartMode.IsChecked = settings.EcSmartFanMode;

                if (settings.UnifiedMode)
                {
                    // Unified mode: show Profile + Smart Mode, hide CPU/GPU Profile
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
        /// Smart Fan Mode is only available in unified mode.
        /// </summary>
        private void UpdateTrayFanProfileVisibility()
        {
            try
            {
                var settings = FanControlSettingsService.Load();
                if (settings.UnifiedMode)
                {
                    miFanSmartMode.Visibility = Visibility.Visible;
                    miFanCurveProfile.Visibility = Visibility.Visible;
                    miFanCpuProfile.Visibility = Visibility.Collapsed;
                    miFanGpuProfile.Visibility = Visibility.Collapsed;
                }
                else
                {
                    miFanSmartMode.Visibility = Visibility.Collapsed;
                    miFanCurveProfile.Visibility = Visibility.Collapsed;
                    miFanCpuProfile.Visibility = Visibility.Visible;
                    miFanGpuProfile.Visibility = Visibility.Visible;
                }
            }
            catch { /* Non-critical: failing to update tray fan profile visibility only affects visual state */ }
        }

        /// <summary>
        /// Hides the Hydro UI tray section when no supported hardware or no cooler is connected.
        /// </summary>
        private void UpdateHydroTrayVisibility()
        {
            if (!WaterCoolerHardwareDetector.IsSupportedHardware())
            {
                miSectionHydro.Visibility = Visibility.Collapsed;
                return;
            }

            var wc = App.GetService<HydroUIService>();
            miSectionHydro.Visibility = wc?.IsConnected == true
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        /// <summary>
        /// Disables or enables tray controls based on Adaptive Mode override state.
        /// Only applies when Adaptive Mode is actually running (isAdaptiveModeRunning = true).
        /// Fan Control is additionally disabled while a stock power preset is active,
        /// since stock presets own their fan curves in firmware.
        /// </summary>
        private void UpdateTrayAdaptiveOverrideState()
        {
            // Only apply override restrictions when Adaptive Mode is running
            if (!Settings.Default.isAdaptiveModeRunning)
            {
                // All controls enabled when Adaptive Mode is off
                miSectionPresets.IsEnabled = true;
                miSectionHydro.IsEnabled = true;
                miSectionFanControl.IsEnabled = IsFanCustomizationAllowed();
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
            miSectionFanControl.IsEnabled = !ecFanOverridden && IsFanCustomizationAllowed();
        }

        #endregion

        #region Custom Preset Boot Apply

        /// <summary>
        /// Builds a RyzenAdj command string from a CustomPreset, mirroring Presets.buildCommandString().
        /// </summary>
        private static string BuildCustomCommandString(Models.CustomPreset p)
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

            // When a V-F curve is active, ApplyCustomOnStart handles the power limit
            // around the curve write. Emit only the clock tokens here to avoid a
            // redundant SetPowerLimit call from the translator.
            if (p.VfCurveOffsets.Count > 0)
            {
                parts.Add($"--NVIDIA-Clocks=0-{core}-{mem}");
            }
            else
            {
                parts.Add($"--NVIDIA-Clocks=0-{core}-{mem}-{totalWatts}-{dbOffset}");
            }

            return string.Join(" ", parts) + " ";
        }

        /// <summary>
        /// Applies a Custom preset on startup: SMU commands, V-F curve, and EC writes.
        /// </summary>
        private static async Task ApplyCustomOnStart(string command, Models.CustomPreset p)
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
