# XMG Fan Control via Uniwill EC

This document describes how to control the CPU and GPU fan curves on SCH XMG Neo (and related TUXEDO InfinityBook) laptops through the Uniwill Embedded Controller (EC). It is intended as a reference for developers who want to implement their own fan control software for this hardware.

The approach used by UXTU reads from and writes to EC registers through `ACPIDriverDll.dll`, a native OEM library shipped with the XMG Control Center. All register addresses are cross-referenced with the open-source [uniwill-laptop-driver](https://github.com/t-8ch/uniwill-laptop-driver) Linux kernel module, which documents the same EC under Linux.

---

## Table of Contents

- [Hardware Overview](#hardware-overview)
- [EC Communication Stack](#ec-communication-stack)
- [EC Register Map](#ec-register-map)
- [Fan Curve Table Format](#fan-curve-table-format)
- [EC Activation Handshake](#ec-activation-handshake)
- [Applying a Fan Curve](#applying-a-fan-curve)
- [Reading Fan State](#reading-fan-state)
- [Restoring Automatic Control](#restoring-automatic-control)
- [Recovering from EC Lockup](#recovering-from-ec-lockup)
- [Pitfalls and Gotchas](#pitfalls-and-gotchas)
- [Reference Implementation](#reference-implementation)

---

## Hardware Overview

The SCH XMG Neo laptops use a **Uniwill Embedded Controller** that manages thermal control, keyboard backlighting, and power limits. The EC exposes a register space accessible through the `UWACPIDriver.sys` kernel driver, bound to ACPI device `INOU0000`.

Key facts:
- **Single fan** drives both CPU and GPU cooling (a dual-heatpipe solution).
- The EC maintains **separate fan curve tables** for CPU and GPU triggers, but both resolve to the same physical fan.
- The EC uses a **0–200 duty scale** (not 0–255). A value of 100 = 50% PWM.
- The EC enforces a **25% minimum fan speed** (50/200). Values below this are clamped by the hardware.
- Fan curve tables have **16 zones** (indices 0–15), but only zones 0–10 are active. Zones 11–15 are padded with the maximum duty value.
- Zone 0 is always duty 0 (fan off below the lowest temperature threshold).

---

## EC Communication Stack

```
Application (C#)
    │  P/Invoke (cdecl)
    ▼
ACPIDriverDll.dll  (native MFC/C++ OEM library)
    │  IOCTL
    ▼
UWACPIDriver.sys   (Windows kernel-mode driver)
    │  ACPI I/O ports
    ▼
Uniwill EC         (Embedded Controller hardware)
```

### ACPIDriverDll.dll Functions

The OEM DLL exports these functions (all `cdecl`, returning `int`):

| Function | Signature | Description |
|----------|-----------|-------------|
| `ReadEC` | `int ReadEC(ushort address)` | Read a byte from EC address |
| `WriteEC` | `int WriteEC(ushort address, byte value)` | Write a byte to EC address |
| `ReadIO` | `int ReadIO(ushort port)` | Read I/O port |
| `WriteIO` | `int WriteIO(ushort port, byte value)` | Write I/O port |
| `ReadIndexIO` | `int ReadIndexIO(ushort address)` | Read indexed I/O |
| `WriteIndexIO` | `int WriteIndexIO(ushort address, byte value)` | Write indexed I/O |

For fan control, only `ReadEC` and `WriteEC` are needed.

**Timing constraint:** Each EC operation requires a **6ms delay** between calls. The Linux driver defines this as `UNIWILL_EC_DELAY_US = 6000`. Skipping this delay causes unreliable reads/writes.

### Locating the DLL

Search order used by UXTU:
1. Application directory (`ACPIDriverDll.dll` bundled with the app)
2. `C:\Program Files\OEM\Control Center\AiStoneService\MyControlCenter\ACPIDriverDll.dll`
3. `C:\Program Files (x86)\OEM\Control Center\AiStoneService\MyControlCenter\ACPIDriverDll.dll`

If none exist, EC access is unavailable.

---

## EC Register Map

### Temperature & Sensors

| Address | Name | Description |
|---------|------|-------------|
| `0x043E` | CPU_TEMP | CPU temperature (°C) |
| `0x044F` | GPU_TEMP | GPU temperature (°C) |
| `0x07D1` | SSD_TEMP | SSD temperature (°C) |

### Fan RPM (big-endian u16)

| Address | Name | Description |
|---------|------|-------------|
| `0x0464` | MAIN_FAN_RPM_LO | Main fan RPM, low byte (high-byte of the u16) |
| `0x0465` | MAIN_FAN_RPM_HI | Main fan RPM, high byte (low-byte of the u16) |
| `0x046B` | SECOND_FAN_RPM_HI | Second fan RPM, high byte |
| `0x046C` | SECOND_FAN_RPM_LO | Second fan RPM, low byte |

> **Note:** The main fan RPM register pair is swapped — `0x0464` is the high byte, `0x0465` is the low byte. This is confirmed by the Linux driver source.

### Fan Control

| Address | Name | Description |
|---------|------|-------------|
| `0x0751` | MANUAL_FAN_CTRL | Manual fan mode flags |
| `0x075B` | PWM_1 | CPU fan PWM readback (0–200) |
| `0x075C` | PWM_2 | GPU fan PWM readback (0–200) |
| `0x0787` | FAN_SWITCH_SPEED | Target fan speed in RPM (u16, 100–12700) |
| `0x07C5` | UNIVERSAL_FAN_CTRL | Universal fan control flags |
| `0x07C6` | AP_OEM_6 | Custom fan enable flag |
| `0x07C7` | Fan curve parameter | Set to `0x0C` during handshake |

#### MANUAL_FAN_CTRL bits

| Bit | Name | Description |
|-----|------|-------------|
| 0–2 | FAN_LEVEL_MASK | Preset level |
| 4 | FAN_MODE_TURBO | Turbo mode |
| 5 | FAN_MODE_HIGH | High profile |
| 6 | FAN_MODE_BOOST | Boost mode (max speed) |
| 7 | FAN_MODE_USER | User/custom mode |

#### UNIVERSAL_FAN_CTRL bits

| Bit | Name | Description |
|-----|------|-------------|
| 7 | SPLIT_TABLES | When set, CPU and GPU use separate curve tables |

#### AP_OEM_6 bits

| Bit | Name | Description |
|-----|------|-------------|
| 2 | ENABLE_UNIVERSAL_FAN_CTRL | Enable custom fan table mode |

### Fan Curve Tables

Each fan source (CPU / GPU) has three 16-byte tables:

| Address | CPU Base | GPU Base | Description |
|---------|----------|----------|-------------|
| — | `0x0F00` | `0x0F30` | Temp End (ramp-up threshold, °C) |
| — | `0x0F10` | `0x0F40` | Temp Start (ramp-down threshold, °C, hysteresis) |
| — | `0x0F20` | `0x0F50` | Fan Speed (duty 0–200) |

### Power Limits

| Address | Name | Description |
|---------|------|-------------|
| `0x0783` | PL1_SETTING | Sustained power limit |
| `0x0784` | PL2_SETTING | Burst power limit |
| `0x0785` | PL4_SETTING | Cache power limit |
| `0x0786` | TCC_OFFSET | Thermal throttling offset |
| `0x07AB` | MODE_INDEX | Performance mode index |

---

## Fan Curve Table Format

Each table has 16 zones. Only zones 0–10 are active; zones 11–15 are padding.

### Zone 0 (Fan Off)

- **Duty:** Always 0
- **Temp End:** Below-threshold temperature (~52°C for CPU)
- **Purpose:** Fan remains off below this temperature

### Zones 1–10 (Active Curve)

- **Duty:** 0–200 scale (100 = 50% PWM)
- **Temp End:** Temperature at which this zone activates (ramp up)
- **Temp Start:** Temperature at which this zone deactivates (ramp down, hysteresis)
- **Must be monotonically non-decreasing** — each zone's duty must be ≥ the previous zone

### Zones 11–15 (Padding)

- **Duty:** Set to the maximum active duty value
- **Temperature:** `0xFF` (255, inactive sentinel)

### Default OEM Values

The factory-default fan curve (captured from XMG Control Center):

| Zone | Temp (°C) | Duty (0–200) |
|------|-----------|--------------|
| 0 | 48 (CPU) / 46 (GPU) | 0 |
| 1 | 52 / 50 | 64 (CPU) / 60 (GPU) |
| 2 | 56 / 54 | 70 |
| 3 | 60 / 58 | 80 |
| 4 | 64 / 62 | 100 (CPU) / 102 (GPU) |
| 5 | 68 / 66 | 110 |
| 6 | 72 / 70 | 120 |
| 7 | 76 / 74 | 140 |
| 8 | 80 / 78 | 160 |
| 9 | 85 / 81 | 180 |
| 10 | — | 180 |
| 11–15 | 255 | 180 |

---

## EC Activation Handshake

**Before the EC accepts custom fan curve writes, it requires an 8-step activation handshake.** Without this sequence, writes to the fan table registers are accepted but silently ignored by the EC's fan control engine.

The handshake was reverse-engineered from the MechControl (XMG Control Center) initialization sequence using Frida tracing of `GCUService.exe`.

### Steps

| Step | Action | Register | Value |
|------|--------|----------|-------|
| 1a | Handshake | `0x0709` | `0x05` |
| 1b | Handshake | `0x0708` | `0x01` |
| 2 | Activation | `0x0741` (AP_OEM) | `0x05` |
| 3 | Feature enable | `0x0748` | `0x81` |
| 4 | Fan curve enable | `0x07C5` | OR with `0xE0` (bits 7,6,5) |
| 5 | Write default OEM tables | All curve registers | See default values above |
| 6 | Fan curve parameter | `0x07C7` | `0x0C` |
| 7 | Fan curve mode | `0x07C6` | `0x04` |
| 8 | BIOS_CTRL | `0x0706` | `0x41` |

**Each step requires a 50ms delay between writes.** Step 5 involves writing 6 × 16-byte tables (96 EC writes total, each with a 6ms inter-byte delay).

**The handshake is idempotent** — it should only run once per service lifetime. Repeating it may confuse the EC state machine.

---

## Applying a Fan Curve

To apply a custom fan curve:

1. **Run the EC activation handshake** (if not already done)
2. **Build the 16-byte tables** for both CPU and GPU:
   - `temp_up[]` — ramp-up thresholds derived from user temperature points
   - `temp_down[]` — ramp-down thresholds (hysteresis), typically `temp - 1` or `temp - 2`
   - `duty[]` — 0–200 scale, monotonically non-decreasing, padded to 16 zones
3. **Write the tables** to the EC registers:
   - CPU: `0x0F00`, `0x0F10`, `0x0F20`
   - GPU: `0x0F30`, `0x0F40`, `0x0F50`
4. **Set SPLIT_TABLES bit** on `0x07C5` (if not already set)
5. **Toggle ENABLE_UNIVERSAL_FAN_CTRL** on `0x07C6`:
   - Clear bit 2, wait 100ms
   - Set bit 2, wait 100ms
   - This forces the EC to re-read the newly written tables

### Temperature Derivation

User-facing temperature steps (e.g., `[55, 60, 65, 70, 75, 80, 85, 90, 95, 97]`) map to EC tables as:

- `temp_up[i] = step[i] + 1` (zone activates above this)
- `temp_down[i] = step[i] - 1` (zone deactivates below this, hysteresis)
- Zone 0: `temp_up = 0`, `temp_down = step[0] - 2`

### Duty Conversion

User-facing percentages (0–100) convert to EC duty (0–200) by multiplying by 2:

```
ec_duty = user_percent * 2
```

Zone 0 is always 0. Zones 11–15 are padded with the maximum duty value.

---

## Reading Fan State

### Current PWM Duty

| Register | Fan |
|----------|-----|
| `0x075B` | CPU fan PWM (0–200) |
| `0x075C` | GPU fan PWM (0–200) |

Convert to percentage: `percent = pwm * 100 / 200`

### Fan RPM

Read `0x0464` (high byte) and `0x0465` (low byte), then combine:

```
rpm = (byte_0x0464 << 8) | byte_0x0465
```

### Current Curve Tables

Read 16 bytes from the table base addresses to retrieve the active curve.

---

## Restoring Automatic Control

To undo custom fan curves and return the EC to OEM automatic mode:

1. Clear `ENABLE_UNIVERSAL_FAN_CTRL` bit on `0x07C6`
2. Clear `SPLIT_TABLES` bit on `0x07C5`
3. Clear `FAN_MODE_USER`, `FAN_MODE_BOOST`, `FAN_MODE_TURBO`, `FAN_MODE_HIGH` bits on `0x0751`

This returns the EC to its built-in thermal management profile.

---

## Recovering from EC Lockup

If the EC fan state machine locks up (fans stuck at a fixed speed, unresponsive to both UXTU and XMG Control Center), perform a **fan state reset**:

1. Clear all control bits on `0x07C6`, `0x0751`, `0x07C5`
2. Wait 200ms for the EC to process
3. Write safe default tables (100% duty gradient)
4. Wait 100ms for tables to settle
5. Re-enable `SPLIT_TABLES` and `ENABLE_UNIVERSAL_FAN_CTRL`

This brings the EC back online without requiring a full reboot.

---

## Pitfalls and Gotchas

### EC Rejects Flat Tables

The EC rejects perfectly flat duty tables (all zones identical). Zone 0 must be 0, and zones 1+ must form a gradient. For a "fixed speed" effect, use zone 0 = 0 and zones 1–15 = target duty.

### SPLIT_TABLES Toggle Can Lock the EC

Clearing `SPLIT_TABLES` (bit 7 of `0x07C5`) while custom fan mode is already active can cause the EC fan state machine to lock up, requiring a reboot. **Only transition False → True; never clear the bit if it's already set.**

### Handshake Is Required Before Curve Writes

The EC will silently ignore custom table writes if the activation handshake hasn't been performed. Always run the handshake before uploading curves.

### Minimum Fan Speed Is 25%

The EC hardware enforces a 25% minimum (50/200). Writing values below this results in the fan running at 25% regardless.

### 6ms Delay Between EC Operations

Each `ReadEC`/`WriteEC` call requires a 6ms delay. Skipping this causes unreliable reads and writes.

### RGB Color 0x000000 Means "Restore Default"

When setting keyboard backlight color, the EC interprets `0x000000` as "restore default color." Use `0x010101` as the minimum if you want a near-black color.

### Direct PWM Writes Require USER Mode

Writing directly to `0x075B`/`0x075C` (PWM registers) only works when `FAN_MODE_USER` (bit 7 of `0x0751`) is set. Otherwise, the EC's auto fan loop overwrites the values.

---

## Reference Implementation

UXTU's `UniwillECService` provides a complete C# implementation of all EC operations described in this document. Key entry points:

| Method | Description |
|--------|-------------|
| `Initialize()` | Load DLL, verify EC access |
| `InitializeEc()` | Run the 8-step activation handshake |
| `ApplyFanCurve(cpuCurve, gpuCurve, cpuTemps, gpuTemps)` | Upload and activate custom curves |
| `SetFanSpeedPercent(percent)` | Set both fans to a fixed speed |
| `GetFanPwm(fanIndex)` | Read current PWM duty for a fan |
| `GetMainFanRpm()` | Read current fan RPM |
| `ToggleTurbo()` | Enable/disable turbo (max speed) mode |
| `RestoreAutoFanControl()` | Revert to OEM automatic mode |
| `ResetFanState()` | Recover from EC lockup |
| `ReadECRegister(address)` | Raw read from any EC register |
| `WriteECRegister(address, value)` | Raw write to any EC register |

The `EcFanCurve` model class provides preset curves (Silent, Balanced, Performance, Full Speed, Off) and handles 0–100% → 0–200 duty conversion with 16-byte padding.
