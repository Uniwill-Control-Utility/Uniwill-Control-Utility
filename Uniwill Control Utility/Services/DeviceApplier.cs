using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Serilog;
using System.Windows.Media;
using Uniwill_Control_Utility.Models;
using Uniwill_Control_Utility.Scripts.Misc;

namespace Uniwill_Control_Utility.Services
{
    /// <summary>
    /// Centralized service for applying device settings to Flydigi cooler, LCT watercooler,
    /// and Uniwill EC fan curves. This is the single point of truth for what gets sent
    /// to the hardware. Both the device pages and Adaptive Mode route through this service.
    /// </summary>
    public class DeviceApplier
    {
        private readonly FlydigiCoolerHidService _flydigiService;
        private readonly FlydigiCoolerBleService? _bs1Service;
        private readonly HydroUIService _hydroUIService;
        private readonly UniwillECService? _uniwillEc;

        /// <summary>Whether Adaptive Mode is currently overriding Flydigi device control.</summary>
        public bool IsFlydigiOverridden { get; private set; }

        /// <summary>Whether Adaptive Mode is currently overriding HydroUI device control.</summary>
        public bool IsWatercoolerOverridden { get; private set; }

        /// <summary>Whether Adaptive Mode is currently overriding EC Fan Control.</summary>
        public bool IsEcFanOverridden { get; private set; }

        /// <summary>
        /// Whether EC manual control is unavailable: the firmware's 0x0741 bit0 is clear,
        /// so a stock preset (Office/Gaming/Turbo) is active and the OEM Performance-button
        /// engine is armed. Manual control and the button engine are mutually exclusive.
        /// </summary>
        public bool IsManualControlLocked { get; private set; }

        /// <summary>Whether Adaptive Mode is currently overriding Keyboard RGB.</summary>
        public bool IsKeyboardOverridden { get; private set; }

        /// <summary>Whether Adaptive Mode is currently overriding Light Bar.</summary>
        public bool IsLightBarOverridden { get; private set; }

        /// <summary>The last Flydigi preset applied from Adaptive Mode (null when nothing has been applied yet).</summary>
        public FlydigiPresetAppliedEventArgs? LastAppliedFlydigiPreset { get; private set; }

        /// <summary>The last HydroUI preset applied from Adaptive Mode (null when nothing has been applied yet).</summary>
        public WatercoolerPresetAppliedEventArgs? LastAppliedWatercoolerPreset { get; private set; }

        /// <summary>The last EC Fan curve applied from Adaptive Mode (null when nothing has been applied yet).</summary>
        public EcFanPresetAppliedEventArgs? LastAppliedEcFanPreset { get; private set; }

        /// <summary>The last Keyboard preset applied from Adaptive Mode (null when nothing has been applied yet).</summary>
        public KeyboardPresetAppliedEventArgs? LastAppliedKeyboardPreset { get; private set; }

        /// <summary>
        /// Raised when the Flydigi override state changes.
        /// Args: (isOverridden)
        /// </summary>
        public event EventHandler<bool>? FlydigiOverrideChanged;

        /// <summary>
        /// Raised when the HydroUI override state changes.
        /// Args: (isOverridden)
        /// </summary>
        public event EventHandler<bool>? WatercoolerOverrideChanged;

        /// <summary>
        /// Raised when profile-specific Flydigi settings have been applied and the page
        /// should update its UI controls to reflect the profile's values.
        /// </summary>
        public event EventHandler<FlydigiPresetAppliedEventArgs>? FlydigiPresetApplied;

        /// <summary>
        /// Raised when profile-specific HydroUI settings have been applied and the page
        /// should update its UI controls to reflect the profile's values.
        /// </summary>
        public event EventHandler<WatercoolerPresetAppliedEventArgs>? WatercoolerPresetApplied;

        /// <summary>
        /// Raised when the EC Fan override state changes.
        /// Args: (isOverridden)
        /// </summary>
        public event EventHandler<bool>? EcFanOverrideChanged;

        /// <summary>
        /// Raised when the EC manual-control lock state changes.
        /// Args: (isLocked)
        /// </summary>
        public event EventHandler<bool>? ManualControlLockChanged;

        /// <summary>
        /// Raised when profile-specific EC Fan settings have been applied and the Fan Control
        /// page should update its UI controls to reflect the profile's values.
        /// </summary>
        public event EventHandler<EcFanPresetAppliedEventArgs>? EcFanPresetApplied;

        /// <summary>
        /// Raised when the Keyboard RGB override state changes.
        /// Args: (isOverridden)
        /// </summary>
        public event EventHandler<bool>? KeyboardOverrideChanged;

        /// <summary>
        /// Raised when profile-specific Keyboard RGB settings have been applied and the Keyboard
        /// page should update its UI controls to reflect the profile's values.
        /// </summary>
        public event EventHandler<KeyboardPresetAppliedEventArgs>? KeyboardPresetApplied;

        /// <summary>
        /// Raised when the Light Bar override state changes.
        /// Args: (isOverridden)
        /// </summary>
        public event EventHandler<bool>? LightBarOverrideChanged;

        /// <summary>
        /// Saved user Flydigi settings, captured when override is enabled.
        /// Restored when override is lifted.
        /// </summary>
        private Bs2ProSettings? _savedFlydigiSettings;

        /// <summary>
        /// Saved user HydroUI settings, captured when override is enabled.
        /// Restored when override is lifted.
        /// </summary>
        private WaterCoolerSettings? _savedWatercoolerSettings;

       /// <summary>
        /// Saved user EC Fan settings, captured when override is enabled.
        /// Restored when override is lifted.
        /// </summary>
        private FanControlSettings? _savedEcFanSettings;

        /// <summary>
        /// Saved user Keyboard settings, captured when override is enabled.
        /// Restored when override is lifted.
        /// </summary>
        private KeyboardSettings? _savedKeyboardSettings;

        /// <summary>
        /// Saved user Light Bar settings, captured when override is enabled.
        /// Restored when override is lifted.
        /// </summary>
        private (byte R, byte G, byte B, int Brightness, int EffectIndex, int Speed, bool Enabled)? _savedLightBarSettings;

        public DeviceApplier(FlydigiCoolerHidService flydigiService, FlydigiCoolerBleService? bs1Service, HydroUIService hydroUIService, UniwillECService? uniwillEc = null)
        {
            _flydigiService = flydigiService;
            _bs1Service = bs1Service;
            _hydroUIService = hydroUIService;
            _uniwillEc = uniwillEc;
        }

        /* ------------------------------------------------------------------ */
        /*  Override State Management                                          */
        /* ------------------------------------------------------------------ */

        /// <summary>
        /// Enables Adaptive Mode override for the Flydigi device.
        /// Captures the current user settings so they can be restored later.
        /// </summary>
        public void EnableFlydigiOverride()
        {
            if (IsFlydigiOverridden)
                return;

            // Capture current user settings before overriding
            _savedFlydigiSettings = CaptureFlydigiSettings();
            IsFlydigiOverridden = true;
            FlydigiOverrideChanged?.Invoke(this, true);
        }

        /// <summary>
        /// Disables Adaptive Mode override for the Flydigi device.
        /// Restores the previously saved user settings to the service's settings object
        /// and re-applies them to the device immediately (regardless of page visibility).
        /// </summary>
        public async Task DisableFlydigiOverrideAsync()
        {
            if (!IsFlydigiOverridden)
                return;

            IsFlydigiOverridden = false;

            // Restore saved user settings to the service's settings object.
            Bs2ProSettings? saved = _savedFlydigiSettings;
            if (saved != null)
            {
                var currentSettings = _flydigiService.GetSettings();
                CopyFlydigiSettings(saved, currentSettings);
                _flydigiService.PersistSettings();
                _savedFlydigiSettings = null;

                // Re-apply restored settings to the device immediately so the user doesn't
                // have to navigate to the Flydigi page for changes to take effect.
                if (_flydigiService.IsConnected)
                {
                    try
                    {
                        // Map FanMode int to string for ApplyFlydigiFanAsync
                        string fanMode = saved.FanMode switch
                        {
                            0 => "Rpm",   // Manual RPM
                            1 => "Gear",  // Gear Presets
                            2 => "Curve", // Auto Curve (no-op in applier)
                            _ => "Rpm"
                        };
                        await ApplyFlydigiFanAsync(fanMode, saved.ManualGear > 0 ? (byte)saved.ManualGear : null, saved.ManualRpm > 0 ? (ushort?)saved.ManualRpm : null);
                        await ApplyFlydigiRgbAsync(saved.RgbMode, saved.R, saved.G, saved.B, saved.Brightness);
                    }
                    catch (Exception ex)
                    {
                        DiagnosticLogger.LogError(ex, "DeviceApplier: Failed to re-apply user Flydigi settings on override-lift");
                    }
                }
            }

            FlydigiOverrideChanged?.Invoke(this, false);
        }

        /// <summary>
        /// Enables Adaptive Mode override for the HydroUI device.
        /// Captures the current user settings so they can be restored later.
        /// </summary>
        public void EnableWatercoolerOverride()
        {
            if (IsWatercoolerOverridden)
                return;

            // Capture current user settings before overriding
            _savedWatercoolerSettings = CaptureWatercoolerSettings();
            IsWatercoolerOverridden = true;
            WatercoolerOverrideChanged?.Invoke(this, true);
        }

        /// <summary>
        /// Disables Adaptive Mode override for the HydroUI device.
        /// Restores the previously saved user settings to the service's settings object
        /// and re-applies them to the device immediately (regardless of page visibility).
        /// </summary>
        public async Task DisableWatercoolerOverrideAsync()
        {
            if (!IsWatercoolerOverridden)
                return;

            IsWatercoolerOverridden = false;

            // Restore saved user settings to the service's settings object.
            WaterCoolerSettings? saved = _savedWatercoolerSettings;
            if (saved != null)
            {
                var currentSettings = _hydroUIService.GetSettings();
                CopyWatercoolerSettings(saved, currentSettings);
                _hydroUIService.SaveSettings();
                _savedWatercoolerSettings = null;

                // Re-apply restored settings to the device immediately.
                // When Smart Mode is configured, skip pump and fan writes — Smart Mode
                // controls them and would fight with a one-shot restore.
                if (_hydroUIService.IsConnected)
                {
                    try
                    {
                        bool smartModeConfigured = currentSettings.SmartModeEnabled;
                        PumpVoltage? pump = smartModeConfigured ? (PumpVoltage?)null : saved.GetPumpVoltage();
                        FanSpeed? fan = smartModeConfigured ? (FanSpeed?)null : saved.GetFanSpeed();
                        await ApplyWatercoolerAsync(pump, fan, saved.GetRgbMode(), saved.GetRgbColor());
                    }
                    catch (Exception ex)
                    {
                        DiagnosticLogger.LogError(ex, "DeviceApplier: Failed to re-apply user HydroUI settings on override-lift");
                    }
                }
            }

            WatercoolerOverrideChanged?.Invoke(this, false);
        }

        /// <summary>
        /// Enables Adaptive Mode override for EC Fan Control.
        /// Captures the current user settings so they can be restored later.
        /// </summary>
        public void EnableEcFanOverride()
        {
            if (IsEcFanOverridden)
                return;

            // Capture current user Fan Control settings before overriding
            _savedEcFanSettings = FanControlSettingsService.Load();
            IsEcFanOverridden = true;
            EcFanOverrideChanged?.Invoke(this, true);
        }

        /// <summary>
        /// Disables Adaptive Mode override for EC Fan Control.
        /// Restores the previously saved user settings and re-applies them to the EC.
        /// </summary>
        public void DisableEcFanOverride()
        {
            if (!IsEcFanOverridden)
                return;

            IsEcFanOverridden = false;

            FanControlSettings? saved = _savedEcFanSettings;
            if (saved != null)
            {
                FanControlSettingsService.Save(saved);
                _savedEcFanSettings = null;

                // Re-apply restored settings to the EC immediately.
                if (_uniwillEc is not null)
                {
                    try
                    {
                        var cpuCurve = saved.CpuPreset == "Custom" && saved.CpuDuties?.Length == 11
                            ? BuildCurveFromSettings(saved.CpuPreset, saved.CpuDuties)
                            : GetPresetCurve(saved.CpuPreset);

                        var gpuCurve = saved.GpuPreset == "Custom" && saved.GpuDuties?.Length == 11
                            ? BuildCurveFromSettings(saved.GpuPreset, saved.GpuDuties)
                            : GetPresetCurve(saved.GpuPreset);

                        var cpuTemps = saved.CpuTempThresholds ?? EcFanCurve.CpuTemperatures;
                        var gpuTemps = saved.GpuTempThresholds ?? EcFanCurve.GpuTemperatures;

                        _uniwillEc.ApplyFanCurve(cpuCurve, gpuCurve, cpuTemps, gpuTemps);
                    }
                    catch (Exception ex)
                    {
                        DiagnosticLogger.LogError(ex, "DeviceApplier: Failed to re-apply user EC Fan settings on override-lift");
                    }
                }
            }

            EcFanOverrideChanged?.Invoke(this, false);
        }

        /// <summary>
        /// Marks EC manual control as unavailable — a stock preset is active and the OEM
        /// Performance-button engine is armed instead.
        /// </summary>
        public void EnableManualControlLock()
        {
            if (IsManualControlLocked)
                return;

            IsManualControlLocked = true;
            ManualControlLockChanged?.Invoke(this, true);
        }

        /// <summary>
        /// Marks EC manual control as available — a custom preset has unlocked 0x0741 bit0,
        /// which disarms the OEM Performance-button engine.
        /// </summary>
        public void DisableManualControlLock()
        {
            if (!IsManualControlLocked)
                return;

            IsManualControlLocked = false;
            ManualControlLockChanged?.Invoke(this, false);
        }

        /// <summary>
        /// Re-reads the EC manual-mode gate and raises ManualControlLockChanged when the
        /// firmware disagrees with our cached view. Called from the preset-apply flow
        /// (page navigation) rather than on a timer — only a preset apply or the OEM
        /// button ever flips the gate.
        /// </summary>
        public void SyncManualControlLock()
        {
            if (_uniwillEc is null)
                return;

            try
            {
                if (_uniwillEc.IsCustomProfileActive())
                    DisableManualControlLock();
                else
                    EnableManualControlLock();
            }
            catch (Exception ex)
            {
                DiagnosticLogger.LogError(ex, "DeviceApplier: Failed to read EC manual-mode lock state");
            }
        }

        /// <summary>
        /// Applies an EC Fan curve from Adaptive Mode.
        /// Fires EcFanPresetApplied so the Fan Control page can sync its UI.
        /// </summary>
        public void ApplyEcFanFromPreset(
            bool unifiedMode,
            string presetName,
            int[]? customDuties,
            int[]? cpuPresetDuties,
            int[]? gpuPresetDuties,
            int[]? cpuTemps = null,
            int[]? gpuTemps = null)
        {
            if (_uniwillEc is null)
                return;

            try
            {
                EcFanCurve cpuCurve;
                EcFanCurve gpuCurve;
                int[] finalCpuTemps = cpuTemps ?? EcFanCurve.CpuTemperatures;
                int[] finalGpuTemps = gpuTemps ?? EcFanCurve.GpuTemperatures;

                if (unifiedMode)
                {
                    cpuCurve = customDuties?.Length == 11
                        ? BuildCurveFromSettings(presetName, customDuties)
                        : GetPresetCurve(presetName);
                    gpuCurve = cpuCurve;
                    finalGpuTemps = cpuTemps ?? finalGpuTemps;
                }
                else
                {
                    // For split mode we need separate preset names — but the profile
                    // only stores a single preset name. We use the same preset for both
                    // unless custom duties are provided per-source.
                    cpuCurve = cpuPresetDuties?.Length == 11
                        ? BuildCurveFromSettings(presetName, cpuPresetDuties)
                        : GetPresetCurve(presetName);
                    gpuCurve = gpuPresetDuties?.Length == 11
                        ? BuildCurveFromSettings(presetName, gpuPresetDuties)
                        : GetPresetCurve(presetName);
                }

                _uniwillEc.ApplyFanCurve(cpuCurve, gpuCurve, finalCpuTemps, finalGpuTemps);

                var args = new EcFanPresetAppliedEventArgs(
                    unifiedMode, presetName, customDuties, cpuPresetDuties, gpuPresetDuties, cpuTemps, gpuTemps);
                LastAppliedEcFanPreset = args;
                EcFanPresetApplied?.Invoke(this, args);
            }
            catch (Exception ex)
            {
                DiagnosticLogger.LogError(ex, "DeviceApplier: Failed to apply EC Fan curve from preset");
            }
        }

        private static EcFanCurve BuildCurveFromSettings(string presetName, int[]? duties)
        {
            var curve = new EcFanCurve { Name = presetName };
            curve.Duties.Clear();
            if (duties != null)
            {
                foreach (var d in duties)
                    curve.Duties.Add(Math.Clamp(d, 0, 100));
            }
            return curve;
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

        /* ------------------------------------------------------------------ */
        /*  Keyboard Override Management                                       */
        /* ------------------------------------------------------------------ */

        /// <summary>
        /// Enables Adaptive Mode override for Keyboard RGB.
        /// Captures the current user settings so they can be restored later.
        /// Persists the saved settings to a separate file so they survive app restarts.
        /// </summary>
        public void EnableKeyboardOverride()
        {
            if (IsKeyboardOverridden)
                return;

            // If a saved override file exists from a previous session (e.g., the user
            // restarted the app while override was active), use it as the saved settings.
            // Otherwise, capture the current settings from the main settings file.
            var existingSaved = KeyboardSettingsService.LoadForOverride();
            if (existingSaved != null)
            {
                _savedKeyboardSettings = existingSaved;
            }
            else
            {
                _savedKeyboardSettings = KeyboardSettingsService.Load();
                // Persist the captured settings so they survive a restart.
                KeyboardSettingsService.SaveForOverride(_savedKeyboardSettings);
            }

            IsKeyboardOverridden = true;

            // Pause the streamer while Adaptive Mode owns the keyboard; the preset
            // apply below either re-params it (BetterRGB) or uses a temporary handle.
            App.GetService<BetterRgbStreamService>()?.SuspendForOverride();

            KeyboardOverrideChanged?.Invoke(this, true);
        }

        /// <summary>
        /// Disables Adaptive Mode override for Keyboard RGB.
        /// Restores the previously saved user settings, persists them to disk,
        /// and applies them to the HID device immediately.
        /// </summary>
        public void DisableKeyboardOverride()
        {
            if (!IsKeyboardOverridden)
                return;

            IsKeyboardOverridden = false;

            KeyboardSettings? saved = _savedKeyboardSettings;
            if (saved != null)
            {
                KeyboardSettingsService.Save(saved);
                _savedKeyboardSettings = null;
            }

            // Delete the override backup file so it doesn't persist after override is lifted.
            KeyboardSettingsService.DeleteSavedSettings();

            // Clear the cached preset so the Keyboard page doesn't re-apply it
            // when navigating after override is lifted.
            LastAppliedKeyboardPreset = null;

            KeyboardOverrideChanged?.Invoke(this, false);

            // Apply restored settings to the HID device immediately so the hardware
            // doesn't stay on the Adaptive Mode preset while the user is still on
            // the Adaptive page.
            if (saved != null)
            {
                var s = saved;
                var streamer = App.GetService<BetterRgbStreamService>();
                _ = Task.Run(() =>
                {
                    if (streamer != null && s.Engine == KeyboardEngine.BetterRGB)
                    {
                        // Restore the user's BetterRGB engine instead of a firmware
                        // apply (the streamer owns the handle).
                        streamer.SetParams(s);
                        streamer.ResumeFromOverride();
                    }
                    else
                    {
                        // A BetterRGB preset may have left the streamer running even
                        // though the restored user engine is firmware — release the
                        // endpoint before the firmware apply, and clear the suspend
                        // flag so the AC policy can manage the engine afterwards.
                        if (streamer != null)
                        {
                            if (streamer.IsRunning)
                                streamer.StopEngine("override-lift");
                            streamer.ResumeFromOverride();
                        }

                        ApplyKeyboardHid(s.PerKeyMode, s.Brightness, s.EffectMode, s.Speed,
                            s.Direction, s.ColorR, s.ColorG, s.ColorB, s.MultiColors, s.PerKeyColors);
                    }
                });
            }
        }

        /* ------------------------------------------------------------------ */
        /*  Light Bar Override                                                 */
        /* ------------------------------------------------------------------ */

        /// <summary>
        /// Enables Adaptive Mode override for the Light Bar.
        /// Captures the current user settings so they can be restored later.
        /// </summary>
        public void EnableLightBarOverride()
        {
            if (IsLightBarOverridden)
                return;

            // Capture current user settings before overriding
            _savedLightBarSettings = (
                R: Properties.Settings.Default.LightBarR,
                G: Properties.Settings.Default.LightBarG,
                B: Properties.Settings.Default.LightBarB,
                Brightness: Properties.Settings.Default.LightBarBrightness,
                EffectIndex: Properties.Settings.Default.LightBarEffectIndex,
                Speed: Properties.Settings.Default.LightBarSpeed,
                Enabled: Properties.Settings.Default.LightBarEnabled
            );

            IsLightBarOverridden = true;
            LightBarOverrideChanged?.Invoke(this, true);
        }

        /// <summary>
        /// Disables Adaptive Mode override for the Light Bar.
        /// Restores the previously saved user settings and re-applies them to the device.
        /// </summary>
        public void DisableLightBarOverride()
        {
            if (!IsLightBarOverridden)
                return;

            IsLightBarOverridden = false;

            var saved = _savedLightBarSettings;
            if (saved.HasValue)
            {
                var s = saved.Value;
                Properties.Settings.Default.LightBarR = s.R;
                Properties.Settings.Default.LightBarG = s.G;
                Properties.Settings.Default.LightBarB = s.B;
                Properties.Settings.Default.LightBarBrightness = s.Brightness;
                Properties.Settings.Default.LightBarEffectIndex = s.EffectIndex;
                Properties.Settings.Default.LightBarSpeed = s.Speed;
                Properties.Settings.Default.LightBarEnabled = s.Enabled;
                Properties.Settings.Default.Save();
                _savedLightBarSettings = null;

                // Re-apply restored settings to the device immediately.
                _ = Task.Run(() =>
                {
                    try
                    {
                        var hid = new KeyboardHidService();
                        try
                        {
                            if (hid.Open() && hid.IsLightBarAvailable)
                            {
                                if (s.Enabled)
                                {
                                    byte[] effectValues = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06 };
                                    int effectIndex = Math.Clamp(s.EffectIndex, 0, 5);
                                    int speed = Math.Clamp(s.Speed, 0, 11);
                                    byte effect = effectValues[effectIndex];
                                    byte hidSpeed = (byte)(11 - speed);

                                    hid.LightBarTurnOn(s.R, s.G, s.B, s.Brightness);
                                    hid.LightBarSetEffect(effect, hidSpeed, s.Brightness);
                                }
                                else
                                {
                                    hid.LightBarTurnOff();
                                }
                            }
                        }
                        finally
                        {
                            hid.Dispose();
                        }
                    }
                    catch (Exception ex)
                    {
                        DiagnosticLogger.LogError(ex, "DeviceApplier: Failed to re-apply user Light Bar settings on override-lift");
                    }
                });
            }

            LightBarOverrideChanged?.Invoke(this, false);
        }

        /* ------------------------------------------------------------------ */
        /*  CPU / GPU Override                                                 */
        /* ------------------------------------------------------------------ */

        /// <summary>
        /// Enables Adaptive Mode override for CPU/GPU power settings.
        /// Captures the currently active Presets page profile so it can be
        /// re-applied when the override is lifted.
        /// </summary>
        public void EnableCpuGpuOverride()
        {
            // No capture needed — DisableCpuGpuOverride re-applies the last
            // known preset profile from Settings.
        }

        /// <summary>
        /// Disables Adaptive Mode override for CPU/GPU power settings.
        /// Re-applies the last active Presets page profile so the hardware
        /// doesn't stay on the Adaptive Mode profile's override values.
        /// </summary>
        public async Task DisableCpuGpuOverrideAsync()
        {
            string? profile = Properties.Settings.Default.presetProfile;
            if (string.IsNullOrEmpty(profile) || profile.Equals("None", StringComparison.OrdinalIgnoreCase))
                return;

            string command = PresetProfileApplier.BuildCommandString(profile);
            if (string.IsNullOrWhiteSpace(command))
                return;

            try
            {
                // 1. RyzenAdj / SMU commands
                await Scripts.RyzenAdj_To_UCU.TranslateAsync(command);

                // 2. EC CPU power limits (AMD APU only)
                if (Scripts.Family.TYPE == Scripts.Family.ProcessorType.Amd_Apu)
                {
                    var ec = Scripts.RyzenAdj_To_UCU.EcService;
                    if (ec != null)
                    {
                        // Extract PL1/PL2 from the command string or current preset values
                        // The command string contains --slow-limit=X and --fast-limit=Y (in milliwatts)
                        foreach (var arg in command.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                        {
                            if (arg.StartsWith("--slow-limit="))
                            {
                                if (int.TryParse(arg.Substring("--slow-limit=".Length), out int slow))
                                    ec.SetPl1(slow / 1000);
                            }
                            else if (arg.StartsWith("--fast-limit="))
                            {
                                if (int.TryParse(arg.Substring("--fast-limit=".Length), out int fast))
                                    ec.SetPl2(fast / 1000);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                DiagnosticLogger.LogError(ex, "DeviceApplier: Failed to re-apply CPU/GPU preset on override-lift");
            }
        }

        /// <summary>
        /// Applies keyboard RGB settings from Adaptive Mode.
        /// Fires KeyboardPresetApplied so the Keyboard page can sync its UI.
        /// </summary>
        public void ApplyKeyboardFromPreset(
            bool perKeyMode,
            int brightness,
            bool idleTimerEnabled,
            int idleTimerMinutes,
            string effectMode,
            byte effectSpeed,
            string direction,
            byte colorR,
            byte colorG,
            byte colorB,
            string multiColors,
            string? perKeyColors)
        {
            try
            {
                // Persist the preset settings so the Keyboard page can pick them up.
                // Load existing settings first to preserve per-key colors when switching
                // to Effects mode — we don't want to wipe saved per-key data.
                var settings = KeyboardSettingsService.Load();
                settings.PowerOn = true;
                // A firmware preset means firmware lighting for the duration of the
                // override: force the hardware engine so the HID apply below is
                // visible. BetterRGB presets route through ApplyBetterRgbFromPreset
                // instead, and the user's engine is restored on override lift.
                settings.Engine = KeyboardEngine.Hardware;
                settings.PerKeyMode = perKeyMode;
                settings.Brightness = brightness;
                settings.IdleTimerEnabled = idleTimerEnabled;
                settings.IdleTimerMinutes = idleTimerMinutes;
                settings.EffectMode = ParseKeyboardEffect(effectMode);
                settings.Speed = effectSpeed;
                settings.Direction = ParseKeyboardDirection(direction);
                settings.ColorR = colorR;
                settings.ColorG = colorG;
                settings.ColorB = colorB;
                settings.MultiColors = multiColors;
                // Only update per-key colors when actually in per-key mode with data.
                // This preserves previously saved per-key colors when the user switches
                // to Effects mode, so they're available if the user switches back.
                if (perKeyMode && !string.IsNullOrEmpty(perKeyColors))
                    settings.PerKeyColors = perKeyColors;
                KeyboardSettingsService.Save(settings);

                var args = new KeyboardPresetAppliedEventArgs(
                    perKeyMode, brightness, idleTimerEnabled, idleTimerMinutes,
                    effectMode, effectSpeed, settings.Direction,
                    colorR, colorG, colorB, multiColors, perKeyColors);
                LastAppliedKeyboardPreset = args;
                KeyboardPresetApplied?.Invoke(this, args);

                // Stop the streamer if it somehow still runs — the firmware apply
                // below opens a temporary handle and must not fight the stream
                // endpoint (EnableKeyboardOverride already suspended it).
                var streamer = App.GetService<BetterRgbStreamService>();
                if (streamer != null && streamer.IsRunning)
                    streamer.StopEngine("adaptive-override");

                // Send HID commands directly so the keyboard updates immediately even when
                // the Keyboard page is not visible. The Keyboard page's debounce timer will
                // also fire if it's visible, but that's harmless (idempotent HID writes).
                // The ITE firmware needs ~500ms to settle after power-on before accepting
                // direction bytes. Without this delay, direction defaults to Left→Right.
                if (settings.Direction != KeyboardDirection.LeftRight)
                    System.Threading.Thread.Sleep(500);
                bool touchReactive = effectMode == "Reactive";
                ApplyKeyboardHid(perKeyMode, brightness, settings.EffectMode, effectSpeed,
                    settings.Direction, colorR, colorG, colorB, multiColors, perKeyColors, touchReactive);
            }
            catch (Exception ex)
            {
                DiagnosticLogger.LogError(ex, "DeviceApplier: Failed to apply Keyboard preset from profile");
            }
        }

        /// <summary>
        /// Applies a BetterRGB streamer preset from an Adaptive profile: forces the
        /// BetterRGB engine for the override duration, maps the profile's full
        /// per-effect parameter set (AdaptiveBetterRgbSettings) into the matching
        /// BetterRgb* settings fields, and re-params (or starts) the streamer.
        /// Clamps mirror the Keyboard page's SaveSettings so the Adaptive override
        /// and the Keyboard card produce identical renderer parameters.
        /// </summary>
        public void ApplyBetterRgbFromPreset(AdaptiveBetterRgbSettings cfg)
        {
            try
            {
                var settings = KeyboardSettingsService.Load();
                settings.Engine = KeyboardEngine.BetterRGB;
                settings.PowerOn = true;
                settings.BetterRgbMode = ParseBetterRgbEffect(cfg.Mode);
                settings.BetterRgbFps = Math.Clamp((int)Math.Round(Math.Clamp(cfg.Fps, 15, 60) / 15.0) * 15, 15, 60);
                settings.TargetFps = settings.BetterRgbFps;
                settings.BrightnessPercent = Math.Clamp(cfg.BrightnessPercent, 3, 100);
                settings.BetterRgbLightBarSync = cfg.LightBarSync;

                // Per-effect parameters — clamps mirror the Keyboard page's SaveSettings.
                settings.BetterRgbBreathSpeed = Math.Clamp(cfg.BreathSpeed, 1, 5);
                settings.BetterRgbBreathSmooth = Math.Clamp(cfg.BreathSmooth, 2, 150);
                settings.BetterRgbBreathRandom = cfg.BreathRandom;
                settings.BetterRgbBreathColor = cfg.BreathColor;
                settings.BetterRgbWaveAngle = Math.Clamp(cfg.WaveAngle, 0, 315);
                settings.BetterRgbWaveSpeed = Math.Clamp(cfg.WaveSpeed, 1, 5);
                settings.BetterRgbSparkleSpeed = Math.Clamp(cfg.SparkleSpeed, 1, 5);
                settings.BetterRgbSparkleDensity = Math.Clamp(cfg.SparkleDensity, 3, 20);
                settings.BetterRgbReactiveRandom = cfg.ReactiveRandom;
                settings.BetterRgbReactiveColor = cfg.ReactiveColor;
                settings.BetterRgbReactiveDuration = Math.Clamp(cfg.ReactiveDuration, 10, 45);
                settings.BetterRgbWheelReverse = cfg.WheelReverse;
                settings.BetterRgbWheelSpeed = Math.Clamp(cfg.WheelSpeed, 1, 5);
                settings.BetterRgbLightningSpeed = Math.Clamp(cfg.LightningSpeed, 1, 5);
                settings.BetterRgbLightningSmooth = Math.Clamp(cfg.LightningSmooth, 1, 20);
                settings.BetterRgbLightningWidth = Math.Clamp(cfg.LightningWidth, 1, 4);
                settings.BetterRgbLightningConcurrent = Math.Clamp(cfg.LightningConcurrent, 1, 4);
                settings.BetterRgbFlameSpeed = Math.Clamp(cfg.FlameSpeed, 1, 5);
                settings.BetterRgbFlameSmooth = Math.Clamp(cfg.FlameSmooth, 1, 10);
                settings.BetterRgbRainSpeed = Math.Clamp(cfg.RainSpeed, 1, 5);
                settings.BetterRgbRainDensity = Math.Clamp(cfg.RainDensity, 1, 5);
                settings.BetterRgbRainColors = cfg.RainColors;
                settings.BetterRgbRainColorCount = CountColors(cfg.RainColors);
                settings.BetterRgbMatrixStyle = Math.Clamp(cfg.MatrixStyle, 0, 3);
                settings.BetterRgbMatrixSpeed = Math.Clamp(cfg.MatrixSpeed, 1, 5);
                settings.BetterRgbMatrixDensity = Math.Clamp(cfg.MatrixDensity, 1, 5);
                settings.BetterRgbMatrixColors = cfg.MatrixColors;
                settings.BetterRgbMatrixColorCount = CountColors(cfg.MatrixColors);
                settings.BetterRgbStaticLayout = Math.Clamp(cfg.StaticLayout, 0, 1);
                settings.BetterRgbStaticZones = Math.Clamp(cfg.StaticZones, 1, 7);
                settings.BetterRgbStaticZoneColors = cfg.StaticZoneColors;
                settings.BetterRgbRippleRandom = cfg.RippleRandom;
                settings.BetterRgbRippleSpeed = Math.Clamp(cfg.RippleSpeed, 1, 5);
                settings.BetterRgbRippleWidth = Math.Clamp(cfg.RippleWidth, 1, 4);
                settings.BetterRgbRippleColor = cfg.RippleColor;
                settings.BetterRgbRainbowSpeed = Math.Clamp(cfg.RainbowSpeed, 1, 5);
                settings.BetterRgbQuicksandSpeed = Math.Clamp(cfg.QuicksandSpeed, 1, 5);
                settings.BetterRgbQuicksandScale = Math.Clamp(cfg.QuicksandScale, 1, 10);
                settings.BetterRgbQuicksandColors = cfg.QuicksandColors;
                settings.BetterRgbCurrentSpeed = Math.Clamp(cfg.CurrentSpeed, 1, 5);
                settings.BetterRgbCurrentWidth = Math.Clamp(cfg.CurrentWidth, 2, 8);
                settings.BetterRgbCurrentColors = cfg.CurrentColors;
                KeyboardSettingsService.Save(settings);

                // The Keyboard page's preset-sync event carries the selected effect's
                // colors so its pickers land on the profile's values.
                string color = settings.BetterRgbMode switch
                {
                    BetterRgbEffect.Breathing => settings.BetterRgbBreathColor,
                    BetterRgbEffect.Reactive => settings.BetterRgbReactiveColor,
                    BetterRgbEffect.Ripple => settings.BetterRgbRippleColor,
                    _ => "",
                };
                string multiColors = settings.BetterRgbMode switch
                {
                    BetterRgbEffect.Raindrop => settings.BetterRgbRainColors,
                    BetterRgbEffect.Matrix => settings.BetterRgbMatrixColors,
                    BetterRgbEffect.Static => settings.BetterRgbStaticZoneColors,
                    BetterRgbEffect.Quicksand => settings.BetterRgbQuicksandColors,
                    BetterRgbEffect.Current => settings.BetterRgbCurrentColors,
                    _ => "",
                };

                var args = new KeyboardPresetAppliedEventArgs(
                    false, 5, false, 0, "", 10, KeyboardDirection.LeftRight,
                    0, 0, 0, "", null,
                    true, settings.BetterRgbMode, settings.BetterRgbFps,
                    settings.BrightnessPercent, color, multiColors);
                LastAppliedKeyboardPreset = args;
                KeyboardPresetApplied?.Invoke(this, args);

                var streamer = App.GetService<BetterRgbStreamService>();
                if (streamer == null)
                    return;

                streamer.SetParams(settings);
                if (!streamer.IsRunning)
                    streamer.StartEngine("adaptive-better-rgb");
            }
            catch (Exception ex)
            {
                DiagnosticLogger.LogError(ex, "DeviceApplier: Failed to apply BetterRGB preset from profile");
            }
        }

        /// <summary>Counts the colors in a "#RRGGBB,#RRGGBB,..." CSV (min 1).</summary>
        private static int CountColors(string csv) =>
            string.IsNullOrEmpty(csv) ? 1 : csv.Split(',').Length;

        /// <summary>Parses a BetterRGB effect display name to the enum.</summary>
        private static BetterRgbEffect ParseBetterRgbEffect(string name) => name switch
        {
            "Wave" => BetterRgbEffect.Wave,
            "Sparkle" => BetterRgbEffect.Sparkle,
            "Reactive" => BetterRgbEffect.Reactive,
            "Wheel" => BetterRgbEffect.Wheel,
            "Lightning" => BetterRgbEffect.Lightning,
            "Flame" => BetterRgbEffect.Flame,
            "Raindrop" => BetterRgbEffect.Raindrop,
            "Matrix" => BetterRgbEffect.Matrix,
            "Static" => BetterRgbEffect.Static,
            "Ripple" => BetterRgbEffect.Ripple,
            "Rainbow" => BetterRgbEffect.Rainbow,
            "Quicksand" => BetterRgbEffect.Quicksand,
            "Current" => BetterRgbEffect.Current,
            _ => BetterRgbEffect.Breathing,
        };

        // Effects that need 7 multi-color slots
        private static readonly HashSet<KeyboardEffect> s_multiColor7Effects = new()
        {
            KeyboardEffect.Breathing, KeyboardEffect.Wave, KeyboardEffect.Random,
            KeyboardEffect.Ripple, KeyboardEffect.Marquee, KeyboardEffect.Raindrop,
            KeyboardEffect.Aurora, KeyboardEffect.TouchAurora,
            KeyboardEffect.TouchSpark, KeyboardEffect.Spark,
        };

        // Effects that need 4 multi-color slots
        private static readonly HashSet<KeyboardEffect> s_multiColor4Effects = new()
        {
            KeyboardEffect.GamingMode,
        };

        /// <summary>
        /// Sends keyboard HID commands directly to the device.
        /// </summary>
        private static void ApplyKeyboardHid(bool perKeyMode, int brightness, KeyboardEffect effect,
            byte effectSpeed, KeyboardDirection direction, byte r, byte g, byte b,
            string multiColors, string? perKeyColors, bool touchReactive = false)
        {
            var hid = new KeyboardHidService();
            try
            {
                if (!hid.Open())
                    return;

                int brightnessPercent = (brightness * 100) / 7;

                if (perKeyMode && !string.IsNullOrEmpty(perKeyColors))
                {
                    // Per-key mode: SendAllPerKeyColorsFromDict handles entering
                    // UserMode and sending all rows. Set brightness first.
                    hid.SetPerKeyBrightness(brightnessPercent);
                    var tempSettings = new KeyboardSettings { PerKeyColors = perKeyColors };
                    var colors = tempSettings.GetPerKeyColors();
                    if (colors.Count > 0)
                    {
                        hid.SendAllPerKeyColorsFromDict(colors);
                    }
                }
                else
                {
                    // Effects mode: use TurnOnWithEffect to combine power-on and
                    // effect into one sequence, avoiding a visible flash of
                    // intermediate Static color.
                    // Rainbow effect has a firmware bug that leaves the leftmost
                    // columns dark, so we bypass the firmware effect and send a
                    // software-driven per-key gradient instead.
                    if (effect == KeyboardEffect.Rainbow)
                    {
                        hid.SendRainbowEffect(brightnessPercent);
                    }
                    else
                    {
                        hid.TurnOnWithEffect(r, g, b, brightnessPercent, effect, effectSpeed, direction, touchReactive);
                    }

                    // Multi-color effects need additional color reports after effect is set.
                    if (!string.IsNullOrEmpty(multiColors))
                    {
                        var multiColorList = ParseMultiColorString(multiColors);
                        if (multiColorList.Count > 0)
                        {
                            if (s_multiColor7Effects.Contains(effect))
                            {
                                hid.SetMultiColor(multiColorList);
                            }
                            else if (s_multiColor4Effects.Contains(effect))
                            {
                                hid.SetMultiColor(multiColorList.Take(4).ToList());
                            }
                        }
                        else
                        {
                            ApplyFallbackMultiColor(hid, effect, r, g, b);
                        }
                    }
                    else
                    {
                        ApplyFallbackMultiColor(hid, effect, r, g, b);
                    }
                }
            }
            catch (Exception ex)
            {
                DiagnosticLogger.LogError(ex, "DeviceApplier: Failed to send keyboard HID commands");
            }
            finally
            {
                hid.Dispose();
            }
        }

        private static List<Color> ParseMultiColorString(string data)
        {
            var result = new List<Color>();
            foreach (var part in data.Split(',', System.StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    var hex = part.Trim();
                    if (hex.StartsWith('#'))
                        hex = hex.Substring(1);
                    if (hex.Length == 6)
                    {
                        var r = byte.Parse(hex.Substring(0, 2), System.Globalization.NumberStyles.HexNumber);
                        var g = byte.Parse(hex.Substring(2, 2), System.Globalization.NumberStyles.HexNumber);
                        var b = byte.Parse(hex.Substring(4, 2), System.Globalization.NumberStyles.HexNumber);
                        result.Add(Color.FromRgb(r, g, b));
                    }
                }
                catch { /* Invalid hex color entry skipped */ }
            }
            return result;
        }

        private static void ApplyFallbackMultiColor(KeyboardHidService hid, KeyboardEffect effect, byte r, byte g, byte b)
        {
            if (s_multiColor7Effects.Contains(effect)
                || s_multiColor4Effects.Contains(effect))
            {
                hid.SetMultiColor(new[] { Color.FromRgb(r, g, b) });
            }
        }

        private static KeyboardEffect ParseKeyboardEffect(string name) => name switch
        {
            "Static" => KeyboardEffect.Static,
            "Breathing" => KeyboardEffect.Breathing,
            "Wave" => KeyboardEffect.Wave,
            "Reactive" => KeyboardEffect.Random,
            "Random" => KeyboardEffect.Random,
            "Rainbow" => KeyboardEffect.Rainbow,
            "Ripple" => KeyboardEffect.Ripple,
            "TouchRipple" => KeyboardEffect.TouchRipple,
            "Marquee" => KeyboardEffect.Marquee,
            "Raindrop" => KeyboardEffect.Raindrop,
            "Aurora" => KeyboardEffect.Aurora,
            "TouchAurora" => KeyboardEffect.TouchAurora,
            "TouchSpark" => KeyboardEffect.TouchSpark,
            "Spark" => KeyboardEffect.Spark,
            "GamingMode" => KeyboardEffect.GamingMode,
            "Music" => KeyboardEffect.Static,
            _ => KeyboardEffect.Static,
        };

        private static KeyboardDirection ParseKeyboardDirection(string name) => name switch
        {
            "LeftRight" => KeyboardDirection.LeftRight,
            "RightLeft" => KeyboardDirection.RightLeft,
            "DownUp" => KeyboardDirection.DownUp,
            "UpDown" => KeyboardDirection.UpDown,
            "DiagonalBottomRightToTopLeft" => KeyboardDirection.DiagonalBottomRightToTopLeft,
            "DiagonalBottomLeftToTopRight" => KeyboardDirection.DiagonalBottomLeftToTopRight,
            _ => KeyboardDirection.LeftRight,
        };

        /* ------------------------------------------------------------------ */
        /*  Flydigi Device Commands                                            */
        /* ------------------------------------------------------------------ */

        /// <summary>
        /// Applies fan mode + RPM/gear to the Flydigi device (BS2+ or BS1).
        /// "Curve" mode is handled by FlydigiSmartControl — this method is a no-op for Curve.
        /// </summary>
        public async Task ApplyFlydigiFanAsync(string fanMode, byte? gear = null, ushort? rpm = null)
        {
            // Try BS2+ first, then BS1
            if (_flydigiService.IsConnected)
            {
                try
                {
                    switch (fanMode)
                    {
                        case "Off":
                            await _flydigiService.WriteRealtimeRpmAsync(0);
                            break;
                        case "Gear":
                            if (gear.HasValue && gear.Value > 0)
                                await _flydigiService.WriteGearAsync(gear.Value);
                            break;
                        case "Rpm":
                            if (rpm.HasValue && rpm.Value > 0)
                                await _flydigiService.WriteRealtimeRpmAsync(rpm.Value);
                            break;
                    }
                }
                catch (Exception ex)
                {
                    DiagnosticLogger.LogError(ex, "DeviceApplier: Failed to apply Flydigi fan settings (BS2+)");
                }
            }

            // Also apply to BS1 if connected
            if (_bs1Service?.IsConnected == true)
            {
                try
                {
                    ushort? clampedRpm = rpm.HasValue ? (ushort?)Math.Clamp(rpm.Value, Bs1DefaultGearRpm.MinRpm, Bs1DefaultGearRpm.MaxRpm) : null;

                    switch (fanMode)
                    {
                        case "Off":
                            await _bs1Service.WriteFanOffAsync();
                            break;
                        case "Gear":
                            if (gear.HasValue && gear.Value > 0)
                                await _bs1Service.WriteGearAsync(gear.Value);
                            break;
                        case "Rpm":
                            if (clampedRpm.HasValue && clampedRpm.Value > 0)
                                await _bs1Service.WriteRpmAsync(clampedRpm.Value);
                            break;
                    }
                }
                catch (Exception ex)
                {
                    DiagnosticLogger.LogError(ex, "DeviceApplier: Failed to apply Flydigi fan settings (BS1)");
                }
            }

        }

        /// <summary>
        /// Applies RGB settings to the Flydigi device.
        /// </summary>
        public async Task ApplyFlydigiRgbAsync(string rgbMode, byte r, byte g, byte b, byte brightness)
        {
            if (!_flydigiService.IsConnected)
                return;

            try
            {
                switch (rgbMode)
                {
                    case "Off":
                        await _flydigiService.WriteRgbOffAsync();
                        break;

                    case "Static":
                        await _flydigiService.WriteRgbStaticAsync(r, g, b, brightness);
                        break;

                    case "Breathing":
                        await _flydigiService.WriteRgbBreathingAsync(r, g, b, brightness);
                        break;

                    case "SmartTemp":
                        await _flydigiService.WriteRgbSmartTempAsync();
                        break;

                    case "Flowing":
                        await _flydigiService.WriteRgbFlowingAsync("Medium", brightness);
                        break;

                    case "Rotation":
                        await _flydigiService.WriteRgbRotationAsync(r, g, b, "Medium", brightness);
                        break;
                }
            }
            catch (Exception ex)
            {
                DiagnosticLogger.LogError(ex, "DeviceApplier: Failed to apply Flydigi RGB settings");
            }
        }

        /// <summary>
        /// Applies a complete Flydigi preset (fan + RGB) from Adaptive Mode.
        /// Fires <see cref="FlydigiPresetApplied"/> so the Flydigi page can sync its UI.
        /// </summary>
        public async Task ApplyFlydigiFromPresetAsync(
            string fanMode,
            byte? gear,
            ushort? rpm,
            string rgbMode,
            byte r,
            byte g,
            byte b,
            byte brightness)
        {
            await ApplyFlydigiFanAsync(fanMode, gear, rpm);
            await ApplyFlydigiRgbAsync(rgbMode, r, g, b, brightness);

            // Notify subscribers so the Flydigi page can update its UI controls
            FlydigiPresetApplied?.Invoke(this, new FlydigiPresetAppliedEventArgs(
                fanMode, gear, rpm, rgbMode, r, g, b, brightness));
        }

        /// <summary>
        /// Fires <see cref="FlydigiPresetApplied"/> so the Flydigi page can sync its UI.
        /// Used by Adaptive Mode after selectively applying only the changed portion (fan or RGB).
        /// </summary>
        public void RaiseFlydigiPresetApplied(
            string fanMode,
            byte? gear,
            ushort? rpm,
            string rgbMode,
            byte r,
            byte g,
            byte b,
            byte brightness)
        {
            var args = new FlydigiPresetAppliedEventArgs(fanMode, gear, rpm, rgbMode, r, g, b, brightness);
            LastAppliedFlydigiPreset = args;
            FlydigiPresetApplied?.Invoke(this, args);
        }

        /* ------------------------------------------------------------------ */
        /*  HydroUI Device Commands                                        */
        /* ------------------------------------------------------------------ */

        /// <summary>
        /// Applies pump, fan, and RGB settings to the Watercooler device.
        /// </summary>
        public async Task ApplyWatercoolerAsync(
            PumpVoltage? pumpVoltage = null,
            FanSpeed? fanSpeed = null,
            RgbState? rgbMode = null,
            RgbColor? rgbColor = null)
        {
            if (!_hydroUIService.IsConnected)
                return;

            try
            {
                if (pumpVoltage.HasValue)
                    await _hydroUIService.WritePumpModeAsync(pumpVoltage.Value);

                if (fanSpeed.HasValue)
                    await _hydroUIService.WriteFanModeAsync(fanSpeed.Value);

                if (rgbMode.HasValue && rgbColor.HasValue)
                    await _hydroUIService.WriteRgbModeAsync(rgbMode.Value, rgbColor.Value);
            }
            catch (Exception ex)
            {
                DiagnosticLogger.LogError(ex, "DeviceApplier: Failed to apply HydroUI settings");
            }
        }

        /// <summary>
        /// Applies a complete HydroUI preset from Adaptive Mode.
        /// Fires <see cref="WatercoolerPresetApplied"/> so the HydroUI page can sync its UI.
        /// </summary>
        public async Task ApplyWatercoolerFromPresetAsync(
            PumpVoltage pumpVoltage,
            FanSpeed fanSpeed,
            RgbState rgbMode,
            RgbColor rgbColor)
        {
            await ApplyWatercoolerAsync(pumpVoltage, fanSpeed, rgbMode, rgbColor);

            // Notify subscribers so the HydroUI page can update its UI controls
            var args = new WatercoolerPresetAppliedEventArgs(pumpVoltage, fanSpeed, rgbMode, rgbColor);
            LastAppliedWatercoolerPreset = args;
            WatercoolerPresetApplied?.Invoke(this, args);
        }

        /* ------------------------------------------------------------------ */
        /*  Settings Capture & Restore Helpers                                 */
        /* ------------------------------------------------------------------ */

        private Bs2ProSettings CaptureFlydigiSettings()
        {
            var source = _flydigiService.GetSettings();
            var copy = new Bs2ProSettings
            {
                FanMode = source.FanMode,
                ManualGear = source.ManualGear,
                ManualGearSubLevel = source.ManualGearSubLevel,
                ManualRpm = source.ManualRpm,
                SelectedCurveProfile = source.SelectedCurveProfile,
                RgbMode = source.RgbMode,
                R = source.R,
                G = source.G,
                B = source.B,
                Brightness = source.Brightness,
                RgbSpeed = source.RgbSpeed,
                RotationSpeed = source.RotationSpeed,
                RotationBrightness = source.RotationBrightness,
                RotationColors = source.RotationColors,
                TempSource = source.TempSource,
                AvoidanceEnabled = source.AvoidanceEnabled,
                AvoidanceStartRpm = source.AvoidanceStartRpm,
                AvoidanceEndRpm = source.AvoidanceEndRpm,
            };
            // Copy custom curve JSON if present
            if (!string.IsNullOrEmpty(source.CustomCurveJson))
                copy.CustomCurveJson = source.CustomCurveJson;

            return copy;
        }

        private static void CopyFlydigiSettings(Bs2ProSettings from, Bs2ProSettings to)
        {
            to.FanMode = from.FanMode;
            to.ManualGear = from.ManualGear;
            to.ManualGearSubLevel = from.ManualGearSubLevel;
            to.ManualRpm = from.ManualRpm;
            to.SelectedCurveProfile = from.SelectedCurveProfile;
            to.RgbMode = from.RgbMode;
            to.R = from.R;
            to.G = from.G;
            to.B = from.B;
            to.Brightness = from.Brightness;
            to.RgbSpeed = from.RgbSpeed;
            to.RotationSpeed = from.RotationSpeed;
            to.RotationBrightness = from.RotationBrightness;
            to.RotationColors = from.RotationColors;
            to.TempSource = from.TempSource;
            to.AvoidanceEnabled = from.AvoidanceEnabled;
            to.AvoidanceStartRpm = from.AvoidanceStartRpm;
            to.AvoidanceEndRpm = from.AvoidanceEndRpm;
            if (!string.IsNullOrEmpty(from.CustomCurveJson))
                to.CustomCurveJson = from.CustomCurveJson;
        }

        private WaterCoolerSettings CaptureWatercoolerSettings()
        {
            var source = _hydroUIService.GetSettings();
            return new WaterCoolerSettings
            {
                PumpVoltage = source.PumpVoltage,
                FanSpeed = source.FanSpeed,
                RgbMode = source.RgbMode,
                RgbColor = source.RgbColor,
                PumpEnabled = source.PumpEnabled,
                FanEnabled = source.FanEnabled,
                RgbEnabled = source.RgbEnabled,
            };
        }

        private static void CopyWatercoolerSettings(WaterCoolerSettings from, WaterCoolerSettings to)
        {
            to.PumpVoltage = from.PumpVoltage;
            to.FanSpeed = from.FanSpeed;
            to.RgbMode = from.RgbMode;
            to.RgbColor = from.RgbColor;
            to.PumpEnabled = from.PumpEnabled;
            to.FanEnabled = from.FanEnabled;
            to.RgbEnabled = from.RgbEnabled;
        }
    }

    /* ------------------------------------------------------------------ */
    /*  Event Args                                                           */
    /* ------------------------------------------------------------------ */

    /// <summary>
    /// Raised when profile-specific Flydigi settings have been applied to the device.
    /// The Flydigi page should sync its UI controls to reflect these values.
    /// </summary>
    public class FlydigiPresetAppliedEventArgs : EventArgs
    {
        public string FanMode { get; }
        public byte? Gear { get; }
        public ushort? Rpm { get; }
        public string RgbMode { get; }
        public byte R { get; }
        public byte G { get; }
        public byte B { get; }
        public byte Brightness { get; }

        public FlydigiPresetAppliedEventArgs(
            string fanMode, byte? gear, ushort? rpm,
            string rgbMode, byte r, byte g, byte b, byte brightness)
        {
            FanMode = fanMode;
            Gear = gear;
            Rpm = rpm;
            RgbMode = rgbMode;
            R = r;
            G = g;
            B = b;
            Brightness = brightness;
        }
    }

    /// <summary>
    /// Raised when profile-specific HydroUI settings have been applied to the device.
    /// The HydroUI page should sync its UI controls to reflect these values.
    /// </summary>
    public class WatercoolerPresetAppliedEventArgs : EventArgs
    {
        public PumpVoltage PumpVoltage { get; }
        public FanSpeed FanSpeed { get; }
        public RgbState RgbMode { get; }
        public RgbColor RgbColor { get; }

        public WatercoolerPresetAppliedEventArgs(
            PumpVoltage pumpVoltage, FanSpeed fanSpeed, RgbState rgbMode, RgbColor rgbColor)
        {
            PumpVoltage = pumpVoltage;
            FanSpeed = fanSpeed;
            RgbMode = rgbMode;
            RgbColor = rgbColor;
        }
    }

    /// <summary>
    /// Raised when profile-specific Keyboard RGB settings have been applied.
    /// The Keyboard page should sync its UI controls to reflect these values.
    /// </summary>
    public class KeyboardPresetAppliedEventArgs : EventArgs
    {
        public bool PerKeyMode { get; }
        public int Brightness { get; }
        public bool IdleTimerEnabled { get; }
        public int IdleTimerMinutes { get; }
        public string EffectMode { get; }
        public byte EffectSpeed { get; }
        public KeyboardDirection Direction { get; }
        public byte ColorR { get; }
        public byte ColorG { get; }
        public byte ColorB { get; }
        public string MultiColors { get; }
        public string? PerKeyColors { get; }

        /// <summary>Set when the applied preset is a BetterRGB streamer preset.</summary>
        public bool BetterRgbMode { get; }
        public BetterRgbEffect BetterRgbEffectMode { get; }
        public int BetterRgbFps { get; }
        public int BetterRgbBrightness { get; }
        public string BetterRgbColor { get; }
        public string BetterRgbMultiColors { get; }

        public KeyboardPresetAppliedEventArgs(
            bool perKeyMode, int brightness, bool idleTimerEnabled, int idleTimerMinutes,
            string effectMode, byte effectSpeed, KeyboardDirection direction,
            byte colorR, byte colorG, byte colorB,
            string multiColors, string? perKeyColors,
            bool betterRgbMode = false, BetterRgbEffect betterRgbEffect = BetterRgbEffect.Breathing,
            int betterRgbFps = 30, int betterRgbBrightness = 100,
            string betterRgbColor = "", string betterRgbMultiColors = "")
        {
            PerKeyMode = perKeyMode;
            Brightness = brightness;
            IdleTimerEnabled = idleTimerEnabled;
            IdleTimerMinutes = idleTimerMinutes;
            EffectMode = effectMode;
            EffectSpeed = effectSpeed;
            Direction = direction;
            ColorR = colorR;
            ColorG = colorG;
            ColorB = colorB;
            MultiColors = multiColors;
            PerKeyColors = perKeyColors;
            BetterRgbMode = betterRgbMode;
            BetterRgbEffectMode = betterRgbEffect;
            BetterRgbFps = betterRgbFps;
            BetterRgbBrightness = betterRgbBrightness;
            BetterRgbColor = betterRgbColor;
            BetterRgbMultiColors = betterRgbMultiColors;
        }
    }

    /// <summary>
    /// Raised when profile-specific EC Fan settings have been applied to the device.
    /// The Fan Control page should sync its UI controls to reflect these values.
    /// </summary>
    public class EcFanPresetAppliedEventArgs : EventArgs
    {
        public bool UnifiedMode { get; }
        public string PresetName { get; }
        public int[]? CustomDuties { get; }
        public int[]? CpuPresetDuties { get; }
        public int[]? GpuPresetDuties { get; }
        public int[]? CpuTemps { get; }
        public int[]? GpuTemps { get; }

        public EcFanPresetAppliedEventArgs(
            bool unifiedMode, string presetName,
            int[]? customDuties, int[]? cpuPresetDuties, int[]? gpuPresetDuties,
            int[]? cpuTemps, int[]? gpuTemps)
        {
            UnifiedMode = unifiedMode;
            PresetName = presetName;
            CustomDuties = customDuties;
            CpuPresetDuties = cpuPresetDuties;
            GpuPresetDuties = gpuPresetDuties;
            CpuTemps = cpuTemps;
            GpuTemps = gpuTemps;
        }
    }
}
