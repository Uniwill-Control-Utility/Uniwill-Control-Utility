using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Universal_x86_Tuning_Utility.Models;
using Universal_x86_Tuning_Utility.Scripts.Misc;
using Universal_x86_Tuning_Utility.Services;

namespace Universal_x86_Tuning_Utility.Views.Pages
{
    public partial class FanControl : Page
    {
        private readonly IHardwareMonitoringService _hardwareMonitoring;
        private readonly UniwillECService? _uniwillEc;
        private readonly FlydigiCoolerService _flydigiService;
        private readonly Bs1Service _bs1Service;
        private bool _loadingSettings;
        private DispatcherTimer? _statusTimer;
        private IDisposable? _monitoringLease;

        public FanControl(
            IHardwareMonitoringService hardwareMonitoring,
            UniwillECService? uniwillEc = null,
            FlydigiCoolerService? flydigiService = null,
            Bs1Service? bs1Service = null)
        {
            _hardwareMonitoring = hardwareMonitoring;
            _uniwillEc = uniwillEc;
            _flydigiService = flydigiService;
            _bs1Service = bs1Service;
            InitializeComponent();

            // Subscribe to curve changes for banner updates
            FanCurveEditor.CurveChanged += (s, e) => UpdateBanner();
            CpuFanCurveEditor.CurveChanged += (s, e) => UpdateBanner();
            GpuFanCurveEditor.CurveChanged += (s, e) => UpdateBanner();

            // Update banner when Flydigi connection state changes (affects cooler name)
            _flydigiService.ConnectionStateChanged += (s, e) => UpdateBanner();
            _bs1Service.ConnectionStateChanged += (s, e) => UpdateBanner();

            // Try to initialize Uniwill EC if injected
            bool uniwillAvailable = false;
            if (_uniwillEc is not null)
            {
                try
                {
                    uniwillAvailable = _uniwillEc.Initialize();
                }
                catch
                {
                    uniwillAvailable = false;
                }
            }

            if (uniwillAvailable)
            {
                SetStatusText(LocalizationService.Get("Ready"));
                UniwillAvailable.Visibility = Visibility.Visible;
                UniwillUnavailable.Visibility = Visibility.Collapsed;

                // Load persisted settings
                var settings = FanControlSettingsService.Load();
                _loadingSettings = true;
                ApplySettings(settings);
                InitializeTempThresholdInputs();
                _loadingSettings = false;

                // Start status polling timer (every 1 second)
                _statusTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(1)
                };
                _statusTimer.Tick += StatusTimer_Tick;
            }
            else
            {
                SetStatusText(LocalizationService.Get("EC hardware not available"));
                UniwillAvailable.Visibility = Visibility.Collapsed;
                UniwillUnavailable.Visibility = Visibility.Visible;
            }
        }

        private void SetStatusText(string message)
        {
            StatusText.Text = message;
        }

        private void ReadFanRpm()
        {
            try
            {
                if (_uniwillEc is null) return;
                int rpm = _uniwillEc.GetMainFanRpm();
                if (rpm > 0)
                    SetStatusText($"{rpm} RPM");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to read fan RPM: {ex.Message}");
            }
        }

        private void StatusTimer_Tick(object? sender, EventArgs e)
        {
            try
            {
                // Get metrics from hardware monitoring (temp, usage, power, clock)
                var snapshot = _hardwareMonitoring.ReadSnapshot();

                // Enrich with EC fan speed data if available
                if (_uniwillEc is not null)
                {
                    try
                    {
                        int cpuPwm = _uniwillEc.GetFanPwm(0);
                        int gpuPwm = _uniwillEc.GetFanPwm(1);
                        snapshot = snapshot with
                        {
                            CpuFanSpeed = (int)Math.Round(cpuPwm * 100.0 / 200.0),
                            GpuFanSpeed = (int)Math.Round(gpuPwm * 100.0 / 200.0)
                        };
                    }
                    catch { /* EC read failed, fan speed stays 0 */ }
                }

                // Update both controls from the single enriched snapshot
                CpuControl.UpdateMetrics(snapshot);
                GpuControl.UpdateMetrics(snapshot);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to read status: {ex.Message}");
            }
        }

      #region Info Banner

        private static readonly SolidColorBrush InfoBrush
            = new(Colors.White) { Opacity = 0.15 };

        private void UpdateBanner()
        {
            // Check all active curve editors for risk levels
            var bannerState = GetBannerState();

            string coolerName = GetCoolerName();

            switch (bannerState)
            {
                case BannerState.FanOff:
                    InfoBanner.BorderBrush = new SolidColorBrush(Colors.Red) { Opacity = 0.5 };
                    InfoBannerIcon.Glyph = "\xE7A7"; // Warning
                    InfoBannerText.Text =
                        $"Profile \"Off\" has been set for one or more fan curves. Please make sure that you are using a {coolerName} cooler to avoid temperature-induced hardware damage.";
                    break;

                case BannerState.ZeroApplied:
                    InfoBanner.BorderBrush = new SolidColorBrush(Colors.Red) { Opacity = 0.5 };
                    InfoBannerIcon.Glyph = "\xE7A7"; // Warning
                    InfoBannerText.Text =
                        $"A zero-fan setting has been applied. Please make sure that you are using a {coolerName} cooler to avoid temperature-induced hardware damage.";
                    break;

                case BannerState.LowCurve:
                    InfoBanner.BorderBrush = new SolidColorBrush(Colors.Orange) { Opacity = 0.5 };
                    InfoBannerIcon.Glyph = "\xE7A7"; // Warning
                    InfoBannerText.Text =
                        $"A custom profile has been applied with a low fan curve. Please make sure that you are using a {coolerName} cooler to avoid temperature-induced hardware damage.";
                    break;

                default:
                    InfoBanner.BorderBrush = new SolidColorBrush(Colors.White) { Opacity = 0.15 };
                    InfoBannerIcon.Glyph = "\xE946"; // Info
                    InfoBannerText.Text =
                        "Default fan curve profiles cannot be edited, switch to Custom in order to make a personalized curve.";
                    break;
            }
        }

        private enum BannerState
        {
            Info,
            FanOff,
            ZeroApplied,
            LowCurve
        }

        private BannerState GetBannerState()
        {
            // Skip temperature warnings when a Flydigi cooler is connected — it handles its own cooling.
            if (_flydigiService.IsConnected || _bs1Service.IsConnected)
                return BannerState.Info;

            // Check if any active preset is "Off"
            bool hasOffProfile = false;

            if (tsUnifiedCurve.IsChecked == true)
            {
                if (GetSelectedPresetName(cbPreset) == "Off")
                    hasOffProfile = true;
            }
            else
            {
                if (GetSelectedPresetName(cbCpuPreset) == "Off" ||
                    GetSelectedPresetName(cbGpuPreset) == "Off")
                    hasOffProfile = true;
            }

            if (hasOffProfile)
                return BannerState.FanOff;

            // Check Custom curves for zero-fan or low-curve
            int[]? cpuDuties = null;
            int[]? gpuDuties = null;

            if (tsUnifiedCurve.IsChecked == true)
            {
                if (GetSelectedPresetName(cbPreset) == "Custom")
                {
                    cpuDuties = FanCurveEditor.GetCurve().Duties.ToArray();
                    gpuDuties = cpuDuties;
                }
            }
            else
            {
                if (GetSelectedPresetName(cbCpuPreset) == "Custom")
                    cpuDuties = CpuFanCurveEditor.GetCurve().Duties.ToArray();
                if (GetSelectedPresetName(cbGpuPreset) == "Custom")
                    gpuDuties = GpuFanCurveEditor.GetCurve().Duties.ToArray();
            }

            // Check for all-zero custom curve (zone 0 is always 0, so check zones 1-10)
            bool allZero = (cpuDuties != null && cpuDuties.Skip(1).All(d => d == 0)) &&
                           (gpuDuties != null && gpuDuties.Skip(1).All(d => d == 0));
            if (allZero)
                return BannerState.ZeroApplied;

            // Check for low curve (highest point <= 25%)
            bool hasLowCurve = false;
            if (cpuDuties != null && cpuDuties.Skip(1).Max() <= 25)
                hasLowCurve = true;
            if (gpuDuties != null && gpuDuties.Skip(1).Max() <= 25)
                hasLowCurve = true;
            if (hasLowCurve)
                return BannerState.LowCurve;

            return BannerState.Info;
        }

        private string GetCoolerName()
        {
            if (_flydigiService?.IsConnected == true || _bs1Service?.IsConnected == true)
                return "Flydigi";
            return "positive pressure";
        }

        #endregion

        #region Settings Persistence

        private void ApplySettings(FanControlSettings settings)
        {
            // Apply temperature thresholds if user customized them
            if (settings.CpuTempThresholds != null && settings.CpuTempThresholds.Length == 11)
            {
                CpuFanCurveEditor.Temperatures = settings.CpuTempThresholds;
            }
            else
            {
                CpuFanCurveEditor.Temperatures = EcFanCurve.CpuTemperatures;
            }

            if (settings.GpuTempThresholds != null && settings.GpuTempThresholds.Length == 11)
            {
                GpuFanCurveEditor.Temperatures = settings.GpuTempThresholds;
            }
            else
            {
                GpuFanCurveEditor.Temperatures = EcFanCurve.GpuTemperatures;
            }

            if (settings.UnifiedTempThresholds != null && settings.UnifiedTempThresholds.Length == 11)
            {
                FanCurveEditor.Temperatures = settings.UnifiedTempThresholds;
            }
            else
            {
                FanCurveEditor.Temperatures = EcFanCurve.CpuTemperatures;
            }

            // Restore saved preset selections and Custom duties.
            if (settings.UnifiedMode)
            {
                tsUnifiedCurve.IsChecked = true;
                UnifiedCard.IsExpanded = true;
                SplitCurvePanel.Visibility = Visibility.Collapsed;
                LoadPresetIntoEditor(FanCurveEditor, cbPreset, settings.UnifiedPreset, settings.UnifiedDuties);
            }
            else
            {
                tsUnifiedCurve.IsChecked = false;
                UnifiedCard.IsExpanded = false;
                SplitCurvePanel.Visibility = Visibility.Visible;
                LoadPresetIntoEditor(CpuFanCurveEditor, cbCpuPreset, settings.CpuPreset, settings.CpuDuties);
                LoadPresetIntoEditor(GpuFanCurveEditor, cbGpuPreset, settings.GpuPreset, settings.GpuDuties);
            }
        }

        private void InitializeTempThresholdInputs()
        {
            PopulateTempGrid(ugUnifiedTemps, ugUnifiedTempInputs, FanCurveEditor.Temperatures, "Unified");
            PopulateTempGrid(ugCpuTemps, ugCpuTempInputs, CpuFanCurveEditor.Temperatures, "CPU");
            PopulateTempGrid(ugGpuTemps, ugGpuTempInputs, GpuFanCurveEditor.Temperatures, "GPU");
        }

        private void PopulateTempGrid(UniformGrid labelsGrid, UniformGrid inputsGrid, int[] temps, string prefix)
        {
            labelsGrid.Children.Clear();
            inputsGrid.Children.Clear();

            for (int i = 0; i < temps.Length; i++)
            {
                // Zone label
                var label = new TextBlock
                {
                    Text = i == 0 ? "OFF" : $"Z{i}",
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    FontSize = 10,
                    Foreground = (Brush?)Application.Current.Resources["TextFillColorTertiaryBrush"],
                };
                labelsGrid.Children.Add(label);

                // Temperature input
                var textBox = new TextBox
                {
                    Text = temps[i].ToString(),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Width = 40,
                    Padding = new Thickness(2, 2, 2, 2),
                    FontSize = 11,
                    HorizontalContentAlignment = HorizontalAlignment.Center,
                    Tag = new object[] { prefix, i },
                };
                textBox.LostKeyboardFocus += TempTextBox_LostFocus;
                inputsGrid.Children.Add(textBox);
            }
        }

        private void TempTextBox_LostFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            if (sender is not TextBox tb) return;
            if (tb.Tag is not object[] tag || tag.Length != 2) return;

            string prefix = (string)tag[0];
            int index = (int)tag[1];

            if (!int.TryParse(tb.Text, out int value))
            {
                // Revert to original value on invalid input
                tb.Text = "0";
                value = 0;
            }

            value = Math.Clamp(value, 0, 127);
            tb.Text = value.ToString();

            // Update the corresponding editor's temperatures array
            int[]? newTemps = null;
            if (prefix == "Unified")
                newTemps = (int[])FanCurveEditor.Temperatures.Clone();
            else if (prefix == "CPU")
                newTemps = (int[])CpuFanCurveEditor.Temperatures.Clone();
            else if (prefix == "GPU")
                newTemps = (int[])GpuFanCurveEditor.Temperatures.Clone();

            if (newTemps != null && index < newTemps.Length)
            {
                newTemps[index] = value;
                if (prefix == "Unified")
                    FanCurveEditor.Temperatures = newTemps;
                else if (prefix == "CPU")
                    CpuFanCurveEditor.Temperatures = newTemps;
                else
                    GpuFanCurveEditor.Temperatures = newTemps;
            }

            // Save settings when not loading
            if (!_loadingSettings)
                SaveCurrentSettings();
        }

        /// <summary>Reset unified temperature thresholds to XMG Control Center defaults.</summary>
        private void btnResetUnifiedTemps_Click(object sender, RoutedEventArgs e)
        {
            ResetTempsToDefault(FanCurveEditor, ugUnifiedTemps, ugUnifiedTempInputs);
        }

        /// <summary>Reset CPU temperature thresholds to XMG Control Center defaults.</summary>
        private void btnResetCpuTemps_Click(object sender, RoutedEventArgs e)
        {
            ResetTempsToDefault(CpuFanCurveEditor, ugCpuTemps, ugCpuTempInputs);
        }

        /// <summary>Reset GPU temperature thresholds to XMG Control Center defaults.</summary>
        private void btnResetGpuTemps_Click(object sender, RoutedEventArgs e)
        {
            ResetTempsToDefault(GpuFanCurveEditor, ugGpuTemps, ugGpuTempInputs);
        }

        private void ResetTempsToDefault(
            Universal_x86_Tuning_Utility.Views.Controls.EcFanCurveEditor editor,
            UniformGrid labelsGrid, UniformGrid inputsGrid)
        {
            int[] defaults = EcFanCurve.DefaultTemperatures;
            editor.Temperatures = (int[])defaults.Clone();

            // Refresh the input boxes
            inputsGrid.Children.Clear();
            foreach (var temp in defaults)
            {
                var textBox = new TextBox
                {
                    Text = temp.ToString(),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Width = 40,
                    Padding = new Thickness(2, 2, 2, 2),
                    FontSize = 11,
                    HorizontalContentAlignment = HorizontalAlignment.Center,
                };
                // Re-derive prefix from which grid we're updating
                string prefix = labelsGrid == ugUnifiedTemps ? "Unified"
                              : labelsGrid == ugCpuTemps ? "CPU"
                              : "GPU";
                int index = inputsGrid.Children.Count;
                textBox.Tag = new object[] { prefix, index };
                textBox.LostKeyboardFocus += TempTextBox_LostFocus;
                inputsGrid.Children.Add(textBox);
            }

            if (!_loadingSettings)
                SaveCurrentSettings();
        }

        private static void LoadPresetIntoEditor(
            Universal_x86_Tuning_Utility.Views.Controls.EcFanCurveEditor editor,
            ComboBox comboBox, string presetName, int[]? customDuties)
        {
            EcFanCurve curve;
            int selectedIndex;

            if (presetName == "Custom")
            {
                // Custom: use saved duties, or fall back to Performance as baseline.
                if (customDuties != null && customDuties.Length == 11)
                {
                    curve = new EcFanCurve { Name = "Custom" };
                    curve.Duties.Clear();
                    foreach (var d in customDuties)
                        curve.Duties.Add(Math.Clamp(d, 0, 100));
                }
                else
                {
                    curve = EcFanCurve.CreatePerformance();
                    curve.Name = "Custom";
                }
                selectedIndex = 5; // Custom
                editor.IsReadOnly = false;
            }
            else
            {
                // Default preset: load from factory definition, lock the editor.
                curve = presetName switch
                {
                    "Silent" => EcFanCurve.CreateSilent(),
                    "Performance" => EcFanCurve.CreatePerformance(),
                    "Full Speed" => EcFanCurve.CreateFullSpeed(),
                    "Off" => EcFanCurve.CreateOff(),
                    _ => EcFanCurve.CreateBalanced()
                };
                selectedIndex = presetName switch
                {
                    "Silent" => 0,
                    "Performance" => 2,
                    "Full Speed" => 3,
                    "Off" => 4,
                    _ => 1
                };
                editor.IsReadOnly = true;
            }

            editor.SetCurve(curve);
            comboBox.SelectedIndex = selectedIndex;
        }

        private void SaveCurrentSettings()
        {
            var settings = FanControlSettingsService.Load(); // merge with existing

            // Save unified mode toggle
            settings.UnifiedMode = tsUnifiedCurve.IsChecked == true;

            // Save preset selections
            settings.UnifiedPreset = GetSelectedPresetName(cbPreset);
            settings.CpuPreset = GetSelectedPresetName(cbCpuPreset);
            settings.GpuPreset = GetSelectedPresetName(cbGpuPreset);

            // Save Custom duties only when Custom is selected
            if (settings.UnifiedPreset == "Custom")
            {
                var curve = FanCurveEditor.GetCurve();
                settings.UnifiedDuties = curve.Duties.ToArray();
            }

            if (settings.CpuPreset == "Custom")
            {
                var curve = CpuFanCurveEditor.GetCurve();
                settings.CpuDuties = curve.Duties.ToArray();
            }

            if (settings.GpuPreset == "Custom")
            {
                var curve = GpuFanCurveEditor.GetCurve();
                settings.GpuDuties = curve.Duties.ToArray();
            }

            // Save temperature thresholds
            settings.CpuTempThresholds = CpuFanCurveEditor.Temperatures;
            settings.GpuTempThresholds = GpuFanCurveEditor.Temperatures;
            settings.UnifiedTempThresholds = FanCurveEditor.Temperatures;

            FanControlSettingsService.Save(settings);
        }

        private static string GetSelectedPresetName(ComboBox comboBox)
        {
            return (comboBox.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content?.ToString()
                   ?? "Balanced";
        }

        #endregion

        #region Preset Selection

        private void cbPreset_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (cbPreset.SelectedItem is not ComboBoxItem item) return;
            string? name = item.Content?.ToString();
            HandlePresetSelection(FanCurveEditor, name);
            if (!_loadingSettings)
                SaveCurrentSettings();
            UpdateBanner();
        }

        private void cbCpuPreset_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (cbCpuPreset.SelectedItem is not ComboBoxItem item) return;
            string? name = item.Content?.ToString();
            HandlePresetSelection(CpuFanCurveEditor, name);
            if (!_loadingSettings)
                SaveCurrentSettings();
            UpdateBanner();
        }

        private void cbGpuPreset_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (cbGpuPreset.SelectedItem is not ComboBoxItem item) return;
            string? name = item.Content?.ToString();
            HandlePresetSelection(GpuFanCurveEditor, name);
            if (!_loadingSettings)
                SaveCurrentSettings();
            UpdateBanner();
        }

        private void HandlePresetSelection(
            Universal_x86_Tuning_Utility.Views.Controls.EcFanCurveEditor editor, string? name)
        {
            if (name == "Custom")
            {
                // Load saved Custom duties, or fall back to Performance as baseline.
                int[]? savedDuties = GetSavedCustomDuties(editor);
                if (savedDuties != null)
                {
                    var curve = new EcFanCurve { Name = "Custom" };
                    curve.Duties.Clear();
                    foreach (var d in savedDuties)
                        curve.Duties.Add(Math.Clamp(d, 0, 100));
                    editor.SetCurve(curve);
                }
                else
                {
                    editor.SetCurve(EcFanCurve.CreatePerformance());
                }
                editor.IsReadOnly = false;
            }
            else
            {
                editor.SetCurve(GetPresetCurve(name));
                editor.IsReadOnly = true;
            }
        }

        private int[]? GetSavedCustomDuties(Universal_x86_Tuning_Utility.Views.Controls.EcFanCurveEditor editor)
        {
            var settings = FanControlSettingsService.Load();
            if (editor == FanCurveEditor)
                return settings.UnifiedDuties;
            if (editor == CpuFanCurveEditor)
                return settings.CpuDuties;
            if (editor == GpuFanCurveEditor)
                return settings.GpuDuties;
            return null;
        }

        private static EcFanCurve GetPresetCurve(string? name) => name switch
        {
            "Silent" => EcFanCurve.CreateSilent(),
            "Balanced" => EcFanCurve.CreateBalanced(),
            "Performance" => EcFanCurve.CreatePerformance(),
            "Full Speed" => EcFanCurve.CreateFullSpeed(),
            "Off" => EcFanCurve.CreateOff(),
            _ => EcFanCurve.CreateBalanced()
        };

        #endregion

         #region Unified Curve

      private void tsUnifiedCurve_Checked(object sender, RoutedEventArgs e)
        {
            // Skip if triggered by ApplySettings during startup
            if (_loadingSettings) return;

            // Read settings once, save CPU/GPU state, then load unified preset.
            var settings = FanControlSettingsService.Load();
            settings.CpuPreset = GetSelectedPresetName(cbCpuPreset);
            settings.GpuPreset = GetSelectedPresetName(cbGpuPreset);
            if (settings.CpuPreset == "Custom")
                settings.CpuDuties = CpuFanCurveEditor.GetCurve().Duties.ToArray();
            if (settings.GpuPreset == "Custom")
                settings.GpuDuties = GpuFanCurveEditor.GetCurve().Duties.ToArray();

            // Guard against SelectionChanged events firing and corrupting state
            _loadingSettings = true;

            // Load unified preset into the unified editor
            LoadPresetIntoEditor(FanCurveEditor, cbPreset, settings.UnifiedPreset, settings.UnifiedDuties);

            // Expand the card, hide split panel
            UnifiedCard.IsExpanded = true;
            SplitCurvePanel.Visibility = Visibility.Collapsed;

            _loadingSettings = false;
            settings.UnifiedMode = true;
            FanControlSettingsService.Save(settings);

            // Apply curve in the background without blocking the UI
            ApplyCurveInBackground();
            UpdateBanner();
        }

        private void tsUnifiedCurve_Unchecked(object sender, RoutedEventArgs e)
        {
            // Skip if triggered by ApplySettings during startup
            if (_loadingSettings) return;

            // Read settings once, save unified state, then restore CPU/GPU.
            var settings = FanControlSettingsService.Load();
            settings.UnifiedPreset = GetSelectedPresetName(cbPreset);
            if (settings.UnifiedPreset == "Custom")
                settings.UnifiedDuties = FanCurveEditor.GetCurve().Duties.ToArray();

            // Guard against SelectionChanged events firing and corrupting state
            _loadingSettings = true;

            // Restore saved CPU/GPU state
            LoadPresetIntoEditor(CpuFanCurveEditor, cbCpuPreset, settings.CpuPreset, settings.CpuDuties);
            LoadPresetIntoEditor(GpuFanCurveEditor, cbGpuPreset, settings.GpuPreset, settings.GpuDuties);

            // Collapse the card, show split panel
            UnifiedCard.IsExpanded = false;
            SplitCurvePanel.Visibility = Visibility.Visible;

            _loadingSettings = false;
            settings.UnifiedMode = false;
            FanControlSettingsService.Save(settings);

            // Apply curves in the background without blocking the UI
            ApplyCurveInBackground();
            UpdateBanner();
        }

        /// <summary>
        /// If the user clicked the chevron (not the toggle), sync the toggle to match.
        /// </summary>
        private void UnifiedCard_Expanded(object sender, RoutedEventArgs e)
        {
            if (tsUnifiedCurve.IsChecked != true)
            {
                // Chevron was clicked — sync toggle and apply unified logic
                tsUnifiedCurve.IsChecked = true;
                var cpuCurve = CpuFanCurveEditor.GetCurve();
                FanCurveEditor.SetCurve(cpuCurve);
                SplitCurvePanel.Visibility = Visibility.Collapsed;
            }
        }

        /// <summary>
        /// If the user clicked the chevron (not the toggle), sync the toggle to match.
        /// </summary>
        private void UnifiedCard_Collapsed(object sender, RoutedEventArgs e)
        {
            if (tsUnifiedCurve.IsChecked != false)
            {
                // Chevron was clicked — sync toggle and apply split logic
                tsUnifiedCurve.IsChecked = false;
                var unifiedCurve = FanCurveEditor.GetCurve();
                CpuFanCurveEditor.SetCurve(unifiedCurve);
                SplitCurvePanel.Visibility = Visibility.Visible;
            }
        }

        #endregion

       #region Apply / Restore

        /// <summary>
        /// Captures the current curve state and applies it to the EC in the background.
        /// Returns immediately — the UI never blocks.
        /// </summary>
        private void ApplyCurveInBackground()
        {
            if (_uniwillEc is null) return;

            // Capture the curve data on the UI thread, then send to EC in the background.
            EcFanCurve cpuCurve;
            EcFanCurve gpuCurve;
            int[] cpuTemps;
            int[] gpuTemps;

            if (tsUnifiedCurve.IsChecked == true)
            {
                cpuCurve = FanCurveEditor.GetCurve();
                cpuCurve.Name = GetSelectedPresetName(cbPreset);
                gpuCurve = cpuCurve;
                cpuTemps = FanCurveEditor.Temperatures;
                gpuTemps = cpuTemps;
            }
            else
            {
                cpuCurve = CpuFanCurveEditor.GetCurve();
                cpuCurve.Name = GetSelectedPresetName(cbCpuPreset);
                gpuCurve = GpuFanCurveEditor.GetCurve();
                gpuCurve.Name = GetSelectedPresetName(cbGpuPreset);
                cpuTemps = CpuFanCurveEditor.Temperatures;
                gpuTemps = GpuFanCurveEditor.Temperatures;
            }

            _ = Task.Run(() =>
            {
                try
                {
                    _uniwillEc.ApplyFanCurve(cpuCurve, gpuCurve, cpuTemps, gpuTemps);
                    Dispatcher.Invoke(() =>
                    {
                        SaveCurrentSettings();
                        SetStatusText(LocalizationService.Get("Fan curve applied"));
                        UpdateBanner();
                    });
                }
                catch (Exception ex)
                {
                    Dispatcher.Invoke(() =>
                    {
                        SetStatusText($"Error: {ex.Message}");
                    });
                    System.Diagnostics.Debug.WriteLine($"Failed to apply fan curve: {ex.Message}");
                }
            });
        }

        private void btnApplyCurve_Click(object sender, RoutedEventArgs e)
        {
            if (_uniwillEc is null) return;

            // Show immediate feedback, then apply in background
            if (tsUnifiedCurve.IsChecked == true)
            {
                var name = (cbPreset.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Custom";
                SetStatusText(LocalizationService.Format("Applying unified curve: {0}", name));
            }
            else
            {
                SetStatusText(LocalizationService.Get("Applying fan curves..."));
            }

            ApplyCurveInBackground();
        }

        private void btnRestore_Click(object sender, RoutedEventArgs e)
        {
            if (_uniwillEc is null) return;

            SetStatusText(LocalizationService.Get("Restoring auto mode..."));

            _ = Task.Run(() =>
            {
                try
                {
                    _uniwillEc.RestoreAutoFanControl();
                    Dispatcher.Invoke(() =>
                    {
                        ReadFanRpm();
                        SetStatusText(LocalizationService.Get("Auto mode restored"));

                        // Reset UI to Balanced presets
                        _loadingSettings = true;

                        // Disable unified mode if active
                        if (tsUnifiedCurve.IsChecked == true)
                        {
                            tsUnifiedCurve.IsChecked = false;
                            UnifiedCard.IsExpanded = false;
                            SplitCurvePanel.Visibility = Visibility.Visible;
                        }

                        // Set all combo boxes to Balanced
                        cbPreset.SelectedIndex = 1;
                        cbCpuPreset.SelectedIndex = 1;
                        cbGpuPreset.SelectedIndex = 1;

                        // Load Balanced curves into editors
                        CpuFanCurveEditor.SetCurve(EcFanCurve.CreateBalanced());
                        CpuFanCurveEditor.IsReadOnly = true;
                        GpuFanCurveEditor.SetCurve(EcFanCurve.CreateBalanced());
                        GpuFanCurveEditor.IsReadOnly = true;
                        FanCurveEditor.SetCurve(EcFanCurve.CreateBalanced());
                        FanCurveEditor.IsReadOnly = true;

                        _loadingSettings = false;

                        // Persist reset state
                        var settings = FanControlSettingsService.Load();
                        settings.UnifiedMode = false;
                        settings.UnifiedPreset = "Balanced";
                        settings.CpuPreset = "Balanced";
                        settings.GpuPreset = "Balanced";
                        settings.UnifiedDuties = null;
                        settings.CpuDuties = null;
                        settings.GpuDuties = null;
                        FanControlSettingsService.Save(settings);
                    });
                }
                catch (Exception ex)
                {
                    Dispatcher.Invoke(() =>
                    {
                        SetStatusText($"Error: {ex.Message}");
                        System.Windows.MessageBox.Show(
                            $"Failed to restore auto mode: {ex.Message}",
                            "Error",
                            System.Windows.MessageBoxButton.OK,
                            System.Windows.MessageBoxImage.Error);
                    });
                }
            });
        }

        #endregion

        #region Turbo

        private void tsTurbo_Checked(object sender, RoutedEventArgs e)
        {
            if (_uniwillEc is null) return;

            SetStatusText(LocalizationService.Get("Enabling turbo mode..."));
            SetTurboSwitchColor(true);

            _ = Task.Run(() =>
            {
                try
                {
                    _uniwillEc.ToggleTurboAsync().GetAwaiter().GetResult();
                    Dispatcher.Invoke(() =>
                    {
                        SetStatusText(LocalizationService.Get("Turbo mode ENABLED — fans at max duty"));
                        ReadFanRpm();
                    });
                }
                catch (Exception ex)
                {
                    Dispatcher.Invoke(() =>
                    {
                        tsTurbo.IsChecked = false;
                        SetTurboSwitchColor(false);
                        SetStatusText($"Error: {ex.Message}");
                        System.Windows.MessageBox.Show(
                            $"Failed to enable turbo: {ex.Message}",
                            "Error",
                            System.Windows.MessageBoxButton.OK,
                            System.Windows.MessageBoxImage.Error);
                    });
                }
            });
        }

        private void tsTurbo_Unchecked(object sender, RoutedEventArgs e)
        {
            if (_uniwillEc is null) return;

            SetStatusText(LocalizationService.Get("Disabling turbo mode..."));

            _ = Task.Run(() =>
            {
                try
                {
                    _uniwillEc.ToggleTurbo();
                    Dispatcher.Invoke(() =>
                    {
                        SetStatusText(LocalizationService.Get("Turbo mode DISABLED — previous settings restored"));
                        SetTurboSwitchColor(false);
                        ReadFanRpm();
                    });
                }
                catch (Exception ex)
                {
                    Dispatcher.Invoke(() =>
                    {
                        SetStatusText($"Error: {ex.Message}");
                        System.Windows.MessageBox.Show(
                            $"Failed to disable turbo: {ex.Message}",
                            "Error",
                            System.Windows.MessageBoxButton.OK,
                            System.Windows.MessageBoxImage.Error);
                    });
                }
            });
        }

        /// <summary>
        /// Sets an intense red color on the turbo toggle switch when enabled,
        /// disregarding the system theme's accent color.
        /// </summary>
        private void SetTurboSwitchColor(bool isOn)
        {
            if (isOn)
            {
                // Intense red that stands out regardless of theme
                tsTurbo.Foreground = new SolidColorBrush(Color.FromRgb(255, 30, 30));
            }
            else
            {
                tsTurbo.Foreground = null; // Reset to theme default
            }
        }

        #endregion

        #region Reset Fan State

        private void btnResetFanState_Click(object sender, RoutedEventArgs e)
        {
            if (_uniwillEc is null) return;

            SetStatusText(LocalizationService.Get("Resetting fan state..."));

            _ = Task.Run(() =>
            {
                try
                {
                    _uniwillEc.ResetFanState();
                    Dispatcher.Invoke(() =>
                    {
                        if (tsTurbo.IsChecked == true)
                        {
                            tsTurbo.IsChecked = false;
                            SetTurboSwitchColor(false);
                        }

                        SetStatusText(LocalizationService.Get("Fan state reset — fans at 100%"));
                        ReadFanRpm();
                    });
                }
                catch (Exception ex)
                {
                    Dispatcher.Invoke(() =>
                    {
                        SetStatusText($"Error: {ex.Message}");
                        System.Windows.MessageBox.Show(
                            $"Failed to reset fan state: {ex.Message}",
                            "Error",
                            System.Windows.MessageBoxButton.OK,
                            System.Windows.MessageBoxImage.Error);
                    });
                }
            });
        }

        #endregion

        #region Page Lifecycle

        private void UnifiedCard_Loaded(object sender, RoutedEventArgs e)
        {
            // Defer until template is fully applied
            Application.Current.Dispatcher.BeginInvoke(
                (Action)(() =>
                {
                    // Find the ExpanderToggleButton template part, then locate the chevron inside it
                    var toggle = UnifiedCard.Template?.FindName("ExpanderToggleButton", UnifiedCard) as ToggleButton;
                    if (toggle?.Template is not null)
                    {
                        // The chevron grid lives inside the toggle button's template
                        var chevronGrid = toggle.Template.FindName("ChevronGrid", toggle) as FrameworkElement;
                        if (chevronGrid is not null)
                        {
                            chevronGrid.Visibility = Visibility.Collapsed;
                            return;
                        }
                    }

                    // Fallback: walk visual tree for the first SymbolIcon (the chevron)
                    WalkAndHideChevron(UnifiedCard);
                }),
                DispatcherPriority.Loaded);
        }

        private void WalkAndHideChevron(DependencyObject parent)
        {
            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is Wpf.Ui.Controls.SymbolIcon)
                {
                    ((FrameworkElement)child).Visibility = Visibility.Collapsed;
                    return;
                }
                WalkAndHideChevron(child);
            }
        }

        private void Page_Loaded(object sender, RoutedEventArgs e)
        {
            _statusTimer?.Start();
            UpdateBanner();

            // Acquire hardware monitoring lease on a background thread to avoid
            // blocking the UI while LibreHardwareMonitor probes hardware.
            if (_monitoringLease is null)
            {
                _ = Task.Run(() =>
                {
                    _monitoringLease = _hardwareMonitoring.Acquire(
                        HardwareMonitoringCategory.Cpu |
                        HardwareMonitoringCategory.Gpu);
                });
            }
        }

        private void Page_Unloaded(object? sender, EventArgs e)
        {
            _statusTimer?.Stop();

            // Restore auto fan control and release lease on a background thread
            // to avoid blocking the UI during navigation.
            var ec = _uniwillEc;
            var lease = _monitoringLease;
            _monitoringLease = null;

            if (ec is not null || lease is not null)
            {
                _ = Task.Run(() =>
                {
                    try
                    {
                        ec?.RestoreAutoFanControl();
                    }
                    catch { /* silent fail on unload */ }
                    lease?.Dispose();
                });
            }
        }

        #endregion
    }
}
