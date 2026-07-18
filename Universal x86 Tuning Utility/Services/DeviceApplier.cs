using System;
using System.Threading.Tasks;
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
        /// Restores the previously saved user settings.
        /// </summary>
        public void DisableFlydigiOverride()
        {
            if (!IsFlydigiOverridden)
                return;

            IsFlydigiOverridden = false;
            FlydigiOverrideChanged?.Invoke(this, false);

            // Restore saved user settings to the service's settings object
            if (_savedFlydigiSettings != null)
            {
                var currentSettings = _flydigiService.GetSettings();
                CopyFlydigiSettings(_savedFlydigiSettings, currentSettings);
                _flydigiService.PersistSettings();
                _savedFlydigiSettings = null;
            }
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
        /// Restores the previously saved user settings.
        /// </summary>
        public void DisableWatercoolerOverride()
        {
            if (!IsWatercoolerOverridden)
                return;

            IsWatercoolerOverridden = false;
            WatercoolerOverrideChanged?.Invoke(this, false);

            // Restore saved user settings to the service's settings object
            if (_savedWatercoolerSettings != null)
            {
                var currentSettings = _waterCoolerService.GetSettings();
                CopyWatercoolerSettings(_savedWatercoolerSettings, currentSettings);
                _waterCoolerService.SaveSettings();
                _savedWatercoolerSettings = null;
            }
        }

        /* ------------------------------------------------------------------ */
        /*  Flydigi Device Commands                                            */
        /* ------------------------------------------------------------------ */

        /// <summary>
        /// Applies fan mode + RPM/gear/curve settings to the Flydigi device.
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
                    // The caller (Flydigi page or Adaptive) manages the smart control instance.
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
}
