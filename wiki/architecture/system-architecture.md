# System Architecture

## Executive view

UCU is a two-process product. The UI is a WPF application built on the .NET Generic Host that runs **unelevated** (`asInvoker` manifest). All privileged Embedded Controller work lives in a LocalSystem Windows service, **UCUEngine** (`Uniwill Control Utility Engine.exe`, staged in the `Service\` subfolder), which the UI drives over a named-pipe RPC protocol. This is the XMG Control Center architecture (service + `GCUBridge` shell): the service owns the hardware, the shell owns the user experience, and the shell never needs UAC.

The codebase is divided by project boundary, not convention. The UI assembly cannot reference the hardware assembly — a test pins that. The only path from a UI page to an EC register is:

```text
page → capability interface → RPC proxy (UCU.Client) → \\.\pipe\UCUEngine.v1
     → engine dispatcher → domain queue → EcEngine → \\.\ACPIDriver → EC
```

CPU/GPU tuning backends that never went through the EC (AMD SMU via PawnIO, Intel MSR/MCHBAR, NVAPI/NVML, HID, BLE, WMI, LibreHardwareMonitor, RTSS) remain in the UI process, exactly as before; operations that need admin use a per-operation elevation helper, not a process-wide elevation.

## Projects

| Project | TFM | Role |
| --- | --- | --- |
| `Uniwill Control Utility` | net10.0-windows10.0.22621.0 | WPF UI, asInvoker. Pages, view models, device services (HID/BLE), SMU/Intel/NVIDIA backends, presets, overlay, tray. |
| `UCU.Core` | net10.0 | Pure domain: EC byte encodings, curve validation, supervision policy. No I/O, no Windows APIs. |
| `UCU.Contracts` | net10.0 | The wire contract: verb names, request/response envelope, DTOs, frame codec, capability interfaces, source-generated JSON context. |
| `UCU.Client` | net10.0 | RPC client (`EngineClient`) and the capability-interface proxies the app registers in DI. |
| `UCU.Hardware` | net10.0-windows | EC I/O: `EcEngine` (register-level operations) over `IEcTransport` (`\\.\ACPIDriver` IOCTL transport; fakes in tests). Loaded only by the engine. |
| `UCU.Engine` | net10.0-windows | The service: pipe server, dispatcher, per-domain queues, EC gate, watchdog, app supervisor, SCM installer. |

Test projects mirror each layer: `UCU.Core.Tests` (121), `UCU.Contracts.Tests` (22), `UCU.Hardware.Tests` (52), `UCU.Engine.Tests` (34), `Uniwill Control Utility.Tests` (157 + 4 pre-existing UI-composition failures).

## Component model

```mermaid
flowchart TB
    subgraph AppProcess["Uniwill Control Utility.exe (asInvoker)"]
        Pages[Pages, view models, tray, overlay]
        Caps[Capability proxies (UCU.Client)]
        Telemetry[EcTelemetryService cache]
        Backends[SMU / Intel / NVAPI / HID / BLE / WMI / LHM / RTSS]
        Lifecycle[EngineLifecycle + VelopackUpdateService]
    end

    subgraph EngineProcess["Uniwill Control Utility Engine.exe (LocalSystem, service UCUEngine)"]
        Pipe[EnginePipeServer]
        Dispatch[EngineDispatcher]
        Queues[SerialExecutor queues per domain]
        Gate[EcEngineGate]
        HW[EcEngine (UCU.Hardware)]
        Watchdog[EngineWatchdog]
        Supervise[AppSupervision]
    end

    Pages --> Caps --> Pipe --> Dispatch --> Queues --> Gate --> HW
    Telemetry --> Caps
    Pages --> Backends
    Lifecycle -->|install/start| SCM[(SCM)]
    Supervise -->|CreateProcessAsUser| AppProcess
    HW -->|IOCTL| Driver[\\.\ACPIDriver — UWACPIDriver.sys]
```

## Layer responsibilities

### UI process

- `App.xaml.cs` composes the Generic Host, enforces the single-instance mutex, runs prerequisite and detection work, registers the capability proxies as singletons, and kicks `EngineLifecycle.EnsureAsync()` (install/start the engine) plus task migrations as deferred work.
- Feature pages own UI state and domain orchestration, but EC-touching features (Fan Control, Keyboard, Light Bar, Battery, dashboard telemetry) call the capability interfaces, never hardware types.
- `EcTelemetryService` polls temperatures, fan RPM and fan duty through `IEcTelemetryCapability` and feeds the tray, overlay and dashboard.
- SMU/Intel/NVIDIA/HID/BLE/WMI code stays as-is; `Scripts/Misc/ElevatedProcess` performs the few operations that need admin (webcam and dGPU PnP toggles, touchpad admin flow) with a per-operation `runas` launch.

### Engine process

- `EngineHostFactory` composes the service graph identically for the installed service, `--console` dev runs and in-process tests (fake transport, private pipe name, pipe DACL off).
- `EnginePipeServer` accepts connections on `\\.\pipe\UCUEngine.v1` and frames requests (4-byte length prefix, JSON envelope, request ≤ 256 KiB, response ≤ 1 MiB).
- `EngineDispatcher` checks the protocol version, looks up the verb, selects the queue, and maps exceptions to stable error codes so verb handlers stay terse.
- `EngineDomainQueues`/`SerialExecutor` give each domain (fan, power, mux, keyboard, battery, telemetry, engine) a FIFO, single-threaded executor: hardware writes in a domain stay ordered, a call budget of 30 s and a depth of 64 answer `Busy` instead of growing, and the telemetry queue keeps the UI responsive while a slow fan-curve write runs.
- `EcEngineGate` owns the `EcEngine` lifecycle: first use opens `\\.\ACPIDriver`; if the driver is absent every verb answers `HardwareUnavailable` — the service survives and the UI degrades.
- `EngineWatchdog` fails fast (`FailFast`) when a queue is parked beyond 2 min; SCM failure actions (restart ×3/min) bring the service back.
- `AppSupervision` relaunches the UI app after a crash/kill (see [Startup and lifecycle](startup-and-lifecycle.md)).
- `EngineInstaller` runs the `--install-engine` / `--uninstall-engine` SCM passes.

## Protocol

The RPC is hand-rolled and small by design. A request envelope carries `Id`, `Verb`, `ProtocolVersion`, and optional `Params`; the response carries `Ok`, `Result`, and an error code/message. All JSON goes through `EngineJsonContext` (source-generated contracts); a type missing from the context throws at the first call, which keeps trimming and AOT publishing safe. Verb names, DTOs, capability interfaces, error codes and the frame codec live only in `UCU.Contracts`, the single contract shared by app, client and engine.

The pipe DACL is `D:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;GRGW;;;IU)`: System and Administrators get full control, every interactive user get-and-read — no network access, no other users' impersonation surface.

## Elevation model

- The app manifest is `asInvoker`. Double-clicking the app shows no UAC prompt. The legacy App Paths auto-elevation shim is retired (the uninstall shim still deletes the key on old machines).
- The single UAC prompt of the product is the one-time elevated engine install: `EngineLifecycle.EnsureAsync` self-launches `Service\Uniwill Control Utility Engine.exe --install-engine` (verb `runas`) when the service is missing, and the app runs that ensure **before** the startup EC writes - so a first launch creates the service, starts it, and shows the EC-fed metrics, with no manual step in `services.msc`. `sc create start= auto` plus the service DACL `D:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;CCSWRPRC;;;AU)` means every later start is unelevated: the Authenticated Users ace is query-config (`CC`) + query-status (`SW`) + **RP**/`SERVICE_START` + read-control, asserted by `UCU.Engine.Tests\ServiceSecurityTests.cs`. The ace deliberately excludes `LC`/`SERVICE_QUERY_INTERROGATE` (a LocalSystem service's interrogate right is the token-duplication elevation hole: `InterrogateService` opens the service process's token) and `LO`/`SERVICE_CHANGE_CONFIG` (with it, any authenticated user re-points `binPath` and runs code as LocalSystem). **The DACL forbids .NET's `ServiceController`**: its read-open requests `SERVICE_QUERY_ALL_ACCESS`, which includes interrogate and enumerate-dependents, so every `ServiceController` status query against a healthy install answers Access Denied - and the field proved where that ends (the app read Access Denied as "the DACL needs repair" and elevated on every launch, install log full of `sc start FAILED 1056` against an already-running service). The app's SCM access is therefore `ScmNative` (`Uniwill Control Utility\Services\ScmNative.cs`): opens for exactly `SERVICE_QUERY_STATUS` (liveness, the gate) and `SERVICE_START` (the unelevated repair), rights the ace grants. A registration whose DACL predates the RP grant surfaces as access denied on that minimal open; `EnsureAsync` recognizes that state and re-runs the elevated install, whose `sc sdset` re-asserts the DACL - one prompt, repaired permanently.
- The update apply is the other elevation point: `UCU.Updater.exe` (stamped `requireAdministrator`, compiled by `build\pack.ps1`) stops the engine, runs the elevated Velopack apply, and restarts the engine.
- The DACL is asserted by the service itself, not only installed. `ServiceSelfSecurityService` (engine, service mode) calls `SetServiceObjectSecurity` with the contract SDDL on every start and every 5 minutes. The reason is field-proven, not theoretical: a security product with service protection (observed: Emsisoft Anti-Malware) reverts guarded service descriptors - `sc sdset` answers SUCCESS and `sc sdshow` reads the create template back immediately, in the install pass, in steady state, running and stopped. On such a machine no installer-side write is durable, the app would see AccessDenied forever, and its self-heal would elevate on every launch. The service is the writer that always has rights (LocalSystem, object owner) and its write lands in the SCM record where the app's opens are authorized; a policy revert converges back within the assertion interval. `EngineLifecycle.EnsureAsync` gives one 750 ms grace re-query on AccessDenied (the assertion may be in flight right after a service start) before it will consider elevation: elevation stays once-ever.
- The DACL is verified, not assumed. The install pass reads the live descriptor back with `sc sdshow` after `sc start` and re-asserts `sdset` until the AU contract ace is visible (`EnsureSddlCommittedAsync`). The reason is a proven SCM race: `sc create` commits the new registration's default security descriptor to the registry after sc.exe has already returned, so a `sdset` inside that window is silently reverted - the field incident behind "UAC prompt on every launch": the install log recorded `sdset` rc=0, twenty seconds later `sc sdshow` showed the create template (AU: `KR` only), the app could not open its own service, and its self-heal elevated for a DACL repair on every launch. `ServiceSecurityTests` pins the read-back predicate: exact contract ace accepted, create template rejected.
- The engine service is registered by the app, not by the MSI. `vpk pack` generates the MSI from its own template; shipping a WiX custom-action DLL into that template would couple the product's installation to the packer's internals and run in the Windows Installer's SYSTEM context on every machine that installs - more fragile, more invisible, and it cannot serve the portable (zip) layout at all. The app-side ensure is the single registration path for every layout, it is idempotent, and its one elevation is visible and accountable in the engine's install log.
- The Velopack MSI is a **thin bootstrap, and uninstall custom actions in it are a proven dead end** (field-proven, catalog in `build\pack.ps1` step 5a). The layout is deployed by the packer's own `RustEarlyBootstrap` DLL custom action, so `RemoveFiles` sees almost nothing at uninstall, and every exe-style custom action form fails at uninstall time: the `SourceDir`-resolved target hits 1721 ("empty source location") for the `/qn` + service context, and the forms that pass the costings fail process creation under `msiserver` (-2147450726); deferred custom actions also fire asynchronously after `msiexec /x` reports completion. A complete uninstall therefore cannot be built inside the MSI.
- The complete uninstall is therefore an **app-side self-heal convergence on one verb**. `UninstallSentinel` (app, started right after logging init - before the blocking startup stages, on a machine where startup can block for minutes) probes the machine ARP registration (the `MsiExec.exe`-string product key, with a DisplayName fallback) every 5 s once armed by a present install layout, and when the registration vanishes - the Control Panel / Settings uninstall finished its `msiexec /x` - it self-runs `--uninstall-engine` and exits. That verb is the **single full-cleanup entry point**, and every removal route converges on it: the Control Panel `/x` (via the sentinel), the shim (`Uninstall.exe` forwarding, Control Panel's `UninstallString`), and Velopack's own `--velopackuninstall` hook. The verb runs as **SYSTEM via a one-shot `/ru SYSTEM` scheduled task** because on a policy-guarded machine (observed: Emsisoft service protection) an elevated administrator gets Access Denied on the service descriptor where LocalSystem does not, and the verb's SCM passes must never be at the mercy of the interactive user's consent. The task's `/tr` carries `cmd.exe /c <8.3-short-path script>` - Task Scheduler splits quoted arguments containing escaped quotes (`\"`) and dies with 0x5, so all quoting lives inside the script files, and short paths carry the layout's spaces past cmd's parser (`GetShortPathNameW`). The verb performs the full machine sweep: stop+delete the service (with a process-kill force ladder for the guarded box where stopped services are relaunched by policy and `sc delete` parks), kill the running app, delete the scheduled tasks, remove the machine and per-user ARP husks, sweep stale per-user MSI registrations (their husks are unremovable via the Control Panel - the MSI's cached package is gone; the verb runs `/x` + registration-key surgery), delete the logs, and delete the install layout (the verb's own `current\Service` image survives; a detached tail removes the rest after exit - AV file-guard self-defense can block layout-root deletion even for SYSTEM, a machine-specific condition, not a product defect). `IsBeingUninstalled()` gates app relaunches during the sweep window (engine supervision relaunches the killed app; the gate stops the zombie-window/elevation-prompt loop), and the sentinel fires exactly once.
- A stopped engine is answered by the SCM, not by the pipe. `EngineClient` consults `IEngineServiceGate` (the app implements it with `EngineAvailability`: a `ScmNative` status query - `SERVICE_QUERY_STATUS` only, sub-millisecond, cached 2 s) and refuses a verb with `HardwareUnavailable` **before** connecting. The reason is the cost of a negative: `ConnectAsync` against the missing pipe of a stopped service polls for the whole 5 s connect timeout, and the client is called from the startup EC chain, page constructors, the tray tick and the three EC telemetry caches - un-gated, that is a frozen UI thread and a splash stalled for tens of seconds. "Not installed" and "not openable" count as available: a developer targets a console-mode engine with no service, and a state the app cannot read must not be guessed.
- Global input hooks (overlay hotkeys) live on a dedicated pumped thread (`OverlayManagerService.PostToHookThread`), installed by `BeginInvoke`, never on the UI thread. The proof is a process dump of a frozen app: the UI thread sat inside native `SetWindowsHookEx(WH_KEYBOARD_LL)` - hook installation during the secure-desktop transition of a startup UAC prompt stalls in win32k - and the frozen dispatcher left the splash overlay unable to close (the "Initializing at 92 %, window painted, dashboard never" report: 92 % is phase 1 complete, `50 + 6/6 × 42`, with only the overlay dismissal and dashboard render left, all queued behind the stalled call). The pump is mandatory: low-level hook callbacks deliver on the installing thread's message queue, and an unpumped thread gets the hook silently dropped after `LowLevelHooksTimeout`. A stalled install on the dedicated thread now costs a late-activating hotkey, never a frozen window.
- Per-operation elevation (`ElevatedProcess`) covers the handful of non-EC admin operations.

## State and communication styles

- Capability interfaces with async methods are the UI-to-EC channel; proxies complete on the thread pool, so synchronous call sites use `.GetAwaiter().GetResult()` without deadlocking the dispatcher.
- .NET events for override changes, applied presets, connection state, settings changes, monitoring updates.
- WPF `DispatcherTimer` and thread-pool timers for polling/control loops.
- `Properties.Settings.Default` and JSON stores for durable state; the engine keeps only its logs (`%ProgramData%\UCU\engine\`).

## Concurrency model

- The WPF dispatcher owns visual state; the RPC client is connection-per-call, so UI concurrency is unchanged.
- Hardware ordering is enforced in the engine: one queue per domain, FIFO, one operation at a time; cross-domain writes are intentionally unordered (they touch different register groups).
- The supervision loop, pipe accept loop and watchdog run as hosted background services.
- Shutdown: the app reports its intentional exit over RPC (`engine.notify_app_exit`) first so the supervisor distinguishes exit from crash; the engine keeps running after the app exits (it is a machine service).

## Architectural constraints

- Windows only; app targets the Windows 11 SDK (`10.0.22621.0`).
- EC features require `UWACPIDriver.sys` installed (the app's prerequisite detection checks `C:\Windows\System32\drivers\UWACPIDriver.sys`); the engine service requires the `Service\` payload staged by the csproj targets.
- SMU/PawnIO/NVIDIA features require the bundled native modules beside the app exe (PawnIO `.bin` modules stay in the app root because the RyzenSmu loader resolves them there).
- Hardware behavior is model- and firmware-dependent; SKU resolution and detector results determine which controls are shown. The engine resolves the CPU platform (AMD/Intel) from the kernel's boot-processor vendor identifier; the app resolves the same fact from `Family.TYPE` for its own encodings.

## Related pages

- [Startup and lifecycle](startup-and-lifecycle.md)
- [Hardware integration](hardware-integration.md)
- [Preset and Adaptive Mode pipelines](preset-and-adaptive-pipelines.md)
- [Background processing](background-processing.md)
- [Dependency inventory](dependency-inventory.md)
- [Configuration and data](../operations/configuration-and-data.md)
- [Codebase map](codebase-map.md)
