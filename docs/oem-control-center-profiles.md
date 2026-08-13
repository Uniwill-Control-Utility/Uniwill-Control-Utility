# OEM Control Center Profiles & Fan Curves

> Extracted from `C:\Program Files\OEM\Control Center\AiStoneService\MyControlCenter`
> Control Center v5.57.51.60 (XMG-branded, AiStone/GCUBridge service)

## Platform Identification

| Platform Code | Model | CPU | GPU TGP |
|---|---|---|---|
| **PH6PGEx** | XMG Neo 16 A25 | AMD Ryzen 8040HS (Phoenix) | RTX 5080 Laptop (150W base) |
| **PH6PG0x** | XMG Neo 16 E25 | Intel Core Ultra (standard wattage) | RTX 5080 Laptop |
| **PH6PG0x150W** | XMG Neo 16 E25 (150W) | Intel Core Ultra (150W variant) | RTX 5080 Laptop |
| PH6PG7x | Sibling AMD variant | AMD Ryzen 8040HS | — |

---

## Power Profiles

### Mode-Level Power Limits

Applied via `UserPofiles/Mode{N}_Profile{M}.json`. Each mode maps to a fan table (M1T1–M4T5).

| Mode | CPU PL1 | CPU PL2 | CPU PL4 | AMD SPL | AMD sPPT | AMD tPPT | TCC Target | GPU TGP | GPU DynBoost |
|------|---------|---------|---------|---------|----------|----------|------------|---------|-------------|
| **Gaming (M1)** | 75W | 85W | 85W | 75W | 85W | 85W | 95°C | 150W | 5W |
| **Office (M2)** | 45W | 45W | 45W | 45W | 45W | 45W | 80°C | 95W | N/A |
| **Turbo (M3)** | 145W | 145W | 174W | 145W | 145W | 174W | 95°C | 175W | 5W |
| **Custom (M4)** | 75W | 85W | 85W | 75W | 85W | 85W | 95°C | 150W | N/A |

**Note**: All profiles have `PL1_Enabled: false`, `PL2_Enabled: false`, `OffsetCoreVoltage: 0`, `TccOffsetSwitch: 0` — meaning the values above are the *defaults*; undervolt and custom PL overrides are not enabled by default.

### Model-Specific Table Overrides

Fan table JSON files can override the mode-level power limits:

#### PH6PGEx (AMD A25)
| Table | PL1 | PL2 | CTGP | DB | Notes |
|-------|-----|-----|------|----|-------|
| M1T1 | 45W | 65W | — | — | TCC offset: -5 |
| M1T2 | 55W | 65W | 10 | 15 | TCC offset: -5 |

#### PH6PG0x (Intel E25 standard)
| Table | PL1 | PL2 | CTGP | DB | Notes |
|-------|-----|-----|------|----|-------|
| M1T1 | 35W | 55W | 0 | 25 | TCC offset: -5 |
| M1T2/M1T3 | 45W | 60W | 10 | 25 | PL1_S2: 15W, PL2_S2: 15W, DB2: 20 |

#### PH6PG0x150W (Intel E25 150W)
| Table | PL1 | PL2 | CTGP | DB | Notes |
|-------|-----|-----|------|----|-------|
| M1T1 | 35W | 55W | 0 | 0 | No dynamic boost |
| M1T2/M1T3 | 45W | 55W | 10 | 0 | DB2: 20 |

---

## Fan Curves

### Scale Conversion

OEM JSON uses **0–255 duty scale**. UXTU's `EcFanCurve` uses **0–100% UI scale** which maps to **0–200 EC scale** via `ToEcDutyTable()`.

```
OEM duty (0-255) → percentage = duty / 255 * 100
UXTU percentage → EC duty = percentage * 2
```

### PH6PGEx (XMG Neo 16 A25 — AMD) — Active Model

#### M1T1 — Gaming Profile 1
*Power: PL1=45W, PL2=65W, TCC offset=-5*

**CPU Fan Curve (11 points):**

| Zone | UpT (°C) | DownT (°C) | OEM Duty (0-255) | Duty % | UXTU EC Duty (0-200) |
|------|----------|------------|-------------------|--------|---------------------|
| 0 | 0 | 52 | 0 | 0% | 0 |
| 1 | 54 | 56 | 30 | 12% | 24 |
| 2 | 58 | 59 | 32 | 13% | 25 |
| 3 | 61 | 62 | 36 | 14% | 28 |
| 4 | 64 | 65 | 40 | 16% | 31 |
| 5 | 67 | 68 | 47 | 18% | 37 |
| 6 | 70 | 73 | 55 | 22% | 43 |
| 7 | 75 | 88 | 62 | 24% | 49 |
| 8 | 90 | 93 | 72 | 28% | 56 |
| 9 | 95 | 95 | 84 | 33% | 66 |
| 10 | 97 | 255 | 100 | 39% | 78 |

**GPU Fan Curve (11 points):**

| Zone | UpT (°C) | DownT (°C) | OEM Duty (0-255) | Duty % | UXTU EC Duty (0-200) |
|------|----------|------------|-------------------|--------|---------------------|
| 0 | 0 | 41 | 0 | 0% | 0 |
| 1 | 43 | 46 | 30 | 12% | 24 |
| 2 | 48 | 50 | 34 | 13% | 27 |
| 3 | 52 | 54 | 38 | 15% | 30 |
| 4 | 56 | 58 | 44 | 17% | 34 |
| 5 | 60 | 62 | 52 | 20% | 41 |
| 6 | 64 | 66 | 61 | 24% | 48 |
| 7 | 68 | 86 | 70 | 27% | 55 |
| 8 | 88 | 91 | 86 | 34% | 68 |
| 9 | 93 | 94 | 100 | 39% | 78 |
| 10 | 96 | 255 | 100 | 39% | 78 |

#### M1T2 — Gaming Profile 2 (Aggressive)
*Power: PL1=55W, PL2=65W, TCC offset=-5, CTGP=10, DB=15*

**CPU Fan Curve:**

| Zone | UpT (°C) | DownT (°C) | OEM Duty (0-255) | Duty % | UXTU EC Duty (0-200) |
|------|----------|------------|-------------------|--------|---------------------|
| 0 | 0 | 52 | 0 | 0% | 0 |
| 1 | 54 | 56 | 30 | 12% | 24 |
| 2 | 58 | 59 | 32 | 13% | 25 |
| 3 | 61 | 62 | 36 | 14% | 28 |
| 4 | 64 | 65 | 40 | 16% | 31 |
| 5 | 67 | 68 | 47 | 18% | 37 |
| 6 | 70 | 73 | 55 | 22% | 43 |
| 7 | 75 | 80 | 62 | 24% | 49 |
| 8 | 82 | 82 | 72 | 28% | 56 |
| 9 | 94 | 96 | 84 | 33% | 66 |
| 10 | 98 | 255 | 100 | 39% | 78 |

**GPU Fan Curve (more aggressive — reaches 100/255 at 74°C):**

| Zone | UpT (°C) | DownT (°C) | OEM Duty (0-255) | Duty % | UXTU EC Duty (0-200) |
|------|----------|------------|-------------------|--------|---------------------|
| 0 | 0 | 41 | 0 | 0% | 0 |
| 1 | 43 | 46 | 30 | 12% | 24 |
| 2 | 48 | 50 | 34 | 13% | 27 |
| 3 | 52 | 54 | 38 | 15% | 30 |
| 4 | 56 | 58 | 44 | 17% | 34 |
| 5 | 60 | 62 | 52 | 20% | 41 |
| 6 | 64 | 66 | 61 | 24% | 48 |
| 7 | 68 | 70 | 70 | 27% | 55 |
| 8 | 72 | 72 | 86 | 34% | 68 |
| 9 | 74 | 78 | 100 | 39% | 78 |
| 10 | 80 | 255 | 100 | 39% | 78 |

#### M2T1/M2T2/M2T3 — Office/Quiet (identical)
*Power: PL1=15W, PL2=15W*

**CPU Fan Curve (9 points, very conservative — max 23%):**

| Zone | UpT (°C) | DownT (°C) | OEM Duty (0-255) | Duty % | UXTU EC Duty (0-200) |
|------|----------|------------|-------------------|--------|---------------------|
| 0 | 0 | 37 | 0 | 0% | 0 |
| 1 | 39 | 40 | 20 | 8% | 15 |
| 2 | 42 | 43 | 25 | 10% | 19 |
| 3 | 45 | 46 | 30 | 12% | 23 |
| 4 | 48 | 51 | 38 | 15% | 29 |
| 5 | 53 | 56 | 43 | 17% | 33 |
| 6 | 58 | 61 | 48 | 19% | 38 |
| 7 | 63 | 66 | 53 | 21% | 41 |
| 8 | 68 | 255 | 58 | 23% | 45 |

**GPU Fan Curve (9 points, max 24%):**

| Zone | UpT (°C) | DownT (°C) | OEM Duty (0-255) | Duty % | UXTU EC Duty (0-200) |
|------|----------|------------|-------------------|--------|---------------------|
| 0 | 0 | 40 | 0 | 0% | 0 |
| 1 | 42 | 46 | 20 | 8% | 15 |
| 2 | 48 | 52 | 30 | 12% | 23 |
| 3 | 54 | 58 | 40 | 16% | 31 |
| 4 | 60 | 63 | 45 | 18% | 35 |
| 5 | 65 | 68 | 52 | 20% | 41 |
| 6 | 70 | 73 | 54 | 21% | 43 |
| 7 | 75 | 78 | 57 | 22% | 45 |
| 8 | 80 | 255 | 60 | 24% | 47 |

**Note**: PH6PGEx has **NO M3 (Turbo) fan tables** — only M1 (x2 tables) and M2 (x3 tables).

---

### PH6PG0x (Intel E25 — standard wattage)

#### M1T1
*Power: PL1=35W, PL2=55W, TCC offset=-5, CTGP=0, DB=25*

CPU: 0/255 at 52°C, ramps to 100/255 at 97°C (same shape as PH6PGEx M1T1)

#### M1T2/M1T3
*Power: PL1=45W, PL2=60W, PL1_S2=15W, PL2_S2=15W, CTGP=10, DB=25, CTGP2=0, DB2=20*

CPU: starts at 42°C, reaches 100/255 at 98°C. GPU reaches 100/255 at 84°C.

#### M2T1/M2T2/M2T3
*Power: PL1=15W, PL2=15W*

CPU: 0/255 until 37°C, then 20/255 at 39°C, jumps to 38/255 at 70°C, max 58/255 from 86°C.
GPU: 0/255 until 40°C, max 60/255 from 86°C.

---

### Generic Root-Level Fan Curves

Fallback curves in `UserFanTables/` (not model-specific). Used by all DefaultFanTable presets.

**Generic CPU Curve (11 points):**

| Zone | UpT (°C) | DownT (°C) | OEM Duty (0-255) | Duty % | UXTU EC Duty (0-200) |
|------|----------|------------|-------------------|--------|---------------------|
| 0 | 0 | 46 | 0 | 0% | 0 |
| 1 | 48 | 50 | 30 | 12% | 23 |
| 2 | 52 | 54 | 35 | 14% | 27 |
| 3 | 56 | 58 | 40 | 16% | 31 |
| 4 | 60 | 62 | 50 | 20% | 39 |
| 5 | 64 | 66 | 55 | 22% | 43 |
| 6 | 68 | 70 | 60 | 24% | 47 |
| 7 | 72 | 74 | 70 | 27% | 55 |
| 8 | 76 | 78 | 80 | 31% | 63 |
| 9 | 80 | 82 | 90 | 35% | 71 |
| 10 | 85 | 255 | 100 | 39% | 78 |

**Generic GPU Curve (11 points):**

| Zone | UpT (°C) | DownT (°C) | OEM Duty (0-255) | Duty % | UXTU EC Duty (0-200) |
|------|----------|------------|-------------------|--------|---------------------|
| 0 | 0 | 44 | 0 | 0% | 0 |
| 1 | 46 | 48 | 30 | 12% | 23 |
| 2 | 50 | 52 | 35 | 14% | 27 |
| 3 | 54 | 56 | 40 | 16% | 31 |
| 4 | 58 | 60 | 50 | 20% | 39 |
| 5 | 62 | 64 | 55 | 22% | 43 |
| 6 | 66 | 68 | 60 | 24% | 47 |
| 7 | 70 | 72 | 70 | 27% | 55 |
| 8 | 74 | 76 | 80 | 31% | 63 |
| 9 | 78 | 80 | 90 | 35% | 71 |
| 10 | 81 | 255 | 100 | 39% | 78 |

---

## Comparison: OEM vs UXTU Fan Curves

### CPU Fan Curves — Maximum Duty

| Preset | Max Duty (%) | Notes |
|--------|-------------|-------|
| OEM Gaming (M1T1) | 39% | Very conservative, fan-off until 52°C |
| OEM Gaming Aggressive (M1T2) | 39% | Same max, earlier ramp-up |
| OEM Office (M2T1) | 23% | Extremely quiet, only 9 points |
| UXTU Silent | 58% | Significantly louder than OEM |
| UXTU Balanced | 100% | OEM M1T1 shape, but full range |
| UXTU Performance | 100% | Aggressive ramp |
| UXTU Full Speed | 100% | All zones at 100% |

**Key observation**: OEM Gaming curves max at 39% duty — dramatically quieter than any UXTU preset. The OEM approach relies on the hardware being designed to handle load at low fan speeds. UXTU's presets were designed for generic hardware with unknown thermal capacity.

### GPU Fan Curves — Maximum Duty

| Preset | Max Duty (%) | Fan-off Threshold |
|--------|-------------|-------------------|
| OEM Gaming (M1T1) | 39% | 41°C |
| OEM Gaming Aggressive (M1T2) | 39% | 41°C, reaches max at 74°C |
| OEM Office (M2T1) | 24% | 40°C |
| UXTU Silent | 58% | — |
| UXTU Balanced | 100% | — |

---

## Comparison: OEM vs UXTU Power Limits

### AMD (PH6PGEx)

| Profile | CPU PL1 | CPU PL2 | CPU PL4 | GPU TGP | Notes |
|---------|---------|---------|---------|---------|-------|
| OEM Gaming (M1) | 75W | 85W | 85W | 150W | Mode-level default |
| OEM Gaming M1T1 | 45W | 65W | — | — | Table override |
| OEM Gaming M1T2 | 55W | 65W | — | 160W | Table override + CTGP/DB |
| OEM Office (M2) | 45W | 45W | 45W | 95W | GPU switch OFF |
| OEM Turbo (M3) | 145W | 145W | 174W | 175W | Maximum |
| UXTU Eco | 15W | 25W | — | — | SMU-based |
| UXTU Balanced | 25W | 35W | — | — | SMU-based |
| UXTU Performance | 45W | 55W | — | — | SMU-based |
| UXTU Extreme | 65W | 80W | — | — | SMU-based |

**Key observation**: UXTU's Extreme (65W/80W) is *below* OEM Gaming mode defaults (75W/85W). The OEM Turbo mode (145W/174W) is nearly 2.5× the UXTU Extreme PL1.

### Intel (PH6PG0x)

| Profile | CPU PL1 | CPU PL2 | GPU TGP | GPU DynBoost |
|---------|---------|---------|---------|-------------|
| OEM Gaming M1T1 | 35W | 55W | — | 25W |
| OEM Gaming M1T2 | 45W | 60W | 10W CTGP | 25W |
| OEM Office | 15W | 15W | — | — |

---

## Key Configuration Files

| Path | Purpose |
|------|---------|
| `UserPofiles/MainOption.json` | Active mode, profile indices, FanBoost, TurboGPUOCOffset |
| `UserPofiles/Mode{N}_Profile{M}.json` | Power limits per mode/profile |
| `UserFanTables/PH6PGEx/` | Model-specific fan curves (AMD A25) |
| `UserFanTables/PH6PG0x/` | Model-specific fan curves (Intel E25) |
| `UserFanTables/M1T1.json` — `M4T5.json` | Generic root-level fan tables |
| `UserFanTables/DefaultFanTable_*.json` | Default fan tables (Gaming/Office/Turbo) |
| `LiquidHWOC/LiquidHWOC.json` | HW overclocking (CoreFreqOffset, MemFreqOffset) |
| `LCSavingSettings/LCSavingSettings.json` | Liquid cooler (pump voltage, fan duty, Bluetooth MAC) |
| `GPUPowerSavingSettings/GPUPowerSavingSettings.json` | GPU refresh rate settings |
| `DisplayProfile/DisplayTable.json` | Display color profiles (6 modes) |
| `AppSettings/AppProfileBinding.json` | App-to-profile bindings |

---

## Architecture Notes

### EC Register Map (from `docs/fan-control.md`)

| Register | Meaning |
|----------|---------|
| 0x0783 | PL1_SETTING |
| 0x0784 | PL2_SETTING |
| 0x0785 | PL4_SETTING |
| 0x0786 | TCC_OFFSET |
| 0x07AB | MODE_INDEX |

Accessed via `ACPIDriverDll.dll` (ReadEC/WriteEC with 6ms delay after write).

### UXTU Translate Pipeline Gap

The current `RyzenAdj_To_UXTU.TranslateCore()` has **NO handler for EC register writes**. The AMD SMU path (`SMUCommands.applySettings`) handles SoC-level commands. EC writes (chassis layer) require a separate path through `ACPIDriverDll.dll`.

**Required new command keys**:
- `--ec-pl1=value` → `ACPI write 0x0783`
- `--ec-pl2=value` → `ACPI write 0x0784`
- `--ec-pl4=value` → `ACPI write 0x0785`
- `--ec-tcc-offset=value` → `ACPI write 0x0786`
- `--ec-mode=value` → `ACPI write 0x07AB`

### EC Activation Handshake

Before EC writes can take effect, an 8-step activation handshake (documented in `docs/fan-control.md`) must be completed. This includes setting bits in EC registers 0x726 and 0x727.

### Fan Curve Write Path

Fan curves are written via the RAMFAN2 table format (16-byte duty table, 0-200 EC scale). The existing `EcFanCurve.ToEcDutyTable()` method handles conversion from 0-100% UI scale.

---

## What's NOT in OEM Configs

- **No undervolt/curve optimizer values** — `OffsetCoreVoltage`, `TccOffsetSwitch` all 0
- **No STAPM values** — Not exposed in config files
- **No EC register write patterns** — Handled by compiled GCUBridge.exe / GCUService.exe binaries
- **No Intel-specific overclocking data on AMD machine** — `IntelOverclockingSDK.dll` present but unused on PH6PGEx
- **No Turbo (M3) tables for PH6PGEx** — AMD Neo 16 A25 only has M1 and M2 model-specific tables
