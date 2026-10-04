# Uniwill Control Utility

A hardware tuning/monitoring application for laptops with **Uniwill-based embedded controllers**, with additional support for AMD Ryzen and Intel CPU tuning, NVIDIA GPU controls, and peripheral device management.

Its purpose is to serve as an open-source alternative to the XMG/PCSpecialist's official Control Center.

UCU was built on top of the foundation laid by [UXTU](https://github.com/JamesCJ60/Universal-x86-Tuning-Utility) (Universal x86 Tuning Utility), reworking/extending its capabilities with Uniwill EC integration, Flydigi cooler support, watercooler control, and a modern Fluent Design interface in order to provide a tailored software for the hardware used by the likes of XMG Neo A25/E25, PCSpecialist Recoil 3 16 (Intel/AMD) and so on.

![Dashboard](screenshots/dashboard.png)



## ⚠️ Disclaimer & Trademarks

* **This project is still very much a WORK IN PROGRESS** - Although a lot of attention and time has been invested into polishing this app, one should still expect some (minor) bugs
* **Unofficial Software:** This project is an independent, community-driven, free and open-source utility. It is **not** affiliated with, endorsed by, or sponsored by Uniwill, Tongfang, Flydigi or any laptop/cooler brand/reseller mentioned above.
* **Trademarks:** All product names, logos, images, brands, trademarks, and registered trademarks are property of their respective owners. Their use here is strictly for identification and compatibility purposes.
* **No Warranty / Hardware Risks:** This software interfaces directly with hardware management features. **Use it at your own risk.** The developer assumes no liability for any hardware damage, system instability, battery degradation, or voided warranties resulting from the use of this utility.

If you come across any issues or errors, please [open an issue on GitHub](https://github.com/Uniwill-Control-Utility/Uniwill-Control-Utility/issues).


## Which hardware has this tool been tested on?
 - **XMG Neo 16 (A25)**
    - CPU: **AMD Ryzen 9 9955HX3D**
    - GPU: **NVIDIA GeForce RTX 5090 Laptop**
    - Panel: **MiniLED**
 - **PCSpecialist Recoil 3 16 (Intel)**
    - CPU: **Intel® Core™ Ultra 9 Processor 275HX**
    - GPU: **NVIDIA GeForce RTX 5080 Laptop**
    - Panel: **MiniLED**
 - **Mechrevo Aurora 16 Ultra**
    - CPU: **Intel® Core™ Ultra 9 Processor 275HX**
    - GPU: **NVIDIA GeForce RTX 5080 Laptop**
    - Panel: **IPS**   
 - **Tuxedo Stellaris 16 Gen7**
    - CPU: **Intel® Core™ Ultra 9 Processor 275HX**
    - GPU: **NVIDIA GeForce RTX 5080 Laptop**
    - Panel: **IPS**   
 - **Mifcom 16**
    - CPU: **AMD Ryzen 9 9955HX3D**
    - GPU: **NVIDIA GeForce RTX 5090 Laptop**
    - Panel: **MiniLED**  

## Testing limitations

Intel CPU voltage control is currently not available.

However, CPU TDP control has been tested and confirmed to be working on both AMD and Intel variants.

## How were the features achieved?

A fair amount of research into other projects that have been mentioned in the Credits section.

Without the hard work of all those developers, most of the features would have been either harder or impossible to implement.

During my personal debugging/tinkering, I happened to stumble upon additional RGB effects for the Keyboard and Light Bar, and attempted to improve them further. 

And truth be told, a local AI agent was a game-changer when it came to discovering how everything ticked, which brings me to the next point.

## AI  Disclaimer
AI (**Qwen 3.6 27B**) was used to:
 - reverse-engineer closed-source software from the Credits section
 - research open-source software from the Credits section
 - implement a fair amount of the backend
 - set up initial skeletons of the UI

## Pull Requests

PRs with additional (well-structured and tested) features are welcome.

We do not mind features/code created with the assistance of AI, as long as it's thoroughly tested by the owner of said code and as long as the code is not of bad quality (aka "AI slop").

## Missing features 
 - MUX switching
 - Power Preset button interception

 These are the features I was unable to implement due to various blockers.
 PRs are more than welcome.

## Features replicated from XMG's Control Center
 - Performance profiles
 - Keyboard global/per-key RGB control
 - Light Bar RGB control
 - Liquid Cooler control
 - CPU and GPU Fan Control
 - Battery control
 - MiniLED Local Dimming control

## Features replicated from Flydigi Space Center
 - Flydigi BS1, BS2, BS2 Pro, BS3 and BS3 Pro support
 - pre-set and custom fan curves
 - RGB control
 - auto power-on

## Additional features 
 - Per-game presets of above features (except the battery, of course)
 - Automatic performance profile application based on the charger being (un)plugged
 - Extra RGB effects/directions for the keyboard
 - Live metric overlay (WIP)
 - System tray controls for Power profile, Hydro UI, Fan and FLydigi control
 - Smart Hydro UI - turn the pump on/off and the fan up/down (noise optimization)
 - Smart Fan Mode - all fans now use one temperature sensor as the reference

# App Features

### **Tray Widget**

Simple metrics and toggles for quick changes.

![Tray](screenshots/tray.png)

### **Dashboard**

At-a-glance system overview showing your device's specifications and live hardware metrics.

- Device identification with laptop image (XMG, PC Specialist Recoil, and more)
- Live CPU, GPU, memory, and battery telemetry cards
- Quick-launch device controls for Flydigi coolers and Hydro UI watercoolers (when supported)

![Dashboard](screenshots/dashboard.png)

### **Presets**

One-click tuning profiles and granular hardware controls for CPU, GPU, and system power management. This is a mix of XMG Control Center's and MSI Afterburner main features.

- **Built-in profiles** — Office, Gaming, and Turbo presets optimized for common use cases
- **Custom profiles** — Save and load your own tuned configurations
- **Status card** — Shows active values for CPU power limits, TjMax, Curve Optimizer, GPU TGP, and Dynamic Boost
- **CPU Power Tuning** (AMD) — Sustained Power Limit (SPL), Fast Power Limit (FPL), TjMax, and Curve Optimizer
- **Intel Tuning** — PL1, PL2 power limits and CPU undervolting via FIVR
- **GPU Tuning** (NVIDIA) — GPU TGP, Dynamic Boost, Dynamic Boost Offset, Core/Memory Clock Offsets, and V-F Curve Editor (a fully working curve editor for undervolting)
- **Custom presets** — Up to 5 custom presets can be set up (mirrored by another set when Power Boost Mode is ON)
- **Preset backup & import** — Export and restore all custom presets and adaptive mode profiles

![Presets](screenshots/presets.png)
![GPU Curve](screenshots/gpu-curve.png)

### **Adaptive Mode**

Intelligent, game-aware performance optimization that automatically adjusts settings based on the active application.

- Per-game profile support — each game gets its own tuned configuration
- **CPU controls** — Power limits (SPL/FPL), TjMax, Curve Optimizer (AMD), PL1/PL2 and undervolt (Intel)
- **GPU controls** — TGP, Dynamic Boost, V-F Curve Editor, Core/Memory Clock Offsets
- **Fan Control** — Custom EC fan curves per game
- **Flydigi cooler** — Custom fan curves and RGB speed per game
- **Keyboard RGB** — Per-game multi-color lighting profiles with auto-detection of running games
- **Hydro UI** - per-game configuration of the liquid cooler RGB and fan/pump speed
- **Light Bar** - per-game customization of the light bar
- **Per-game presets** — Automatic profile switching when games launch or close

![Adaptive](screenshots/adaptive.png)

### **Game Overlay**

Click-through hardware monitoring overlay for real-time performance tracking while gaming.

- Configurable sensor tiles — CPU temp, GPU temp, RAM usage, FPS, and more
- Customizable overlay scale (50–200%) and panel opacity
- Adjustable refresh interval to balance accuracy and overhead
- Global keyboard shortcuts to toggle overlay visibility and sensor polling
- Relies on RTSS for safe metric retrieval (thus avoiding the risk of EAC game bans and such)

![Overlay](screenshots/overlay.png)

### **Automations**

Set-and-forget rules that apply presets automatically when system state changes.

- **Preset on Battery Charge** — Apply a preset when the system switches to AC power
- **Preset on Battery Discharge** — Apply a preset when running on battery
- **Preset on System Resume** — Reapply settings after sleep or hibernation

![Auto](screenshots/auto.png)

### **Fan Control**

Full control over system fan curves with real-time temperature monitoring.

- **Unified or split curves** — Single curve for both fans, or independent CPU and GPU curves
- Multi-point temperature-speed mapping with presets (Silent, Balanced, Performance, Full Speed, Off, Custom)
- 11 thermal threshold zones per curve
- Turbo mode (100% fans) and Restore Auto to return control to the EC firmware
- Live CPU/GPU temperature display
- **Smart Fan Mode** — forces all the fans to use the same temperature source - CPU,GPU or the Hottest of the two


![Fan Control](screenshots/fan-control.png)

### Keyboard

Per-key and global RGB keyboard backlight control via the ITE HID controller.

- **Effects mode** — Multiple animation types (Static, Rainbow, Wave, Breathing, Ripple, Aurora, Spark, Random) with speed control
- **Per-key RGB mode** — 61-key grid editor with individual color swatches
- Multi-color palette picker (4 or 7 colors) for complex effects
- Touch reactive toggle for Ripple, Aurora, Spark, and Random effects
- Idle timer — Auto-turn-off backlight after configurable inactivity
- On/off toggle and brightness slider
- White-backlight-only mode for keyboards without RGB

![Keyboard](screenshots/keyboard.png)
![Per-Key Keyboard](screenshots/keyboard-per-key.png)

### Light Bar

RGB light bar control with multiple effects and color options.

- Power toggle, color picker, and brightness slider (0–100%)
- Effects — Static, Breathing, Wave, Loading, Marquee, Meteor
- Speed slider for animated effects
- Fixed rainbow palette for Wave and Marquee effects

![Light Bar](screenshots/light-bar.png)

### Battery


Battery charging management and live telemetry from the EC.

- **Charging Limit** — Set maximum charge to 100%, 80%, or 60% to extend battery lifespan
- **Live telemetry** (polled every 5 seconds):
  - State of charge, battery health (remaining vs. design capacity)
  - Temperature, cycle count, present voltage, charge/discharge rate
  - Power source indicator (AC / battery)
- Manual refresh button

![Battery](screenshots/battery.png)

### Flydigi Cooler

Control Flydigi laptop cooling pads via BLE (BS1) and HID (BS2, BS2 Pro, BS3, BS3 Pro).

- **Fan Control** with 3 modes:
  - **Manual** — Direct RPM slider (1300–4000 RPM)
  - **Gear Presets** — Quiet, Standard, Strong, Overclock with Low/Medium/High sub-levels
  - **Auto (Curve)** — Temperature-to-RPM fan curve profiles with editable custom curves
- **Smart Control** — Automatic fan speed based on laptop temperature
- **RGB Control** (BS2+ only): Off, Smart-Temp, Static, Rotation, Flowing, Breathing with color, brightness and speed controls
- **Device Settings** — Temperature source, RPM Range avoidance, Smart Start/Stop
- Live RPM and temperature readback
- Dual protocol support — HID (BS2/BS2 Pro/BS3) and BLE (BS1)

![Flydigi](screenshots/flydigi.png)

### Hydro UI 

Full control of LCT liquid cooling systems (e.g. XMG Oasis) via Bluetooth Low Energy.

- BLE device discovery with scan and device selection
- **Pump Voltage** — Off, 7V, 8V, 11V
- **Fan Speed** — Off, 25%, 50%, 75%, 90%, 95%, 100%
- **RGB Effect** — Off, Static, Breathe, Colorful, Breathe Color with color picker
- Auto-connect on system startup and auto-reconnect after system resume
- **Smart Mode** — Set lower and upper temperature thresholds, at which the pump/fan slow down, speed up or turn off/on

![Hydro UI](screenshots/hydro-ui.png)

### Settings

Application preferences, diagnostics, updates, and embedded system information.

- **Application behaviour** — Start on boot, start minimized, minimize-to-tray, reapply settings on start, auto-reapply interval, auto-start Adaptive Mode
- **Preset backup** — Export and import custom and adaptive mode presets as JSON
- **Feature Control** - enable/disable various pages (e.g. the Flydigi cooler page if you do not need it)
- **Diagnostics** — Diagnostic logging level (Off, Error, Info, Debug)
- **System Information** — Device, processor, memory, and battery specs with detailed RAM timings and SPD data

![Settings](screenshots/settings.png)


## Prerequisites

UCU requires the following to be installed and configured before first use:

### UWACPIDriver.sys (Uniwill EC Driver)

UCU communicates with the Uniwill Embedded Controller through the `UWACPIDriver.sys` kernel driver. Check if it's already present at:

```
C:\Windows\System32\drivers\UWACPIDriver.sys
```

If the file exists, you're good to go. If it doesn't, install the official Control Center for your laptop (e.g., XMG Control Center, MechRevo Control Center, or another OEM utility that ships the Uniwill driver), then uninstall it — the driver file persists on disk after uninstall and is all that's needed.

> **Note:** UCU does not bundle or redistribute this driver. Running as Administrator is required for EC access.

### Pawn.IO

[Pawn.IO](https://github.com/JamesCJ60/Pawn.IO) is required for AMD Ryzen CPU tuning features (power limits, Curve Optimizer, TjMax). Download and install it from the [releases page](https://github.com/JamesCJ60/Pawn.IO/releases).

### RivaTuner Statistics Server (RTSS)

RTSS is required for the **Game Overlay** FPS sensor. The overlay integrates with RTSS to display real-time frame rates. Download it from the [RivaTuner website](https://www.guru3d.com/download/rivatuner-statistics-server-download/).

## Installation

1. Head over to the [Releases](https://github.com/jgudec/Uniwill-Control-Utility/releases) page
2. Download the latest zip
3. Extract the zip
4. Launch **Uniwill-Control-Utility.exe**

## Credits

UCU builds on the work of many open-source projects and reverse-engineering efforts. We thank the authors of the following projects for their contributions:

### Foundation
- [UXTU (GitHub)](https://github.com/JamesCJ60/Universal-x86-Tuning-Utility) — Universal x86 Tuning Utility. UCU is built on top of UXTU's codebase, extending it with Uniwill EC support, Flydigi cooler controls, watercooler management, and a rebranded Fluent Design interface.

### Contribution
- [noteMASTER11](https://github.com/noteMASTER11) - help with optimizing the fan control EC write flow, dashboard re-design and tray feature suggestion/foundation
- [Heavyfail](https://github.com/Heavyfail) - help with getting MiniLED Local Dimming control to work

### Core & Inspiration
- [G-Helper (GitHub)](https://github.com/seerge/g-helper) — Lightweight laptop tuning utility that inspired UCU's design philosophy
- [LenovoLegionToolkit (GitHub)](https://github.com/LenovoLegionToolkit/LenovoLegionToolkit) — Reference for the color picker UI pattern, fan curve editor approach, and keyboard visualizer layout.
- [WPF UI (GitHub)](https://github.com/lepoco/wpfui) — Modern Fluent Design UI library used for the UCU interface
- [Magpie (GitHub)](https://github.com/Blinue/Magpie) — Translation management library used for localization

### Device Protocol References
- [THRM (GitHub)](https://github.com/TIANLI0/THRM) (user and repository gone at the time of writing - October 4th 2026) by TIANLI0 — Primary reference for the Flydigi `5A A5` cooler protocol. Provided the most complete documentation of the frame format, RGB upload sequence, and gear RPM table.
- [watercooler-manager (GitHub)](https://github.com/tomups/watercooler-manager) by tomups — Primary reference for the LCT watercooler BLE protocol. The Python implementation confirmed the Nordic UART UUIDs, frame format, and command byte values.
- [UCC (GitHub)](https://github.com/nanomatters/ucc) by nanomatters — Reference for the LCT watercooler protocol (BLE connection lifecycle, error recovery, state management) and for the ITE 8291 keyboard HID controller protocol (HID device enumeration, ACPI interface, zone-based control commands).
- [tuxedo-drivers (GitLab)](https://gitlab.com/tuxedocomputers/development/packages/tuxedo-drivers) by TUXEDO Computers — Authoritative reference for the ITE 8291 per-key RGB protocol. The Linux kernel driver (`src/ite_8291/ite_8291.c`) provided the exact byte layouts for UserMode entry, row announcement, and the 65-byte BGR-planar output report format. Also provided the EC register definitions (`src/uniwill_interfaces.h`) for NVIDIA GPU power control on Schenker/XMG laptops (cTGP/TPP/DB offset registers and the `0x0743` enable register).

- **MechControl** (decompiled, closed-source) — Original by Okashira (Discord: defty1234), fork by [Heavyfail (GitHub)](https://github.com/Heavyfail). Schenker/Mechrevo utility. Source for the global keyboard effects protocol (zone-based 9-byte HID feature reports), MiniLED Local Dimming control (WMI `AcpiTest_MULong` / `GetSetULong` invocation pattern), and UWACPIDriver.sys IOCTL codes (0x9C40A488/0x9C40A48C) for direct EC access.
- **XMG Control Center / GCUService.exe** (closed-source) — Official Schenker/XMG utility. Studied for HID device communication patterns, handle acquisition, and GPU power control EC register sequences (via `GPUInfoDLL.dll` and `ACPIDriverDll.dll` binary analysis).

