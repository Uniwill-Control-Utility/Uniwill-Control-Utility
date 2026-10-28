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

Logs are written per user, under the local application data directory:

```text
%LOCALAPPDATA%/UCU/logs/ucu_log.txt
```

The file sink rolls daily and limits each file to 8 MiB. Logging level is controlled in Settings through `DiagnosticLogLevel` and the shared Serilog level switch. The sink is never a startup requirement: if the directory cannot be created (a read-only profile, a full disk) logging degrades to the console and the launch continues.

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

### Startup stalls in the "Initializing" phase (60–71 %)

The phase progress reflects deferred tasks, and the game-library scan walks every local volume. A **disconnected mapped network drive** blocks the directory probe in the redirector until it times out (21–63 s measured against a dead host), which is the classic long stall. The scan now skips `DriveType.Network` and the splash releases at a 10 s budget, so the list appears shortly after the window.

Check the app log for "Game library scan outlived the ... startup budget": it names the resource class. `net use` shows mapped drives and their `Disconnected` state. A second machine, or the same drive connected, confirms the environment rather than the app.

### PawnIO-backed controls are unavailable

Open the prerequisite flow and verify PawnIO is installed and its device path exists. Confirm the correct AMD or Intel module files were copied into the application output. Restart UCU after driver installation.

### The engine service is not created at all

The app registers the service in its post-startup chain, so a service that never appears means that chain never ran: read the app log (`%LOCALAPPDATA%\UCU\logs`) for the phase markers and the `[Engine]` lines, and the engine installer's own log (`%LOCALAPPDATA%\UCU\engine\install<yyyyMMdd>.log`), which records every `sc` step with its exit code. An install log with no entry for the launch means the app never reached the ensure - a frozen window is the usual cause, because the chain runs after the window renders. `sc create` failing with 1073 means a registration exists (a leftover), and 1072 means it is delete-pending: an `msiexec /x` uninstall leaves the registration (only the `Uninstall.exe` shim, now also what Control Panel runs, deletes it), and a tombstone from an open `services.msc` blocks re-creation until the last handle closes.

### Dashboard CPU temperature shows `--`

The CPU's own sensor requires the PawnIO driver, and PawnIO's device ACL admits System and Administrators only - an installed, unelevated app cannot open it even when the driver is installed and running, which is the normal state of a per-machine install. UCU then displays the embedded controller's reading and the card's tooltip names the source ("Embedded controller sensor"), so a value from the EC is expected, not a defect: the two sensors are not the same number.

`--` means neither source answered. Check that the engine is running (`sc.exe query UCUEngine`) and that its log (`%ProgramData%\UCU\engine\`) reports the EC driver opened; an engine-side loss shows `--` on every EC-fed card (fan speed, battery health) at the same time. A tooltip reading "CPU core sensor" with no value points at the PawnIO path (driver installed, device openable, module files present).

### EC fan or battery controls are unavailable

Confirm laptop detection, `ACPIDriverDll.dll` loading, and EC initialization in the log. OEM firmware and register maps differ; do not infer support only from the chassis brand.

### The engine service is installed, stopped, and the app does not start it

The app owns the engine's lifecycle and starts a stopped engine unelevated, because the service DACL grants Authenticated Users `SERVICE_START` (RP). When the app logs `Could not start the engine service` / `not allowed to start it` with native code 5 (`Access is denied`), the registration carries a DACL from a build that predates the grant - typical after an `msiexec /x` uninstall, which leaves the registration (and its DACL) behind, followed by a reinstall.

`sc.exe sdshow UCUEngine` must read `D:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;CCSWRPRC;;;AU)`. The app's install pass verifies this itself: it reads the descriptor back with `sc sdshow` after `sc start` and re-asserts `sdset` until the AU contract ace is visible - a fresh `sc create` commits its default SD into the registry after sc.exe returns, and that late write silently reverts a too-early `sdset` (the classic field signature: install log says `sc start ... FAILED 1056` against an already-running service and the user sees a UAC prompt for "Uniwill Control Utility Engine" on **every** launch, with `sdshow` answering `(A;;KR;;;AU)`). The app repairs this itself: it re-runs the elevated install (whose `sc sdset` re-asserts the DACL) and starts the service, one prompt. Manually: `"Service\Uniwill Control Utility Engine.exe" --install-engine` elevated, or `sc sdset` with the SDDL above. Reading the state is a `ScmNative` query (`SERVICE_QUERY_STATUS` only) and never needs privilege under the contract descriptor; only the DACL repair does. If the window freezes (painted, splash overlay stuck), capture a dump first - Task Manager → right-click the process → Create dump file - `dotnet-dump analyze <dmp> -c clrstack` names the frozen frame (a real case: the UI thread inside `SetWindowsHookEx` from the overlay hotkey hook).

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
