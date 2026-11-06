using UCU.Core.Ec;

namespace UCU.Contracts
{
    /// <summary>Which CPU power-limit register a write addresses.</summary>
    public enum PowerLimitKind
    {
        Pl1 = 0,
        Pl2 = 1,
        Pl4 = 2,
        Pl5 = 3,
    }

    // ---------------------------------------------------------------- parameters
    //
    // One small record per verb's parameter object. Explicit rather than positional
    // JSON arrays so a verb can grow a field without breaking the other side, and so
    // the source-generated serializer has a concrete type per verb.

    public sealed record IntValueDto
    {
        public int Value { get; init; }
    }

    public sealed record BoolValueDto
    {
        public bool Value { get; init; }
    }

    /// <summary>
    /// Request for <c>engine.set_autostart</c>. Deliberately carries the user's
    /// choice and nothing else: the engine resolves the action path from its own
    /// install layout and the account from the interactive session, so a caller
    /// cannot name a program for the service to schedule.
    /// </summary>
    public sealed record AutostartRequestDto
    {
        /// <summary>True creates or updates the entry, false removes it.</summary>
        public bool Enabled { get; init; }

        /// <summary>Logon delay in seconds (0-120, the Settings row's range).</summary>
        public int DelaySeconds { get; init; }
    }

    public sealed record WordAddressDto
    {
        public ushort Address { get; init; }
    }

    public sealed record RegisterRangeDto
    {
        public ushort Address { get; init; }

        public int Count { get; init; }
    }

    public sealed record RegisterWriteDto
    {
        public ushort Address { get; init; }

        public byte Value { get; init; }
    }

    /// <summary>A run of bytes to write at consecutive EC registers (fan-table blocks).</summary>
    public sealed record EcRegisterBlockDto
    {
        public ushort Address { get; init; }

        public byte[] Data { get; init; } = Array.Empty<byte>();
    }

    public sealed record RegisterBlocksDto
    {
        public IReadOnlyList<EcRegisterBlockDto> Blocks { get; init; } = Array.Empty<EcRegisterBlockDto>();
    }

    public sealed record FanCurveDto
    {
        /// <summary>Curve name, for the engine's write-sequence log.</summary>
        public string Name { get; init; } = string.Empty;

        /// <summary>Per-zone duty as a UI percentage (0-100); index 0 is the OFF zone.</summary>
        public IReadOnlyList<int> DutyPercentByZone { get; init; } = Array.Empty<int>();
    }

    public sealed record CurveSourceDto
    {
        public EcFanCurveSource Source { get; init; }
    }

    public sealed record ApplyCurveDto
    {
        public FanCurveDto Cpu { get; init; } = new();

        public FanCurveDto Gpu { get; init; } = new();

        /// <summary>Temperature steps per zone; null uses the OEM 9-point default.</summary>
        public int[]? CpuTemps { get; init; }

        public int[]? GpuTemps { get; init; }
    }

    public sealed record UploadCurveDto
    {
        public EcFanCurveSource Source { get; init; }

        public int[] TempUp { get; init; } = Array.Empty<int>();

        public int[] TempDown { get; init; } = Array.Empty<int>();

        public int[] Duty { get; init; } = Array.Empty<int>();
    }

    public sealed record FanSpeedDto
    {
        public int Percent { get; init; }
    }

    public sealed record FanPwmDto
    {
        public int FanIndex { get; init; }

        public int Duty { get; init; }
    }

    public sealed record FanSwitchSpeedDto
    {
        public int Rpm { get; init; }
    }

    public sealed record SmartFanCurveDto
    {
        public byte[] CpuTempUp { get; init; } = Array.Empty<byte>();

        public byte[] CpuTempDown { get; init; } = Array.Empty<byte>();

        public byte[] CpuDuty { get; init; } = Array.Empty<byte>();

        public byte[] GpuTempUp { get; init; } = Array.Empty<byte>();

        public byte[] GpuTempDown { get; init; } = Array.Empty<byte>();

        public byte[] GpuDuty { get; init; } = Array.Empty<byte>();
    }

    public sealed record SmartFanDutyDto
    {
        public byte[] CpuDuty { get; init; } = Array.Empty<byte>();

        public byte[] GpuDuty { get; init; } = Array.Empty<byte>();
    }

    public sealed record PowerLimitDto
    {
        public PowerLimitKind Kind { get; init; }

        public int Watts { get; init; }
    }

    public sealed record CpuPowerLimitsDto
    {
        public int Pl1Watts { get; init; }

        public int Pl2Watts { get; init; }
    }

    public sealed record GpuPowerControlDto
    {
        public int CtgpOffset { get; init; }

        public int DbOffset { get; init; }
    }

    public sealed record PerformanceModeDto
    {
        public int Mode { get; init; }
    }

    public sealed record StockModeDto
    {
        public int ModeCase { get; init; }
    }

    public sealed record MuxModeDto
    {
        public GpuMuxMode Mode { get; init; }

        /// <summary>
        /// The platform the engine resolved. The NVRAM mirror of the mux mode stores a
        /// raw byte, and only the engine knows the platform that encodes it, so the
        /// read that answers the page also answers the encoding question.
        /// </summary>
        public EcPlatform Platform { get; init; }
    }

    public sealed record KeyboardColorDto
    {
        public byte Red { get; init; }

        public byte Green { get; init; }

        public byte Blue { get; init; }
    }

    public sealed record KeyboardBrightnessDto
    {
        public int Brightness { get; init; }
    }

    public sealed record BatteryModeDto
    {
        public int Mode { get; init; }
    }

    /// <summary>
    /// The charge-threshold pair the current charging mode implies (U-103). A
    /// READ-ONLY view: the lab-box probe (2026-10-30, docs\acpi\BatteryDiagnostic)
    /// refuted the open-revo chassis' explicit threshold registers on this family
    /// (0x07C9/0x07D0 do not read as a percent pair), so the thresholds ARE the
    /// mode's capacity ceiling plus hysteresis, derived in the codec. There is
    /// no threshold write verb, by that evidence.
    /// </summary>
    public sealed record BatteryThresholdsDto
    {
        /// <summary>Charge-up threshold (the mode's capacity ceiling), in percent.</summary>
        public int UpPercent { get; init; }

        /// <summary>Re-charge restart point, in percent (below the up threshold).</summary>
        public int DownPercent { get; init; }
    }

    /// <summary>Device enable/disable parameter for the <c>device.*</c> verbs.</summary>
    public sealed record DeviceToggleDto
    {
        public bool Enabled { get; init; }
    }

    /// <summary>
    /// One UEFI firmware environment variable. As a verb parameter it addresses
    /// the variable (Name + Guid; Data unset) for a read, and carries the whole
    /// buffer for a write. As a result it answers a read with the buffer the
    /// firmware reported, or null data when the variable does not exist.
    /// Bytes ride base64 by System.Text.Json convention, which keeps the wire
    /// text-safe for a 4 KB buffer.
    /// </summary>
    public sealed record NvramValueDto
    {
        public string Name { get; init; } = string.Empty;

        public string Guid { get; init; } = string.Empty;

        public byte[]? Data { get; init; }
    }

    /// <summary>
    /// One AMD SMU mailbox command: the preset's command name (the vocabulary
    /// the per-socket address tables key on: "set-coper", "ppt-limit", ...)
    /// and the packed argument value. The value is uint because the packed
    /// forms (Curve Optimizer signs in the high bits: 0xE0000004) exceed the
    /// int range; the verb answers with the SMU's status code (0x01 = OK).
    /// </summary>
    public sealed record SmuCommandDto
    {
        public string Name { get; init; } = string.Empty;

        public uint Value { get; init; }
    }

    /// <summary>The TDP MSR pair: PL1 (low dword) and PL2 (high dword), watts.</summary>
    public sealed record IntelTdpDto
    {
        public int Pl1 { get; init; }

        public int Pl2 { get; init; }
    }

    /// <summary>An int value addressed to a hardware plane: the Intel power
    /// balance (plane 0 cpu / 1 gpu) and the Intel voltage offset (plane 0 cpu
    /// / 1 gpu / 2 cache / 3 sa). The plane is an int because that is the
    /// preset's vocabulary; the engine's ported stack validates it (an unknown
    /// plane is the stack's silent no-op, answered true).</summary>
    public sealed record PlaneValueDto
    {
        public int Value { get; init; }

        public int Plane { get; init; }
    }

    // ---------------------------------------------------------------- results

    public sealed record FanStatusDto
    {
        public int CpuTemperature { get; init; }

        public int GpuTemperature { get; init; }

        public int MainFanRpm { get; init; }

        public int SecondFanRpm { get; init; }

        public int CpuFanDuty { get; init; }

        public int GpuFanDuty { get; init; }

        public bool TurboActive { get; init; }

        public bool CustomProfileActive { get; init; }

        public bool FanControlSupported { get; init; }

        public int PerformanceMode { get; init; }
    }

    public sealed record FanCurveTablesDto
    {
        public int[] TempUp { get; init; } = Array.Empty<int>();

        public int[] TempDown { get; init; } = Array.Empty<int>();

        public int[] Duty { get; init; } = Array.Empty<int>();
    }

    public sealed record FanDutySnapshotDto
    {
        public int CpuFanDuty { get; init; }

        public int GpuFanDuty { get; init; }
    }

    /// <summary>
    /// The kernel-side sensor readings the LocalSystem engine can take and an
    /// asInvoker app cannot: CPU package power and average core clock (Ring0 MSRs
    /// via PawnIO) and the DIMM SPD data rate (SMBus). 0 means the engine's probe
    /// has no reading for that field - the UI shows "--", per the snapshot's
    /// zero-is-unavailable convention. ProbeOpen distinguishes "the sensor stack
    /// is not up yet" from "the stack is up and this field is silent".
    /// </summary>
    public sealed record MetricsSnapshotDto
    {
        /// <summary>CPU package power in watts.</summary>
        public int CpuPowerWatts { get; init; }

        /// <summary>Average CPU core clock in MHz.</summary>
        public int CpuClockMhz { get; init; }

        /// <summary>Memory module data rate in MT/s (SPD).</summary>
        public int MemorySpeedMts { get; init; }

        /// <summary>DIMM 0 temperature in Celsius (SPD thermal sensor).</summary>
        public int MemoryTemperature0 { get; init; }

        /// <summary>DIMM 1 temperature in Celsius (SPD thermal sensor).</summary>
        public int MemoryTemperature1 { get; init; }

        /// <summary>The engine's sensor probe is open and updating.</summary>
        public bool ProbeOpen { get; init; }

        /// <summary>
        /// Seconds between the probe publishing this reading and the verb serving
        /// it. The probe loop is deliberately outside the watchdog (a wedged
        /// sensor open must not FailFast an engine whose EC queues are healthy),
        /// so freshness is carried with the data: a wedged loop keeps publishing
        /// nothing, and the age tells the app to render "--" instead of trusting
        /// frozen numbers. App-side budget: readings older than ~6 cycles are no
        /// readings.
        /// </summary>
        public int ReadingAgeSeconds { get; init; }
    }

    public sealed record PowerLimitsDto
    {
        public int Pl1 { get; init; }

        public int Pl2 { get; init; }

        public int Pl4 { get; init; }

        public int Pl5 { get; init; }

        public int TccOffset { get; init; }
    }

    /// <summary>
    /// The EC's answer to "what power contract is enforced right now": the GPU
    /// cTGP/DB register bytes (0x0743/0x0744/0x0745/0x0746, 0x0727 bit6) and
    /// the CPU PL1/PL2 wattage. This is the drift oracle for the power-tuning
    /// guardian: laptop NVML answers NOT_SUPPORTED for power limits (the app
    /// substitutes the default), so the firmware's own registers are the only
    /// truth about what the EC enforces.
    /// </summary>
    public sealed record PowerContractTruthDto
    {
        /// <summary>0x0743 raw: GENERAL(0x01) / DB(0x02) / CTGP(0x04) enable bits.</summary>
        public int GpuEnable { get; init; }

        /// <summary>0x0744 raw cTGP offset byte.</summary>
        public int GpuCtgp { get; init; }

        /// <summary>0x0745 raw total-power-pool byte.</summary>
        public int GpuTpp { get; init; }

        /// <summary>0x0746 raw Dynamic Boost offset byte.</summary>
        public int GpuDb { get; init; }

        /// <summary>0x0727 bit6: a custom preset owns the EC power/profile values.</summary>
        public bool GpuCustomProfileActive { get; init; }

        /// <summary>CPU PL1 in watts (EC 0x0783 readback).</summary>
        public int Pl1 { get; init; }

        /// <summary>CPU PL2 in watts (EC 0x0784 readback).</summary>
        public int Pl2 { get; init; }
    }

    /// <summary>
    /// One serialized re-apply of a drifted half of the power contract: the
    /// GPU cTGP/DB activation sequence and/or the CPU PL pair, each behind
    /// its own Apply* flag. The engine is policy-illiterate here — it replays
    /// the same activation methods the preset pipeline uses; deciding WHAT
    /// to heal is the app's Core policy's job.
    /// </summary>
    public sealed record HealContractDto
    {
        public bool ApplyGpu { get; init; }

        public int GpuCtgpOffset { get; init; }

        public int GpuDbOffset { get; init; }

        public bool ApplyCpu { get; init; }

        public int Pl1Watts { get; init; }

        public int Pl2Watts { get; init; }
    }

    public sealed record MuxStateDto
    {
        /// <summary>The semantic mode the EC reports for this platform.</summary>
        public GpuMuxMode Mode { get; init; }

        /// <summary>The platform the engine resolved; callers encode raw bytes with it.</summary>
        public EcPlatform Platform { get; init; }

        public ushort HardwareState { get; init; }

        public byte ModuleId { get; init; }
    }

    public sealed record BatteryTelemetryDto
    {
        public bool IsAcPowered { get; init; }

        public int StateOfCharge { get; init; }

        public int HealthPercent { get; init; }

        public int TemperatureC { get; init; }

        public int CycleCount { get; init; }

        public int PresentVoltageMv { get; init; }

        public int ChargeRateMa { get; init; }
    }

    public sealed record RegisterValueDto
    {
        public ushort Address { get; init; }

        public byte Value { get; init; }
    }

    public sealed record RegisterBytesDto
    {
        public ushort Address { get; init; }

        public byte[] Data { get; init; } = Array.Empty<byte>();
    }

    /// <summary>
    /// The PnP state of one device row (webcam, dGPU), mirroring what
    /// Get-PnpDevice reported when these rows shelled out to PowerShell:
    /// Status is the PnP status string ("OK" = running and enabled, "Disable",
    /// "Error", "Unknown", ...; "Unknown Device" for an invalid device id), and
    /// Present distinguishes "the row's device is not in the system" (the UI
    /// shows nothing to toggle) from "present and disabled" (the toggle's off
    /// state). The status string travels for the logs: the field's question is
    /// always which status the device ended in.
    /// </summary>
    public sealed record PnpDeviceStateDto
    {
        public string InstanceId { get; init; } = string.Empty;

        public string Status { get; init; } = string.Empty;

        public bool Present { get; init; }
    }

    /// <summary>What the engine is, so the app can show a version-skew error clearly.</summary>
    public sealed record EngineInfoDto
    {
        public int ProtocolVersion { get; init; }

        public string EngineVersion { get; init; } = string.Empty;

        public EcPlatform Platform { get; init; }

        public bool EcAvailable { get; init; }

        public int ProcessId { get; init; }
    }

    /// <summary>
    /// The machine-scope OEM takeover inventory (story U-203; see
    /// <see cref="EngineVerbs.Oem"/>). The counts are what the caller shows;
    /// <see cref="State"/> is the engine's own machine-scope reading of them
    /// (the name of a Core TakeoverPolicy.State). The user-scope half of the
    /// picture (HKCU run keys, UWP AppModel state, StartupApproved flags) is
    /// app-side user-registry work the engine must not write blind, so the
    /// app composition merges these counts with its own before deciding the
    /// state the UI renders.
    /// </summary>
    public sealed record OemStatusDto
    {
        /// <summary>A Core TakeoverPolicy.State name: NotInstalled, Active,
        /// Paused, PartiallyPaused, Stale.</summary>
        public string State { get; init; } = "NotInstalled";

        /// <summary>How many catalog services exist on the machine.</summary>
        public int ServicesInstalled { get; init; }

        /// <summary>How many catalog services are currently running.</summary>
        public int ServicesRunning { get; init; }

        /// <summary>Verified OEM processes currently alive.</summary>
        public int OemProcessesRunning { get; init; }

        /// <summary>Engine journal depth (machine-scope mutations recorded).</summary>
        public int JournalCount { get; init; }

        /// <summary>Machine-scope autostart entries currently neutralized
        /// (run keys in backup form, catalog tasks disabled).</summary>
        public int AutostartNeutralized { get; init; }

        /// <summary>Targets that carry a catalog name / OEM keyword but
        /// failed the path+signature+publisher gate. Audit text ("what an
        /// attacker named like the OEM stack"); never touched, and the list
        /// is the reason a pause reports fewer mutations than the catalog.</summary>
        public List<string> SquatSuspects { get; init; } = new();

        /// <summary>Currently neutralized machine-scope targets (backup run
        /// keys, disabled tasks, services held at demand-start).</summary>
        public List<string> NeutralizedTargets { get; init; } = new();
    }

    /// <summary>
    /// The result of oem.pause / oem.resume: the state the machine is in
    /// after the action, how much landed, and an audit line per mutation
    /// (what the journal view shows). A machine without the stack answers
    /// zero mutations - the reference's auto-bypass, not an error.
    /// </summary>
    public sealed record OemActionDto
    {
        /// <summary>The post-action machine-scope state (same names as
        /// <see cref="OemStatusDto.State"/>).</summary>
        public string State { get; init; } = "NotInstalled";

        /// <summary>Journal mutations applied (a kill counts as recorded
        /// audit, not a reversible mutation).</summary>
        public int MutationsApplied { get; init; }

        /// <summary>Gate refusals during the action (squat-suspects).</summary>
        public int SquatSuspects { get; init; }

        /// <summary>One human-readable line per mutation, journal order.</summary>
        public List<string> Actions { get; init; } = new();
    }
}
