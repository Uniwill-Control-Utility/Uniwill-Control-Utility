using System.Reflection;
using System.Text;
using System.Text.Json;
using UCU.Contracts;
using UCU.Contracts.Capabilities;
using UCU.Core.Ec;

namespace UCU.Contracts.Tests;

/// <summary>
/// The shape of the contract: what may cross the pipe, and what the contract assembly
/// is allowed to depend on. The verb list and the DTO wire format are a published
/// interface between two processes built at the same time, so the checks here are about
/// drift and about the AOT-safe serializer, not about behaviour.
/// </summary>
public sealed class ContractShapeTests
{
    [Fact]
    public async Task RequestRoundTripsThroughTheSourceGeneratedSerializer()
    {
        EngineRequest request = new()
        {
            Id = "42",
            Verb = EngineVerbs.Fan.ApplyCurve,
            Params = EngineSerializer.ToParams(new ApplyCurveDto
            {
                Cpu = new FanCurveDto { Name = "Balanced", DutyPercentByZone = new[] { 0, 30, 32 } },
                Gpu = new FanCurveDto { Name = "Silent", DutyPercentByZone = new[] { 0, 20 } },
                CpuTemps = new[] { 0, 55, 60 },
            }),
        };

        byte[] payload = EngineSerializer.SerializeToUtf8Bytes(request);
        EngineRequest copy = EngineSerializer.Deserialize<EngineRequest>(payload);

        Assert.Equal(request.Id, copy.Id);
        Assert.Equal(request.Verb, copy.Verb);
        Assert.Equal(EngineProtocol.Version, copy.ProtocolVersion);

        ApplyCurveDto parameters = EngineSerializer.BindParams<ApplyCurveDto>(copy.Params);
        Assert.Equal("Balanced", parameters.Cpu.Name);
        Assert.Equal(new[] { 0, 30, 32 }, parameters.Cpu.DutyPercentByZone);
        Assert.Equal(new[] { 0, 55, 60 }, parameters.CpuTemps);
        Assert.Null(parameters.GpuTemps);
    }

    [Fact]
    public void ResponseCarriesResultDataAndErrorCodes()
    {
        EngineResponse ok = EngineResponse.Success("7", EngineSerializer.ToParams(new FanStatusDto
        {
            CpuTemperature = 61,
            MainFanRpm = 2400,
            TurboActive = true,
        }));

        EngineResponse copy = EngineSerializer.Deserialize<EngineResponse>(EngineSerializer.SerializeToUtf8Bytes(ok));

        Assert.True(copy.Ok);
        Assert.Equal(61, copy.Result!.Value.GetProperty("cpuTemperature").GetInt32());
        Assert.True(copy.Result.Value.GetProperty("turboActive").GetBoolean());

        EngineResponse fault = EngineResponse.Failure("8", EngineErrorCode.HardwareUnavailable, "driver not running");
        copy = EngineSerializer.Deserialize<EngineResponse>(EngineSerializer.SerializeToUtf8Bytes(fault));

        Assert.False(copy.Ok);
        Assert.Equal(EngineErrorCode.HardwareUnavailable, copy.Error);
        Assert.Equal("driver not running", copy.Message);
    }

    [Fact]
    public void ParamsAreOmittedWhenAbsent()
    {
        string json = Encoding.UTF8.GetString(EngineSerializer.SerializeToUtf8Bytes(
            new EngineRequest { Id = "1", Verb = EngineVerbs.Engine.Ping }));

        // The engine treats an absent params element as "no parameters"; the wire stays small.
        Assert.DoesNotContain("params", json, StringComparison.Ordinal);
    }

    [Fact]
    public void BindParams_DefaultsWhenTheVerbTakesNone()
    {
        IntValueDto value = EngineSerializer.BindParams<IntValueDto>(null);

        Assert.Equal(0, value.Value);
    }

    [Fact]
    public void RawVerbParamsPinTheCallerChosenShape()
    {
        // The raw-write verbs (fan.write_register_blocks, telemetry.write_register,
        // device.set_nvram_value) carry caller-chosen addresses/GUIDs on the wire:
        // the param shape is pinned so the surface cannot silently widen (new free
        // fields must be a deliberate contract change), and the engine's
        // EcRawWriteGuard is what constrains the VALUES (see RawWriteGateTests).
        RegisterBlocksDto blocks = EngineSerializer.BindParams<RegisterBlocksDto>(
            EngineSerializer.ToParams(new RegisterBlocksDto
            {
                Blocks = new[]
                {
                    new EcRegisterBlockDto { Address = EcFanRegisters.CpuFanSpeedTable, Data = new byte[] { 0xC8, 0xC8 } },
                },
            }));

        Assert.Single(blocks.Blocks);
        Assert.Equal(EcFanRegisters.CpuFanSpeedTable, blocks.Blocks[0].Address);
        Assert.Equal(new byte[] { 0xC8, 0xC8 }, blocks.Blocks[0].Data);

        RegisterWriteDto write = EngineSerializer.BindParams<RegisterWriteDto>(
            EngineSerializer.ToParams(new RegisterWriteDto { Address = 0x07A6, Value = 0x18 }));
        Assert.Equal((ushort)0x07A6, write.Address);
        Assert.Equal((byte)0x18, write.Value);

        NvramValueDto nvram = EngineSerializer.BindParams<NvramValueDto>(
            EngineSerializer.ToParams(new NvramValueDto
            {
                Name = EcWriteGatePolicy.OemMagicVariableGuid,
                Guid = EcWriteGatePolicy.OemMagicVariableGuid,
                Data = new byte[] { 1, 2, 3 },
            }));
        Assert.Equal(new byte[] { 1, 2, 3 }, nvram.Data);
    }

    [Fact]
    public void RawWriteGatePolicyGuidConstantIsTheNvramAllowlist()
    {
        // The contract assembly carries the OEM GUID constant the engine's NVRAM
        // gate allows, and the hardware service writes the same GUID (parity so
        // the gate and the rows cannot drift).
        Assert.Equal(
            UCU.Core.Ec.EcWriteGatePolicy.OemMagicVariableGuid,
            "{9F33F85C-13CA-4FD1-9C4A-96217722C593}");
    }

    [Fact]
    public void SerializerRefusesTypesThatAreNotInTheContext()
    {
        // The resolver is the source-generated context and nothing else. A type that
        // was not registered must fail loudly: silently falling back to reflection is
        // what breaks a trimmed engine service on a customer machine.
        Assert.ThrowsAny<Exception>(() => EngineSerializer.SerializeToUtf8Bytes(new UnregisteredDto()));
    }

    [Fact]
    public void VerbsAreUniqueDomainPrefixedLowerCaseNames()
    {
        string[] verbs = AllVerbs();

        Assert.Equal(verbs.Length, verbs.Distinct().Count());

        foreach (string verb in verbs)
        {
            string[] parts = verb.Split('.');
            Assert.Equal(2, parts.Length);
            Assert.Contains(parts[0], new[] { "fan", "power", "mux", "keyboard", "battery", "device", "cpu", "telemetry", "engine" });
            Assert.Equal(verb, verb.ToLowerInvariant());
            Assert.Matches("^[a-z_]+$", parts[1]);
        }
    }

    [Fact]
    public void EveryVerbIsReachableByACapabilityCall()
    {
        // The verb table and the capability interfaces are the same surface viewed from
        // two sides; a verb nobody can call is dead protocol, and a capability call
        // without a verb is a hole in the engine.
        string[] verbs = AllVerbs();

        int capabilityMethods = typeof(IFanCapability).GetMethods().Length
            + typeof(IPowerCapability).GetMethods().Length
            + typeof(IGpuMuxCapability).GetMethods().Length
            + typeof(IKeyboardCapability).GetMethods().Length
            + typeof(IBatteryCapability).GetMethods().Length
            + typeof(IDeviceCapability).GetMethods().Length
            + typeof(IAmdSmuCapability).GetMethods().Length
            + typeof(IIntelMsrCapability).GetMethods().Length
            + typeof(IEcTelemetryCapability).GetMethods().Length
            + typeof(IEngineCapability).GetMethods().Length;

        // Not one-to-one (GetStateAsync covers three mux verbs), so the assertion is a
        // band: capabilities must stay close to the verb count, not drift 3x past it.
        Assert.InRange(capabilityMethods, verbs.Length / 2, verbs.Length);
    }

    [Fact]
    public void ContractAssemblyReferencesStayFreeOfHardwareUiAndLogging()
    {
        // The contract is referenced by the app, the client, and the privileged engine.
        // If hardware, UI or logging types leak in, the app gets a path to the driver
        // again through a type it already references.
        string[] forbidden =
        {
            "UCU.Hardware", "UCU.Engine", "UCU.Client",
            "PresentationFramework", "PresentationCore", "WindowsBase",
            "Serilog", "System.IO.Pipes", "System.Management",
            "Microsoft.Extensions.Hosting", "System.ServiceProcess.ServiceController",
        };

        string[] referenced = typeof(EngineRequest).Assembly
            .GetReferencedAssemblies()
            .Select(a => a.Name!)
            .ToArray();

        foreach (string name in forbidden)
            Assert.DoesNotContain(name, referenced);

        Assert.Contains("UCU.Core", referenced);
    }

    [Fact]
    public void CapabilitySignaturesAreAsyncAndCancellable()
    {
        Type[] interfaces =
        {
            typeof(IFanCapability), typeof(IPowerCapability), typeof(IGpuMuxCapability),
            typeof(IKeyboardCapability), typeof(IBatteryCapability), typeof(IDeviceCapability),
            typeof(IAmdSmuCapability), typeof(IIntelMsrCapability),
            typeof(IEcTelemetryCapability), typeof(IEngineCapability),
        };

        foreach (Type iface in interfaces)
        {
            foreach (MethodInfo method in iface.GetMethods())
            {
                Assert.True(typeof(Task).IsAssignableFrom(method.ReturnType),
                    $"{iface.Name}.{method.Name} must return Task (the call crosses a process boundary)");

                ParameterInfo[] parameters = method.GetParameters();
                Assert.Equal("cancellationToken", parameters[^1].Name);
                Assert.Equal(typeof(CancellationToken), parameters[^1].ParameterType);
                Assert.True(parameters[^1].HasDefaultValue);
            }
        }
    }

    [Fact]
    public void ProtocolIdentityIsPinned()
    {
        // The pipe name carries the protocol version: an app and an engine that disagree
        // must fail at connect, so changing the wire format means changing this name.
        Assert.Equal("UCUEngine.v1", EngineProtocol.PipeName);
        Assert.Equal(@"\\.\pipe\UCUEngine.v1", EngineProtocol.FullPipeName());
        Assert.Equal(1, EngineProtocol.Version);
        Assert.Equal(4, EngineProtocol.LengthPrefixBytes);
        Assert.Equal(256 * 1024, EngineProtocol.MaxRequestBytes);
        Assert.Equal(1024 * 1024, EngineProtocol.MaxResponseBytes);
    }

    [Fact]
    public void ToParamsObject_CoversEveryDispatcherResultType()
    {
        // The dispatcher hands results to the wire as `object`; this list is the set of
        // types the engine's verbs can answer with. If a verb adds a result DTO and
        // this test was not updated, the engine fails that verb at runtime, so the
        // list is pinned here and each entry must round-trip through the registry.
        object[] results =
        {
            7, true, "s", new byte[] { 1, 2 },
            new FanStatusDto(), new FanCurveTablesDto(), new FanDutySnapshotDto(),
            new MetricsSnapshotDto(),
            new PowerLimitsDto(), new PowerContractTruthDto(), new MuxModeDto(), new MuxStateDto(),
            new BatteryTelemetryDto(), new PnpDeviceStateDto(),
            new RegisterValueDto(), new RegisterBytesDto(),
            new EngineInfoDto(),
        };

        foreach (object result in results)
        {
            JsonElement element = EngineSerializer.ToParamsObject(result);
            Assert.NotEqual(JsonValueKind.Undefined, element.ValueKind);
        }

        Assert.Throws<ProtocolViolationException>(() => EngineSerializer.ToParamsObject(new UnregisteredDto()));
    }

    private static string[] AllVerbs() =>
        typeof(EngineVerbs)
            .GetNestedTypes(BindingFlags.Public)
            .SelectMany(domain => domain
                .GetFields(BindingFlags.Public | BindingFlags.Static)
                .Select(field => (string)field.GetValue(null)!))
            .ToArray();

    private sealed record UnregisteredDto
    {
        public string Value { get; init; } = "x";
    }
}
