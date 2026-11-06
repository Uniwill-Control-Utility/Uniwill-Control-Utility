using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using EcRegistryGen;
using UCU.Core.Ec.Generated;

namespace UCU.Core.Tests.Ec;

/// <summary>
/// The EC registry is the single source of truth for EC wiring (U-109). These tests are
/// what makes the arrangement safe rather than decorative:
///
///  * the committed artifact is byte-exactly what the generator derives from the registry
///    (so a stale artifact, an edited artifact, or a hand-written EC number fails),
///  * the generator is deterministic (no clocks, no culture-sensitive formatting),
///  * every constant keeps the exact value the product used before the registry existed
///    (the table below is transcribed from the pre-registry sources; the refactor is
///    behaviour-preserving by construction, and this is the pin that proves it),
///  * and the generator refuses malformed registries, including entries with no evidence:
///    an EC number whose provenance is unknown is not allowed into the vault.
/// </summary>
public sealed class EcVaultRegistryTests
{
    private const string RegistryRelative = @"UCU.Core\Ec\Registry\EcAddresses.json";
    private const string ArtifactRelative = @"UCU.Core\Ec\Generated\EcAddresses.g.cs";

    /// <summary>
    /// Every vault constant and the value it carried in the shipped product before the
    /// registry existed. Source of each row: the HEAD~ file that declared it (address consts
    /// in UCU.Hardware\Ec\EcEngine.cs, encoding consts in the UCU.Core\Ec codecs, the
    /// raw-write allowlist in EcWriteGatePolicy, the IOCTL pair in EcDriverTransport), and
    /// for names the registry introduced, the HEAD line whose inline literal carried it.
    /// </summary>
    private static readonly Dictionary<string, long> PreRegistryValues = new()
    {
                ["AC_RECOVERY_BIT"] = 0x08, // HEAD UCU.Core\Ec\EcSwitchCodec.cs
                ["AP_OEM_FIRMWARE_BOOT_BIT"] = 0x04, // HEAD UCU.Hardware\Ec\EcEngine.cs
                ["BATTERY_GATE_ACTIVE_BIT"] = 0x10, // HEAD UCU.Core\Ec\EcBatteryCodec.cs
                ["BATTERY_MODE_MASK"] = 0x38, // HEAD UCU.Core\Ec\EcBatteryCodec.cs
                ["BATTERY_PLATFORM_OVERRIDE_PINNED"] = 0x05, // HEAD UCU.Core\Ec\EcBatteryCodec.cs
                ["BATTERY_PLATFORM_OVERRIDE_RESET"] = 0xFF, // HEAD UCU.Core\Ec\EcBatteryCodec.cs
                ["CTGP_DB_ENABLE_CTGP"] = 0x04, // HEAD UCU.Hardware\Ec\EcEngine.cs
                ["CTGP_DB_ENABLE_DB"] = 0x02, // HEAD UCU.Hardware\Ec\EcEngine.cs
                ["CTGP_DB_ENABLE_GENERAL"] = 0x01, // HEAD UCU.Hardware\Ec\EcEngine.cs
                ["CUSTOM_PROFILE_ENABLE"] = 0x40, // HEAD UCU.Core\Ec\EcPowerCodec.cs
                ["EC_DUTY_MAX"] = 200, // HEAD UCU.Core\Ec\EcDutyCodec.cs
                ["EC_DUTY_PER_PERCENT"] = 2, // HEAD UCU.Core\Ec\EcDutyCodec.cs
                ["EC_TABLE_LENGTH"] = 16, // HEAD UCU.Core\Ec\EcDutyCodec.cs
                ["ENABLE_CHINA_MODE"] = 0x40, // HEAD UCU.Hardware\Ec\EcEngine.cs
                ["ENABLE_UNIVERSAL_FAN_CTRL"] = 0x04, // HEAD UCU.Hardware\Ec\EcEngine.cs
                ["FAN_LEVEL_MASK"] = 0x07, // HEAD UCU.Hardware\Ec\EcEngine.cs
                ["FAN_MODE_BOOST"] = 0x40, // HEAD UCU.Hardware\Ec\EcEngine.cs
                ["FAN_MODE_HIGH"] = 0x20, // HEAD UCU.Hardware\Ec\EcEngine.cs
                ["FAN_MODE_TURBO"] = 0x10, // HEAD UCU.Core\Ec\EcPowerCodec.cs
                ["FAN_MODE_USER"] = 0x80, // HEAD UCU.Hardware\Ec\EcEngine.cs
                ["FAN_PROFILE_BALANCE"] = 0x00, // HEAD UCU.Core\Ec\EcPowerCodec.cs
                ["FAN_PROFILE_BEAST"] = 0x10, // HEAD UCU.Hardware\Ec\EcEngine.cs
                ["FAN_PROFILE_OFFICE"] = 0xA0, // HEAD UCU.Core\Ec\EcPowerCodec.cs
                ["FAN_SUPPORT"] = 0x20, // HEAD UCU.Hardware\Ec\EcEngine.cs
                ["FAN_TURBO_SUPPORTED"] = 0x10, // HEAD UCU.Hardware\Ec\EcEngine.cs
                ["FAN_V2_NEW"] = 0x01, // HEAD UCU.Hardware\Ec\EcEngine.cs
                ["FAN_V3"] = 0x08, // HEAD UCU.Hardware\Ec\EcEngine.cs
                ["FN_LOCK_BIT"] = 0x10, // HEAD UCU.Core\Ec\EcSwitchCodec.cs
                ["IOCTL_ECREAD"] = 0x9C40A488, // HEAD UCU.Hardware\Ec\EcDriverTransport.cs
                ["IOCTL_ECWRITE"] = 0x9C40A48C, // HEAD UCU.Hardware\Ec\EcDriverTransport.cs
                ["KBD_APPLY"] = 0x10, // HEAD UCU.Core\Ec\EcKeyboardCodec.cs
                ["KBD_BRIGHTNESS_MASK"] = 0xE0, // HEAD UCU.Core\Ec\EcKeyboardCodec.cs
                ["KBD_POWER_OFF"] = 0x02, // HEAD UCU.Core\Ec\EcKeyboardCodec.cs
                ["KBD_WHITE_ONLY"] = 0x01, // HEAD UCU.Core\Ec\EcKeyboardCodec.cs
                ["MANUAL_MODE_ENABLE"] = 0x01, // HEAD UCU.Hardware\Ec\EcEngine.cs
                ["MODE_INDEX_ARM"] = 0x10, // HEAD UCU.Core\Ec\EcPowerCodec.cs
                ["MODE_INDEX_HIGH_BITS"] = 0xE0, // HEAD UCU.Core\Ec\EcPowerCodec.cs
                ["MODE_INDEX_LOW_BIT"] = 0x01, // HEAD UCU.Core\Ec\EcPowerCodec.cs
                ["MODE_INDEX_MASK"] = 0x0F, // HEAD UCU.Core\Ec\EcPowerCodec.cs
                ["MUX_MODE_DGPU_ONLY"] = 0x01, // HEAD UCU.Core\Ec\EcMuxCodec.cs
                ["MUX_MODE_HYBRID"] = 0x00, // HEAD UCU.Core\Ec\EcMuxCodec.cs
                ["MUX_MODE_HYBRID_INTEL"] = 0x04, // HEAD UCU.Core\Ec\EcMuxCodec.cs
                ["MUX_MODE_IGPU_ONLY"] = 0x02, // HEAD UCU.Core\Ec\EcMuxCodec.cs
                ["OEM_MODE_COMMIT"] = 0x83, // HEAD UCU.Core\Ec\EcPowerCodec.cs
                ["POWER_MGMT_726_ENABLE"] = 0x80, // HEAD UCU.Core\Ec\EcPowerCodec.cs
                ["REG_AP_OEM"] = 0x0741, // HEAD UCU.Hardware\Ec\EcEngine.cs
                ["REG_AP_OEM_6"] = 0x07C6, // HEAD UCU.Hardware\Ec\EcEngine.cs
                ["REG_BATTERY_CHARGE_RATE_LO"] = 0x0434, // HEAD UCU.Hardware\Ec\EcEngine.cs
                ["REG_BATTERY_CYCLE_COUNT_LO"] = 0x04A6, // HEAD UCU.Hardware\Ec\EcEngine.cs
                ["REG_BATTERY_DESIGN_CAP_LO"] = 0x0402, // HEAD UCU.Hardware\Ec\EcEngine.cs
                ["REG_BATTERY_DESIGN_VOLTAGE_LO"] = 0x0408, // HEAD UCU.Hardware\Ec\EcEngine.cs
                ["REG_BATTERY_FULL_CHARGE_CAP_LO"] = 0x0404, // HEAD UCU.Hardware\Ec\EcEngine.cs
                ["REG_BATTERY_GATE_STATUS"] = 0x0742, // HEAD UCU.Hardware\Ec\EcEngine.cs
                ["REG_BATTERY_LIMIT_DOWN"] = 0x07D0, // HEAD UCU.Hardware\Ec\EcEngine.cs
                ["REG_BATTERY_LIMIT_UP"] = 0x07C9, // HEAD UCU.Hardware\Ec\EcEngine.cs
                ["REG_BATTERY_MODE"] = 0x07A6, // HEAD UCU.Hardware\Ec\EcEngine.cs
                ["REG_BATTERY_PLATFORM_OVERRIDE"] = 0x0770, // HEAD UCU.Hardware\Ec\EcEngine.cs
                ["REG_BATTERY_POWER_SOURCE"] = 0x0490, // HEAD UCU.Hardware\Ec\EcEngine.cs
                ["REG_BATTERY_PRESENT_VOLTAGE_LO"] = 0x0438, // HEAD UCU.Hardware\Ec\EcEngine.cs
                ["REG_BATTERY_REMAINING_CAP_LO"] = 0x0436, // HEAD UCU.Hardware\Ec\EcEngine.cs
                ["REG_BATTERY_SOC"] = 0x04AB, // HEAD UCU.Hardware\Ec\EcEngine.cs
                ["REG_BATTERY_TEMP_LO"] = 0x04A2, // HEAD UCU.Hardware\Ec\EcEngine.cs
                ["REG_BIOS_OEM_2"] = 0x0782, // HEAD UCU.Hardware\Ec\EcEngine.cs
                ["REG_CPU_FAN_SPEED_TABLE"] = 0x0F20, // HEAD UCU.Core\Ec\EcFanRegisters.cs
                ["REG_CPU_TEMP"] = 0x043E, // HEAD UCU.Hardware\Ec\EcEngine.cs
                ["REG_CPU_TEMP_END_TABLE"] = 0x0F00, // HEAD UCU.Hardware\Ec\EcEngine.cs
                ["REG_CPU_TEMP_START_TABLE"] = 0x0F10, // HEAD UCU.Hardware\Ec\EcEngine.cs
                ["REG_CTGP_DB_CTGP_OFFSET"] = 0x0744, // HEAD UCU.Hardware\Ec\EcEngine.cs
                ["REG_CTGP_DB_DB_OFFSET"] = 0x0746, // HEAD UCU.Hardware\Ec\EcEngine.cs
                ["REG_CTGP_DB_ENABLE"] = 0x0743, // HEAD UCU.Hardware\Ec\EcEngine.cs
                ["REG_CTGP_DB_TPP_OFFSET"] = 0x0745, // HEAD UCU.Hardware\Ec\EcEngine.cs
                ["REG_CUSTOM_PROFILE"] = 0x0727, // HEAD UCU.Hardware\Ec\EcEngine.cs
                ["REG_DISPLAY_VARIANT_HI"] = 0x0409, // HEAD UCU.Hardware\Ec\EcEngine.cs
                ["REG_DISPLAY_VARIANT_LO"] = 0x0408, // HEAD UCU.Hardware\Ec\EcEngine.cs
                ["REG_FAN_SWITCH_SPEED"] = 0x0787, // HEAD UCU.Hardware\Ec\EcEngine.cs
                ["REG_FN_LOCK"] = 0x074E, // HEAD UCU.Hardware\Ec\EcEngine.cs
                ["REG_GPU_FAN_SPEED_TABLE"] = 0x0F50, // HEAD UCU.Core\Ec\EcFanRegisters.cs
                ["REG_GPU_STATUS"] = 0x072A, // HEAD UCU.Hardware\Ec\EcEngine.cs
                ["REG_GPU_TEMP"] = 0x044F, // HEAD UCU.Hardware\Ec\EcEngine.cs
                ["REG_GPU_TEMP_END_TABLE"] = 0x0F30, // HEAD UCU.Hardware\Ec\EcEngine.cs
                ["REG_GPU_TEMP_START_TABLE"] = 0x0F40, // HEAD UCU.Hardware\Ec\EcEngine.cs
                ["REG_KBD_STATUS"] = 0x078C, // HEAD UCU.Hardware\Ec\EcEngine.cs
                ["REG_MAIN_FAN_RPM_HI"] = 0x0465, // HEAD UCU.Hardware\Ec\EcEngine.cs
                ["REG_MAIN_FAN_RPM_LO"] = 0x0464, // HEAD UCU.Hardware\Ec\EcEngine.cs
                ["REG_MANUAL_FAN_CTRL"] = 0x0751, // HEAD UCU.Hardware\Ec\EcEngine.cs
                ["REG_MODE_INDEX"] = 0x07AB, // HEAD UCU.Hardware\Ec\EcEngine.cs
                ["REG_MODULE_ID_GPU"] = 0x07D2, // HEAD UCU.Hardware\Ec\EcEngine.cs
                ["REG_MUX_STATE_HI"] = 0x0403, // HEAD UCU.Hardware\Ec\EcEngine.cs
                ["REG_MUX_STATE_LO"] = 0x0402, // HEAD UCU.Hardware\Ec\EcEngine.cs
                ["REG_OEM_MODE_FLAG_45B"] = 0x045B, // HEAD UCU.Hardware\Ec\EcEngine.cs
                ["REG_PL1_SETTING"] = 0x0783, // HEAD UCU.Hardware\Ec\EcEngine.cs
                ["REG_PL2_SETTING"] = 0x0784, // HEAD UCU.Hardware\Ec\EcEngine.cs
                ["REG_PL4_SETTING"] = 0x0785, // HEAD UCU.Hardware\Ec\EcEngine.cs
                ["REG_PL5_SETTING"] = 0x0788, // HEAD UCU.Hardware\Ec\EcEngine.cs
                ["REG_POWER_MGMT_726"] = 0x0726, // HEAD UCU.Hardware\Ec\EcEngine.cs
                ["REG_PROJECT_ID"] = 0x0740, // HEAD UCU.Hardware\Ec\EcEngine.cs
                ["REG_PWM_1"] = 0x075B, // HEAD UCU.Hardware\Ec\EcEngine.cs
                ["REG_PWM_1_WRITEABLE"] = 0x1804, // HEAD UCU.Hardware\Ec\EcEngine.cs
                ["REG_PWM_2"] = 0x075C, // HEAD UCU.Hardware\Ec\EcEngine.cs
                ["REG_PWM_2_WRITEABLE"] = 0x1809, // HEAD UCU.Hardware\Ec\EcEngine.cs
                ["REG_RGB_BLUE"] = 0x076B, // HEAD UCU.Hardware\Ec\EcEngine.cs
                ["REG_RGB_GREEN"] = 0x076A, // HEAD UCU.Hardware\Ec\EcEngine.cs
                ["REG_RGB_RED"] = 0x0769, // HEAD UCU.Hardware\Ec\EcEngine.cs
                ["REG_SECOND_FAN_RPM_HI"] = 0x046B, // HEAD UCU.Hardware\Ec\EcEngine.cs
                ["REG_SECOND_FAN_RPM_LO"] = 0x046C, // HEAD UCU.Hardware\Ec\EcEngine.cs
                ["REG_SSD_TEMP"] = 0x07D1, // HEAD UCU.Hardware\Ec\EcEngine.cs
                ["REG_SUPPORT_1"] = 0x0765, // HEAD UCU.Hardware\Ec\EcEngine.cs
                ["REG_SUPPORT_2"] = 0x0766, // HEAD UCU.Hardware\Ec\EcEngine.cs
                ["REG_SUPPORT_5"] = 0x0742, // HEAD UCU.Hardware\Ec\EcEngine.cs
                ["REG_TCC_OFFSET"] = 0x0786, // HEAD UCU.Hardware\Ec\EcEngine.cs
                ["REG_TRIGGER"] = 0x0767, // HEAD UCU.Hardware\Ec\EcEngine.cs
                ["REG_UNIVERSAL_FAN_CTRL"] = 0x07C5, // HEAD UCU.Hardware\Ec\EcEngine.cs
                ["REG_WINLOCK_STATUS"] = 0x0768, // HEAD UCU.Hardware\Ec\EcEngine.cs
                ["RGB_KEYBOARD"] = 0x04, // HEAD UCU.Hardware\Ec\EcEngine.cs
                ["SPLIT_TABLES"] = 0x80, // HEAD UCU.Hardware\Ec\EcEngine.cs
                ["TRIGGER_OFFICE_BIT"] = 0x08, // HEAD UCU.Core\Ec\EcPowerCodec.cs
                ["TRIGGER_PRESERVED_BIT"] = 0x10, // HEAD UCU.Core\Ec\EcPowerCodec.cs
                ["TRIGGER_RGB_APPLY_COLOR"] = 0x20, // HEAD UCU.Core\Ec\EcPowerCodec.cs
                ["TRIGGER_RGB_LOGO_EFFECT"] = 0x40, // HEAD UCU.Hardware\Ec\EcEngine.cs
                ["TRIGGER_RGB_RAINBOW_EFFECT"] = 0x80, // HEAD UCU.Hardware\Ec\EcEngine.cs
                ["WINLOCK_STATUS_BIT"] = 0x01, // HEAD UCU.Core\Ec\EcSwitchCodec.cs
                ["WINLOCK_TRIGGER_BIT"] = 0x01, // HEAD UCU.Core\Ec\EcSwitchCodec.cs
                // Named for inline literals the product wrote at the cited HEAD lines: the init
                // handshake and the sequence encodings, which had no const before the registry.
                ["REG_HANDSHAKE_709"] = 0x0709, // HEAD EcEngine.cs:317 WriteECRaw(0x0709, 0x05)
                ["HANDSHAKE_709_VALUE"] = 0x05, // HEAD EcEngine.cs:317
                ["REG_HANDSHAKE_708"] = 0x0708, // HEAD EcEngine.cs:319 WriteECRaw(0x0708, 0x01)
                ["HANDSHAKE_708_VALUE"] = 0x01, // HEAD EcEngine.cs:319
                ["REG_FEATURE_ENABLE_748"] = 0x0748, // HEAD EcEngine.cs:333 WriteECRaw(0x0748, 0x81)
                ["FEATURE_ENABLE_748_VALUE"] = 0x81, // HEAD EcEngine.cs:333
                ["UNIVERSAL_FAN_CURVE_ENABLE_BITS"] = 0xE0, // HEAD EcEngine.cs:349 ReadECRaw(0x07C5) | 0xE0
                ["REG_FAN_CURVE_PARAM_7C7"] = 0x07C7, // HEAD EcEngine.cs:368 WriteECRaw(0x07C7, 0x0C)
                ["FAN_CURVE_PARAM_7C7_VALUE"] = 0x0C, // HEAD EcEngine.cs:368
                ["AP_OEM_6_FAN_CURVE_MODE"] = 0x04, // HEAD EcEngine.cs:373 WriteECRaw(0x07C6, 0x04)
                ["REG_BIOS_CTRL_706"] = 0x0706, // HEAD EcEngine.cs:378 WriteECRaw(0x0706, 0x41)
                ["BIOS_CTRL_706_VALUE"] = 0x41, // HEAD EcEngine.cs:378
                ["FAN_TABLE_TEMP_START_OFFSET"] = 0x10, // HEAD EcEngine.cs:1197 baseAddr + 0x10
                ["FAN_TABLE_DUTY_OFFSET"] = 0x20, // HEAD EcEngine.cs:1201 baseAddr + 0x20
                ["FAN_OVERBOOST_CLEAR_MASK"] = 0xB0, // HEAD EcEngine.cs:1992 ~(0xa0 | 0x10)
                ["BATTERY_AC_CONNECTED_BIT"] = 0x01, // HEAD UCU.Core\Ec\EcWordCodec.cs:36 (powerSourceRegister & 1)
                // The raw-write allowlist at HEAD: EcWriteGatePolicy.AllowedRanges tuples.
                ["REGION_FAN_TABLE_BLOCK_START"] = 0x0F00, // HEAD EcWriteGatePolicy.cs:44 (0x0F00, 0x0F5F)
                ["REGION_FAN_TABLE_BLOCK_END"] = 0x0F5F, // HEAD EcWriteGatePolicy.cs:44
                ["REGION_PWM_WRITE_1_BLOCK_START"] = 0x1804, // HEAD EcWriteGatePolicy.cs:45 (0x1804, 0x1808)
                ["REGION_PWM_WRITE_1_BLOCK_END"] = 0x1808, // HEAD EcWriteGatePolicy.cs:45
                ["REGION_PWM_WRITE_2_BLOCK_START"] = 0x1809, // HEAD EcWriteGatePolicy.cs:46 (0x1809, 0x180D)
                ["REGION_PWM_WRITE_2_BLOCK_END"] = 0x180D, // HEAD EcWriteGatePolicy.cs:46
                ["REGION_KBD_RGB_BLOCK_START"] = 0x0767, // HEAD EcWriteGatePolicy.cs:48 (0x0767, 0x076B)
                ["REGION_KBD_RGB_BLOCK_END"] = 0x076B, // HEAD EcWriteGatePolicy.cs:48
                ["REGION_KBD_STATUS_BLOCK_START"] = 0x078C, // HEAD EcWriteGatePolicy.cs:49 (0x078C, 0x078C)
                ["REGION_KBD_STATUS_BLOCK_END"] = 0x078C, // HEAD EcWriteGatePolicy.cs:49
                ["REGION_BATTERY_MODE_BLOCK_START"] = 0x07A6, // HEAD EcWriteGatePolicy.cs:51 (0x07A6, 0x07A6)
                ["REGION_BATTERY_MODE_BLOCK_END"] = 0x07A6, // HEAD EcWriteGatePolicy.cs:51
    };

    [Fact]
    public void TheCommittedArtifactIsExactlyWhatTheRegistryGenerates()
    {
        EcRegistryGenerator.Result result = EcRegistryGenerator.Generate(ReadRegistry());
        Assert.True(result.Success, "registry must validate: " + result.Error);

        string committed = File.ReadAllText(Path.Combine(RepositoryRoot(), ArtifactRelative));
        Assert.Equal(Normalize(result.Code), Normalize(committed));
    }

    [Fact]
    public void TheGeneratorIsDeterministic()
    {
        string registry = ReadRegistry();

        EcRegistryGenerator.Result first = EcRegistryGenerator.Generate(registry);
        EcRegistryGenerator.Result second = EcRegistryGenerator.Generate(registry);

        Assert.True(first.Success, "registry must validate: " + first.Error);
        Assert.True(second.Success, "registry must validate: " + second.Error);
        Assert.Equal(first.Code, second.Code);
    }

    [Fact]
    public void EveryVaultConstantKeepsTheValueItHadBeforeTheRegistry()
    {
        foreach ((string name, long expected) in PreRegistryValues)
        {
            FieldInfo field = VaultField(name);
            Assert.True(field.IsLiteral, $"{name} must be a compile-time constant.");

            long actual = Convert.ToInt64(field.GetRawConstantValue());
            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void TheVaultDeclaresNoConstantThisTableDoesNotPin()
    {
        string[] unpinned = VaultConstants()
            .Where(field => !PreRegistryValues.ContainsKey(field.Name))
            .Select(field => field.Name)
            .ToArray();

        Assert.True(
            unpinned.Length == 0,
            "New EC wiring entered the vault without a pre-registry value pin. Add the value it " +
            "carried at the call site it came from (provenance, not invention): " + string.Join(", ", unpinned));
    }

    [Fact]
    public void TheVaultDeclaresNoNameTwice()
    {
        string[] duplicates = VaultConstants()
            .GroupBy(field => field.Name, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();

        Assert.Empty(duplicates);
    }

    [Fact]
    public void TheBatteryModeEncodingsKeepTheirPreRegistryRawValues()
    {
        Assert.Equal(
            new byte[] { 0x08, 0x18, 0x28 },   // HEAD EcBatteryCodec.ModeRawValues
            EcVault.BATTERY_MODE_RAW_VALUES);
    }

    [Fact]
    public void TheRawWriteAllowlistStillAnswersTheHeadRangeTable()
    {
        // The regions the raw verbs could touch before the registry: fan tables, the two
        // writeable PWM blocks, the keyboard RGB set, the brightness row, the battery mode.
        Assert.True(UCU.Core.Ec.EcWriteGatePolicy.IsRawWriteAllowed(EcVault.REGION_FAN_TABLE_BLOCK_START, 16));
        Assert.True(UCU.Core.Ec.EcWriteGatePolicy.IsRawWriteAllowed(EcVault.REGION_BATTERY_MODE_BLOCK_START, 1));
        Assert.True(UCU.Core.Ec.EcWriteGatePolicy.IsRawWriteAllowed(EcVault.REGION_KBD_RGB_BLOCK_START, 5));

        // The denied domains stay denied (the region set did not grow during the refactor).
        Assert.False(UCU.Core.Ec.EcWriteGatePolicy.IsRawWriteAllowed(EcVault.REG_MODE_INDEX, 1));
        Assert.False(UCU.Core.Ec.EcWriteGatePolicy.IsRawWriteAllowed(EcVault.REG_PL1_SETTING, 1));
        Assert.False(UCU.Core.Ec.EcWriteGatePolicy.IsRawWriteAllowed(EcVault.REG_POWER_MGMT_726, 1));

        // A block straddling the fan-table boundary stays refused as a whole.
        Assert.False(UCU.Core.Ec.EcWriteGatePolicy.IsRawWriteAllowed(EcVault.REGION_FAN_TABLE_BLOCK_END - 3, 8));
    }

    [Theory]
    [MemberData(nameof(MalformedRegistries))]
    public void TheGeneratorRefusesARegistryThatCannotBeTrusted(string caseName, string json, string expectedInError)
    {
        EcRegistryGenerator.Result result = EcRegistryGenerator.Generate(json);

        Assert.False(result.Success, caseName + " must be refused: " + result.Error);
        Assert.NotNull(result.Error);
        Assert.Contains(expectedInError, result.Error, StringComparison.Ordinal);
    }

    public static TheoryData<string, string, string> MalformedRegistries => new()
    {
        { "duplicate-name", Registry("{\"address\":[{\"name\":\"REG_A\",\"value\":\"0x10\",\"domain\":\"fan\",\"evidence\":\"probe: observed\"},{\"name\":\"REG_A\",\"value\":\"0x11\",\"domain\":\"fan\",\"evidence\":\"probe: observed\"}]}"), "duplicate identifier" },
        { "no-evidence", Registry("{\"address\":[{\"name\":\"REG_A\",\"value\":\"0x10\",\"domain\":\"fan\"}]}"), "missing required field 'evidence'" },
        { "thin-evidence", Registry("{\"address\":[{\"name\":\"REG_A\",\"value\":\"0x10\",\"domain\":\"fan\",\"evidence\":\"?\"}]}"), "provenance discipline" },
        { "address-out-of-range", Registry("{\"address\":[{\"name\":\"REG_A\",\"value\":\"0x10000\",\"domain\":\"fan\",\"evidence\":\"probe: observed\"}]}"), "exceeds the EC address space" },
        { "byte-overflow", Registry("{\"value\":[{\"name\":\"MASK_A\",\"value\":\"0x1FF\",\"register\":\"REG_A\",\"evidence\":\"probe: observed\"}]}"), "exceeds byte" },
        { "region-inverted", Registry("{\"region\":[{\"name\":\"BLOCK\",\"low\":\"0x0F5F\",\"high\":\"0x0F00\",\"domain\":\"fan\",\"evidence\":\"probe: observed\"}]}"), "low is above high" },
        { "unknown-field", Registry("{\"address\":[{\"name\":\"REG_A\",\"value\":\"0x10\",\"domain\":\"fan\",\"evidence\":\"probe: observed\",\"colour\":\"red\"}]}"), "unknown field 'colour'" },
        { "unknown-section", Registry("{\"registers\":[{\"name\":\"REG_A\",\"value\":\"0x10\",\"domain\":\"fan\",\"evidence\":\"probe: observed\"}]}"), "unknown registry section" },
        { "bad-platform", Registry("{\"value\":[{\"name\":\"MASK_A\",\"value\":\"0x01\",\"register\":\"REG_A\",\"platform\":\"arm64\",\"evidence\":\"probe: observed\"}]}"), "EcPlatform vocabulary" },
        { "bad-identifier", Registry("{\"address\":[{\"name\":\"9Lives\",\"value\":\"0x10\",\"domain\":\"fan\",\"evidence\":\"probe: observed\"}]}"), "not a C# identifier" },
        { "address-must-be-ushort", Registry("{\"address\":[{\"name\":\"REG_A\",\"value\":\"0x10\",\"type\":\"uint\",\"domain\":\"fan\",\"evidence\":\"probe: observed\"}]}"), "EC addresses are ushort" },
        { "not-json", "{ this is not json", "not valid JSON" },
    };

    private static string Registry(string body) => body;

    private static IEnumerable<FieldInfo> VaultConstants() =>
        typeof(EcVault).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral);

    private static FieldInfo VaultField(string name)
    {
        FieldInfo? field = typeof(EcVault).GetField(name, BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(field);
        return field!;
    }

    private static string ReadRegistry() =>
        File.ReadAllText(Path.Combine(RepositoryRoot(), RegistryRelative));

    private static string Normalize(string text) => text.Replace("\r\n", "\n");

    private static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "UCU.Core")) &&
                Directory.Exists(Path.Combine(directory.FullName, "UCU.Core.Tests")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Repository root with the EC registry not found.");
    }
}