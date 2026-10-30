# Codebase Map

## Top-level layout

```text
.
├── .github/                  CI (workflow validating build/pack)
├── build/                    pack.ps1, uninstall-shim/, updater-shim/ (framework-csc shims)
├── docs/                     Protocol research (ACPI dumps, EC notes) + EC probe tools
│                             (FanDiagnostic, BatteryDiagnostic: read-only, run elevated)
├── screenshots/              README screenshots
├── wiki/                     Maintainer and contributor documentation
├── UCU.Core/                 Pure domain: EC encodings, validation, GuardianPolicy,
│                             PresetArbiterPolicy, EcWriteGatePolicy
├── UCU.Contracts/            Verbs, envelope, DTOs, frame codec, capability interfaces, JSON context
├── UCU.Client/               EngineClient RPC + capability proxies (the app's only EC path)
├── UCU.Hardware/             EcEngine + IEcTransport (\\.\ACPIDriver); engine-only assembly
├── UCU.Engine/               The UCUEngine service (pipe server, dispatcher, queues, gate,
│                             raw-write guard, watchdog, AppSupervision, AppSessionLauncher,
│                             EngineInstaller)
├── UCU.*.Tests/              Core/Contracts/Hardware/Engine test projects
├── Uniwill Control Utility/  WPF UI (asInvoker)
│   ├── Assets/               Images, PawnIO modules, RTSS and stress-test payloads
│   ├── Helpers/              WPF converters and behaviors
│   ├── Localization/         22 embedded translation JSON files
│   ├── Models/               Settings, protocol frames, curves, SKU and device models
│   ├── Properties/           Generated .NET user settings
│   ├── Scripts/              CPU/GPU/system backends, ElevatedProcess, misc helpers
│   ├── Services/             Device sessions, EC proxies/telemetry, engine lifecycle, profiles
│   ├── SkuData/              Model-specific profile database
│   ├── ViewModels/           MVVM state for selected surfaces
│   ├── Views/                WPF controls, pages, windows and overlay
│   ├── App.xaml(.cs)         Process composition and lifecycle
│   └── Uniwill Control Utility.csproj
├── Uniwill Control Utility.Tests/
├── LICENSE
├── README.md
└── Uniwill Control Utility.sln
```

`UCU.Guardian` (the user-mode watchdog) and `UCU.Bridge` (the dev in-process EC bridge) were deleted when the engine service took over supervision and became the single EC path; the uninstall shim still cleans up their leftovers.

## Entry and composition files

| File | Role |
| --- | --- |
| `App.xaml` | WPF resources, startup/exit exception wiring. |
| `App.xaml.cs` | Logging, prerequisites, host/DI composition (RPC proxies for EC capabilities), single-instance, detection, task migration, shutdown. |
| `app.manifest` | **asInvoker** execution level, Windows compatibility, Per-Monitor DPI, long paths. |
| `UCU.Engine\Program.cs` | Engine entry: service host, `--console`, `--install-engine`, `--uninstall-engine`. |
| `UCU.Engine\EngineHostFactory.cs` | Service graph shared by installed service, console runs and in-process tests. |
| `ApplicationHostService.cs` | Creates and activates the main window when the Generic Host starts. |
| `MainWindow.xaml(.cs)` | Navigation shell, tray, power/resume handling, startup application of persisted state. |

## Engine file guide

| File | Purpose |
| --- | --- |
| `EnginePipeServer.cs` | Accepts pipe connections, reads/writes length-prefixed frames. |
| `EngineDispatcher.cs` | Verb lookup, protocol-version check, exception→error-code mapping. |
| `EngineDomainQueues.cs`, `SerialExecutor.cs` | Per-domain FIFO executors, depth/timeout limits. |
| `EcEngineGate.cs` | EC engine init/availability, `\\.\ACPIDriver` handle lifetime. |
| `EcRawWriteGuard.cs` | Raw-verb chokepoint: address/GUID allowlist over the raw register + NVRAM write verbs (`EcWriteGatePolicy`). |
| `Verbs/*.cs` | The verb handlers per domain (fan, power, mux, keyboard, battery, telemetry, engine). |
| `EngineWatchdog.cs` | Fail-fast on wedged queues. |
| `AppSupervision.cs`, `AppSessionLauncher.cs` | Crash relaunch of the app into the user session. |
| `EngineInstaller.cs` | `sc` install/uninstall/self-heal passes, service DACL. |
| `PipeSecurityBuilder.cs` | Pipe DACL SDDL. |
| `CpuPlatformDetector.cs`, `EnginePaths.cs` | AMD/Intel detection, log/flag paths. |

## App service groups

| Group | Files | Purpose |
| --- | --- | --- |
| EC via engine | `EcTelemetryService`, `EngineLifecycle`, `EcPlatformResolver`, `EcDriverDetectionService`, `EcSmartFanControl` | Telemetry cache over RPC proxies, engine install/start, platform identity, driver prerequisite, smart-fan loop (writes via capabilities). |
| Platform hardware | `HardwareMonitoringService`, `GpuInventoryService`, `SkuResolver`, `LaptopDetector` | Telemetry leases and identity (app-side). |
| Peripheral sessions | `KeyboardHidService`, `FlydigiCoolerHidService`, `FlydigiCoolerBleService`, `WaterCoolerService`/`HydroUIService` | Transport lifecycle and commands. |
| Smart control | `EcSmartFanControl`, `FlydigiSmartControl`, `FlydigiBs1SmartControl` | Temperature-driven periodic control. |
| Profiles | `PresetArbiter`, `PresetProfileApplier`, `AdaptivePresetManager`, `PresetBackupService`, `DeviceApplier` | Intent arbitration (serialization, boot grace, Adaptive ownership, debounce), profile storage, translation, application, override, import/export. |
| Overlay/performance | `OverlayManagerService`, `OverlaySettingsStore`, `Performance/*` | Overlay lifecycle, telemetry and FPS sources. |
| App support | `LocalizationService`, `VelopackUpdateService`, `PawnIoDetectionService`, `ScheduledTaskService`, `LastAppliedSettingsService`, `Scripts/Misc/ElevatedProcess` | Translation, updates, prerequisites, autostart task, last-applied state, per-operation elevation. |

## Backend scripts (app-side)

- `Scripts/AMD Backend/` — PawnIO modules + Ryzen SMU mailbox.
- `Scripts/Intel Backend/` — PawnIO modules, MSR/MCHBAR tuning.
- `Scripts/GPUs/NVIDIA/` — MUX detection, NVAPI/NVML, power/clock and V-F curve logic.
- `Scripts/Misc/` — logging, cached system data, WMI discovery, display operations, `HardwareManagement` (elevated PnP flows), `NvramAccess` (NVRAM routes: engine-first, local fallback with the 50-series door handshake), notifications.
- `RyzenAdj_To_UCU.cs` — shared command interpreter for AMD/Intel/NVIDIA/display/Windows-power options.

## Presentation surfaces

| Page/window | Main responsibility |
| --- | --- |
| Dashboard | Device identity and hardware snapshots. |
| Presets | Built-in/custom CPU and GPU profiles, V-F curves, backup/import. |
| Adaptive | Per-game profiles and selected-domain overrides. |
| Automations | AC, battery and resume profile selection. |
| Fan Control | EC curves, turbo, split/unified modes, smart fan mode. |
| Keyboard / Light Bar | HID per-key editor + EC effects. |
| Battery | Charge modes (engine verbs) and telemetry. |
| Flydigi Cooler / Watercooler | HID/BLE devices, fan/RGB/curve control. |
| Overlay Settings | Overlay metrics, appearance, placement, hotkeys. |
| Settings | Preferences, update controls, autostart, backup/import, diagnostics, system details. |

## Size and concentration

The app projects contain about 60,800 C# lines and 14,500 XAML lines; the engine-era libraries add 5,700 and the test projects 4,300. Across app and library code, 35 source files exceed 500 lines, 21 exceed 1,000, 13 exceed 1,500, and 7 exceed 2,000.

| File | Approximate lines |
| --- | ---: |
| `Views/Pages/Adaptive.xaml.cs` | 4,366 |
| `Views/Windows/MainWindow.xaml.cs` | 2,909 |
| `Views/Pages/FlydigiCooler.xaml.cs` | 2,892 |
| `Views/Pages/Keyboard.xaml.cs` | 2,708 |
| `Services/FlydigiCoolerHidService.cs` | 2,418 |
| `Services/HydroUIService.cs` | 2,307 |
| `UCU.Hardware/Ec/EcEngine.cs` | 2,118 |
| `Views/Pages/Presets.xaml.cs` | 1,971 |
| `Views/Pages/FanControl.xaml.cs` | 1,970 |
| `Services/KeyboardHidService.cs` | 1,962 |

## Generated and bundled content

- `Properties/Settings.Designer.cs` is generated from `Settings.settings` (the legacy `GuardianEnabled` entry is kept for schema stability and cleared by `App.RetireGuardianTask`).
- Localization JSON files are embedded resources; `SkuData/skus.json` is copied to output.
- PawnIO modules, native DLLs, the RTSS bridge and the AVX2 stress helper ship in the app root (the RyzenSmu loader resolves the `.bin` modules there).
- The engine's payload closure is staged in `Service\` (see [Dependency inventory](dependency-inventory.md)).

[Back to documentation index](../README.md)
