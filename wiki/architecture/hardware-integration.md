# Hardware Integration

## Integration map

The processes split by privilege: the **engine service** owns the Embedded Controller (the only operation that made the old build require elevation); the **UI process** owns everything that runs at user privilege.

```mermaid
flowchart LR
    subgraph Engine[UCUEngine service — LocalSystem]
        EC[EcEngine] --> T[EcDriverTransport]
        T --> DRV[\\.\ACPIDriver]
        DRV --> ECHW[Uniwill embedded controller<br/>UWACPIDriver.sys on INOU0000]
    end
    App[UI app — asInvoker] -->|RPC verbs| Engine
    App --> Pawn[PawnIO device]
    Pawn --> SMU[AMD SMU]
    Pawn --> MSR[Intel MSR/MCHBAR]
    App --> NV[NvAPI / NVML]
    NV --> GPU[NVIDIA GPU driver]
    App --> HID[Windows HID]
    HID --> ITE[ITE 8291 keyboard]
    HID --> Fly[Flydigi BS2+]
    App --> GATT[Windows BLE/GATT]
    GATT --> BS1[Flydigi BS1]
    GATT --> LCT[LCT Hydro UI]
    App --> WMI[WMI]
    WMI --> OEM[System identity / MiniLED interface]
    App --> LHM[LibreHardwareMonitor]
    LHM --> Sensors[CPU, GPU, memory, battery sensors]
    LHM -.->|CPU temperature needs| Pawn
```

## Embedded controller (engine-owned)

`UCU.Hardware\Ec\EcEngine.cs` is the register-level EC implementation: the activation handshake, fan tables and PWM, turbo/boost fields, GPU power control and Dynamic Boost fields, platform power values, keyboard/light-bar operations, battery charge mode, and telemetry reads. It talks to `UWACPIDriver.sys` through the `\\.\ACPIDriver` device IOCTLs via `EcDriverTransport` (`IEcTransport`), which the hardware test suite replaces with a fake.

The engine exposes the EC as a **verb surface** — the domain verbs `fan`, `power`, `mux`, `keyboard`, `battery`, `telemetry` and the engine-management verbs — and the UI consumes it through the capability interfaces (`IFanCapability`, `IPowerCapability`, `IGpuMuxCapability`, `IKeyboardCapability`, `IBatteryCapability`, `IEcTelemetryCapability`, `IEngineCapability`) implemented as RPC proxies. The scope started EC-only, with SMU, Intel MSR and NVAPI left app-side; the privilege-gated rows have since moved one at a time, each for a documented reason: the sensor snapshot, the PnP device rows, NVRAM, the SMU/Intel mailboxes, MiniLED, the OEM takeover, and now the NVAPI **writes** (`gpu.apply_vf_curve`, `gpu.set_drs_preference` — an unelevated NVAPI clock/voltage write is refused with `NVAPI_INVALID_USER_PRIVILEGE`, so only the reads stay app-side, where they work unelevated and feed the curve editor).

Ordering and fault isolation are structural:

- One `SerialExecutor` FIFO queue per domain — a fan-curve write is ordered against every other fan write, and a slow write parks only its domain.
- `EcEngineGate` opens the driver on first use; when `UWACPIDriver.sys` is absent, every verb answers `HardwareUnavailable` and the UI disables EC features. The app's `EcDriverDetectionService` surfaces the prerequisite (driver file presence) up front.
- The engine watchdog fails fast on a queue wedged beyond 2 minutes; SCM recovery restarts the service.

### Write validation and the raw-write gate

Every EC write passes a server-side check before the transport moves a byte, at two layers:

- **Semantic verbs validate their values.** The `UCU.Core` codec validators (`EcDutyCodec.ValidateDutyPercent/Table`, `EcTemperatureCodec.ValidateTemperatureSteps`, `EcPowerCodec.ValidatePowerLimit/TccOffset/PerformanceMode/StockMode`, `EcBatteryCodec.ValidateMode`, `EcKeyboardCodec.ValidateColor/Brightness`) are called by the `EcEngine` setters **before** any handshake or register sequence, so a bad preset is refused with `InvalidValue` and the EC keeps its previous bytes — the engine reports, it does not clamp.
- **Raw verbs pass an address/GUID allowlist.** `fan.write_register_blocks`, `telemetry.write_register` and `device.set_nvram_value` carry caller-chosen targets, and the pipe's DACL admits every interactive user, so `EcRawWriteGuard` (engine) enforces the pure policy `UCU.Core\Ec\EcWriteGatePolicy.cs` over the whole parameter set before the first write: the fan-table block (0x0F00–0x0F5F), the writeable PWM registers (0x1804/0x1809 families), the keyboard RGB set (0x0767–0x076B, 0x078C) and the battery charge mode (0x07A6) are allowed; the power limits, mode index, magic registers, OEM-mode flag, mux and fan-control registers are not reachable at all (they have semantic verbs that validate first). A block straddling an allow boundary is refused whole; a mixed plan is refused before its first legal block. NVRAM writes are gated to the `OemMagicVariable` GUID family. Refusals surface as `BadRequest` with the EC untouched. Every write of that entry — raw verb, AP-menu pair-write, and the app's local fallback — lands the 50-series gatekeeper handshake first: unlock `OemMagicDoor` (one byte, 0x01) immediately before the entry write, refuse the entry write when the door refuses (no blind write), never create the door on a firmware that has no such variable (`FwNvramService.UnlockMagicDoor`, `NvramAccess.UnlockMagicDoorLocal`, golden-order pinned in `FwNvramServiceMagicDoorTests` + parity tests).

`--gate=report-only` (engine CLI, never set by the installer) logs a refusal and lets the raw write through — the field-debugging escape valve for an allowlist that turns out too strict on a chassis. The codec value checks are never report-only.

CPU platform (AMD vs Intel) matters for the GPU-mux byte encodings: the engine reads the boot processor's vendor identifier from `HARDWARE\DESCRIPTION\System\CentralProcessor\0`, the app resolves the same fact from `Family.TYPE` (CPUID + WMI) for its own encodings.

Register and table formats are documented in [Uniwill EC fan control](../protocols/uniwill-ec-fan-control.md), [Uniwill EC battery charge control](../protocols/uniwill-ec-battery-control.md) and [NVIDIA GPU power control](../protocols/nvidia-gpu-power-control.md).

The battery rows are read-mostly by evidence: the chassis' charge mode (`0x07A6`, field bits 3-5) is writable through `battery.set_mode` with the codec validator in front; the charge-gate bit (`0x0742!bit4`, multiplexed with the fan-turbo support flag), the platform-override byte (`0x0770`) and the charge thresholds (**derived from the mode**, because the field probe refuted the sibling chassis' threshold registers on this family) surface through read verbs and the Battery page's 5 s poll. A pre-U-103 engine answers the new verbs with `UnsupportedVerb` and the rows hide; nothing writes an address whose semantics this chassis has not demonstrated.

The power-contract truth path (U-107) is the guardian's drift oracle: `power.get_contract_truth` reads the GPU cTGP/DB register set (`0x0743` enable / `0x0744` cTGP / `0x0745` pool / `0x0746` DB, plus `0x0727!bit6` custom-profile) and the CPU PL pair (`0x0783`/`0x0784`) in one serialized power-domain pass. The truth is the EC's own bytes because laptop NVML answers NOT_SUPPORTED for power limits (the app substitutes the default); `power.heal_contract` replays the preset pipeline's own activation sequence (`ActivateGpuPowerControl` / `ApplyCpuPowerLimits`, validators intact) behind per-half flags — deciding **what** to heal is the app's pure policy (`UCU.Core\Supervision\PowerContractPolicy.cs`, whose expected bytes mirror the activation writes, parity pinned on both sides in `PowerContractPolicyTests` + `EcEngineGpuPowerTruthTests`).

The device-switch verbs (U-108) ride the shared-byte discipline. AC recovery is `0x0726!bit3` — the **same byte** the activation handshake RMWs at bit 7 (and the open-revo registry names bit 7's neighbourhood for the Q-key LED white bit, `MASK_QKEY_WHITE_1830`), so every writer on the address is bit-scoped (`EcSwitchCodec.AcRecoveryByte` returns every other bit unchanged, pinned across all 256 input bytes) and the power-domain queue serializes them; `power.set_ac_recovery` answers with the byte the EC reads back. Fn lock is the plain RMW `0x074E!bit4` (open-revo's `0x11`/`0x01` encoding, field-verified writable here). Win-lock has no state write at all: the truth is the status byte `0x0768!bit0` and the act **fires** `0x0767!bit0` (the trigger byte the lighting effects share, ordered on the keyboard domain), which TOGGLES the lock, self-consumes, and debounces re-fires (lab box 2026-10-31: flip in ~0.1 s, a re-fire seconds after the previous one swallowed). So the engine fires ONLY on truth mismatch — a fire-on-match toggles away from the target — exactly once per command, verifies by re-read, and `keyboard.set_win_lock` reports the **observed** lock state. The state is EC-owned across app restarts, so the Settings rows re-read truth at construction and never re-apply; a pre-U-108 engine answers `UnsupportedVerb` and the rows hide (`docs\acpi\SwitchDiagnostic` / `WinLockProbe` fix the semantics; report: `wiki\runtime-tests\0.9.7-u108-device-switch-probe.md`).

## AMD CPU path (app-side)

The AMD backend loads PawnIO modules from the app's `Assets/AMD/PawnIO` (the `.bin` files stay in the app root where the RyzenSmu loader resolves them) and uses a mailbox abstraction to talk to the Ryzen SMU. `Family.setCpuFamily()` selects the processor family and command mapping. Profile commands apply sustained/fast power limits, thermal limits, Curve Optimizer offsets and other inherited UXTU operations. The shared Ryzen access layer is deinitialized on shutdown.

## Intel CPU path (app-side)

The Intel backend loads `IntelMSR.bin` and `IntelMCHBAR.bin` through PawnIO and implements power-limit and voltage controls. CPU voltage controls may be unavailable when firmware locks the interface even if the family is recognized.

## NVIDIA GPU path (app-side reads, engine-side writes)

`NvTuning` combines the managed NvAPI wrapper, NVML P/Invoke, private NVAPI calls for V-F curve access, and — for laptop power ceilings and Dynamic Boost coordination — the EC fields, which now travel as `power`-domain RPC verbs. Application order matters: power ceilings affect whether the driver accepts a curve, so custom-profile logic sets power before and after curve changes.

**Reads are app-side, writes are not.** NVAPI's clock and voltage setters are privilege-gated: an `asInvoker` caller is answered `0xFFFFFF77` = `NVAPI_INVALID_USER_PRIVILEGE` and the write never lands (field, 2026-11-10: every V-F curve apply after phase 4 returned it, so presets kept showing offsets the GPU never had — MSI Afterburner applies the same curve because it runs elevated). The V-F curve apply (`0x0733E009` clock-boost table), the core/memory pstate write (`SetPerformanceStates20`) and the DRS preference (`0x8B34C9A2`) therefore travel as `gpu.apply_vf_curve` / `gpu.set_clocks` / `gpu.set_drs_preference` through `UCU.Hardware\Gpu\NvApiGpuTuning.cs`, whose LocalSystem token the driver accepts; the preset pipeline (startup, tray, Adaptive, Automations) and the Presets page reach them through `IGpuTuningCapability`. The pstate write keeps its honesty rule engine-side: a driver that declares `PerformanceStatesInfo.IsReadOnly` is reported as a refusal, never as success. The app's local NVAPI writes stay as the fallback for a machine whose engine is not answering (and are what an elevated dev run uses), and a refusal is logged at Warning naming the privilege answer instead of a bare hex status. Curve *reads* (`TryGetVfCurve`) stay in the app process — they answer unelevated, and the editor polls them live.

Per-operation admin flows (dGPU PnP hotswap, webcam PnP) elevate through `Scripts/Misc\ElevatedProcess` (`runas`, hidden, exit-code verified) rather than running the whole app elevated.

## ITE keyboard HID (app-side)

`KeyboardHidService` owns the HID transport (discovery, feature reports, per-key direct output reports, settle delays, BGR order, BIOS persistence). The EC-side keyboard/light-bar operations are `keyboard`-domain verbs. See [ITE 8291 keyboard HID protocol](../protocols/ite-8291-keyboard-hid-protocol.md).

## Flydigi HID devices (app-side)

`FlydigiCoolerHidService` supports the BS2-family USB protocol: enumeration, input-report parsing, fan gear/direct RPM/curve/RGB writes, power and arrival notifications, health checks with reconnect backoff, and settings at `%APPDATA%/UCU/bs2pro_settings.json`. Realtime RPM commands need a keepalive re-send every ≤5 s (firmware leaves realtime mode otherwise).

## Flydigi BS1 BLE (app-side)

`FlydigiCoolerBleService` uses WinRT enumeration and GATT: discovery, characteristic discovery, session state, heartbeat writes (~2.5 s, the device expects traffic near every 3 s), notification parsing, OS arrival/removal monitoring. Settings: `%APPDATA%/UCU/bs1_settings.json`.

## Hydro UI / LCT BLE (app-side)

`WaterCoolerService` uses the Nordic UART GATT service: scanning, connection, pump/fan/RGB commands, smart-mode scheduling, standby/resume, reconnection. Settings: `%APPDATA%/UCU/watercooler_settings.json`.

## Hardware monitoring (app-side)

`HardwareMonitoringService` exposes category leases over LibreHardwareMonitor; Dashboard holds a persistent CPU/GPU/memory/battery lease, temporary consumers get `IDisposable` leases.

`EcTelemetryService` is the EC-telemetry counterpart: it polls fan duty, temperatures and RPM through `IEcTelemetryCapability` (the `telemetry` queue keeps these fast reads unblocked behind writes in other domains) and feeds the tray, overlay and dashboard.

**CPU temperature has two sources, and which one answers is a privilege question.** The CPU's own sensor (LHM's MSR-derived "Core Average"/"CPU Package") needs the PawnIO driver, and PawnIO's device ACL is System + Administrators (its INF ships `D:P(A;;GA;;;SY)(A;;GA;;;BA)`), so an asInvoker process - the app in a per-machine install - gets access denied and no CPU sensor at all. The fallback is the EC's own reading (register `0x043E`, the sensor the EC fan curve runs on), which reaches the app over `telemetry.get_cpu_temperature` with no privilege: the LocalSystem engine opens `\\.\ACPIDriver`, and the pipe DACL admits the interactive user.

`HardwareMonitoringService.ApplyCpuTemperatureSource` applies the precedence: the CPU sensor wins when it answered, the EC replaces it when it did not, and both absent leaves `0` with source `None`, which the UI renders as `--`. The source is recorded on the snapshot (`CpuTemperatureSource`) and the dashboard card's tooltip names it, because the two sensors are not the same number - the EC reading is quantized and follows the EC's own polling - so a fallback must never be silent. Consumers of the snapshot (dashboard, tray widget, overlay, Flydigi smart-fan temperature input) all receive the fallback through that one seam; `ReadSensor` consumers (Adaptive mode's per-sensor reads) see only the LHM tree and have no EC fallback.

## FPS monitoring

`FpsMonitoringService` prefers RTSS shared memory, falls back to ETW DXGI/D3D present events, and yields no FPS for protected/unrecognized processes; process metadata is cached because enumeration can be expensive or denied.

## Display and MiniLED

Refresh-rate selection uses Win32 display configuration APIs; MiniLED dimming uses the OEM WMI class/methods with write verification; panel identity is cached at startup.

## Common adapter behavior

- Unsupported or missing devices hide/disable feature surfaces; discovery failures log or raise status events.
- An engine that is stopped, crashed mid-call, or missing answers `EngineException` with a stable code (`HardwareUnavailable`, `Timeout`, `Busy`, `InvalidValue`, `HardwareFault`); pages degrade per feature.
- Reconnect-capable services back off or wait for device-arrival events; resume treats BLE/HID sessions as stale and rebuilds them.
- Persistence loads defaults when a settings file is absent or unreadable.

Wire formats live under [`wiki/protocols`](../protocols/device-communication-protocols.md).

[Back to documentation index](../README.md)
