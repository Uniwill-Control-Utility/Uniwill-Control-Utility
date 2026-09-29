using System;
using System.Linq;
using System.Threading.Tasks;
using Uniwill_Control_Utility.Models;
using Uniwill_Control_Utility.Properties;
using Uniwill_Control_Utility.Scripts;
using Uniwill_Control_Utility.Scripts.GPUs.NVIDIA;
using Uniwill_Control_Utility.Scripts.Misc;

namespace Uniwill_Control_Utility.Services
{
    public sealed record TrayQuickControlSnapshot(
        string[] PresetNames,
        string SelectedPreset,
        bool FullFanEnabled,
        int[] RefreshRates,
        int RefreshRate,
        PumpVoltage PumpVoltage,
        FanSpeed FanSpeed,
        TrayQuickControlPolicy Policy);

    public sealed class TrayQuickControlService
    {
        private readonly UniwillECService _ec;
        private readonly WaterCoolerService _waterCooler;
        private readonly DeviceApplier _deviceApplier;
        private readonly IHardwareMonitoringService _hardwareMonitoring;
        private readonly EcTelemetryService _ecTelemetry;
        private readonly FullFanController _fullFanController;

        public TrayQuickControlService(
            UniwillECService ec,
            WaterCoolerService waterCooler,
            DeviceApplier deviceApplier,
            IHardwareMonitoringService hardwareMonitoring,
            EcTelemetryService ecTelemetry)
        {
            _ec = ec;
            _waterCooler = waterCooler;
            _deviceApplier = deviceApplier;
            _hardwareMonitoring = hardwareMonitoring;
            _ecTelemetry = ecTelemetry;
            _fullFanController = new FullFanController(_ec.IsTurboActive, _ec.ToggleTurboAsync);
        }

        public TrayQuickTelemetrySnapshot GetTelemetrySnapshot() =>
            TrayQuickTelemetryComposer.Compose(
                _hardwareMonitoring.ReadSnapshot(),
                _ecTelemetry.ReadFanDuty());

        public bool IsFullFanActive => _fullFanController.IsActive;

        public TrayQuickControlSnapshot GetSnapshot()
        {
            bool hasGpu = !string.IsNullOrWhiteSpace(NvTuning.GetGpuName());
            bool adaptiveRunning = Settings.Default.isAdaptiveModeRunning;
            bool hydroSupported = WaterCoolerHardwareDetector.IsSupportedHardware();

            var policy = TrayQuickControlPolicy.Create(
                hasNvidiaGpu: hasGpu,
                hasSupportedWaterCooler: hydroSupported,
                waterCoolerConnected: _waterCooler.IsConnected,
                adaptiveCpuEnabled: adaptiveRunning && Settings.Default.AdaptiveCpuEnabled,
                adaptiveGpuEnabled: adaptiveRunning && Settings.Default.AdaptiveGpuEnabled,
                adaptiveFanEnabled: adaptiveRunning && _deviceApplier.IsEcFanOverridden,
                adaptiveWaterCoolerEnabled: adaptiveRunning && _deviceApplier.IsWatercoolerOverridden);

            Display.SetUpLists();
            int refreshRate = Display.GetCurrentRefreshRate();
            int[] refreshRates = TrayRefreshRateOptions.Build(Display.UniqueRefreshRates, refreshRate);
            if (refreshRate <= 0)
                refreshRate = refreshRates.FirstOrDefault();

            var waterSettings = _waterCooler.GetSettings();

            string selectedPreset = Settings.Default.presetProfile;
            string[] presetNames = TrayPresetOptions.ProfileNames.ToArray();
            if (!presetNames.Contains(selectedPreset, StringComparer.OrdinalIgnoreCase))
                selectedPreset = presetNames.FirstOrDefault() ?? "Office";

            return new TrayQuickControlSnapshot(
                PresetNames: presetNames,
                SelectedPreset: selectedPreset,
                FullFanEnabled: _fullFanController.IsActive,
                RefreshRates: refreshRates,
                RefreshRate: refreshRate,
                PumpVoltage: waterSettings.GetPumpVoltage(),
                FanSpeed: waterSettings.GetFanSpeed(),
                Policy: policy);
        }

        public async Task ApplyPresetAsync(string profileName)
        {
            if (string.IsNullOrWhiteSpace(profileName))
                return;

            PresetMode activeMode = NvTuning.IsBlackwellHighEnd() && Settings.Default.EnablePowerBoostMode
                ? PresetMode.Boost
                : PresetMode.Normal;
            int activeSlot = activeMode == PresetMode.Boost
                ? Settings.Default.customPresetSlotBoost
                : Settings.Default.customPresetSlot;
            activeSlot = Math.Clamp(activeSlot, 0, CustomPreset.MaxSlots - 1);

            CustomPreset[] customPresets = CustomPresetStore.Load(activeMode);
            bool slotHasChanges = activeSlot < customPresets.Length && customPresets[activeSlot].HasChanges;
            string applicationKey = TrayPresetOptions.ResolveApplicationKey(
                profileName,
                activeMode,
                activeSlot,
                slotHasChanges);

            await PresetProfileApplier.ApplyProfileAsync(applicationKey, profileName);
            ApplyPresetFanCurve(profileName);
            Settings.Default.presetProfile = profileName;
            Settings.Default.Save();
        }

        public async Task SetFullFanAsync(bool enabled)
        {
            // Turbo is an EC mode, not a persisted fan-curve preset. Applying a
            // "Full Speed" curve here was immediately overwritten by Adaptive
            // Mode and also made the toggle appear to do nothing.
            EcSmartFanControl.ClearInstance();
            await _fullFanController.SetAsync(enabled);
            DiagnosticLogger.LogInfo($"Quick Control full fan is now {(enabled ? "enabled" : "disabled")}");
        }

        public Task SetRefreshRateAsync(int refreshRate)
        {
            return Task.Run(() => Display.ApplySettings(refreshRate));
        }

        public async Task SetHydroPumpAsync(PumpVoltage voltage)
        {
            if (!_waterCooler.IsConnected)
                return;

            await _waterCooler.WritePumpModeAsync(voltage);
            _waterCooler.UpdatePumpVoltage(voltage);
        }

        public async Task SetHydroFanAsync(FanSpeed speed)
        {
            if (!_waterCooler.IsConnected)
                return;

            await _waterCooler.WriteFanModeAsync(speed);
            _waterCooler.UpdateFanSpeed(speed);
        }

        private void ApplyPresetFanCurve(string profileName)
        {
            string? fanPreset = profileName.ToLowerInvariant() switch
            {
                "office" => "Silent",
                "gaming" => "Balanced",
                "turbo" => "Performance",
                _ => null
            };
            if (fanPreset is null)
                return;

            FanControlSettings settings = FanControlSettingsService.Load();
            settings.UnifiedMode = true;
            settings.UnifiedPreset = fanPreset;
            FanControlSettingsService.Save(settings);
            _deviceApplier.ApplyEcFanFromPreset(
                unifiedMode: true,
                presetName: fanPreset,
                customDuties: null,
                cpuPresetDuties: null,
                gpuPresetDuties: null);
        }
    }
}
