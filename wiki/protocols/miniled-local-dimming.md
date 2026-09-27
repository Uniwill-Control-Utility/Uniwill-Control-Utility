# MiniLED Local Dimming Control

> **Baseline and evidence:** This reference is aligned with the WMI implementation and panel database at `eea723e`. The code enumerates specific panel IDs and WMI instance variants; actual firmware behavior requires a recorded device run.

## Overview

MiniLED Local Dimming can be toggled on Uniwill IDY/Tongfang X6 chassis laptops (e.g., XMG Neo 16 A25) via a combination of WMI/ACPI commands for write and EC register readback for verification.

## Hardware

- **Feature**: MiniLED backlight panel with Local Dimming (LCDL)
- **EC Register**: `0x72B` bit 1 — status/readback only
  - Bit set (`& 0x02 != 0`) = Local Dimming **ON**
  - Bit clear (`& 0x02 == 0`) = Local Dimming **OFF**
- **Control path**: WMI `AcpiTest_MULong` → `GetSetULong` method

## Control Flow

### Write (Enable/Disable)

Use WMI/ACPI to send commands. **Do NOT write directly to EC 0x72B** — it is read-only for verification.

#### WMI Interface

| Property | Value |
|----------|-------|
| Namespace | `root\WMI` |
| Class | `AcpiTest_MULong` |
| Instance | `AcpiTest_MULong.InstanceName='ACPI\PNP0C14\1_1'` |
| Method | `GetSetULong` |
| Input property | `Data` (ulong) |
| Output property | `Return` (uint, cast to byte) |

#### Correct Invocation Pattern

```csharp
private static byte WmiCall(ulong data)
{
    using var mo = new ManagementObject(
        @"root\WMI",
        @"AcpiTest_MULong.InstanceName='ACPI\PNP0C14\1_1'",
        null);

    using var input = mo.GetMethodParameters("GetSetULong");
    input["Data"] = data;

    using var output = mo.InvokeMethod("GetSetULong", input, null);
    return (byte)(uint)output["Return"];
}
```

**Critical**: You must use `GetMethodParameters` to obtain the input parameter object, set the `Data` property, then pass it to `InvokeMethod`. Calling `InvokeMethod` directly with an `object[]` argument array returns `null` because the WMI provider expects named parameters via the `ManagementBaseObject` input.

#### Command Sequence

**Enable Local Dimming:**
1. `WmiCall(0x50000000002)` → expect return `0xBB` (EC write mode ON)
2. `WmiCall(0x5000000000E)` → expect return `0x0E` (GPIO HIGH)

**Disable Local Dimming:**
1. `WmiCall(0x50000000001)` → expect return `0xAA` (EC write mode OFF)
2. `WmiCall(0x5000000000D)` → expect return `0x0D` (GPIO LOW)

#### Command Byte Breakdown

The `0x5000000000X` pattern:
- High byte `0x50` — command prefix (EC I/O mode)
- Low byte — sub-command:
  - `0x01` = Exit EC write mode (expected return `0xAA`)
  - `0x02` = Enter EC write mode (expected return `0xBB`)
  - `0x0D` = Drive GPIO LOW
  - `0x0E` = Drive GPIO HIGH

### Read (Verification)

After sending WMI commands, verify the state by reading EC register `0x72B`:

```csharp
byte ecValue = ecService.ReadECRegister(0x072B);
bool actualState = (ecValue & 0x02) != 0;
```

The read must match the requested state. If it doesn't, the toggle should be considered failed and the UI should revert.

## Error Handling

- Each WMI call returns a single byte. If the return doesn't match the expected value, fail immediately.
- After the GPIO command, verify via EC readback. If the EC state doesn't match, fail.
- No retry logic is needed — a single attempt per step is sufficient.
- If verification fails, revert the UI toggle to reflect actual hardware state.

## Instance Name Variants

The WMI instance name `ACPI\PNP0C14\1_1` is the most common for Uniwill IDY/Tongfang X6 chassis laptops. Alternative instance names that may work on other hardware:

- `ACPI\PNP0C14\0_0`
- `ACPI\PNP0C14`
- `ACPI0003\0_0`
- `ACPI0003\1_1`
- `ACPI0003`

To discover available instances, enumerate `AcpiTest_MULong` in `root\WMI` and read the `InstanceName` property of each instance.

## Source

Reverse-engineered from MechControl (Heavyfail fork), which implements a Local Dimming toggle. The original research notes report a successful run on an XMG Neo 16 A25 (Uniwill IDY/Tongfang X6 chassis) with a MiniLED panel; the repository does not contain an executable result record for that run.

## References

- MechControl `LocalDimmingControl.cs` — WMI call pattern, command constants
- MechControl `EcDevice.cs` — EC register read via `\\.\ACPIDriver`
- EC register `0x72B` bit 1 — Local Dimming status flag

[Back to documentation index](../README.md)
