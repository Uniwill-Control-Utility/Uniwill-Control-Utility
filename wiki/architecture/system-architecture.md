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
- The single UAC prompt of the product is the one-time elevated engine install: `EngineLifecycle.EnsureAsync` self-launches `Service\Uniwill Control Utility Engine.exe --install-engine` (verb `runas`) when the service is missing. `sc create start= auto` plus a DACL granting Authenticated Users `SERVICE_START` means every later start is unelevated.
- The update apply is the other elevation point: `UCU.Updater.exe` (stamped `requireAdministrator`, compiled by `build\pack.ps1`) stops the engine, runs the elevated Velopack apply, and restarts the engine.
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
- Shutdown: the app writes `logs\graceful-exit.flag` first so the supervisor distinguishes exit from crash; the engine keeps running after the app exits (it is a machine service).

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
