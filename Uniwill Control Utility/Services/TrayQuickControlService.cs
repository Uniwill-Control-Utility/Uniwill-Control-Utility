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
        bool LocalDimmingEnabled,
        PumpVoltage PumpVoltage,
        FanSpeed FanSpeed,
        TrayQuickControlPolicy Policy,
        string[] CustomPresetNames,
        bool IsPowerBoostMode);

    public sealed class TrayQuickControlService
    {
        private readonly UniwillECService _ec;
        private readonly WaterCoolerService _waterCooler;
        private readonly DeviceApplier _deviceApplier;
        private readonly IHardwareMonitoringService _hardwareMonitoring;
        private readonly EcTelemetryService _ecTelemetry;

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

            var policy = TrayQuickControlPolicy.Create(
                hasNvidiaGpu: hasGpu,
                hasSupportedWaterCooler: hydroSupported,
                waterCoolerConnected: _waterCooler.IsConnected,
                adaptiveCpuEnabled: adaptiveRunning && Settings.Default.AdaptiveCpuEnabled,
                adaptiveGpuEnabled: adaptiveRunning && Settings.Default.AdaptiveGpuEnabled,
                adaptiveFanEnabled: adaptiveRunning && _deviceApplier.IsEcFanOverridden,
                adaptiveWaterCoolerEnabled: adaptiveRunning && _deviceApplier.IsWatercoolerOverridden);

            var waterSettings = _waterCooler.GetSettings();

            bool isPowerBoostMode = NvTuning.IsBlackwellHighEnd() && Settings.Default.EnablePowerBoostMode;
            PresetMode activeMode = isPowerBoostMode ? PresetMode.Boost : PresetMode.Normal;
            string[] customPresetNames = TrayPresetOptions.BuildCustomPresetNames(
                activeMode,
                isPowerBoostMode);

            // Resolve selected preset from saved profile
            string selectedPreset = Settings.Default.presetProfile;
            string[] presetNames = TrayPresetOptions.ProfileNames.ToArray();
            if (presetNames.Contains(selectedPreset, StringComparer.OrdinalIgnoreCase))
            {
                // Stock preset (Office, Gaming, Turbo)
            }
            else if (TrayPresetOptions.IsCustomPreset(selectedPreset))
            {
                // Custom preset — resolve to display name.
                // "Custom" → use active slot from settings.
                // "Custom N" / "Custom N (Boost)" → parse slot from name.
                int slot;
                var parts = selectedPreset.Replace("(Boost)", "").Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2 && int.TryParse(parts[1], out int parsed))
                    slot = Math.Clamp(parsed - 1, 0, CustomPreset.MaxSlots - 1);
                else
                    slot = activeMode == PresetMode.Boost
                        ? Settings.Default.customPresetSlotBoost
                        : Settings.Default.customPresetSlot;
                slot = Math.Clamp(slot, 0, CustomPreset.MaxSlots - 1);

                selectedPreset = TrayPresetOptions.CustomPresetDisplayName(slot, isPowerBoostMode);
                if (!customPresetNames.Contains(selectedPreset, StringComparer.OrdinalIgnoreCase))
                    selectedPreset = presetNames.FirstOrDefault() ?? "Office";
            }
            else
            {
                // Unknown — fall back to first stock preset
                selectedPreset = presetNames.FirstOrDefault() ?? "Office";
            }

            return new TrayQuickControlSnapshot(
                PresetNames: presetNames,
                SelectedPreset: selectedPreset,
                LocalDimmingEnabled: Settings.Default.DisplayLocalDimming,
                PumpVoltage: waterSettings.GetPumpVoltage(),
                FanSpeed: waterSettings.GetFanSpeed(),
                Policy: policy,
                CustomPresetNames: customPresetNames,
                IsPowerBoostMode: isPowerBoostMode);
        }

        public async Task ApplyPresetAsync(string profileName)
        {
            if (string.IsNullOrWhiteSpace(profileName))
                return;

            PresetMode activeMode = NvTuning.IsBlackwellHighEnd() && Settings.Default.EnablePowerBoostMode
                ? PresetMode.Boost
                : PresetMode.Normal;

            string applicationKey;
            int? targetSlot = null;

            if (TrayPresetOptions.IsCustomPreset(profileName))
            {
                // Try to parse "Custom N" or "Custom N (Boost)" to get the slot index
                int? parsedSlot = TryParseCustomSlot(profileName);
                if (parsedSlot.HasValue)
                {
                    targetSlot = Math.Clamp(parsedSlot.Value, 0, CustomPreset.MaxSlots - 1);
                    var customPresets = CustomPresetStore.Load(activeMode);
                    bool slotHasChanges = targetSlot.Value < customPresets.Length && customPresets[targetSlot.Value].HasChanges;
                    applicationKey = TrayPresetOptions.ResolveApplicationKey(
                        profileName,
                        activeMode,
                        targetSlot.Value,
                        slotHasChanges);
                }
                else
                {
                    // Fallback: plain "Custom" — use active slot from settings
                    int activeSlot = activeMode == PresetMode.Boost
                        ? Settings.Default.customPresetSlotBoost
                        : Settings.Default.customPresetSlot;
                    activeSlot = Math.Clamp(activeSlot, 0, CustomPreset.MaxSlots - 1);
                    var customPresets = CustomPresetStore.Load(activeMode);
                    bool slotHasChanges = activeSlot < customPresets.Length && customPresets[activeSlot].HasChanges;
                    applicationKey = TrayPresetOptions.ResolveApplicationKey(
                        profileName,
                        activeMode,
                        activeSlot,
                        slotHasChanges);
                }
            }
            else
            {
                applicationKey = profileName;
            }

            await PresetProfileApplier.ApplyProfileAsync(applicationKey, profileName);
            ApplyPresetFanCurve(profileName);
            Settings.Default.presetProfile = profileName;
            Settings.Default.Save();

            // Update the active slot in settings when a specific custom preset is applied
            if (targetSlot.HasValue)
            {
                if (activeMode == PresetMode.Boost)
                    Settings.Default.customPresetSlotBoost = targetSlot.Value;
                else
                    Settings.Default.customPresetSlot = targetSlot.Value;
                Settings.Default.Save();
            }
        }

        /// <summary>
        /// Parses "Custom N" or "Custom N (Boost)" and returns the 0-based slot index.
        /// Returns null if the format doesn't match.
        /// </summary>
        private static int? TryParseCustomSlot(string profileName)
        {
            string text = profileName.Trim();
            // Remove " (Boost)" suffix if present
            int boostIndex = text.IndexOf(" (Boost)", StringComparison.OrdinalIgnoreCase);
            if (boostIndex >= 0)
                text = text.Substring(0, boostIndex);

            // Expect "Custom N"
            if (!text.StartsWith("Custom ", StringComparison.OrdinalIgnoreCase))
                return null;

            string numberPart = text.Substring("Custom ".Length).Trim();
            if (int.TryParse(numberPart, out int slot) && slot >= 1 && slot <= CustomPreset.MaxSlots)
                return slot - 1; // 0-based

            return null;
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
