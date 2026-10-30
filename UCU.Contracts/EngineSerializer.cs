using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace UCU.Contracts
{
    /// <summary>
    /// Source-generated JSON metadata for the whole contract. Reflection-free so the
    /// engine service can be published trimmed, and so a type that is not in the list
    /// fails at build time rather than at run time on a customer machine.
    /// </summary>
    [JsonSourceGenerationOptions(
        PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false)]
    [JsonSerializable(typeof(EngineRequest))]
    [JsonSerializable(typeof(EngineResponse))]
    [JsonSerializable(typeof(JsonElement))]
    [JsonSerializable(typeof(int))]
    [JsonSerializable(typeof(bool))]
    [JsonSerializable(typeof(int[]))]
    [JsonSerializable(typeof(byte[]))]
    [JsonSerializable(typeof(List<int>))]
    [JsonSerializable(typeof(List<EcRegisterBlockDto>))]
    [JsonSerializable(typeof(IntValueDto))]
    [JsonSerializable(typeof(BoolValueDto))]
    [JsonSerializable(typeof(WordAddressDto))]
    [JsonSerializable(typeof(RegisterRangeDto))]
    [JsonSerializable(typeof(RegisterWriteDto))]
    [JsonSerializable(typeof(EcRegisterBlockDto))]
    [JsonSerializable(typeof(RegisterBlocksDto))]
    [JsonSerializable(typeof(FanCurveDto))]
    [JsonSerializable(typeof(CurveSourceDto))]
    [JsonSerializable(typeof(ApplyCurveDto))]
    [JsonSerializable(typeof(UploadCurveDto))]
    [JsonSerializable(typeof(FanSpeedDto))]
    [JsonSerializable(typeof(FanPwmDto))]
    [JsonSerializable(typeof(FanSwitchSpeedDto))]
    [JsonSerializable(typeof(SmartFanCurveDto))]
    [JsonSerializable(typeof(SmartFanDutyDto))]
    [JsonSerializable(typeof(PowerLimitDto))]
    [JsonSerializable(typeof(CpuPowerLimitsDto))]
    [JsonSerializable(typeof(GpuPowerControlDto))]
    [JsonSerializable(typeof(PerformanceModeDto))]
    [JsonSerializable(typeof(StockModeDto))]
    [JsonSerializable(typeof(MuxModeDto))]
    [JsonSerializable(typeof(KeyboardColorDto))]
    [JsonSerializable(typeof(KeyboardBrightnessDto))]
    [JsonSerializable(typeof(BatteryModeDto))]
    [JsonSerializable(typeof(DeviceToggleDto))]
    [JsonSerializable(typeof(NvramValueDto))]
    [JsonSerializable(typeof(string))]
    [JsonSerializable(typeof(FanStatusDto))]
    [JsonSerializable(typeof(FanCurveTablesDto))]
    [JsonSerializable(typeof(FanDutySnapshotDto))]
    [JsonSerializable(typeof(MetricsSnapshotDto))]
    [JsonSerializable(typeof(PowerLimitsDto))]
    [JsonSerializable(typeof(MuxStateDto))]
    [JsonSerializable(typeof(BatteryTelemetryDto))]
    [JsonSerializable(typeof(PnpDeviceStateDto))]
    [JsonSerializable(typeof(RegisterValueDto))]
    [JsonSerializable(typeof(RegisterBytesDto))]
    [JsonSerializable(typeof(EngineInfoDto))]
    public partial class EngineJsonContext : JsonSerializerContext
    {
    }

    /// <summary>
    /// The single set of JSON options for the wire. Anything serialized over the pipe
    /// goes through here, so the resolver cannot silently fall back to reflection.
    /// </summary>
    public static class EngineSerializer
    {
        public static readonly JsonSerializerOptions Options = new()
        {
            TypeInfoResolver = EngineJsonContext.Default,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false,
        };

        /// <summary>Wraps a verb's parameters as the envelope's params element.</summary>
        public static JsonElement ToParams<T>(T value) => JsonSerializer.SerializeToElement(value, TypeInfo<T>());

        /// <summary>
        /// Wraps a dispatcher result, whose static type is <c>object</c>, using its
        /// runtime type's registered metadata. This is the one place the wire picks a
        /// type from a value rather than a signature, so the type list is written out
        /// explicitly: a result DTO that is not in the list fails at the first call
        /// with a naming message, which the verb tests catch, instead of shipping a
        /// reflection fallback that breaks on a trimmed publish. Adding a result DTO
        /// to a verb means adding an arm here; the shape test pins the set.
        /// </summary>
        public static JsonElement ToParamsObject(object value) => value switch
        {
            int v => ToParams(v),
            bool v => ToParams(v),
            string v => ToParams(v),
            byte[] v => ToParams(v),
            FanStatusDto v => ToParams(v),
            FanCurveTablesDto v => ToParams(v),
            FanDutySnapshotDto v => ToParams(v),
            MetricsSnapshotDto v => ToParams(v),
            PowerLimitsDto v => ToParams(v),
            MuxModeDto v => ToParams(v),
            MuxStateDto v => ToParams(v),
            BatteryTelemetryDto v => ToParams(v),
            PnpDeviceStateDto v => ToParams(v),
            NvramValueDto v => ToParams(v),
            RegisterValueDto v => ToParams(v),
            RegisterBytesDto v => ToParams(v),
            EngineInfoDto v => ToParams(v),
            _ => throw new ProtocolViolationException($"result type {value.GetType().Name} is not registered in {nameof(EngineJsonContext)}"),
        };

        /// <summary>
        /// Binds a verb's parameters. A verb with no parameters accepts an absent
        /// element and returns the default instance, which keeps the client terse.
        /// </summary>
        public static T BindParams<T>(JsonElement? element)
            where T : new()
        {
            if (element == null)
                return new T();

            return element.Value.Deserialize(TypeInfo<T>()) ?? new T();
        }

        public static byte[] SerializeToUtf8Bytes<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, TypeInfo<T>());

        public static T Deserialize<T>(ReadOnlySpan<byte> payload) =>
            JsonSerializer.Deserialize(payload, TypeInfo<T>())
                ?? throw new ProtocolViolationException($"{typeof(T).Name} deserialized to null");

        /// <summary>Read a typed value out of an envelope's result/params element.</summary>
        public static T FromElement<T>(JsonElement element) =>
            element.Deserialize(TypeInfo<T>())
                ?? throw new ProtocolViolationException($"{typeof(T).Name} deserialized to null");

        /// <summary>
        /// Metadata resolved from <see cref="EngineJsonContext"/>, never from reflection:
        /// a type missing from the context throws here at the first call, which is the
        /// failure mode that keeps trimming and AOT publishing safe.
        /// </summary>
        private static JsonTypeInfo<T> TypeInfo<T>()
        {
            IJsonTypeInfoResolver resolver = Options.TypeInfoResolver
                ?? throw new InvalidOperationException("the contract serializer has no resolver");

            return (JsonTypeInfo<T>?)resolver.GetTypeInfo(typeof(T), Options)
                ?? throw new InvalidOperationException(
                    $"'{typeof(T).Name}' is not registered in {nameof(EngineJsonContext)}");
        }
    }
}
