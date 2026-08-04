using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Media;
using Universal_x86_Tuning_Utility.Models;
using Universal_x86_Tuning_Utility.Scripts.Misc;

namespace Universal_x86_Tuning_Utility.Services
{
    /// <summary>
    /// Centralized service for applying device settings to Flydigi cooler, LCT watercooler,
    /// and Uniwill EC fan curves. This is the single point of truth for what gets sent
    /// to the hardware. Both the device pages and Adaptive Mode route through this service.
    /// </summary>
    public class DeviceApplier
    {
        private readonly FlydigiCoolerService _flydigiService;
        private readonly Bs1Service? _bs1Service;
        private readonly WaterCoolerService _waterCoolerService;
        private readonly UniwillECService? _uniwillEc;

        /// <summary>Whether Adaptive Mode is currently overriding Flydigi device control.</summary>
        public bool IsFlydigiOverridden { get; private set; }

        /// <summary>Whether Adaptive Mode is currently overriding Watercooler device control.</summary>
        public bool IsWatercoolerOverridden { get; private set; }

        /// <summary>Whether Adaptive Mode is currently overriding EC Fan Control.</summary>
        public bool IsEcFanOverridden { get; private set; }

        /// <summary>Whether Adaptive Mode is currently overriding Keyboard RGB.</summary>
        public bool IsKeyboardOverridden { get; private set; }

        /// <summary>The last Flydigi preset applied from Adaptive Mode (null when nothing has been applied yet).</summary>
        public FlydigiPresetAppliedEventArgs? LastAppliedFlydigiPreset { get; private set; }

        /// <summary>The last Watercooler preset applied from Adaptive Mode (null when nothing has been applied yet).</summary>
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
        /// Raised when the Watercooler override state changes.
        /// Args: (isOverridden)
        /// </summary>
        public event EventHandler<bool>? WatercoolerOverrideChanged;

        /// <summary>
        /// Raised when profile-specific Flydigi settings have been applied and the page
        /// should update its UI controls to reflect the profile's values.
        /// </summary>
        public event EventHandler<FlydigiPresetAppliedEventArgs>? FlydigiPresetApplied;

        /// <summary>
        /// Raised when profile-specific Watercooler settings have been applied and the page
        /// should update its UI controls to reflect the profile's values.
        /// </summary>
        public event EventHandler<WatercoolerPresetAppliedEventArgs>? WatercoolerPresetApplied;

        /// <summary>
        /// Raised when the EC Fan override state changes.
        /// Args: (isOverridden)
        /// </summary>
        public event EventHandler<bool>? EcFanOverrideChanged;

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
        /// Saved user Flydigi settings, captured when override is enabled.
        /// Restored when override is lifted.
        /// </summary>
        private Bs2ProSettings? _savedFlydigiSettings;

        /// <summary>
        /// Saved user Watercooler settings, captured when override is enabled.
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

        public DeviceApplier(FlydigiCoolerService flydigiService, Bs1Service? bs1Service, WaterCoolerService waterCoolerService, UniwillECService? uniwillEc = null)
        {
            _flydigiService = flydigiService;
            _bs1Service = bs1Service;
            _waterCoolerService = waterCoolerService;
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
                        await ApplyFlydigiFanAsync(fanMode, saved.ManualGear > 0 ? (byte)saved.ManualGear : (byte?)null, saved.ManualRpm > 0 ? (ushort?)saved.ManualRpm : null);
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
        /// Enables Adaptive Mode override for the Watercooler device.
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
        /// Disables Adaptive Mode override for the Watercooler device.
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
                var currentSettings = _waterCoolerService.GetSettings();
                CopyWatercoolerSettings(saved, currentSettings);
                _waterCoolerService.SaveSettings();
                _savedWatercoolerSettings = null;

                // Re-apply restored settings to the device immediately.
                if (_waterCoolerService.IsConnected)
                {
                    try
                    {
                        await ApplyWatercoolerAsync(saved.GetPumpVoltage(), saved.GetFanSpeed(), saved.GetRgbMode(), saved.GetRgbColor());
                    }
                    catch (Exception ex)
                    {
                        DiagnosticLogger.LogError(ex, "DeviceApplier: Failed to re-apply user Watercooler settings on override-lift");
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

        private static EcFanCurve BuildCurveFromSettings(string presetName, int[] duties)
        {
            var curve = new EcFanCurve { Name = presetName };
            curve.Duties.Clear();
            foreach (var d in duties)
                curve.Duties.Add(Math.Clamp(d, 0, 100));
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
        /// </summary>
        public void EnableKeyboardOverride()
        {
            if (IsKeyboardOverridden)
                return;

            // Capture current user keyboard settings before overriding
            _savedKeyboardSettings = KeyboardSettingsService.Load();
            IsKeyboardOverridden = true;
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
                _ = Task.Run(() =>
                {
                    ApplyKeyboardHid(s.PerKeyMode, s.Brightness, s.EffectMode, s.Speed,
                        s.Direction, s.ColorR, s.ColorG, s.ColorB, s.MultiColors, s.PerKeyColors,
                        0, 0, 0);
                });
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
            string? perKeyColors,
            byte restColorR,
            byte restColorG,
            byte restColorB)
        {
            try
            {
                // Persist the preset settings so the Keyboard page can pick them up.
                // Load existing settings first to preserve per-key colors when switching
                // to Effects mode — we don't want to wipe saved per-key data.
                var settings = KeyboardSettingsService.Load();
                settings.PowerOn = true;
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
                    colorR, colorG, colorB, multiColors, perKeyColors,
                    restColorR, restColorG, restColorB);
                LastAppliedKeyboardPreset = args;
                KeyboardPresetApplied?.Invoke(this, args);

                // Send HID commands directly so the keyboard updates immediately even when
                // the Keyboard page is not visible. The Keyboard page's debounce timer will
                // also fire if it's visible, but that's harmless (idempotent HID writes).
                // The ITE firmware needs ~500ms to settle after power-on before accepting
                // direction bytes. Without this delay, direction defaults to Left→Right.
                if (settings.Direction != KeyboardDirection.LeftRight)
                    System.Threading.Thread.Sleep(500);
                ApplyKeyboardHid(perKeyMode, brightness, settings.EffectMode, effectSpeed,
                    settings.Direction, colorR, colorG, colorB, multiColors, perKeyColors,
                    restColorR, restColorG, restColorB);
            }
            catch (Exception ex)
            {
                DiagnosticLogger.LogError(ex, "DeviceApplier: Failed to apply Keyboard preset from profile");
            }
        }

        // Effects that need 7 multi-color slots
        private static readonly HashSet<KeyboardEffect> s_multiColor7Effects = new()
        {
            KeyboardEffect.Breathing, KeyboardEffect.Wave, KeyboardEffect.Reactive,
            KeyboardEffect.Ripple, KeyboardEffect.Marquee, KeyboardEffect.Raindrop,
            KeyboardEffect.Aurora, KeyboardEffect.TouchAurora,
            KeyboardEffect.TouchSpark, KeyboardEffect.Spark, KeyboardEffect.Music,
        };

        // Effects that need 4 multi-color slots
        private static readonly HashSet<KeyboardEffect> s_multiColor4Effects = new()
        {
            KeyboardEffect.GamingMode,
        };

        // Effects that need 4+1 multi-color slots
        private static readonly HashSet<KeyboardEffect> s_multiColor4Plus1Effects = new()
        {
            KeyboardEffect.GamingModeFull,
        };

        /// <summary>
        /// Sends keyboard HID commands directly to the device.
        /// </summary>
        private void ApplyKeyboardHid(bool perKeyMode, int brightness, KeyboardEffect effect,
            byte effectSpeed, KeyboardDirection direction, byte r, byte g, byte b,
            string multiColors, string? perKeyColors,
            byte restColorR, byte restColorG, byte restColorB)
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
                    // Effects mode: exit per-key mode, set color, then apply effect.
                    // The effect must be sent BEFORE the multi-color palette so the
                    // ITE controller knows how to interpret the upcoming color data.
                    hid.ExitPerKeyMode();
                    hid.TurnOn(r, g, b, brightnessPercent);

                    hid.SetEffect(effect, effectSpeed, direction);

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
                            else if (s_multiColor4Plus1Effects.Contains(effect))
                            {
                                var allColors = multiColorList.Take(4).ToList();
                                allColors.Add(Color.FromRgb(restColorR, restColorG, restColorB));
                                hid.SetMultiColor(allColors);
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
                    if (hex.StartsWith("#"))
                        hex = hex.Substring(1);
                    if (hex.Length == 6)
                    {
                        var r = byte.Parse(hex.Substring(0, 2), System.Globalization.NumberStyles.HexNumber);
                        var g = byte.Parse(hex.Substring(2, 2), System.Globalization.NumberStyles.HexNumber);
                        var b = byte.Parse(hex.Substring(4, 2), System.Globalization.NumberStyles.HexNumber);
                        result.Add(Color.FromRgb(r, g, b));
                    }
                }
                catch { }
            }
            return result;
        }

        private static void ApplyFallbackMultiColor(KeyboardHidService hid, KeyboardEffect effect, byte r, byte g, byte b)
        {
            if (s_multiColor7Effects.Contains(effect))
            {
                hid.SetMultiColor(new[] { Color.FromRgb(r, g, b) });
            }
            else if (s_multiColor4Effects.Contains(effect))
            {
                hid.SetMultiColor(new[] { Color.FromRgb(r, g, b) });
            }
            else if (s_multiColor4Plus1Effects.Contains(effect))
            {
                hid.SetMultiColor(new[] { Color.FromRgb(r, g, b) });
            }
        }

        private static KeyboardEffect ParseKeyboardEffect(string name) => name switch
        {
            "Static" => KeyboardEffect.Static,
            "Breathing" => KeyboardEffect.Breathing,
            "Wave" => KeyboardEffect.Wave,
            "Reactive" => KeyboardEffect.Reactive,
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
            "GamingModeFull" => KeyboardEffect.GamingModeFull,
            "Music" => KeyboardEffect.Music,
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
            bool applied = false;

            if (_flydigiService.IsConnected)
            {
                applied = true;
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
                applied = true;
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

            // Neither connected — no-op
            if (!applied)
                return;
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
        /*  Watercooler Device Commands                                        */
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
            if (!_waterCoolerService.IsConnected)
                return;

            try
            {
                if (pumpVoltage.HasValue)
                    await _waterCoolerService.WritePumpModeAsync(pumpVoltage.Value);

                if (fanSpeed.HasValue)
                    await _waterCoolerService.WriteFanModeAsync(fanSpeed.Value);

                if (rgbMode.HasValue && rgbColor.HasValue)
                    await _waterCoolerService.WriteRgbModeAsync(rgbMode.Value, rgbColor.Value);
            }
            catch (Exception ex)
            {
                DiagnosticLogger.LogError(ex, "DeviceApplier: Failed to apply Watercooler settings");
            }
        }

        /// <summary>
        /// Applies a complete Watercooler preset from Adaptive Mode.
        /// Fires <see cref="WatercoolerPresetApplied"/> so the Watercooler page can sync its UI.
        /// </summary>
        public async Task ApplyWatercoolerFromPresetAsync(
            PumpVoltage pumpVoltage,
            FanSpeed fanSpeed,
            RgbState rgbMode,
            RgbColor rgbColor)
        {
            await ApplyWatercoolerAsync(pumpVoltage, fanSpeed, rgbMode, rgbColor);

            // Notify subscribers so the Watercooler page can update its UI controls
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

        private void CopyFlydigiSettings(Bs2ProSettings from, Bs2ProSettings to)
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
            var source = _waterCoolerService.GetSettings();
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

        private void CopyWatercoolerSettings(WaterCoolerSettings from, WaterCoolerSettings to)
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
    /// Raised when profile-specific Watercooler settings have been applied to the device.
    /// The Watercooler page should sync its UI controls to reflect these values.
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
        public byte RestColorR { get; }
        public byte RestColorG { get; }
        public byte RestColorB { get; }
        public string MultiColors { get; }
        public string? PerKeyColors { get; }

        public KeyboardPresetAppliedEventArgs(
            bool perKeyMode, int brightness, bool idleTimerEnabled, int idleTimerMinutes,
            string effectMode, byte effectSpeed, KeyboardDirection direction,
            byte colorR, byte colorG, byte colorB,
            string multiColors, string? perKeyColors,
            byte restColorR, byte restColorG, byte restColorB)
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
            RestColorR = restColorR;
            RestColorG = restColorG;
            RestColorB = restColorB;
            MultiColors = multiColors;
            PerKeyColors = perKeyColors;
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
