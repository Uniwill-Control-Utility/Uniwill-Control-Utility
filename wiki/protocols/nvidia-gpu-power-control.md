# NVIDIA GPU Power Control (Dynamic Boost / cTGP)

> **Baseline and evidence:** This reference is aligned with `NvTuning`, `UniwillECService`, `PresetProfileApplier`, and `SkuData/skus.json` at `eea723e`. It describes the implemented ordering and EC fields; it does not establish behavior for every GPU/firmware combination.

## Overview

Universal x86 Tuning Utility supports NVIDIA GPU power limit control on Schenker/XMG laptops (XMG Neo 16 A25 / A26) through direct EC (Embedded Controller) register writes. This allows raising the GPU TGP (Total Graphics Power) from the default 150W up to 175W via Dynamic Boost offset, and lowering it back for power-efficient profiles.

## How It Works

GPU power control on these laptops operates **exclusively through EC registers** — not through NVML, NvAPI, or any NVIDIA driver API:

- **NVML** (`nvmlDeviceSetPowerManagementLimit`) returns `NOT_SUPPORTED` (error 3) on all laptop GPUs
- **NvAPI** has no GPU power control functions — the `GPUInfoDLL.dll` from XMG Control Center uses only read-only NvAPI calls (e.g., `NvAPI_GPU_GetThermalSettings`, `NvAPI_GPU_GetDynamicPstatesInfo`)
- The NVIDIA driver on laptops reads power limits from EC registers that the platform firmware exposes

The control path is:

```
UXTU → UniwillECService → EC registers → NVIDIA driver (reads on next polling cycle)
```

## EC Register Map

Register addresses from tuxedo-drivers (`uniwill_interfaces.h`) and validated against XMG Control Center behavior:

| Register | Name | Purpose | Valid Values |
|----------|------|---------|--------------|
| `0x0727` | CUSTOM_PROFILE | Enable custom profile mode (prerequisite) | Bit 6 (`0x40`) must be set |
| `0x0741` | GPU power mgmt enable | Enable GPU power management | `0x01` |
| `0x0743` | CTGP_DB_ENABLE | **Enable GPU power control features** | `0x07` (GENERAL\|DB\|CTGP) |
| `0x0744` | CTGP_OFFSET | Configurable TGP offset | 0-255 (XMG CC uses 55/0x37) |
| `0x0745` | TPP_OFFSET | Total Power Package offset | 0-255 (255 = max) |
| `0x0746` | DB_OFFSET | Dynamic Boost offset (watts) | 0-25 |

### Enable Register (0x0743) Bit Definitions

From tuxedo-drivers `uniwill_interfaces.h`:

```c
#define UW_EC_REG_CTGP_DB_ENABLE   0x0743
#define UW_EC_CTGP_DB_ENABLE_GENERAL  0x01  // General enable
#define UW_EC_CTGP_DB_ENABLE_DB       0x02  // Dynamic Boost enable
#define UW_EC_CTGP_DB_ENABLE_CTGP     0x04  // Configurable TGP enable
```

All three bits must be set (`0x07`) for the NVIDIA driver to recognize EC power limits. **Writing the offset registers without setting `0x0743` has no effect** — the EC accepts the writes but does not apply them.

## Write Sequence

### Activating GPU Power Control (e.g., Turbo profile — 175W)

```
1. 0x0727 |= 0x40          // Enable custom profile mode
2. 0x0741 = 0x01           // Enable GPU power management
3. 0x0743 = 0x07           // Enable GENERAL + DB + CTGP features
4. 0x0744 = 0x37 (55)      // cTGP offset
5. 0x0745 = 0xFF (255)     // TPP offset (max)
6. 0x0746 = 0x19 (25)      // Dynamic Boost = +25W (150W base + 25W = 175W)
```

Each write is followed by a ~50ms delay to ensure the EC processes the value.

### Deactivating GPU Power Control (e.g., Gaming profile — 150W)

```
1. 0x0727 |= 0x40          // Enable custom profile mode
2. 0x0741 = 0x01           // Enable GPU power management
3. 0x0743 = 0x07           // Enable features (required even for deactivation)
4. 0x0744 = 0x37 (55)      // cTGP offset (unchanged)
5. 0x0745 = 0x00           // TPP offset = 0
6. 0x0746 = 0x00           // Dynamic Boost = 0W (150W base)
```

## Implementation

### Code Locations

| File | Method | Purpose |
|------|--------|---------|
| `Services/UniwillECService.cs` | `ActivateGpuPowerControl(int dbOffset)` | Write EC register sequence |
| `Services/UniwillECService.cs` | `DeactivateGpuPowerControl()` | Reset to defaults |
| `Services/UniwillECService.cs` | `GetTccOffset()` | Read current DB offset |
| `Scripts/GPUs/NVIDIA/NvTuning.cs` | `SetPowerLimit(int totalWatts, int dbOffset)` | Public API entry point |

### Preset Integration

GPU power control is triggered through the `--NVIDIA-Clocks` translate string parameter:

```
--NVIDIA-Clocks=0-0-0-175-25
```

The 4th field (175) is the target TGP in watts, the 5th field (25) is the Dynamic Boost offset. The `RyzenAdj_To_UXTU.TranslateCore()` method parses this and calls `NvTuning.SetPowerLimit(175, dbOffset: 25)`.

## Discovery Notes

### What Didn't Work

1. **NVML** — `nvmlDeviceSetPowerManagementLimit` returns `NOT_SUPPORTED` (error 3) on all laptop GPUs
2. **NvAPI_GPU_SetDynamicPstatesEnabledEx** — Interface ID `0xB9306D9B` was initially believed to be this function. Binary analysis of `GPUInfoDLL.dll` revealed it is actually `NvAPI_GPU_GetThermalSettings` (read-only). GPUInfoDLL.dll uses only read-only NvAPI functions.
3. **EC writes without `0x0743`** — Writing offset registers (`0x0744`-`0x0746`) without first setting the enable register (`0x0743 = 0x07`) has no effect. The EC accepts the writes but the NVIDIA driver never sees the new limits.

### What Made It Work

The missing piece was **register `0x0743`** — discovered in the tuxedo-drivers Linux kernel source (`src/uniwill_interfaces.h`). The tuxedo-drivers kernel module (`tuxedo_nb02_nvidia_power_ctrl.c`) writes this register to enable the GPU power control feature before writing the offset registers. On Linux, the NVIDIA driver polls the EC autonomously. On Windows, the driver appears to read the EC values when the enable register is set.

### XMG Control Center Architecture

XMG CC (`GCUService.exe`) uses a kernel driver (`ACPIDriverDll.dll`) with custom IOCTLs (`IOCTL_GPD_ACPI_ECWRITE`, `IOCTL_GPD_ACPI_SMAPCTABLE`) for GPU power control. The EC register sequence is the same as tuxedo-drivers. XMG CC additionally writes ACPI/SMM tables via the Smart APC Table IOCTL. The original research notes report that the EC-only path worked on Windows; this repository does not contain a versioned run record across GPU/firmware combinations.

## References

- tuxedo-drivers: [`src/uniwill_interfaces.h`](https://gitlab.com/tuxedocomputers/development/packages/tuxedo-drivers/-/blob/main/src/uniwill_interfaces.h)
- tuxedo-drivers: [`src/tuxedo_nb02_nvidia_power_ctrl/`](https://gitlab.com/tuxedocomputers/development/packages/tuxedo-drivers/-/tree/main/src/tuxedo_nb02_nvidia_power_ctrl)
- XMG Control Center: `C:\Program Files\OEM\Control Center\AiStoneService\MyControlCenter\`

[Back to documentation index](../README.md)
