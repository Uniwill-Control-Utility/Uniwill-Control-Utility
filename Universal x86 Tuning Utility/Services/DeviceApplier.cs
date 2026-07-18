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
    /// Centralized service for applying device settings to Flydigi cooler and LCT watercooler.
    /// This is the single point of truth for what gets sent to the hardware.
    /// Both the Flydigi/Watercooler pages and Adaptive Mode route through this service.
    /// </summary>
    public class DeviceApplier
    {
        private readonly FlydigiCoolerService _flydigiService;
        private readonly WaterCoolerService _waterCoolerService;

        /// <summary>Whether Adaptive Mode is currently overriding Flydigi device control.</summary>
        public bool IsFlydigiOverridden { get; private set; }

        /// <summary>Whether Adaptive Mode is currently overriding Watercooler device control.</summary>
        public bool IsWatercoolerOverridden { get; private set; }

        /// <summary>The last Flydigi preset applied from Adaptive Mode (null when nothing has been applied yet).</summary>
        public FlydigiPresetAppliedEventArgs? LastAppliedFlydigiPreset { get; private set; }

        /// <summary>The last Watercooler preset applied from Adaptive Mode (null when nothing has been applied yet).</summary>
        public WatercoolerPresetAppliedEventArgs? LastAppliedWatercoolerPreset { get; private set; }

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
        /// Saved user Flydigi settings, captured when override is enabled.
        /// Restored when override is lifted.
        /// </summary>
        private Bs2ProSettings? _savedFlydigiSettings;

        /// <summary>
        /// Saved user Watercooler settings, captured when override is enabled.
        /// Restored when override is lifted.
        /// </summary>
        private WaterCoolerSettings? _savedWatercoolerSettings;

        public DeviceApplier(FlydigiCoolerService flydigiService, WaterCoolerService waterCoolerService)
        {
            _flydigiService = flydigiService;
            _waterCoolerService = waterCoolerService;
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

        /* ------------------------------------------------------------------ */
        /*  Flydigi Device Commands                                            */
        /* ------------------------------------------------------------------ */

        /// <summary>
        /// Applies fan mode + RPM/gear to the Flydigi device.
        /// "Curve" mode is handled by FlydigiSmartControl — this method is a no-op for Curve.
        /// </summary>
        public async Task ApplyFlydigiFanAsync(string fanMode, byte? gear = null, ushort? rpm = null)
        {
            if (!_flydigiService.IsConnected)
                return;

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

                    // "Curve" is handled by FlydigiSmartControl, not this applier.
                    break;
                }
            }
            catch (Exception ex)
            {
                DiagnosticLogger.LogError(ex, "DeviceApplier: Failed to apply Flydigi fan settings");
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
}
