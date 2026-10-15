using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using Uniwill_Control_Utility.Models;
using Uniwill_Control_Utility.Properties;
using Uniwill_Control_Utility.Scripts;
using Uniwill_Control_Utility.Scripts.GPUs.NVIDIA;
using Uniwill_Control_Utility.Scripts.Misc;
using Uniwill_Control_Utility.ViewModels;

namespace Uniwill_Control_Utility.Services
{
    public sealed record TrayQuickControlSnapshot(
        TrayQuickControlPolicy Policy,
        bool AdaptiveModeRunning,
        bool HydroSmartMode,
        bool HydroRgbOn,
        bool KeyboardRgbOn,
        bool LightBarOn,
        bool FanSmartMode,
        bool LocalDimmingOn,
        bool OverlayOn,
        bool FlydigiRgbOn,
        bool GpuModeHybrid,
        bool GpuModeDgpuOnly,
        int GpuPowerSavingMode,
        int RefreshRateHz,
        bool ColorCalAvailable,
        string? ColorProfileFile);

    public sealed class TrayQuickControlService
    {
        private readonly UniwillECService _ec;
        private readonly HydroUIService _waterCooler;
        private readonly DeviceApplier _deviceApplier;
        private readonly IHardwareMonitoringService _hardwareMonitoring;
        private readonly EcTelemetryService _ecTelemetry;
        private readonly OverlaySettingsStore _overlaySettingsStore;
        private readonly OverlayManagerService _overlayManager;
        private readonly FlydigiCoolerHidService _flydigiCooler;

        public TrayQuickControlService(
            UniwillECService ec,
            HydroUIService waterCooler,
            DeviceApplier deviceApplier,
            IHardwareMonitoringService hardwareMonitoring,
            EcTelemetryService ecTelemetry,
            OverlaySettingsStore overlaySettingsStore,
            OverlayManagerService overlayManager,
            FlydigiCoolerHidService flydigiCooler)
        {
            _ec = ec;
            _waterCooler = waterCooler;
            _deviceApplier = deviceApplier;
            _hardwareMonitoring = hardwareMonitoring;
            _ecTelemetry = ecTelemetry;
            _overlaySettingsStore = overlaySettingsStore;
            _overlayManager = overlayManager;
            _flydigiCooler = flydigiCooler;
        }

        public TrayQuickTelemetrySnapshot GetTelemetrySnapshot() =>
            TrayQuickTelemetryComposer.Compose(
                _hardwareMonitoring.ReadSnapshot(),
                _ecTelemetry.ReadFanDuty());

        public TrayQuickControlSnapshot GetSnapshot()
        {
            bool hasGpu = !string.IsNullOrWhiteSpace(NvTuning.GetGpuName());
            bool adaptiveRunning = Settings.Default.isAdaptiveModeRunning;
            bool hydroSupported = WaterCoolerHardwareDetector.IsSupportedHardware();
            bool hasMiniLed = App.HasMiniLEDPanel;
            bool flydigiAvailable = FlydigiCoolerHidService.IsDeviceAvailable();
            bool flydigiEnabled = Properties.Settings.Default.EnableFlydigi;

            // Runtime GPU mux mode — the tray's Display page shows the Nvidia Optimus
            // (power saving) row only while the runtime mode is Hybrid. The EC runtime
            // read is the right condition here (not the persisted NVRAM value): power
            // saving is a runtime feature and NVRAM access is admin-gated.
            bool gpuModeHybrid = true;
            bool gpuModeDgpuOnly = false;
            try
            {
                var muxMode = _ec.GetCurrentGpuMuxMode();
                gpuModeHybrid = muxMode == UniwillECService.GpuMuxMode.Hybrid;
                gpuModeDgpuOnly = muxMode == UniwillECService.GpuMuxMode.DgpuOnly;
            }
            catch (Exception ex)
            {
                Log.Warning("[Tray] GPU mux mode read failed: {Message}", ex.Message);
            }

            // Live Windows display mode refresh rate for the display in use (primary).
            int refreshRateHz = 0;
            bool refreshRateSwitchable = false;
            try
            {
                var refreshTarget = Display.CurrentDisplayName();
                refreshRateHz = Display.GetCurrentRefreshRate(refreshTarget);
                refreshRateSwitchable = Display.GetOfferedRefreshRates(refreshTarget, gpuModeDgpuOnly).Count > 1;
            }
            catch (Exception ex)
            {
                Log.Warning("[Tray] Refresh rate read failed: {Message}", ex.Message);
            }

            // Live ICM association for the internal panel — SetColorProfileAsync
            // applies laptop-panel-only, so the readback must read the same display.
            string? colorProfileFile = null;
            bool colorCalAvailable = false;
            try
            {
                colorProfileFile = Display.GetInternalColorProfile();
                colorCalAvailable = Display.GetOfferedColorProfileFiles().Count > 1;
            }
            catch (Exception ex)
            {
                Log.Warning("[Tray] Color calibration read failed: {Message}", ex.Message);
            }

            var features = new FeatureNavigationPreferences(
                Adaptive: Settings.Default.EnableAdaptiveNavigation,
                HydroUi: Settings.Default.EnableHydroUiNavigation,
                LightBar: Settings.Default.EnableLightBarNavigation,
                Overlay: Settings.Default.EnableOverlayNavigation,
                Auto: Settings.Default.EnableAutoNavigation);

            /*
             * Use persisted adaptive settings as the source of truth for button
             * disable state, NOT runtime override flags (IsWatercoolerOverridden,
             * IsFlydigiOverridden, etc.).  The runtime flags are set asynchronously
             * during RestoreAdaptiveModeAsync and the tray may snapshot before they
             * are restored, causing buttons to appear enabled when they shouldn't.
             */
            var policy = TrayQuickControlPolicy.Create(
                hasNvidiaGpu: hasGpu,
                hasSupportedWaterCooler: hydroSupported,
                waterCoolerConnected: _waterCooler.IsConnected,
                adaptiveCpuEnabled: adaptiveRunning && Settings.Default.AdaptiveCpuEnabled,
                adaptiveGpuEnabled: adaptiveRunning && Settings.Default.AdaptiveGpuEnabled,
                adaptiveFanEnabled: adaptiveRunning && Settings.Default.AdaptiveEcFanEnabled,
                adaptiveWaterCoolerEnabled: adaptiveRunning && Settings.Default.AdaptiveWcEnabled,
                adaptiveKeyboardEnabled: adaptiveRunning && Settings.Default.AdaptiveKeyboardEnabled,
                adaptiveLightBarEnabled: adaptiveRunning && Settings.Default.AdaptiveLbEnabled,
                adaptiveFlydigiEnabled: adaptiveRunning && Settings.Default.AdaptiveBs2ProEnabled,
                hasMiniLedPanel: hasMiniLed,
                flydigiDeviceAvailable: flydigiAvailable,
                flydigiEnabled: flydigiEnabled,
                refreshRateSwitchable: refreshRateSwitchable,
                features: features);

            var waterSettings = _waterCooler.GetSettings();
            var keyboardSettings = KeyboardSettingsService.Load();
            var fanSettings = FanControlSettingsService.Load();
            var overlayOptions = _overlaySettingsStore.Current;
            var flydigiSettings = _flydigiCooler.GetSettings();

            return new TrayQuickControlSnapshot(
                Policy: policy,
                AdaptiveModeRunning: adaptiveRunning,
                HydroSmartMode: waterSettings.SmartModeEnabled,
                HydroRgbOn: waterSettings.RgbEnabled && waterSettings.GetRgbMode() != RgbState.Off,
                KeyboardRgbOn: keyboardSettings.PowerOn,
                LightBarOn: Settings.Default.LightBarEnabled,
                FanSmartMode: fanSettings.EcSmartFanMode,
                LocalDimmingOn: Settings.Default.DisplayLocalDimming,
                OverlayOn: overlayOptions.Enabled,
                FlydigiRgbOn: flydigiSettings.RgbMode != "Off",
                GpuModeHybrid: gpuModeHybrid,
                GpuModeDgpuOnly: gpuModeDgpuOnly,
                GpuPowerSavingMode: Settings.Default.GpuPowerSavingMode,
                RefreshRateHz: refreshRateHz,
                ColorCalAvailable: colorCalAvailable,
                ColorProfileFile: colorProfileFile);
        }

        #region Toggle Handlers

        /// <summary>Toggles Adaptive Mode on/off. Lifts/applies overrides and updates the page.</summary>
        public async Task SetAdaptiveModeAsync(bool enabled)
        {
            if (enabled)
            {
                Settings.Default.isAdaptiveModeRunning = true;
                Settings.Default.isStartAdpative = true;
            }
            else
            {
                Settings.Default.isAdaptiveModeRunning = false;
                Settings.Default.isStartAdpative = false;

                // Lift all DeviceApplier overrides so pages regain control.
                if (_deviceApplier != null)
                {
                    await _deviceApplier.DisableFlydigiOverrideAsync();
                    await _deviceApplier.DisableWatercoolerOverrideAsync();
                    _deviceApplier.DisableEcFanOverride();
                    _deviceApplier.DisableKeyboardOverride();
                    _deviceApplier.DisableLightBarOverride();
                    await _deviceApplier.DisableCpuGpuOverrideAsync();
                }
            }
            Settings.Default.Save();
        }

        /// <summary>Toggles Hydro UI Smart Mode on/off.</summary>
        public async Task SetHydroSmartModeAsync(bool enabled)
        {
            if (!_waterCooler.IsConnected)
                return;

            var settings = _waterCooler.GetSettings();
            settings.SmartModeEnabled = enabled;
            _waterCooler.SaveSettings();

            if (enabled)
            {
                // Start smart mode: apply idle speeds
                await _waterCooler.WritePumpModeAsync(settings.GetSmartModeIdlePump());
                await _waterCooler.WriteFanModeAsync(settings.GetSmartModeIdleFan());
            }
            else
            {
                // Stop smart mode: restore saved pump/fan speeds
                var pumpVoltage = settings.GetPumpVoltage();
                var fanSpeed = settings.GetFanSpeed();
                if (pumpVoltage != PumpVoltage.Off)
                    await _waterCooler.WritePumpModeAsync(pumpVoltage);
                if (fanSpeed != FanSpeed.Off)
                    await _waterCooler.WriteFanModeAsync(fanSpeed);
            }
        }

        /// <summary>Toggles Hydro UI RGB on/off.</summary>
        public async Task SetHydroRgbAsync(bool enabled)
        {
            if (!_waterCooler.IsConnected)
                return;

            var settings = _waterCooler.GetSettings();
            settings.RgbEnabled = enabled;
            _waterCooler.SaveSettings();

            if (enabled)
            {
                var rgbMode = settings.GetRgbMode();
                var rgbColor = settings.GetRgbColor();
                if (rgbMode != RgbState.Off)
                    await _waterCooler.WriteRgbModeAsync(rgbMode, rgbColor);
            }
            else
            {
                await _waterCooler.WriteRgbModeAsync(RgbState.Off);
            }
        }

        /// <summary>Toggles Keyboard backlight on/off.</summary>
        public void SetKeyboardBacklight(bool enabled)
        {
            var settings = KeyboardSettingsService.Load();
            settings.PowerOn = enabled;
            KeyboardSettingsService.Save(settings);

            // Apply to HID directly
            var hid = new KeyboardHidService();
            if (!hid.Open())
            {
                hid.Dispose();
                return;
            }

            try
            {
                if (enabled)
                {
                    System.Threading.Thread.Sleep(500); // ITE firmware needs settle time
                    hid.TurnOnWithEffect(
                        settings.ColorR, settings.ColorG, settings.ColorB,
                        (settings.Brightness * 100) / 7,
                        settings.EffectMode, settings.Speed, settings.Direction);
                }
                else
                {
                    hid.TurnOff();
                }
            }
            finally
            {
                hid.Dispose();
            }
        }

        /// <summary>Toggles Light Bar on/off.</summary>
        public void SetLightBar(bool enabled)
        {
            Settings.Default.LightBarEnabled = enabled;
            Settings.Default.Save();

            var hid = new KeyboardHidService();
            if (!hid.Open())
            {
                hid.Dispose();
                return;
            }

            try
            {
                if (enabled)
                {
                    hid.LightBarTurnOn(
                        Settings.Default.LightBarR,
                        Settings.Default.LightBarG,
                        Settings.Default.LightBarB,
                        Settings.Default.LightBarBrightness);

                    // Restore saved effect and speed so the light bar returns to its
                    // previous effect (e.g. Wave) instead of defaulting to Static.
                    // LightBarEffectIndex is a 0-based index into the LightBar page's Effects array:
                    //   0=Static(0x01), 1=Breathing(0x02), 2=Wave(0x03), 3=Loading(0x04), 4=Marquee(0x05), 5=Meteor(0x06)
                    byte effect = (byte)(1 + Settings.Default.LightBarEffectIndex);
                    byte hidSpeed = (byte)(11 - Settings.Default.LightBarSpeed);
                    hid.LightBarSetEffect(effect, hidSpeed, Settings.Default.LightBarBrightness);
                }
                else
                {
                    hid.LightBarTurnOff();
                }
            }
            finally
            {
                hid.Dispose();
            }
        }

        /// <summary>Toggles Fan Smart Mode on/off.</summary>
        public void SetFanSmartMode(bool enabled)
        {
            var settings = FanControlSettingsService.Load();
            settings.EcSmartFanMode = enabled;
            FanControlSettingsService.Save(settings);

            if (enabled)
            {
                // Build a default curve from the active preset
                string preset = settings.UnifiedMode
                    ? (settings.UnifiedPreset ?? "Balanced")
                    : (settings.CpuPreset ?? "Balanced");
                var curve = BuildPresetCurve(preset);
                int[] temps = settings.UnifiedMode
                    ? settings.UnifiedTempThresholds
                    : settings.CpuTempThresholds;

                var tempProvider = new FlydigiTemperatureProvider();
                EcSmartFanControl.GetOrCreate(
                    _ec,
                    tempProvider,
                    settings,
                    curve,
                    temps);
            }
            else
            {
                EcSmartFanControl.ClearInstance();
            }
        }

        /// <summary>Toggles Local Dimming on/off. Returns true if the toggle succeeded.</summary>
        /// <remarks>Local Dimming uses WMI so it must run on a background thread.</remarks>
        public Task<bool> SetLocalDimmingAsync(bool enabled)
        {
            return Task.Run(() =>
            {
                try
                {
                    return SettingsPage_SetLocalDimming(enabled);
                }
                catch
                {
                    return false;
                }
            });
        }

        /// <summary>
        /// Switches the runtime iGPU Power Saving mode (Hybrid/Nvidia Optimus only).
        /// Mirrors the Display page's btnPowerSaving_Click core: persist, PnP hotswap,
        /// LHM reset. Returns true if the switch was applied.
        /// </summary>
        public async Task<bool> SetGpuPowerSavingAsync(int mode)
        {
            // Block during GPU driver transition cooldown (same guard as the Display page).
            if (GpuInteropGuard.IsBlocked)
                return false;

            try
            {
                Settings.Default.GpuPowerSavingMode = mode;
                Settings.Default.Save();

                await GpuPowerSaving.ApplyAsync((GpuPowerSavingMode)mode);

                // Reset LHM Computer so metrics re-probe with fresh driver handles.
                try
                {
                    App.GetService<IHardwareMonitoringService>()?.ResetComputer();
                }
                catch
                {
                    // Best-effort — metrics refresh on their own schedule.
                }

                Log.Information("[Tray] GPU power saving mode set to {Mode} ({Name})",
                    mode, GpuPowerSaving.GetModeDisplayName((GpuPowerSavingMode)mode));
                return true;
            }
            catch (Exception ex)
            {
                Log.Warning("[Tray] GPU power saving switch failed: {Message}", ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Switches the internal panel's refresh rate via the Windows display-mode
        /// pathway (mirrors the Display page's btnRefreshRate_Click core). Returns
        /// true when the readback confirms the requested rate.
        /// </summary>
        public async Task<bool> SetRefreshRateAsync(int hz)
        {
            try
            {
                var target = Display.CurrentDisplayName();
                Display.ChangeDisplaySettings(target, hz);

                // Verify via readback — the engine reports failures only to the console.
                int applied = Display.GetCurrentRefreshRate(target);
                bool success = applied == hz;
                Log.Information("[Tray] Display refresh rate set to {Applied} Hz (requested {Hz} Hz, success: {Success})",
                    applied, hz, success);
                return success;
            }
            catch (Exception ex)
            {
                Log.Warning("[Tray] Display refresh rate apply failed: {Message}", ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Associates an ICC profile with the display in use ("" removes the association).
        /// Returns true when the readback confirms the requested profile.
        /// </summary>
        public async Task<bool> SetColorProfileAsync(string profileFile)
        {
            try
            {
                // Color calibration is laptop-panel-only — never the display in use.
                var target = Display.InternalPanelName();
                if (string.IsNullOrEmpty(target))
                    return false;
                Display.ApplyColorProfile(target, profileFile);

                // Verify via readback — the registry write reports failures only via return code.
                string? applied = Display.GetCurrentColorProfile(target);
                bool success = applied == profileFile;
                Log.Information("[Tray] Display color profile set to {Applied} (requested {File}, success: {Success})",
                    applied, profileFile, success);
                return success;
            }
            catch (Exception ex)
            {
                Log.Warning("[Tray] Display color profile apply failed: {Message}", ex.Message);
                return false;
            }
        }

        /// <remarks>
        /// The WMI-based Local Dimming logic is duplicated from SettingsPage
        /// because it requires System.Management which is page-specific.
        /// </remarks>
        private bool SettingsPage_SetLocalDimming(bool enable)
        {
            // Use the same WMI approach as SettingsPage.
            // The constants match SettingsPage.xaml.cs
            const string WmiClassName = "AcpiTest_MULong";
            const string DefaultInstanceName = @"ACPI\PNP0C14\1_1";
            const ulong CmdEnableStep1 = 0x50000000002;
            const ulong CmdEnableStep2 = 0x5000000000E;
            const ulong CmdDisableStep1 = 0x50000000001;
            const ulong CmdDisableStep2 = 0x5000000000D;
            const byte ExpectedEnableStep1 = 0xBB;
            const byte ExpectedEnableStep2 = 0x0E;
            const byte ExpectedDisableStep1 = 0xAA;
            const byte ExpectedDisableStep2 = 0x0D;

            try
            {
                var instancePath = $@"{WmiClassName}.InstanceName='{DefaultInstanceName}'";
                using var mo = new System.Management.ManagementObject(
                    @"root\WMI", instancePath, null);

                if (enable)
                {
                    byte result1 = WmiCall(mo, CmdEnableStep1);
                    if (result1 != ExpectedEnableStep1)
                        return false;

                    byte result2 = WmiCall(mo, CmdEnableStep2);
                    if (result2 != ExpectedEnableStep2)
                        return false;
                }
                else
                {
                    byte result1 = WmiCall(mo, CmdDisableStep1);
                    if (result1 != ExpectedDisableStep1)
                        return false;

                    byte result2 = WmiCall(mo, CmdDisableStep2);
                    if (result2 != ExpectedDisableStep2)
                        return false;
                }

                // Give the EC time to process the WMI command.
                Thread.Sleep(100);

                Settings.Default.DisplayLocalDimming = enable;
                Settings.Default.Save();
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Calls GetSetULong via WMI using the correct GetMethodParameters pattern.
        /// Matches the pattern used by MechControl (Heavyfail fork) on Uniwill IDY/Tongfang X6 laptops.
        /// </summary>
        private static byte WmiCall(
            System.Management.ManagementObject mo, ulong data)
        {
            using var input = mo.GetMethodParameters("GetSetULong");
            input["Data"] = data;

            using var output = (System.Management.ManagementBaseObject)mo.InvokeMethod("GetSetULong", input, null);
            return (byte)(uint)output["Return"];
        }

        /// <summary>Toggles Performance Overlay on/off.</summary>
        public void SetOverlay(bool enabled)
        {
            var options = _overlaySettingsStore.Current;
            _overlaySettingsStore.Save(options with { Enabled = enabled });

            // Also show/hide the overlay window immediately.
            if (enabled)
                _overlayManager.ShowOverlay();
            else
                _overlayManager.HideOverlay();
        }

        /// <summary>Toggles Flydigi RGB on/off.</summary>
        public async Task SetFlydigiRgbAsync(bool enabled)
        {
            if (!_flydigiCooler.IsConnected)
                return;

            var settings = _flydigiCooler.GetSettings();
            if (enabled)
            {
                // Restore to SmartTemp as default RGB mode when turning on
                settings.RgbMode = "SmartTemp";
                _flydigiCooler.PersistSettings();
                await _flydigiCooler.WriteRgbSmartTempAsync();
            }
            else
            {
                settings.RgbMode = "Off";
                _flydigiCooler.PersistSettings();
                await _flydigiCooler.WriteRgbOffAsync();
            }
        }

        #endregion

        #region Helpers

        /// <summary>Builds a default EcFanCurve from a named preset.</summary>
        private static EcFanCurve BuildPresetCurve(string presetName)
        {
            var curve = new EcFanCurve { Name = presetName };
            int[] duties = presetName.ToLowerInvariant() switch
            {
                "silent" => new int[] { 0, 10, 15, 20, 25, 30, 40, 50, 60, 70, 80 },
                "performance" => new int[] { 0, 20, 30, 40, 50, 60, 70, 80, 90, 100, 100 },
                _ => new int[] { 0, 10, 15, 20, 30, 40, 50, 65, 80, 90, 100 }, // Balanced
            };
            curve.Duties = duties.ToList();
            return curve;
        }

        #endregion
    }
}
