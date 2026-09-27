# Operations Runbook

## Installation state

The repository does not contain an installer project, packaging script, release workflow, checksums, or a committed release manifest. Runtime code supports two different distribution assumptions:

- The project can publish a self-contained win-x64 file set.
- The in-app updater downloads the first GitHub release asset and then expects `C:\Uniwill.Control.Utility.msi` for `msiexec`.

The source tree alone cannot establish which artifact is the current supported installation method. Any release must preserve all native/content payloads and a stable writable location for the application-directory data and logs. The executable requests administrator elevation.

## First-run prerequisites

| Prerequisite | Detection | Result when absent |
| --- | --- | --- |
| Microsoft VC++ Redistributable | Registry under Visual Studio VC runtime keys | Startup offers the Microsoft download page and exits. |
| PawnIO | Device path and uninstall/version registry data | Deferred prerequisite flow opens the bundled installer UI. |
| RTSS | Process/runtime detection in prerequisite dialog | Optional download guidance; ETW remains the FPS fallback. |
| Compatible hardware | WMI, SKU and device enumeration | Unsupported pages or controls remain unavailable/degraded. |

The PawnIO installer is downloaded from the upstream PawnIO.Setup latest-release URL into a temporary application-data directory, run silently, and then removed.

## Normal startup observations

Expected sequence:

1. Elevation prompt.
2. Short detection delay before the main window is built.
3. Dashboard populated from cached WMI and LibreHardwareMonitor data.
4. Connected-device cards appear as delayed HID/BLE auto-connects complete.
5. Persisted profile, keyboard, light-bar, fan, and battery state is applied.
6. Optional prerequisite/update notifications appear after the dashboard.

Only one UCU process should remain. Launching again should bring the existing window forward through the named event.

## Logging

Logs are written relative to the executable:

```text
<application directory>/logs/ucu_log.txt
```

The file sink rolls daily and limits each file to 8 MiB. Logging level is controlled in Settings through `DiagnosticLogLevel` and the shared Serilog level switch.

For a useful incident report, capture:

- UCU version and source/release identity.
- Windows edition/build.
- Laptop manufacturer, product and baseboard identifiers.
- CPU/GPU names and firmware/BIOS version.
- Peripheral model, transport and firmware version where applicable.
- PawnIO version.
- Reproduction steps including sleep/resume or navigation state.
- The relevant log file and screenshots.

## Common symptoms

### Application exits immediately after launch

Check the VC++ runtime prompt and registry detection. Confirm all extracted DLLs remain beside the executable. Review the log for host-composition or native-library load errors.

If the saved language is `zh-CN`, the current source catalogue contains invalid JSON at line 389. Catalogue loading occurs before the normal startup logging/exception setup, so this can prevent normal initialization. Reset the saved language to a valid catalogue or correct the JSON in a development build.

### A second launch does nothing visible

The first instance should receive `UniwillControlUtility.Show`. Check whether an existing process is hidden in the notification area. If the process is present but cannot surface, collect its log before terminating it.

### PawnIO-backed controls are unavailable

Open the prerequisite flow and verify PawnIO is installed and its device path exists. Confirm the correct AMD or Intel module files were copied into the application output. Restart UCU after driver installation.

### EC fan or battery controls are unavailable

Confirm laptop detection, `ACPIDriverDll.dll` loading, and EC initialization in the log. OEM firmware and register maps differ; do not infer support only from the chassis brand.

### Flydigi device does not connect

- Confirm whether the device is BS1 (BLE) or BS2+ (HID).
- For BLE, remove stale Windows connections if the GATT session remains owned after an interrupted run.
- For HID, confirm the device enumerates and that the supported product/name filters match.
- Wait for the reconnect loop/device-arrival notification before repeatedly pressing Scan.
- Capture device status messages and the persisted Flydigi JSON.

### Hydro UI does not reconnect after resume

UCU deliberately disconnects a session it considers connected, waits, and reconnects because BLE links can become stale after sleep. If it remains offline, capture the GATT state log, Windows Bluetooth device state, and watercooler firmware string.

### Settings revert after restart

Identify the owning store in [Configuration and data](configuration-and-data.md). Check both roaming and local `UCU` directories plus `.NET user.config`. For Adaptive-controlled domains, stop Adaptive Mode first so restored manual state is not immediately overridden.

### Profile applies only partially

Separate the domains:

- CPU backend (AMD SMU or Intel PawnIO).
- NVIDIA power/clock/V-F path.
- Uniwill EC power fields.
- Windows power/display/affinity commands.

Use the log and active command string to locate the failing translator option. For V-F presets, verify the required power-before-curve and power-after-curve sequence.

### Overlay has no FPS value

Confirm RTSS is running and publishing the foreground process. Without RTSS, the ETW fallback must be able to observe present events and resolve the foreground executable. Some protected processes prevent module-path inspection; telemetry may remain unavailable while other overlay metrics continue.

## Suspend/resume verification

After changes to hardware state handling, verify this sequence:

1. Apply a custom profile, lighting, battery limit, fan curve, and connected peripheral state.
2. Enter sleep or hibernation.
3. Resume and wait for the delayed reapplication.
4. Confirm the active profile, EC fans, battery mode, keyboard, light bar, Hydro UI, and Flydigi devices.
5. If Adaptive Mode is enabled, start a configured game and confirm every selected domain reapplies once.
6. Exit UCU and confirm Windows releases BLE/HID sessions.

## Recovery and data reset

Prefer backing up first from Settings. To isolate corrupt state, move one owning JSON file out of its directory and restart; the owner will load defaults. Reset `.NET user.config` only after capturing it, because it contains cross-feature preferences and active profile state.

For EC fan-specific recovery, use the automatic-control restoration flow documented in [Uniwill EC fan control](../protocols/uniwill-ec-fan-control.md) rather than manually editing arbitrary registers.

[Back to documentation index](../README.md)
