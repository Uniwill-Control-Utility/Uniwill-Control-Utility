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

### Dashboard CPU Power / Clock Speed / RAM Speed cards show `--` (per-machine install)

Field report 2026-10-29 (guarded box, the user's double-click route): at launch CPU Power, CPU Clock Speed and RAM speed never appeared; GPU power/clock appeared ~30 s later. The causes are the ACL rule above generalized: CPU package power and core clocks are **Ring0 MSR** readings and the DIMM SPD data rate is an **SMBus** reading - the sensor drivers' device ACLs admit System + Administrators (PawnIO's INF ships `D:P(A;;GA;;;SY)(A;;GA;;;BA)`), so the asInvoker app installed per-machine gets access denied and these cards stay `--` for the whole session. The GPU cards fill late because NVAPI needs no driver - the app's own LHM probe is what parks at startup under resident AV, and it opens at ~+30 s.

The remedy is the **engine-side sensor snapshot** (`telemetry.get_metrics_snapshot`, the one documented exception to the EC-only verb surface - the standing rule keeps SMU/PawnIO/Intel/NVAPI app-side; the field evidence that unelevated reading is structurally impossible justifies exactly this one cached read, and the GPU stays app-side): the LocalSystem engine runs a CPU+Memory LHM probe on a dedicated loop (MetricsProbeService), publishes to a cache, and the verb answers from the cache (inline, never behind the EC queues; a parked probe parks only its own loop). The verb stamps the cache's serve-time age into the answer (`ReadingAgeSeconds`), and the app gate (`EcTelemetryService.ReadEngineMetrics`) turns a stale answer into Empty - the `--` value: the probe loop deliberately runs OUTSIDE the engine watchdog (a wedged sensor open must not FailFast an engine whose EC queues are healthy), and the app's stale-while-revalidate cache keeps serving the last DTO forever when the engine dies, so without the stamp both failure faces paint frozen numbers as live (field audit 2026-10-29). Budget: 30 s, ~6 probe cycles - **a card flipping to `--` ~30 s after the engine dies or its probe loop wedges is correct behavior**; the defect was the 104 W statue, not the `--`.

The engine values merge at the **snapshot producer** (`HardwareMonitoringService.ApplyEngineSensorSource`, beside the EC-temperature merge, applied on every served snapshot in `ApplyOsSources`), filling only the fields the app's own probe left 0 (a live in-process reading always wins; zero is the snapshot's "unavailable" convention), so EVERY consumer - dashboard cards, tray flyout, FanControl cards, the RTSS OSD - gets the engine-fed fields through the same `ReadSnapshot()` (the first fix merged at the dashboard timer and starved all the rest; field report: the OSD showed none of what the dashboard cards showed, 2026-10-29). The GPU fields are untouched (NVAPI stays app-side; there is no engine GPU reading to overlay). The SPD speed paints the RAM card's system-info cache when the WMI source is 0/late (`CachedSystemInfo.PublishRamSpeedIfMissing`, an idempotent 0-to-value transition firing the card's repaint exactly once). The tray renders 0 power/clock as `--`, never the fake `0 W`/`0.0 GHz`.

Diagnosis: the engine log's `sensor probe not open; retrying in 00:01:00` lines show the sensor stack failing (no sensor driver, asInvoker engine - the dev F5 case); a healthy engine shows nothing (the probe logs only failures/slow cycles). The app-side path: engine running + cards still `--` → the probe is open with silent sensors (driver installed on demand by LHM at SYSTEM; a security product blocking the RAMSPD driver load leaves SPD fields 0 - WMI stays the RAM-speed source, and a platform reporting `ConfiguredClockSpeed = 0` shows `Not available` honestly). Cards fill, then flip to `--` ~30 s later → the freshness gate is rejecting: the engine died (check `sc.exe query UCUEngine`), its verb is answering too-old stamps (`verbprobe` prints `age=`), or the probe loop wedged (the loop is outside the watchdog by design, so a wedged open keeps the engine alive and ages the cache - the flip to `--` is the mechanism working). The engine's probe sensor naming is pinned by unit tests to the field machine's empirical elevated probe (power `Package`, clock `Cores (Average)`, SPD `tCKAVGmin` -> 5602 MT/s = the machine's WMI 5600); a library update that renames sensors fails those tests, not the field.

**In-game: metrics freeze, updating 1-2 s around every alt-tab** (field report 2026-10-29, the RTSS OSD): the signature is values frozen mid-game that update briefly each time focus switches - the app-side **LHM probe pass** parks behind the foreground title that owns the GPU (NVAPI/storage calls under AV filter drivers; exclusive fullscreen is the classic parker), and the snapshot cache is single-flight: a parked pass queues no new pass, so the last completed snapshot is served as live indefinitely. The producer's freshness rule closes the statue: past the 5 s probe-staleness budget every LHM-only field is rendered `--` (GPU row, CPU usage; nothing to paint that the producer alone feeds), while the fresh sources keep answering live through the OS-only path - OS memory/battery, EC CPU temperature, **engine-fed CPU power/clock and DIMM temps keep updating in-game** (that is what the engine feature is for). Confirm the park site: `dotnet-stack report -p <app pid>` while frozen shows a thread parked in `_probeGate`/`Computer.Update` (NVAPI or a storage `DeviceIoControl` frame names the driver); the user-side remedy is borderless-window game mode, which usually unblocks the pass.

**The first launch after installing a new build is slow (minutes), later launches are seconds** (measured 2026-10-29: first-launch `Computer.Open` 64844 ms vs 1458 ms steady state, per-subsystem matrix all sub-2 s under the same unelevated token): a security product scrutinizes every fresh binary hash on first use, and the LHM open is where the app touches drivers. The producer keeps serving OS/EC/engine fields throughout (first-frame gate + staleness rule), so the dashboard is usable while the LHM-fed fields fill. Not an app defect; the per-machine remedy is an AV allowlist for the install root, the product remedy is code signing.

**A SMART park serializes the whole producer - Storage is gated to a 60 s cadence** (field breadcrumb 2026-10-29: `device update Storage "CT2000P310SSD8" 21044ms`): the SMART DeviceIoControl behind an AV storage filter sat inside the probe gate, which serializes every device's update, so the fast sources (GPU, CPU) aged past the staleness budget and the LHM-fed fields blanked everywhere. Storage temperature is quasi-static; the probe loop now updates Storage once a minute (first update deferred a minute so the first snapshot never queues behind SMART). The slow-device breadcrumb stays: it named this, and it names the next one.

**The producer is two probe groups, so one park costs two cells instead of the dashboard** (2026-10-29): the single `Computer` gave every subsystem ONE gate and therefore ONE age - the 21 s SMART park above aged the WHOLE producer, and the freshness rule then correctly blanked every LHM-fed field on dashboard, tray, FanControl and OSD while a quasi-static disk temperature was being read. `HardwareMonitoringService` now runs a **fast group** (`cpu`/`gpu`/`battery` - the CPU-utilization and GPU-row fields; open cost measured 97 ms + 1123 ms + 33 ms) and a **slow group** (DIMM SPD, SMART, motherboard/controller SuperIO, carrying the SMART cadence gate), each with its own gate, cadence, staleness age and breadcrumb prefix (`[LHM] probe: [fast] ...`, `[LHM] probe: [slow] ...`). The merge (`MergeGroupSnapshots`) clears a stale group's own fields and nothing else, and the OS/EC/engine sources refill their fields beside it. `ReadSensor` routes by hardware type (motherboard/controller fans → slow, CPU/GPU fans → fast) and gates on the routed group's age, so a parked SMART read stops feeding 0 RPM to the adaptive fan loop. Group rebuilds are driven by each group's own mask because LHM's kernel side (WinRing0/PawnIO) is process-global: closing one group releases the shared handle the other is using.

**The main window blips for one frame at 92 % of the pre-loader - fixed by parking BEFORE the first Show** (field report 2026-10-30): the gated `MainWindow.ShowWindow()` did `Opacity = 0` → `Show()` at the CenterScreen position → park off-screen, and this window's backdrop (`WindowBackdropType="Mica"`) is composited by **DWM**, which WPF's `Opacity=0` does not hide - so a 1080x1080 rounded panel painted one frame at screen center while the splash, a 320x290 card, exposed the whole screen around it (92 % is exactly where the `ParallelPhaseTracker`'s 50→92 span ends and the host starts). Three order rules close it: park before the first `Show()` (`WindowStartupLocation.Manual` + off-screen offsets, so the window's very first composition is off every monitor edge - `Show()` is still required, NavigationView's template only applies through a real Show, field-proven NREs otherwise); `Loaded`'s `FitWindowToWorkArea` skips the parked window (`Screen.FromHandle` on a parked window resolves the **nearest** monitor and the re-center drags it back on-screen); App plays out and **closes the splash before** `ReleaseStartupGate` lands the window at the work-area center, then raises opacity - the window's first visible presentation is the populated dashboard, nothing paints beside the splash. Measure a presentation artifact like this objectively instead of eyeballing it: enumerate the app's top-level windows (`user32 EnumWindows` + `GetWindowRect`) every ~20 ms across the pre-loader. Healthy shape (measured 2026-10-30): the card alone on-screen, then the main window visible **only at x=-32000 while the card is up**, then the main window on-screen after the card is gone. Blip signature: the main window visible with an on-screen rect while the card is up.

**The in-app LHM open costs far more than the subsystems do** (measured 2026-10-29, guarded box, unelevated token): the identical Computer config in a standalone process opens in **1.458 s** (per-subsystem matrix, fresh process each: cpu 97 ms, mem 69 ms, gpu 1123 ms, battery 33 ms, motherboard 97 ms, storage 211 ms, controller 85 ms), while the app's own breadcrumb logged **64619 ms** on a fresh pack's first launch, **22256 ms** on the next, and **no slow-open line at all** on a third. The variance tracks the security product scrutinizing the app process (fresh binary hashes on first use, easing as reputation builds), not the subsystems. The split bounds what such a stall can hide (only the fields of the group being opened), and the ordered kick - fast group opens first, the slow group starts when the fast pass settles - gives the watched fields the driver race. Stack sampling the app during startup (`dotnet-stack report -p <pid>` every 3 s) caught a probe thread parked at `NvidiaGpu.Update() → NvmlDeviceGetPcieThroughput`: NVML calls park under load as well, and per-group freshness is what turns that into `--` for the GPU cells only.

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

### The Control Panel uninstall: single entry, bound to the shim at pack time

The packed MSI carries the whole Control Panel story INSIDE the package (build\pack.ps1 step 5a, tool build\msi-edit\MsiEdit.cs). Field-verified end-to-end on the guarded box 2026-10-29 (packs p26-p33): Control Panel → Uninstall → complete sweep, no dialogs. The two key facts:

- **The engine's GUID-keyed entry is hidden by the documented mechanism**: the vpk template ships `ARPSYSTEMCOMPONENT=1`, and Windows Installer registers the entry with `SystemComponent=1` (field dump: `SystemComponent=1, WindowsInstaller=1, ModifyPath=MsiExec.exe /X{code}`). appwiz.cpl/Settings hide `SystemComponent=1` entries, so the user is never offered the product-code route; the sentinel's GUID-key probe rule still watches the entry (its `MsiExec` `UninstallString`), and the elevated install pass keeps redirecting it at the shim as a belt.
- **The product's ONE visible entry is the vpk-published one** (component `ArpRegistration`, KeyPath = the uninstall subkey, so Windows Installer creates the entry on install and deletes the whole key on removal - husk-free by construction). The pack step RENAMES the vpk key (`MSI:Uniwill-Control-Utility`) to the documented convention **`MSI:{product-code}`** - the real code, read from the Property table - rebinds `UninstallString`/`QuietUninstallString` to `"[INSTALLFOLDER]Uninstall.exe"` (the complete-sweep shim), and adds `UCUProductCode = [ProductCode]` plus Help/URL metadata rows. Identity for the sentinel and the shim is the entry's CONTENT (our DisplayName + GUID-shaped `UCUProductCode`), never the key name.

Why the key name must BE the code (field-proven 2026-10-29, both alternatives produced the same appwiz dialog "An error occurred while trying to uninstall ... It may have already been uninstalled. Would you like to remove ... from the Programs and Features list?" - Yes deletes the entry, the product stays installed): appwiz routes an entry to the Windows Installer APIs by PRODUCT CODE whenever the entry carries `WindowsInstaller=1` - and the ARP anti-hijack enforcer on guarded boxes re-adds that value into Control Panel's VIEW of the entry (the true registry shows it absent in an elevated scan, the dialog still routes by code, so the key name cannot be dodged, it must satisfy the rule). vpk's `MSI:<app-name>` key hits the rule directly (prefix convention); the renamed PLAIN key hits it through the enforcer's re-add. With name = real code, both routes complete: the product-code route resolves (genuine MSI uninstall → sentinel SYSTEM sweep), the shell route (clean boxes, no `WindowsInstaller` value) ignores the name and shell-executes the requireAdministrator shim - UAC consent, complete sweep. The dialog is structurally gone.

The `[INSTALLFOLDER]` expansion (field-proven): it expands to the layout ROOT (where vpk's Update.exe lives), while the MSI ships the shim inside the app dir (current\, packed from publishDir) - the authored target file does not exist, and a shell-execute of a missing file is the same appwiz dialog. The engine install pass closes this: `EnsureShimAtPublishedArpTarget` places the layout shim at the published entry's target (content-identified entry, path inside the engine's own layout tree, pure resolver unit-tested in `ShimPlacementTargetTests`), and the shim discovers its verb exe in both layouts (`Service\` dev, `current\Service\` per-machine).

Values authored into the package at build time mean no runtime registry writer, so the ARP anti-hijack enforcer class never applies to the binding - the 22:13 fight (the runtime redirect's `WindowsInstaller` deletion reverted within a minute) is moot: the binding is no longer a runtime write. The runtime `RepairControlPanelUninstall` pass stays as the belt for dev layouts and hand-installed packages, and the sentinel + evidence floor stay the net for scripted `msiexec /x` routes (scripts, GPO, and the hidden GUID entry's product-code machinery).

Product-code transport (guarded-box signature, 22:37 field): the Windows Installer registration tree is PHANTOM to in-process readers - `MsiEnumProductsW` enumerates 421 other products and never ours, elevated included - while the MsiInstaller event stream shows the registration succeeding (msiserver writes are the ground truth, invisible to the blinding). The published ARP entry stays visible, so the shim and the sentinel read the code from the `UCUProductCode` row first; GUID-key ARP (pre-suppression packs) and the enumeration (ARP-blind views) are the ordered fallbacks. The sentinel's registration probe counts the published entry (our DisplayName + GUID-shaped `UCUProductCode`, non-GUID key) as the live machine registration; its component lifecycle makes the probe vanish exactly when the product does.

### Staging a pack on the guarded box (dev workflow)

The full MSI cycle is the ONLY valid staging (field-proven 2026-10-29: a robocopy-patched layout broke launches; the cycle fixed it):

1. `sc stop UCUEngine` → `sc delete UCUEngine` → `taskkill /f /im "Uniwill Control Utility Engine.exe"` (the guarded keep-running policy relaunches the service between stop and delete; killing the live image completes the pending delete - the same trick `UninstallAsync` uses).
2. `cmd /c "msiexec /x {code} /qn"` then `cmd /c "msiexec /i <msi> /qn"` - **raw line through cmd**: a bare `{GUID}` argument in PowerShell is a script-block literal (braces swallowed → msiexec usage pop-ups on the user's desktop, rc=128 = the verb never ran), and a quoted `"/x"` token is an unrecognized option (usage pop-up again).
3. `"...current\Service\Uniwill Control Utility Engine.exe" --install-engine` - registers the service and runs the ARP belt (shim placement + redirect).
4. Update the staged product-code note (`msi-edit inspect` prints `ProductCode =`); vpk changes it per pack.

Never robocopy-patch the layout to stage a new payload: MSI-written files carry the System-IL mandatory label (SYSTEM writes on guarded boxes) plus SYSTEM ownership; an admin robocopy payload is unlabelled/BA-owned. The mixed-label layout parks the ROOT-STUB launch path (the user's double-click) with the app process frozen at ~2 MB, 1 thread, 0 handles, 0 CPU - while direct launches of `current\...exe` (any elevation) run fully. That elevation/route split is the diagnostic signature; the remedy is the MSI cycle, not a patch. A scripted `/x` on this box is thin ("Removal completed successfully, nothing happened"): sweep leftover ARP husks by key with `reg delete`; the sentinel's net needs the app running, so scripted uninstalls do not exercise the full sweep. `mt.exe` stamping of a fresh Setup.exe can hit a transient AV lock (`c101008d`) - pack.ps1 step 5b retries it bounded.

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
