# Smart Fan Control (Cross-Sensor Fan Linking)

> **Baseline and evidence:** This reference is aligned with `EcSmartFanControl`, `FlydigiTemperatureProvider`, `UniwillECService`, and the Fan Control page at `eea723e`. It documents current algorithm and EC-write behavior, not external hardware qualification.

This document describes the **Smart Fan Control** feature — a temperature-driven control loop that dynamically updates both CPU and GPU fan curve tables so that **both fans react to a single temperature source** (CPU, GPU, or the maximum of both).

This enables "linked fans" behavior where, for example, both fans ramp up together based solely on CPU temperature, even though the EC firmware normally routes each fan to its own temperature sensor.

---

## Table of Contents

- [Problem Statement](#problem-statement)
- [Approach: Curve Table Updates (MechControl Pattern)](#approach-curve-table-updates-mechcontrol-pattern)
- [Architecture](#architecture)
- [Table Builder Design](#table-builder-design)
- [Control Loop](#control-loop)
- [Curve Evaluation](#curve-evaluation)
- [Activation Sequence](#activation-sequence)
- [Key Differences from Normal Fan Curves](#key-differences-from-normal-fan-curves)
- [Pitfalls Discovered During Implementation](#pitfalls-discovered-during-implementation)
- [Reference Implementation](#reference-implementation)

---

## Problem Statement

The Uniwill EC has **separate fan curve tables for CPU and GPU triggers**:
- CPU fan reads CPU curve tables, driven by the EC's internal CPU temperature sensor (`0x043E`)
- GPU fan reads GPU curve tables, driven by the EC's internal GPU temperature sensor (`0x044F`)

A user requested that **both fans react to a single temperature source** (e.g., just CPU, just GPU, or the highest of the two). The EC has no native register for cross-sensor fan routing.

---

## Approach: Curve Table Updates (MechControl Pattern)

The solution follows MechControl's **"Link Fans"** feature. Instead of trying to write direct PWM values (which only affects one fan or gets overwritten by the EC), it **dynamically rebuilds the fan curve tables on a timer**:

1. Read an external temperature (from LibreHardwareMonitor)
2. Evaluate the user's fan curve at that temperature → target duty
3. Build flat duty tables (same duty for all active zones)
4. Write tables to both CPU and GPU EC registers
5. Toggle the EC fan enable bit to force a re-read

The EC reads its **own internal temperature sensor** to pick which zone to activate. Since all active zones have the same duty, the fan runs at the target speed regardless of which zone the EC picks.

### Why Not Direct PWM?

Direct writes to `0x075B`/`0x075C` (PWM registers) require `FAN_MODE_USER` bit on `0x0751`. This mode only controls one fan and the EC's auto loop overwrites the values. Curve table updates are the only reliable method for controlling both fans simultaneously.

---

## Architecture

```
LibreHardwareMonitor (temperature source)
    │
    ▼
FlydigiTemperatureProvider (reads CPU/GPU temp from LHM)
    │
    ▼
EcSmartFanControl (control loop, 1-second interval)
    │  1. Read temp
    │  2. EWMA filter
    │  3. Evaluate fan curve → targetDuty (0-200)
    │  4. Build tables
    │  5. Write to EC
    ▼
UniwillECService.ApplySmartFanCurve()
    │  Write CPU tables (0x0F00, 0x0F10, 0x0F20)
    │  Write GPU tables (0x0F30, 0x0F40, 0x0F50)
    │  Toggle ENABLE_UNIVERSAL_FAN_CTRL
    ▼
Uniwill EC (reads own internal sensor, picks zone, applies duty)
    │
    ├── CPU fan → reads CPU duty table
    └── GPU fan → reads GPU duty table
```

---

## Table Builder Design

The three table builders in `EcSmartFanControl` follow MechControl's `BuildSafeCurve` pattern:

### Temp Up Table (Ramp-Up Thresholds)

Uses **fixed temperature thresholds** matching MechControl's `LinkedFanController`:

```
Zones 0-9:  {19, 29, 39, 49, 57, 65, 73, 81, 89, 97}
Zones 10-15: 255 (padding)
```

The EC reads its own internal sensor to pick a zone based on these thresholds. For example, at 63°C internal GPU temp, the EC picks zone 4 (57°C ≤ 63 < 65°C).

### Temp Down Table (Hysteresis)

```
Zone 0: 0
Zone i: max(0, FixedTemps[i-1] - 4)   (zones 1-9)
Zone 10: FixedTemps[9] + 4 = 101      (boundary)
Zones 11-15: 255                       (padding)
```

The `-4` matches MechControl's `MinHeadroomCelsius = 4`.

### Duty Table (Flat Target)

```
Zone 0: 0                              (EC requirement)
Zones 1-9: targetDuty                  (same duty for all active zones)
Zones 10-15: 200                       (safety max / padding)
```

Since all active zones (1-9) have the same duty, the fan runs at `targetDuty` regardless of which zone the EC picks based on its internal sensor.

### Why Fixed Temps Instead of All-Zeros?

An early approach set all tempUp thresholds to 0-1°C so the EC would always pick a zone with targetDuty. This worked for the CPU fan but **not the GPU fan** — the GPU fan stayed at 25% (hardware minimum). Using MechControl's fixed temps `{19, 29, 39, ...}` resolved this, as the EC firmware expects proper threshold ranges for both fans.

---

## Control Loop

The timer fires every **1000ms** (configurable via `PollIntervalMs`). Each tick:

| Step | Action | Details |
|------|--------|---------|
| 1 | Read temperature | CPU, GPU, or max (from `TempSource` setting) |
| 2 | EWMA filter | Alpha=0.5, smooths temperature jumps |
| 3 | Evaluate curve | Interpolates between curve points → `targetDuty` (0-200) |
| 4 | Critical temp override | ≥92°C → 200, ≥87°C → at least 160 |
| 5 | Hysteresis deadzone | Skips re-evaluation if temp change < 2.0°C |
| 6 | Ramp limiting | Max ±20/200 (10%) per cycle |
| 7 | Build tables | Temp up, temp down, flat duty for both fans |
| 8 | Write to EC | Only if duty changed by ≥ 4/200 (2%) |

### EWMA Filtering

Exponential Weighted Moving Average with alpha=0.5 prevents temperature spikes from causing sudden fan speed jumps. The filter carries state across ticks.

### Ramp Limiting

Limits duty change to ±20 per cycle (10% of 200 scale). This prevents the fans from ramping too aggressively when temperatures change rapidly.

### Hysteresis Deadzone

Temperature changes smaller than 2.0°C are ignored to prevent oscillation. The last computed duty is still valid, so the display updates but the curve is not re-evaluated.

---

## Curve Evaluation

The fan curve from `EcFanCurve` has 11 points (zones 0-10) with user-defined temperatures (default `{0, 55, 60, 65, 70, 75, 80, 85, 90, 95, 97}`).

The `EvaluateCurve()` method:
1. Finds the zone the temperature falls into
2. Gets duties for that zone and the next
3. Linearly interpolates between them
4. **Multiplies by 2** to convert from 0-100% to 0-200 EC scale

### Critical Bug: Scale Conversion

`EcFanCurve.GetDuty()` returns values in **0-100%** (user-facing percentages). The EC expects **0-200** scale. The fix was to multiply by 2:

```csharp
// GetDuty() returns 0-100%, convert to 0-200 scale
int dutyLow = _curve.GetDuty(zoneIndex) * 2;
int dutyHigh = _curve.GetDuty(zoneIndex + 1) * 2;
```

Without this, fans ran at **half the expected speed** (e.g., 41% instead of 82% at 70°C).

---

## Activation Sequence

`ApplySmartFanCurve()` writes tables using the same sequence as the normal `ApplyFanCurve()` (which works for both fans):

1. Ensure `SPLIT_TABLES` bit (0x07C5 bit 7) is set
2. Clear boost mode on `0x0751`
3. Write CPU tables to `0x0F00`, `0x0F10`, `0x0F20`
4. Write GPU tables to `0x0F30`, `0x0F40`, `0x0F50`
5. Toggle `ENABLE_UNIVERSAL_FAN_CTRL` (0x07C6 bit 2):
   - Clear bit 2, wait 150ms
   - Set bit 2, wait 150ms
   - This forces the EC to re-read both tables

The toggle is bit-masking (read-modify-write) to preserve other bits in `0x07C6`.

---

## Key Differences from Normal Fan Curves

| Aspect | Normal Fan Curve | Smart Fan Control |
|--------|-----------------|-------------------|
| Temperature source | EC's internal sensor | External (LibreHardwareMonitor) |
| Table content | Gradient (duty increases with temp) | Flat (same duty for all zones) |
| Temp thresholds | User-defined (55-97°C) | Fixed MechControl temps (19-97°C) |
| Update frequency | Once per preset change | Every 1 second |
| Fan independence | CPU fan → CPU temp, GPU fan → GPU temp | Both fans → same temp source |
| Curve evaluation | EC firmware (hardware) | Software (interpolation in C#) |

---

## Pitfalls Discovered During Implementation

### 1. GPU Fan Ignores All-Zero Temp Thresholds

Setting all tempUp thresholds to 0-1°C made the CPU fan work but the GPU fan stayed at 25%. Using MechControl's fixed temps `{19, 29, 39, ...}` fixed this — the EC firmware expects proper threshold ranges for both fans.

### 2. Duty Scale Mismatch (0-100 vs 0-200)

`EcFanCurve.GetDuty()` returns 0-100% but the EC tables use 0-200 scale. Without multiplying by 2, fans ran at half speed. This was the final bug to fix.

### 3. EC Reads Its Own Internal Sensor

The EC picks zones based on its **own internal temperature sensor** (`0x043E` for CPU, `0x044F` for GPU), NOT LibreHardwareMonitor. The EC's internal temps can differ significantly from LHM readings. This is why flat duty tables are needed — the EC picks whatever zone matches its internal sensor, but all zones have the same duty.

### 4. Zone 0 Must Be Duty 0

Following MechControl's pattern, zone 0 duty must be 0. Setting zone 0 to targetDuty caused unpredictable behavior. The EC expects zone 0 to be the "fan off below threshold" zone.

### 5. Active Entries = 10

The IDY_7Y/NB05 platform uses 10 active entries (zones 0-9), not 11. Zones 10-15 are padding with max duty (200) and temp 255.

### 6. EC Hardware Enforces 25% Minimum

The EC clamps any duty below 50/200 to 25%. This is a hardware limitation, not a software one. Zone 0 duty=0 appears as 25% on the fans.

---

## Reference Implementation

| Class/Method | Description |
|--------------|-------------|
| `EcSmartFanControl` | Core control loop class |
| `EcSmartFanControl.Start()` | Start the 1-second timer |
| `EcSmartFanControl.Stop()` | Stop and dispose the timer |
| `EcSmartFanControl.UpdateCurve()` | Update active curve and temps |
| `EcSmartFanControl.BuildTempUpTable()` | Build temp up table with fixed thresholds |
| `EcSmartFanControl.BuildTempDownTable()` | Build temp down table with hysteresis |
| `EcSmartFanControl.BuildFlatDutyTable()` | Build flat duty table |
| `EcSmartFanControl.EvaluateCurve()` | Interpolate curve at temperature |
| `UniwillECService.ApplySmartFanCurve()` | Write tables to EC and toggle enable |
| `FanControlSettings.EcSmartFanMode` | Boolean toggle |
| `FanControlSettings.EcSmartFanTempSource` | "cpu", "gpu", or "max" |

### Configuration

| Property | Default | Description |
|----------|---------|-------------|
| `PollIntervalMs` | 1000 | Timer interval in milliseconds |
| `MaxDutyChangePerCycle` | 20 | Max duty change per tick (0-200 scale) |
| `HysteresisDeadzone` | 2.0 | Min °C change before re-evaluating curve |
| `CriticalTemperature` | 92.0 | Force 100% duty above this (°C) |
| `WarningTemperature` | 87.0 | Force at least 80% duty above this (°C) |

---

## See Also

- [Uniwill EC Fan Control](uniwill-ec-fan-control.md) — EC register map, handshake, and normal curve application.
- MechControl `LinkedFanController.cs` — decompiled reference implementation cited by the original research notes; not included in this repository.
- MechControl `FanControl.cs` — decompiled `ApplyCurves()` / `WriteCurve()` reference cited by the original research notes; not included in this repository.

[Back to documentation index](../README.md)
