# Project Overview

## Product purpose

UCU is a hardware-control and telemetry application focused on Uniwill/Tongfang laptop platforms. The implemented surfaces expose performance profiles, cooling, lighting, battery behavior, peripherals, telemetry, and game-aware automation.

The current product is a single Windows desktop process. It owns the UI, background monitoring loops, profile state, hardware sessions, persistence, update checks, and notification-area controls.

## Historical context

The Git history begins on 2022-05-25 as the project that became Universal x86 Tuning Utility. Commit `f7b1d82` rebranded the application to Uniwill Control Utility on 2026-08-31. The present code still carries inherited UXTU abstractions and names alongside newer Uniwill-specific services.

The repository contains work from several identities, dominated by James Craddock-Jones/JamesCJ60 and jgudec. Commit-email variants make raw author counts unsuitable as a contributor count without identity normalization.

## Primary capabilities

- CPU profile and tuning paths for AMD Ryzen and Intel processors.
- NVIDIA laptop-GPU power, Dynamic Boost, clock-offset, and V-F curve control.
- Uniwill EC fan curves, direct fan modes, temperature/RPM readings, battery charge modes, keyboard and light-bar operations.
- ITE 8291 global and per-key keyboard lighting.
- MiniLED local-dimming control through the OEM WMI interface.
- Flydigi BS1 over BLE and BS2/BS2 Pro/BS3/BS3 Pro over HID.
- LCT/XMG Hydro UI watercooler control over BLE.
- Built-in and custom performance profiles.
- Per-game adaptive profiles spanning CPU, GPU, cooling, keyboard, and light bar.
- AC/DC and resume automations.
- Hardware dashboard and configurable in-game overlay.
- Notification-area controls for common profiles and devices.

## Architectural character

UCU is a layered monolith with partial MVVM adoption:

1. WPF pages and windows provide most interaction logic.
2. View models are used for the main window, dashboard, settings, and overlay, but many large pages retain substantial code-behind.
3. Singleton services own long-lived hardware connections and coordination state.
4. Static helpers and static state retain inherited tuning and system-information behavior.
5. Hardware access is implemented through managed packages, P/Invoke, WMI, WinRT BLE, HID, and bundled native modules.
6. User state is split across .NET application settings and several JSON stores.

This structure makes the runtime straightforward to package as one application, but also makes page lifecycle, background loops, static state, and hardware teardown tightly coupled. See the [current-state assessment](current-state-assessment.md).

## Repository boundaries

The solution contains one application project:

```text
Uniwill Control Utility.sln
└── Uniwill Control Utility/Uniwill Control Utility.csproj
```

There are no separate class-library, integration-test, unit-test, installer, or packaging projects in the current solution. Native libraries and helper executables are committed inside the application directory and copied into publish output by the project file.

## External context

Source comments and protocol documents reference UXTU, PawnIO/ZenStates, THRM, watercooler-manager, UCC, tuxedo-drivers, MechControl, XMG Control Center, NVAPI/NVML, LibreHardwareMonitor, and RTSS. These references explain implementation lineage; they are not evidence that every referenced device path has been exercised in the current revision.

## Repository identity transition

The clone origin is the `Uniwill-Control-Utility/Uniwill-Control-Utility` organization repository. Some runtime and README links still point to `jgudec/Uniwill-Control-Utility`, including update checks and release links. Release ownership must be decided before publication or reliance on automatic update discovery.

[Back to documentation index](../README.md)
