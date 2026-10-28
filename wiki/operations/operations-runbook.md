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

### Startup freezes at exactly 92 % "Initializing", forever, reboots do not help

92 % is the top of the Phase-1 tracker: all deferred tasks finished, and the chain is building the main window - the dashboard page constructor. A freeze *at exactly this number, every launch, reboot-proof* is the signature of a **synchronous image decode on the UI thread** being held by a security product's file filter: WPF's `BitmapImage` decoder resolves the picture's ICC color context through a synchronous `FileWebRequest` on `C:\Windows\System32\spool\drivers\color`, and a guard that system directory (observed: Emsisoft) parks the open's IRP indefinitely - the dispatcher thread never returns, so the window paints its last progress value and freezes. `dotnet-stack report -p <pid>` shows it directly: the UI thread sits in `FileWebRequest.GetResponse` under `BitmapImage.FinalizeCreation` under the page constructor. The app is not corrupt and the install is not broken; the AV's system-directory guard is the machine condition, and an AV allowlist for the app (or the guard off for its file opens) is the permanent fix on that machine. The product-side fix is the rule the code now follows: images decode off the UI thread with a bounded wait (`SafeImageSource`: pool-thread decode, `IgnoreColorProfile` to skip the ICC file open, cache-on-load + freeze, 5 s budget, image collapses on failure) - the worst case on a guarded machine is a missing laptop photo, never a frozen pre-loader.

### The window appears, metrics stay missing, then the whole app freezes with the dashboard up

The second member of the guarded-machine freeze family; the window paints, so it reads as "it loaded, then froze" rather than a splash stall. `dotnet-stack report -p <pid>`: the **UI thread** sits in `Monitor.Enter` on `HardwareMonitoringService.Acquire` (reached from a device-connect dispatcher callback via the lazy temperature lease), and the lock holder is a refresh thread parked in a storage `DeviceIoControl` (`DiskInfoToolkit...OpenDevice`) inside LibreHardwareMonitor's update pass - a filter driver holding the disk open indefinitely. Root cause was one lock guarding both lease bookkeeping and the entire hardware update, so any hung driver probe froze every UI consumer of the metrics service. The product-side rule the code now follows: **no app thread performs hardware I/O or waits behind it** - lease bookkeeping is a dictionary-only lock, all driver I/O is serialized on a probe gate that only pool threads block on, a hung probe parks a single pool pass, and the dashboard is served an OS-only snapshot (kernel32 battery + `GlobalMemoryStatusEx` + EC CPU temperature, merged with the last completed LHM values) while the probe is stuck. On a guarded machine the dashboard therefore fills battery/memory/EC-CPU numbers with the probe hung; LHM-only fields stay `--`. Verification launches must use the product's real route - **unelevated**: an elevated token passes AV service protection and the PawnIO device ACL, so elevated test runs hide exactly the defects guarded machines surface (blank metrics, service-descriptor denials) and are not proof.

### PawnIO-backed controls are unavailable

Open the prerequisite flow and verify PawnIO is installed and its device path exists. Confirm the correct AMD or Intel module files were copied into the application output. Restart UCU after driver installation.

### The engine service is not created at all

The app registers the service in its post-startup chain, so a service that never appears means that chain never ran: read the app log (`%LOCALAPPDATA%\UCU\logs`) for the phase markers and the `[Engine]` lines, and the engine installer's own log (`%LOCALAPPDATA%\UCU\engine\install<yyyyMMdd>.log`), which records every `sc` step with its exit code. An install log with no entry for the launch means the app never reached the ensure - a frozen window is the usual cause, because the chain runs after the window renders. `sc create` failing with 1073 means a registration exists (a leftover), and 1072 means it is delete-pending: on a machine whose policy relaunches stopped services, an uninstall verb's `sc stop` can leave the registration delete-pending while the policy runs the image again; the verb's force ladder (kill the service process, retry the delete) resolves it, and a tombstone from an open `services.msc` blocks re-creation until the last handle closes.

### Dashboard CPU temperature shows `--`

The CPU's own sensor requires the PawnIO driver, and PawnIO's device ACL admits System and Administrators only - an installed, unelevated app cannot open it even when the driver is installed and running, which is the normal state of a per-machine install. UCU then displays the embedded controller's reading and the card's tooltip names the source ("Embedded controller sensor"), so a value from the EC is expected, not a defect: the two sensors are not the same number.

`--` means neither source answered. Check that the engine is running (`sc.exe query UCUEngine`) and that its log (`%ProgramData%\UCU\engine\`) reports the EC driver opened; an engine-side loss shows `--` on every EC-fed card (fan speed, battery health) at the same time. A tooltip reading "CPU core sensor" with no value points at the PawnIO path (driver installed, device openable, module files present).

### EC fan or battery controls are unavailable

Confirm laptop detection, `ACPIDriverDll.dll` loading, and EC initialization in the log. OEM firmware and register maps differ; do not infer support only from the chassis brand.

When the engine service **is** running yet the unelevated app logs `The engine service is not openable` (the app cannot open the service descriptor, so the RPC channel and lifecycle never form), the machine's security product is guarding the service. An **install-folder exclusion covers only the file scanner**; products with a service-protection module (observed: Emsisoft) additionally police OpenService/QueryServiceStatus handles from unelevated processes, and unsigned Program Files binaries get no reputation to ride on - signed commercial tools (observed: XMG Control Center) sail through the same guard. Two honest remedies, not app workarounds: an **application/program-level allowlist** in the product for the app image and the engine image (the service runs from the same layout, so list both paths as trusted applications), and **Authenticode code signing** of the app, engine, uninstall shim, and MSI with a publisher certificate - this is release-blocking quality work (release checklist), because it is the identity that security products, SmartScreen, and enterprise policy all key on; a self-signed test certificate satisfies none of them.

### The engine service is installed, stopped, and the app does not start it

The app owns the engine's lifecycle and starts a stopped engine unelevated, because the service DACL grants Authenticated Users `SERVICE_START` (RP). When the app logs `Could not start the engine service` / `not allowed to start it` with native code 5 (`Access is denied`), the registration carries a DACL from a build that predates the grant - typical after an `msiexec /x` uninstall, which leaves the registration (and its DACL) behind, followed by a reinstall.

`sc.exe sdshow UCUEngine` must read `D:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;CCSWRPRC;;;AU)`. The service re-asserts this itself (`ServiceSelfSecurityService`: on start and every 5 minutes, `SetServiceObjectSecurity` from LocalSystem) - and that is load-bearing, not belt-and-braces: security products with service protection (observed: **Emsisoft Anti-Malware**) revert `sc sdset` immediately (SUCCESS then template on read-back, every ordering, and the SACL `S:(AU;FA;...;;;WD)` is the enforcer's fingerprint). Diagnose with `sc sdshow` plus, under the template, an unelevated `sc query UCUEngine` answering 5 while the app is running. The app repairs an unrecoverable descriptor with one elevated install (`sc sdset` + start, logged in `%LOCALAPPDATA%\UCU\engine\install<yyyyMMdd>.log`); the classic symptom of a reverted descriptor is a UAC prompt for "Uniwill Control Utility Engine" on **every** launch with the install log showing `sc start ... FAILED 1056` against an already-running service. If the window freezes (painted, splash overlay stuck), capture a dump first - Task Manager → right-click the process → Create dump file - `dotnet-dump analyze <dmp> -c clrstack` names the frozen frame (a real case: the UI thread inside `SetWindowsHookEx` from the overlay hotkey hook). Supervision signatures in the engine log: `App stopped ... graceful=false → Relaunch` followed by `[Supervision] Launched the app in session N` is the crash-respawn working; `EntryPointNotFoundException: WTSGetActiveConsoleSessionId` was a real defect (the import named `wtsapi32.dll`, the export lives in `kernel32.dll`) and its stack ending in `BackgroundService failed ... StopHost` shows why a supervision throw must be caught per tick - unhandled, it took the whole engine down (RPC and watchdog included), and the SCM failure action (restart/60000) was what brought the engine back.

### The uninstall left pieces behind (forensics)

The complete uninstall is an app-side self-heal: Control Panel / Settings runs `msiexec /x`, the MSI removes its thin registration, and the running app's `UninstallSentinel` notices the registration gone within ~10 s and self-runs the engine's `--uninstall-engine` verb as SYSTEM (one-shot `/ru SYSTEM` scheduled task `UCU Uninstall Verb`), which performs the full sweep (service, app processes, tasks, ARP husks, per-user hive husks, stale per-user MSI registrations, logs, layout). Firing requires **removal evidence**, not just a missing registration: the Application event log must show, within 3 minutes, MsiInstaller 11724 "Removal completed successfully" naming the product AND 1042 "Ending a Windows Installer transaction: {code}" for the product code the sentinel watched while the registration was visible - and, for the destructive firing, **both events must postdate this app process's start** (the time floor, unit-pinned in `StaleRemovalEvidenceTests`): pack.ps1 pins the product code per package, so an uninstall-reinstall of the same package within 3 minutes reuses the code, and the user's previous uninstall fingerprint would otherwise license the verb against the fresh install (field-proven 21:55:22: shim uninstall at 21:53:59, reinstall at 21:55:06, the relaunched sentinel executed the stale fingerprint and swept the fresh install 16 s after launch). The evidence rule is field-proven necessity: an AV with ARP-key protection (observed: Emsisoft) makes the live registration invisible in the app's own registry view - a phantom miss on every poll, while msiserver and SYSTEM see it fine; without evidence the sentinel would self-delete a fresh install ~16 s after every launch (and the 3-minute window + code binding + process-start floor stop *older* uninstalls from licensing a fresh install). The sentinel logs the parked phantom at Warning severity. Diagnose a dirty machine by reading the two traces: the app log (`%LOCALAPPDATA%\UCU\logs\ucu_log<yyyyMMdd>.txt`) for `[UninstallSentinel]` lines (parked phantom / fired / "verb task created (SYSTEM) rc=" / "FAILED to fire the verb"), and the verb's install log - elevated launches log to `%LOCALAPPDATA%\UCU\engine\install<yyyyMMdd>.log`, the SYSTEM task logs to `C:\Windows\System32\config\systemprofile\AppData\Local\UCU\engine\install<yyyyMMdd>.log`. The most common sentinel failures, all field-proven:

- **The sentinel never armed** - the layout signal root must be exactly one level above the app image (the app runs at `<root>\current\`); a root that resolves to a generic directory (e.g. `C:\Program Files`) never carries the marker, so the watcher idles forever. Arming requires an install layout under Program Files with `Update.exe` plus an existing `HKLM\SYSTEM\CurrentControlSet\Services\UCUEngine` key.
- **The sentinel never started** - it must be launched right after logging init, before the blocking startup stages. On machines where startup blocks (the modal PawnIO prerequisite dialog, slow EC init), anything wired into the post-startup chain starts too late - after Control Panel's "no dialogs" uninstall window has closed.
- **The verb task dies at `schtasks /create`** - 0x5 (Access Denied) from the UAC consent with `RUNLEVEL LIMITED` (a medium-IL un-elevated process cannot register a SYSTEM task: the verb task must be created by an elevated context - in practice by the app when it is running elevated, which the install flow already does). 1008 / "The syntax of the date, time is invalid" means Task Scheduler mangled the `/tr` string: it cannot carry escaped quotes (`\"`), so the task action is `cmd.exe /c <script>` with all quoting inside the script files, and the script path must be an 8.3 short path (`GetShortPathNameW`) - a long path containing spaces splits in cmd's parser.
- **The verb ran but `sc delete` parked** - the machine's policy relaunches stopped services, so the delete sits pending; the verb's force ladder kills the service process and retries (the verb process shares the engine image, so the ladder must exclude its own pid).
- **The layout root survives** - a security product's file-guard self-defense (observed: Emsisoft) can deny layout-root deletion to SYSTEM and Administrators alike. On such machines the verb logs the access-denied and the root survives as an inert husk; everything user-visible (service, ARP, tasks, app) is gone. The fix is an AV allowlist for the install root, not a product change.
- **A "Uniwill Control Utility" husk remains in Control Panel after the product is gone** - stale per-user MSI registrations (`HKCU\...\Installer\Products`, left by machine-WiX per-user ghost products, Velopack, and interrupted runs). `MsiEnumProductsW` sees them while the ARP key is gone; the husks are unremovable through Control Panel because the MSI's cached package no longer exists (`1619 "This wizard cannot access the setup database"`). The verb sweeps them (`/x` first, registration-key surgery when `/x` fails); Control Panel refreshes its listing lazily, so re-open it before concluding.
- **The app is in Task Manager with no window, and elevation prompts repeat during an uninstall** - engine supervision relaunches the app while the verb sweeps it. `UninstallSentinel.IsBeingUninstalled()` gates relaunches/starts during the sweep window (ARP registration gone + layout present).

Manual reproduction: run the verb directly with the systemprofile logging visible - `schtasks /create /tn "UCU Uninstall Verb" /tr "cmd.exe /c <short-path script>" /sc ONCE /sd <near-future> /st <time> /ru SYSTEM /rl HIGHEST /f` then `schtasks /run`; the script content is `"<engine exe>" --uninstall-engine >> "<log>" 2>&1`. The MSI's own surface is only the registration and ARP entry - the layout is deployed by the packer's bootstrap custom action, which is why `msiexec /x` alone removes almost no files and why the self-heal, not the MSI, owns uninstall completeness.

### The app self-uninstalls a minute after the dashboard appears (Velopack registration repair)

Signature: fresh GUI install → app launches → dashboard fills → ~15-60 s later the app dies; the Application event log shows the app's own process running `msiexec /x {the live product code}` (MsiInstaller 1040/11724/1042, Client Process Id = the app), and the verb log shows the sentinel's sweep executing that genuine, code-bound removal. The sentinel is innocent: this removal is real, watched, and bound - the app killed itself.

The mechanism: Velopack's per-machine registration check is an IN-PROCESS registry read (MSI classes/Installer hives + ARP). A security product that polices registry keys (observed: Emsisoft) blinds those reads for app processes, inconsistently per hive - field-proven from BOTH directions on one machine: ARP-blind while MSI-classes visible, and ARP-visible while the MSI-classes read blind. In either blinded state Velopack concludes "the install is not registered" and its repair runs `msiexec /x` on the LIVE product code ("re-register"), which arms the sentinel's full sweep.

The rule that closes the class (unit-pinned in `VelopackRegistrationGuardTests`): an app running from the per-machine layout - a filesystem fact, no registry consulted - never builds `UpdateManager`; the app log shows `[Update] UpdateManager stays un-built: ... per-machine (MSI) layout ...`. Consequence: installed machines have no in-app update flow (no title-bar card, no auto-check); **updates on installed machines arrive by running a new Setup.msi** - the thin-MSI layout's real update mechanism. Portable/dev layouts keep the full Velopack flow; their per-user root has no MSI registration for a repair to destroy. Do not "improve" this guard into a registry-view condition again: a v1 guard on `MsiEnumProductsW` and a v2 guard on the ARP view each passed its own machine state and each shipped the death from the opposite side.

### The Control Panel uninstall runs the thin /x instead of the redirected shim

The MSI's ARP entry is redirected to the complete-sweep shim (`Uninstall.exe`) by the elevated engine install pass (`RepairControlPanelUninstall`), which also deletes the `WindowsInstaller=1` value: an ARP entry carrying that value is routed by PRODUCT CODE (appwiz.cpl/Settings call the Windows Installer APIs with the key name), and `UninstallString` is never read - with the value gone, Control Panel shell-executes the shim and the thin-MSI "Removal completed successfully, nothing happened" state disappears. Field observation on a guarded machine (22:13:49): the redirect writes persist, the deleted `WindowsInstaller` value is **re-added by the enforcer within a minute** (anti-hijack self-defense of the ARP tree: it restores the MSI routing flag for non-trusted writers, selectively - same SACL-fingerprint enforcer as the service-descriptor reverts). On unguarded machines the binding holds. On guarded machines the remedy is the identity fix (code signing, release checklist 6b) or an AV allowlist for the install root - not app-side re-assertion loops, which fight the enforcer on every launch. The sentinel + evidence floor remain the net for every genuine `msiexec /x` route regardless of binding.

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
