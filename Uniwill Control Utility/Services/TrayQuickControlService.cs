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

        /// <summary>
        /// Fired when the tray writes the Light Bar power state (Settings.Default.LightBarEnabled)
        /// so the visible Light Bar page can re-sync without re-navigation — the Light Bar
        /// analogue of KeyboardSettingsService.SettingsChanged.
        /// </summary>
        public event EventHandler? LightBarSettingsChanged;

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

            // Live Windows display mode values for the internal panel — the tray's
            // refresh-rate and color-calibration controls target the laptop panel
            // exclusively (never an external monitor). When the panel is detached
            // (external-only), GDI hides it: the last-known persisted values are
            // reported instead (so the buttons keep a selection across the panel being
            // switched off), and selections made then are deferred until reattach.
            bool panelAttached = !string.IsNullOrEmpty(Display.InternalPanelName());
            int refreshRateHz = 0;
            bool refreshRateSwitchable = false;
            string? colorProfileFile = null;
            bool colorCalAvailable = false;
            try
            {
                if (panelAttached)
                {
                    // Consume deferred selections from an external-only session first (this
                    // runs off-thread via Task.Run) so the snapshot reflects the reconciled
                    // state rather than the stale pre-switch values.
                    int deferred = Settings.Default.DisplayRefreshRateDeferred;
                    if (deferred > 0)
                    {
                        int applied = Display.ReconcileInternalRefreshRate(deferred);
                        if (applied == deferred)
                        {
                            Settings.Default.DisplayRefreshRateDeferred = 0;
                            Settings.Default.DisplayRefreshRateLastKnown = applied;
                            Settings.Default.Save();
                            Log.Information("[Tray] Deferred refresh rate {Hz} Hz applied on panel reattach", applied);
                        }
                    }

                    string deferredProfile = Settings.Default.DisplayColorProfileDeferred;
                    if (deferredProfile.Length > 0)
                    {
                        string appliedProfile = Display.ReconcileInternalColorProfile(deferredProfile);
                        if (appliedProfile == deferredProfile)
                        {
                            Settings.Default.DisplayColorProfileDeferred = string.Empty;
                            Settings.Default.DisplayColorProfileLastKnown = appliedProfile;
                            Settings.Default.Save();
                            Log.Information("[Tray] Deferred color profile {Profile} applied on panel reattach", appliedProfile);
                        }
                    }

                    refreshRateHz = Display.GetInternalRefreshRate();
                    if (refreshRateHz > 0)
                        Settings.Default.DisplayRefreshRateLastKnown = refreshRateHz;
                    colorProfileFile = Display.GetInternalColorProfile();
                    Settings.Default.DisplayColorProfileLastKnown = colorProfileFile ?? string.Empty;
                }
                else
                {
                    // Panel detached — report the last known values so the tray keeps a
                    // selection while the panel is off.
                    refreshRateHz = Settings.Default.DisplayRefreshRateLastKnown;
                    string lastKnownProfile = Settings.Default.DisplayColorProfileLastKnown;
                    colorProfileFile = lastKnownProfile.Length > 0 ? lastKnownProfile : null;
                }

                refreshRateSwitchable = Display.GetInternalRefreshRates(gpuModeDgpuOnly).Count > 1;
                colorCalAvailable = Display.GetOfferedColorProfileFiles().Count > 1;
            }
            catch (Exception ex)
            {
                Log.Warning("[Tray] Panel display read failed: {Message}", ex.Message);
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
                lightBarSyncActive: App.GetService<BetterRgbStreamService>()?.IsLightBarSyncActive == true,
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

            // BetterRGB engine owns the keyboard endpoint — route the tray toggle
            // through the streamer. A firmware TurnOnWithEffect while the engine
            // streams puts two consumers on the endpoint and seizes the lighting.
            var streamer = App.GetService<BetterRgbStreamService>();
            if (settings.Engine == KeyboardEngine.BetterRGB && streamer != null)
            {
                if (enabled)
                {
                    streamer.SetParams(settings);
                    streamer.StartEngine("tray");
                }
                else
                {
                    streamer.StopEngine("tray");
                    // StopEngine alone leaves the last streamed frame lit — pair with TurnOff.
                    var offHid = new KeyboardHidService();
                    if (offHid.Open())
                    {
                        try
                        {
                            offHid.TurnOff();
                        }
                        finally
                        {
                            offHid.Dispose();
                        }
                    }
                }
                return;
            }

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
            // BetterRGB light bar sync owns the strip while it streams (the tray
            // button is disabled then); ignore stray calls so a firmware write
            // never fights the sync pushes.
            if (App.GetService<BetterRgbStreamService>()?.IsLightBarSyncActive == true)
                return;

            Settings.Default.LightBarEnabled = enabled;
            Settings.Default.Save();
            LightBarSettingsChanged?.Invoke(this, EventArgs.Empty);

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
        /// pathway (mirrors the Display page's refresh-rate apply core). Returns
        /// true when the readback confirms the requested rate, or when the selection
        /// was deferred because the panel is detached (external-only) — it is applied
        /// once the panel reattaches.
        /// </summary>
        public async Task<bool> SetRefreshRateAsync(int hz)
        {
            try
            {
                if (string.IsNullOrEmpty(Display.InternalPanelName()))
                {
                    // Panel detached (lid closed / internal display disconnected) — GDI
                    // hides a display that is off, so the write cannot land now. Persist
                    // the selection (deferred for reattach) and remember it as the panel's
                    // value so the tray keeps a selection while the panel is off.
                    Settings.Default.DisplayRefreshRateDeferred = hz;
                    Settings.Default.DisplayRefreshRateLastKnown = hz;
                    Settings.Default.Save();
                    Log.Information("[Tray] Refresh rate {Hz} Hz deferred — internal panel detached (external-only)", hz);
                    return true;
                }

                Settings.Default.DisplayRefreshRateDeferred = 0;
                Settings.Default.Save();
                int applied = Display.ApplyInternalRefreshRate(hz);

                // Verify via readback — the engine reports failures only to the console.
                bool success = applied == hz;
                if (success)
                {
                    Settings.Default.DisplayRefreshRateLastKnown = applied;
                    Settings.Default.Save();
                }
                Log.Information("[Tray] Internal panel refresh rate set to {Applied} Hz (requested {Hz} Hz, success: {Success})",
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
                if (string.IsNullOrEmpty(Display.InternalPanelName()))
                {
                    // Panel detached (external-only) — persist the selection (deferred for
                    // reattach) and remember it so the tray keeps a selection while the
                    // panel is off.
                    Settings.Default.DisplayColorProfileDeferred = profileFile;
                    Settings.Default.DisplayColorProfileLastKnown = profileFile;
                    Settings.Default.Save();
                    Log.Information("[Tray] Color profile {File} deferred — internal panel detached (external-only)", profileFile);
                    return true;
                }

                Settings.Default.DisplayColorProfileDeferred = string.Empty;
                Settings.Default.Save();
                var target = Display.InternalPanelName();
                Display.ApplyColorProfile(target, profileFile);

                // Verify via readback — the registry write reports failures only via return code.
                string? applied = Display.GetCurrentColorProfile(target);
                bool success = applied == profileFile;
                if (success)
                {
                    Settings.Default.DisplayColorProfileLastKnown = profileFile;
                    Settings.Default.Save();
                }
                Log.Information("[Tray] Internal panel color profile set to {Applied} (requested {File}, success: {Success})",
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
