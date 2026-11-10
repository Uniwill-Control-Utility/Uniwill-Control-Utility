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

            /// <summary>
            /// The power-contract truth (U-107): the GPU cTGP/DB register bytes
            /// (0x0743-0x0746, 0x0727 bit6) and the CPU PL1/PL2 wattage in one
            /// serialized read. Laptop NVML answers NOT_SUPPORTED for power
            /// limits, so the EC's own registers are the only truth about what
            /// power ceiling the firmware enforces — the drift oracle for the
            /// app's tuning guardian.
            /// </summary>
            public const string GetContractTruth = "power.get_contract_truth";

            /// <summary>
            /// One serialized re-apply of a drifted contract half: the GPU
            /// cTGP/DB activation sequence and/or the CPU PL pair, each behind
            /// its own Apply* flag (the engine replays the preset pipeline's
            /// own activation methods; deciding WHAT to heal is app-side Core
            /// policy).
            /// </summary>
            public const string HealContract = "power.heal_contract";

            /// <summary>
            /// AC power recovery (U-108): "turn the machine on when AC power is
            /// restored" (EC 0x0726 bit 3, field-probed on the lab box:
            /// bit-scoped RMW preserves every other bit of the register,
            /// including the power-management gate bit 7 the engine's
            /// activation handshake owns — one shared RMW discipline, never a
            /// second writer that stomps).
            /// </summary>
            public const string GetAcRecovery = "power.get_ac_recovery";
            public const string SetAcRecovery = "power.set_ac_recovery";
        }

        public static class Mux
        {
            public const string GetMode = "mux.get_mode";
            public const string SetMode = "mux.set_mode";
            public const string GetHardwareState = "mux.get_hardware_state";
            public const string GetModuleId = "mux.get_module_id";
        }

        /// <summary>
        /// Privileged NVIDIA tuning writes. NVAPI refuses the clock/voltage
        /// writes to an unelevated token: the asInvoker app's
        /// NvAPI_GPU_SetClockBoostTable answers NVAPI_INVALID_USER_PRIVILEGE
        /// (0xFFFFFF77), so the V-F curve apply and the DRS GPU preference are
        /// dead there (field log, V-F write refused). The engine performs the
        /// same interop as LocalSystem, where the write is accepted. The reads
        /// stay app-side: they answer unelevated and the app must not depend on
        /// the engine for its telemetry.
        /// </summary>
        public static class Gpu
        {
            public const string ApplyVfCurve = "gpu.apply_vf_curve";
            public const string SetDrsPreference = "gpu.set_drs_preference";

            /// <summary>The core/memory offset sliders. Same privilege gate as the
            /// curve: the wrapped NvAPI call throws NVAPI_INVALID_USER_PRIVILEGE on
            /// an unelevated token (field logs: 12 calls today, 42 since 04 Nov,
            /// and the driver's "performance states read-only" line never
            /// appears - so this is the token, not the driver flag).</summary>
            public const string SetClocks = "gpu.set_clocks";
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

            /// <summary>
            /// Win-key lock (U-108): the truth is the EC status byte 0x0768
            /// bit 0; the SET fires the trigger byte 0x0767 (bit 0,
            /// self-consuming) which TOGGLES the lock — the engine fires only
            /// on truth mismatch and verifies the flip by re-read (the lab
            /// box: trigger honored, debounce swallows rapid re-fires, lock
            /// state persists across app restarts in the EC). Fires ride the
            /// keyboard domain so trigger-byte RMWs are ordered against the
            /// effect writes.
            /// </summary>
            public const string GetWinLock = "keyboard.get_win_lock";
            public const string SetWinLock = "keyboard.set_win_lock";

            /// <summary>
            /// Fn-key lock (U-108): EC 0x074E bit 4 (0x10 locked, 0x01
            /// unlocked — open-revo encoding, field-verified writable with
            /// every other bit preserved). Plain bit-scoped RMW.
            /// </summary>
            public const string GetFnLock = "keyboard.get_fn_lock";
            public const string SetFnLock = "keyboard.set_fn_lock";
        }

        public static class Battery
        {
            public const string GetTelemetry = "battery.get_telemetry";
            public const string GetTemperature = "battery.get_temperature";
            public const string GetMode = "battery.get_mode";
            public const string SetMode = "battery.set_mode";
            public const string GetGateStatus = "battery.get_gate_status";
            public const string GetThresholds = "battery.get_thresholds";
            public const string GetPlatformOverride = "battery.get_platform_override";
        }

        /// <summary>
        /// PnP device enable/disable (the Device Manager-style rows). These were the
        /// app's remaining per-operation UAC prompts, and two of them were reachable
        /// from AUTOMATIC paths (GPU power saving applies at startup, on charger
        /// transitions, from the tray, and from Automations) - a consent prompt a
        /// machine-global LocalSystem service simply never needs. The state
        /// mutations run on the engine's CM_/SetupDi APIs directly: no
        /// powershell.exe child, no consent, and the verification re-read happens
        /// in-process. The boundary is the pipe DACL, unchanged: the console user
        /// may already write fan curves, power limits, overboost and the GPU mux,
        /// so enabling a camera-class or display-class device is strictly below the
        /// capability that boundary already grants.
        /// </summary>
        public static class Device
        {
            public const string GetWebcamStatus = "device.get_webcam_status";
            public const string SetWebcamEnabled = "device.set_webcam_enabled";
            public const string GetDgpuStatus = "device.get_dgpu_status";
            public const string SetDgpuEnabled = "device.set_dgpu_enabled";

            /// <summary>
            /// UEFI firmware environment variables (the OemMagicVariable NVRAM
            /// entry and its offsets). Reading and writing them demands
            /// SeSystemEnvironmentPrivilege, which an unelevated process cannot
            /// enable — so on the guarded field machines every app-side use of
            /// these (the CPU Advanced Performance Menu row, the GPU mux's
            /// firmware-visible mirror) was silently dead: the reads returned
            /// nothing, the writes failed with access denied, and no prompt ever
            /// appeared to explain. The engine enables the privilege on its
            /// LocalSystem token, so the same rows work as designed.
            ///
            /// The surface is deliberately this raw — the OEM variable's layout
            /// (which offset is which feature) is machine data the app already
            /// owns, and the engine stays a privilege broker here rather than
            /// growing firmware knowledge per feature. The boundary argument is
            /// the mux one: the OemMagicVariable byte at offset 98 IS the GPU
            /// mode the mux.set_mode verb already writes, so reading and
            /// writing this entry is at the capability level the pipe DACL
            /// already grants.
            ///
            /// The write verb lands the 50-series gatekeeper handshake: the
            /// service unlocks OemMagicDoor (one byte, 0x01) immediately
            /// before writing the OEM entry — the unlock the reference needs
            /// for the write to stick across reboot — and a door write that
            /// fails answers false with the entry untouched (no blind write).
            /// </summary>
            public const string GetNvramValue = "device.get_nvram_value";
            public const string SetNvramValue = "device.set_nvram_value";

            /// <summary>Firmware-reported support byte for the CPU Advanced
            /// Performance Menu (OemMagicVariable offset 110, non-zero = the
            /// BIOS exposes the menu). A verb, not a client-side read, because
            /// the row's support probe needs the same privilege as its write.</summary>
            public const string GetSupportsCpuApMenu = "device.get_supports_cpu_ap_menu";

            /// <summary>Unlocks/locks the BIOS CPU Advanced Performance Menu:
            /// offsets 111 (CPU menu switch) and 51 (memory OC switch) set in
            /// lockstep, tester-confirmed on Intel NB02. The pair-write lives
            /// engine-side so the two offsets can never half-update: the verb
            /// reads, patches, and rewrites the whole entry atomically on the
            /// device queue.</summary>
            public const string SetCpuApMenuUnlock = "device.set_cpu_ap_menu_unlock";

            /// <summary>MiniLED local dimming: the OEM ACPI WMI method
            /// (AcpiTest_MULong.GetSetULong on ACPI\PNP0C14\1_1) that runs the
            /// enable/disable two-step handshake, each step verified against
            /// the firmware's expected reply byte (0xBB/0x0E enable,
            /// 0xAA/0x0D disable). Executing a custom root\WMI method on the
            /// OEM namespace needs the write access an unelevated process does
            /// not have, so the tray and display rows were silently dead on
            /// the guarded field machines (the InvokeMethod threw access
            /// denied into a catch that returned a bare false). The engine
            /// performs the handshake as LocalSystem and the verb answers
            /// "the handshake verified". The EC 0x72B bit-0x02 readback stays
            /// app-side on the existing telemetry verb.</summary>
            public const string SetMiniLedDimming = "device.set_mini_led_dimming";
        }

        /// <summary>
        /// CPU register accesses that demand a privileged token: the AMD SMU
        /// mailboxes (PawnIO's device ACL admits System and Administrators, so
        /// an unelevated app's opens are access-denied and every Curve
        /// Optimizer / SMU preset apply on a field machine was silently dead —
        /// the row saved, the register never moved). The engine runs
        /// LocalSystem: the same PawnIO module loads there, the mailbox
        /// protocol runs there, and the verb answers with the SMU's own status
        /// code - the honest witness the app route never had. The command
        /// names are the preset vocabulary (data), the per-socket address
        /// tables and the mailbox protocol are engine-side, and the pipe DACL
        /// is the boundary: the console user may already write the EC's power
        /// limits, so the CPU's own limits register set is at the level the
        /// boundary already grants.
        /// </summary>
        public static class Cpu
        {
            public const string ExecuteAmdSmuCommand = "cpu.execute_amd_smu_command";

            /// <summary>
            /// Intel MSRs ride the same PawnIO device as the SMU mailboxes
            /// (ACL: System and Administrators), so the app-side TDP / voltage
            /// / ratio applies were the same silent death on Intel machines.
            /// The verbs are per-register-set (not one raw read/write MSR
            /// verb): writing ANY MSR by address would widen what the console
            /// user can reach, while these four register sets are the same
            /// power-limit authority the EC power verbs already grant.
            /// </summary>
            public const string IntelSetTdp = "cpu.intel_set_tdp";
            public const string IntelSetPowerBalance = "cpu.intel_set_power_balance";
            public const string IntelSetVoltageOffset = "cpu.intel_set_voltage_offset";
            public const string IntelSetClockRatio = "cpu.intel_set_clock_ratio";
            public const string IntelGetClockRatios = "cpu.intel_get_clock_ratios";
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

            /// <summary>
            /// The engine's cached kernel-side sensor readings (CPU package power and
            /// core clock via Ring0 MSRs, DIMM SPD speed via the SMBus). These need the
            /// sensor drivers, whose device ACLs admit System + Administrators
            /// (PawnIO's INF ships D:P(A;;GA;;;SY)(A;;GA;;;BA)), so the asInvoker app
            /// installed per-machine gets access denied and no reading at all; the
            /// LocalSystem engine gets both. This is the ONLY sensor-reading verb: the
            /// verb surface stays EC-semantics by decision, the field metrics gap
            /// (dashboard CPU power/clock and RAM speed never appearing unelevated,
            /// 2026-10-29) is closed by one cached read, not by moving the LHM tree
            /// behind the pipe. GPU power/clock stay app-side (NVAPI decision).
            /// The answer is a cache, never a live probe: the verb never queues behind
            /// sensor I/O.
            /// </summary>
            public const string GetMetricsSnapshot = "telemetry.get_metrics_snapshot";
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

            /// <summary>
            /// This user's logon autostart entry ("UCU Autostart"). The service
            /// owns it because the Task Scheduler refuses task creation to a
            /// medium-integrity token: the 0.9.6 app could register its own entry
            /// only because it ran elevated, and 0.9.7's unelevated app cannot
            /// (field, 2026-11-06 - opening Settings threw E_ACCESSDENIED).
            ///
            /// The request carries the user's CHOICE only. The action path comes
            /// from the engine's own install layout and the account from the
            /// interactive session: a caller-supplied path would be a
            /// create-a-task-running-anything-as-the-user primitive, i.e. an
            /// escalation route from any authenticated account.
            /// </summary>
            public const string SetAutostart = "engine.set_autostart";
        }

        /// <summary>
        /// RTSS — the game overlay's frame source. RTSS is a user-session
        /// tray/injection process, so it must be started IN THE ACTIVE SESSION:
        /// the unelevated app cannot do it silently on machines where RTSS
        /// carries the "Run as administrator" launch flag (a consent prompt
        /// every time — field-reported), and a scheduled-task route places a
        /// GUI process in session 0, where it is invisible, injects nothing,
        /// and still satisfies the app's global shared-memory witness
        /// (field-proven). The LocalSystem engine reads the console user's
        /// LINKED (full) token and starts the image as that token: the
        /// session, the privileges the flags demand, and no dialog.
        ///
        /// The verb takes no parameters on purpose: the engine locates RTSS
        /// from the registry/process witnesses itself and refuses any image
        /// not named RTSS.exe, so a service handle is never an
        /// arbitrary-execution surface.
        /// </summary>
        public static class Rtss
        {
            /// <summary>Starts RTSS in the active console session. Answers the
            /// observed launch (true = the process started), never an
            /// assumption.</summary>
            public const string Start = "rtss.start";
        }

        /// <summary>
        /// OEM Control Center takeover (story U-203, open-revo takeover/
        /// lifecycle.rs port). The machine-scope half of the pause: HKLM run
        /// keys, the companion services, the scheduled tasks and the
        /// verified OEM processes. These are LocalSystem mutations by
        /// construction - the HKLM Run roots are admin-write, the services'
        /// Start type needs CHANGE_CONFIG, the tasks are machine objects, and
        /// killing another session's process needs rights an unelevated app
        /// never gets - so they live engine-side like every other privileged
        /// row. The user-scope half (HKCU run keys, UWP AppModel state, the
        /// StartupApproved flags) stays app-side: those are the signed-in
        /// user's keys and the engine, running as LocalSystem, must not write
        /// them blind.
        ///
        /// Hardening (the plan's audit note D.7): the reference mutates by
        /// bare name, which any user can squat. Every engine-side target
        /// passes the path+signature+publisher gate (UCU.Core.Takeover)
        /// before a mutation, gate failures are listed as squat-suspects and
        /// never touched, and UCU's own install roots are untouchable.
        /// </summary>
        public static class Oem
        {
            /// <summary>The machine-scope inventory: catalog service/process
            /// counts, journal size, neutralized-autostart count, the
            /// squat-suspect audit list, and the pause state the counts
            /// decide (TakeoverPolicy.DecideState).</summary>
            public const string GetStatus = "oem.get_status";

            /// <summary>Pauses the OEM stack: gated process kills, service
            /// stop + Start=demand, task disable, HKLM run-key neutralization,
            /// every mutation journaled for restore. Auto-bypasses (zero
            /// mutations) on machines without the stack - the reference's
            /// Thin &amp; Light path, field-proven by the dev-box probe.</summary>
            public const string Pause = "oem.pause";

            /// <summary>Replays the journal: run keys back, tasks enabled,
            /// services re-armed and started in the watchdog order (bridge
            /// first, the service the bridge spawns watched, the rest
            /// started). Idempotent: kills drop out, one replay per target.</summary>
            public const string Resume = "oem.resume";
        }
    }
}
