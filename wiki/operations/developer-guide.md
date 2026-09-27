# Developer Guide

## Development baseline

| Requirement | Current project expectation |
| --- | --- |
| Operating system | Windows 11 or compatible Windows version with build-22621 APIs |
| SDK | .NET 10 SDK with Windows Desktop workload/reference packs |
| Target framework | `net10.0-windows10.0.22621.0` |
| Language | C# language version 10 |
| UI | WPF, WPF UI 4.3, selected Windows Forms APIs |
| Architecture | Develop with x64 when exercising native and hardware paths |
| Native prerequisite | Microsoft Visual C++ 2015-2022 Redistributable, x64 or x86 detection accepted by startup |
| Driver/module prerequisite | PawnIO for AMD/Intel low-level controls |
| Optional runtime | RTSS for the preferred FPS source; ETW is the fallback |

The application manifest requests administrator elevation. A normal debugger launch therefore prompts for elevation or requires an elevated IDE/terminal.

## Clone and restore

```powershell
git clone https://github.com/Uniwill-Control-Utility/Uniwill-Control-Utility.git
Set-Location 'Uniwill-Control-Utility'
dotnet restore '.\Uniwill Control Utility.sln'
```

The repository does not pin an SDK with `global.json`. Use a compatible .NET 10 SDK and record the exact SDK version when diagnosing build differences.

## Build

```powershell
dotnet build '.\Uniwill Control Utility.sln' -c Debug -p:Platform=x64
dotnet build '.\Uniwill Control Utility.sln' -c Release -p:Platform=x64
```

The project declares `AnyCPU` and `x64`, but its native DLLs, PawnIO modules, Windows API bindings, and device drivers make x64 the useful validation target.

### Current validation note

The documentation baseline machine had .NET 10 runtime `10.0.12` and Windows Desktop runtime `10.0.12`, but no .NET SDK. Static project analysis was completed; a local restore/build was attempted and correctly stopped with `No .NET SDKs were found`. Install the SDK before treating the branch as build-validated.

## Publish

The project file defines conditional self-contained, compressed, single-file settings. The documented command is:

```powershell
dotnet publish '.\Uniwill Control Utility\Uniwill Control Utility.csproj' `
  -c Release `
  -r win-x64 `
  -p:SelfContained=true
```

Expected output is under the project `bin/Release/net10.0-windows10.0.22621.0/win-x64/publish/` hierarchy unless an explicit output directory is supplied.

Do not assume the single executable is the complete distributable. Verify that copied native libraries, PawnIO modules, `SkuData/skus.json`, RTSS helpers, stress-test files, and other project content are present in publish output.

## Solution and project conventions

- There is one application project and no test project.
- Nullable reference types are enabled.
- Unsafe blocks are enabled for native/system interaction.
- Debug configurations explicitly enable optimization.
- The source uses both Newtonsoft.Json and System.Text.Json.
- Generated settings live in `Properties/Settings.Designer.cs`; change `Settings.settings` instead.
- Embedded localization resources are loaded from `Localization/*.json`.
- WPF code-behind is an established pattern; new coordination logic should nevertheless prefer focused services when it is not inherently view-specific.

## Local run checklist

1. Build x64 Debug.
2. Confirm `ACPIDriverDll.dll`, PawnIO modules, SKU data, and native dependencies exist beside/in the output expected by the project.
3. Start from a terminal or IDE that can handle the elevation request.
4. On first run, confirm the prerequisite dialog results.
5. Start with diagnostic logging at Info before exercising a new hardware path.
6. Validate unsupported-device behavior as well as the target device.
7. Exit through the tray command when testing device teardown.

## Feature-oriented entry points

| Change type | Start with |
| --- | --- |
| Startup/composition | `App.xaml.cs`, `ApplicationHostService.cs`, `MainWindow.xaml.cs` |
| Built-in/custom presets | `Presets.xaml.cs`, `PresetProfileApplier.cs`, `CustomPreset.cs`, `SkuResolver.cs` |
| Adaptive Mode | `Adaptive.xaml.cs`, `AdaptivePresetManager.cs`, `DeviceApplier.cs` |
| EC fan/battery/power | `UniwillECService.cs`, `FanControl.xaml.cs`, protocol docs |
| Keyboard | `KeyboardHidService.cs`, `Keyboard.xaml.cs`, keyboard protocol doc |
| Flydigi | HID/BLE services, protocol models, `FlydigiCooler.xaml.cs` |
| Hydro UI | `WaterCoolerService.cs`, `Watercooler.xaml.cs`, protocol models |
| Monitoring/overlay | `HardwareMonitoringService.cs`, `Performance/*`, overlay view model/window |
| Localization | `LocalizationService.cs`, all 22 localization JSON files |

## Localization workflow

The English resource acts as the practical key catalog. When adding visible text:

1. Add or reuse a stable key in `en-GB.json`.
2. Add the same key to every other locale or confirm fallback behavior.
3. Use `LocalizationService.Get`/`Format` rather than hard-coded visible text.
4. Exercise right-to-left and longer-string layouts where applicable.
5. Verify JSON parsing and embedded-resource names during build.

## Native and protocol changes

For EC, HID, BLE, SMU, MSR, NVAPI, or WMI changes:

- Record device model, firmware, transport identifier, and before/after observations.
- Preserve required delays, activation handshakes, byte order, report sizes, and restore paths.
- Keep protocol constants in model/protocol types rather than duplicating byte values in page handlers.
- Separate transport state from UI state.
- Ensure suspend, resume, reconnect, navigation away/back, and process exit are covered.
- Update the corresponding file under [`wiki/protocols`](../protocols/device-communication-protocols.md).

## Formatting and generated output

The repository does not currently define an `.editorconfig`, formatter command, analyzer ruleset, or build-warning policy. Preserve the local style of touched files and keep formatting-only edits separate from behavior changes.

[Back to documentation index](../README.md)

