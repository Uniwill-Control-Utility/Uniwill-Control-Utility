# Codebase Map

## Top-level layout

```text
.
├── .github/
│   ├── FUNDING.yml
│   └── ISSUE_TEMPLATE/
├── docs/                       Existing protocol research
├── screenshots/                README screenshots
├── wiki/                       Maintainer and contributor documentation
├── Uniwill Control Utility/
│   ├── Assets/                 Images, PawnIO modules, RTSS and stress-test payloads
│   ├── Helpers/                WPF converters and behaviors
│   ├── Localization/           22 embedded translation JSON files
│   ├── Models/                 Settings, protocol frames, curves, SKU and device models
│   ├── Properties/             Generated .NET user settings
│   ├── Scripts/                CPU/GPU/system backends and inherited helpers
│   ├── Services/               Hardware sessions, coordination and persistence
│   ├── SkuData/                Model-specific profile database
│   ├── ViewModels/             MVVM state for selected surfaces
│   ├── Views/                  WPF controls, pages, windows and overlay
│   ├── App.xaml(.cs)           Process composition and lifecycle
│   └── Uniwill Control Utility.csproj
├── LICENSE
├── README.md
└── Uniwill Control Utility.sln
```

## Entry and composition files

| File | Role |
| --- | --- |
| `App.xaml` | WPF resources and startup/exit exception event wiring. |
| `App.xaml.cs` | Logging, prerequisites, host/DI composition, single-instance logic, detection, device auto-connect, deferred tasks, shutdown. |
| `app.manifest` | Administrator execution level, Windows compatibility, Per-Monitor DPI awareness, long-path awareness. |
| `ApplicationHostService.cs` | Creates and activates the main window when the Generic Host starts. |
| `MainWindow.xaml(.cs)` | Navigation shell, tray surface, power/resume handling, startup application of persisted hardware state. |

## Model groups

| Group | Representative files | Purpose |
| --- | --- | --- |
| Profiles | `CustomPreset.cs`, `VfCurvePreset.cs`, `VfStockCurve.cs`, `EcFanCurve.cs` | Durable preset models and fan/V-F curve data. |
| Peripheral protocols | `Bs1Protocol.cs`, `Bs2ProProtocol.cs`, `WaterCoolerProtocol.cs` | Frame constants, commands, response parsers, RGB uploads. |
| Peripheral settings | `Bs1Settings.cs`, `Bs2ProSettings.cs`, `WaterCoolerSettings.cs`, `KeyboardSettings.cs` | Durable user configuration and migration behavior. |
| Device identity | `LaptopModelInfo.cs`, `PanelInfo.cs`, `SkuDatabase.cs` | Model detection and model-specific profile values. |
| Overlay | `OverlayOptions.cs` | Immutable overlay configuration record. |

## Service groups

| Group | Files | Purpose |
| --- | --- | --- |
| Platform hardware | `UniwillECService`, `HardwareMonitoringService`, `GpuInventoryService`, `SkuResolver`, `LaptopDetector` | Core laptop I/O, telemetry, and identity. |
| Peripheral sessions | `KeyboardHidService`, `FlydigiCoolerHidService`, `FlydigiCoolerBleService`, `WaterCoolerService` | Transport lifecycle and commands. |
| Smart control | `EcSmartFanControl`, `FlydigiSmartControl`, `FlydigiBs1SmartControl` | Temperature-driven periodic control. |
| Profiles | `PresetProfileApplier`, `AdaptivePresetManager`, `PresetBackupService`, `DeviceApplier` | Profile storage, translation, application, override and import/export. |
| Overlay/performance | `OverlayManagerService`, `OverlaySettingsStore`, `Performance/*` | Overlay lifecycle, telemetry and FPS sources. |
| App support | `LocalizationService`, `UpdateManager`, `PawnIoDetectionService`, `LastAppliedSettingsService` | Translation, releases, prerequisites and last-applied state. |

## Backend scripts

- `Scripts/AMD Backend/` loads AMD PawnIO modules and implements the Ryzen SMU mailbox.
- `Scripts/Intel Backend/` loads Intel PawnIO modules and exposes MSR/MCHBAR tuning operations.
- `Scripts/GPUs/NVIDIA/` contains MUX detection, NVAPI/NVML bindings, GPU power/clock logic, and V-F curve operations.
- `Scripts/Misc/` contains logging, cached system data, WMI/system discovery, display operations, sensor compatibility helpers, and notifications.
- `RyzenAdj_To_UCU.cs` is the shared command interpreter used by profile flows for AMD, Intel, NVIDIA, display, and Windows power settings.
- `Game_Manager.cs` discovers installed games and launches configured applications.

## Presentation surfaces

| Page/window | Main responsibility |
| --- | --- |
| Dashboard | Device identity and hardware snapshots. |
| Presets | Built-in/custom CPU and GPU profiles, V-F curves, backup/import. |
| Adaptive | Per-game profiles and selected-domain overrides. |
| Automations | AC, battery and resume profile selection. |
| Fan Control | EC curves, turbo, split/unified modes, smart fan mode. |
| Keyboard | Global effects, per-key editor, brightness, direction, idle behavior. |
| Light Bar | EC-backed color/effect controls. |
| Battery | Charge modes and battery telemetry. |
| Flydigi Cooler | HID/BLE device selection, fan/RGB/curve/device settings. |
| Watercooler | BLE discovery, pump/fan/RGB, smart mode, standby/fill cycle. |
| Overlay Settings | Overlay metrics, appearance, placement and hotkeys. |
| Settings | Application preferences, update controls, backup/import, diagnostics, system details. |

## Size and concentration

The repository contains 53,706 C# lines and 11,920 XAML lines. Thirty-six source files exceed 500 lines, 18 exceed 1,000, 11 exceed 1,500, and four exceed 2,000. The largest file is `Adaptive.xaml.cs` at 3,660 lines.

High-concentration files include:

| File | Approximate lines |
| --- | ---: |
| `Views/Pages/Adaptive.xaml.cs` | 3,660 |
| `Views/Pages/FlydigiCooler.xaml.cs` | 2,872 |
| `Services/FlydigiCoolerHidService.cs` | 2,413 |
| `Views/Windows/MainWindow.xaml.cs` | 2,241 |
| `Services/UniwillECService.cs` | 1,893 |
| `Views/Pages/FanControl.xaml.cs` | 1,862 |
| `Views/Pages/Adaptive.xaml` | 1,812 |
| `Services/WaterCoolerService.cs` | 1,714 |
| `Views/Pages/Presets.xaml.cs` | 1,700 |
| `Views/Controls/VfCurveEditor.xaml.cs` | 1,610 |

These files are the primary orientation points for their features, but also the highest-cost areas for review and change isolation.

## Generated and bundled content

- `Properties/Settings.Designer.cs` is generated from `Settings.settings`; edit the settings definition rather than the generated file.
- Localization JSON files are embedded resources.
- `SkuData/skus.json` is copied to output and read at runtime.
- PawnIO modules, native DLLs, the RTSS bridge, and the AVX2 stress-test helper are copied or bundled according to the project file.
- Thirty tracked `.dll`, `.exe`, `.bin`, `.lib`, and `.exp` files total approximately 12.66 MiB.

[Back to documentation index](../README.md)
