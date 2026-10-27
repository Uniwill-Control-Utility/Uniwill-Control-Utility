namespace UCU.Contracts
{
    /// <summary>
    /// The verb table. Verbs are grouped by hardware domain, which is also the
    /// execution domain inside the engine: each group runs on its own serial queue, so
    /// a long fan-curve upload cannot delay a tray temperature read, while writes that
    /// share a register set stay ordered with respect to each other.
    ///
    /// Strings are lowercase dotted names, treated as a published contract: the app
    /// pins them in its tests, so a rename is a protocol change (bump the pipe name).
    /// </summary>
    public static class EngineVerbs
    {
        public static class Fan
        {
            public const string GetStatus = "fan.get_status";
            public const string SetSpeedPercent = "fan.set_speed_percent";
            public const string RestoreAutoControl = "fan.restore_auto_control";
            public const string ToggleTurbo = "fan.toggle_turbo";
            public const string ResetState = "fan.reset_state";
            public const string ApplyCurve = "fan.apply_curve";
            public const string UploadCurve = "fan.upload_curve";
            public const string ReadCurve = "fan.read_curve";
            public const string SetUserFanMode = "fan.set_user_fan_mode";
            public const string GetFanPwm = "fan.get_pwm";
            public const string SetFanPwm = "fan.set_pwm";
            public const string SetFanPwmBoth = "fan.set_pwm_both";
            public const string SetFanSwitchSpeed = "fan.set_switch_speed";
            public const string ApplySmartFanCurve = "fan.apply_smart_curve";
            public const string ApplySmartFanDuty = "fan.apply_smart_duty";
            public const string UnlockManualMode = "fan.unlock_manual_mode";
            public const string WriteRegisterBlocks = "fan.write_register_blocks";
            public const string IsControlSupported = "fan.is_control_supported";
        }

        public static class Power
        {
            public const string GetLimits = "power.get_limits";
            public const string SetPowerLimit = "power.set_power_limit";
            public const string ApplyCpuPowerLimits = "power.apply_cpu_limits";
            public const string GetTccOffset = "power.get_tcc_offset";
            public const string SetTccOffset = "power.set_tcc_offset";
            public const string GetPerformanceMode = "power.get_mode";
            public const string SetPerformanceMode = "power.set_mode";
            public const string ApplyOemStockMode = "power.apply_stock_mode";
            public const string SetOverboostProfile = "power.set_overboost";
            public const string EnsurePowerManagementActive = "power.ensure_management_active";
            public const string ActivateGpuPowerControl = "power.activate_gpu_control";
            public const string DeactivateGpuPowerControl = "power.deactivate_gpu_control";
            public const string IsCustomProfileActive = "power.is_custom_profile_active";
        }

        public static class Mux
        {
            public const string GetMode = "mux.get_mode";
            public const string SetMode = "mux.set_mode";
            public const string GetHardwareState = "mux.get_hardware_state";
            public const string GetModuleId = "mux.get_module_id";
        }

        public static class Keyboard
        {
            public const string SetColor = "keyboard.set_color";
            public const string SetBrightness = "keyboard.set_brightness";
            public const string TurnOn = "keyboard.turn_on";
            public const string TurnOff = "keyboard.turn_off";
            public const string SetChinaMode = "keyboard.set_china_mode";
            public const string TriggerLogoEffect = "keyboard.trigger_logo_effect";
            public const string TriggerRainbowEffect = "keyboard.trigger_rainbow_effect";
            public const string IsRgbKeyboard = "keyboard.is_rgb";
            public const string IsWhiteOnlyKeyboard = "keyboard.is_white_only";
        }

        public static class Battery
        {
            public const string GetTelemetry = "battery.get_telemetry";
            public const string GetTemperature = "battery.get_temperature";
            public const string GetMode = "battery.get_mode";
            public const string SetMode = "battery.set_mode";
        }

        public static class Telemetry
        {
            public const string GetFanDuty = "telemetry.get_fan_duty";
            public const string GetCpuTemperature = "telemetry.get_cpu_temperature";
            public const string GetGpuTemperature = "telemetry.get_gpu_temperature";
            public const string GetMainFanRpm = "telemetry.get_main_fan_rpm";
            public const string GetSecondFanRpm = "telemetry.get_second_fan_rpm";
            public const string GetProjectId = "telemetry.get_project_id";
            public const string ReadRegister = "telemetry.read_register";
            public const string ReadRegisters = "telemetry.read_registers";
            public const string WriteRegister = "telemetry.write_register";
        }

        public static class Engine
        {
            public const string Ping = "engine.ping";
            public const string GetInfo = "engine.get_info";
            public const string IsHardwareAvailable = "engine.is_hardware_available";

            /// <summary>
            /// "This shutdown is intentional": the app reports its own graceful
            /// exit as a verb, so the supervisor can tell a user close from a
            /// crash without a file path shared between an unelevated app and a
            /// LocalSystem service. Best-effort by contract: a crash never sends it.
            /// </summary>
            public const string NotifyAppExit = "engine.notify_app_exit";
        }
    }
}
