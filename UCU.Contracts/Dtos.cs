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
}
