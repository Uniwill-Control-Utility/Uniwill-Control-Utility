# Uniwill Control Utility

[![Download Latest](https://img.shields.io/github/downloads/jgudec/Uniwill-Control-Utility/latest/total?style=flat-square&color=orange&label=Download%20Latest)](https://github.com/jgudec/Uniwill-Control-Utility/releases/latest)
[![Total Downloads](https://img.shields.io/github/downloads/jgudec/Uniwill-Control-Utility/total?style=flat-square&color=orange&label=Download%20Total)](https://github.com/jgudec/Uniwill-Control-Utility/releases/latest)

<img width="8996" height="1944" alt="Banner" src="https://github.com/user-attachments/assets/ab1623bb-8e16-484b-ac9f-c5a7cee88767" />


This project is still very much a WORK IN PROGRESS!

PLEASE READ THIS: THE SOFTWARE IS PROVIDED "AS IS" WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. MISUSE OF THIS SOFTWARE COULD CAUSE SYSTEM INSTABILITY OR MALFUNCTION.

If you come across any issues or errors with UCU, please open an issue on GitHub.

## What is UCU?
- It's a control utility for Uniwill-based laptop embedded controllers.
- It's created by the developers of [Ryzen Controller](https://gitlab.com/ryzen-controller-team/ryzen-controller), [Renoir Mobile Tuning](https://github.com/sbski/Renoir-Mobile-Tuning), and [Power Control Panel](https://github.com/project-sbc/Power-Control-Panel-v2).
- It provides fine control over your device's processor, GPU, fans, keyboard, and battery via the Uniwill EC and associated hardware interfaces.
- Works best on Zen-based CPUs/APUs or Intel CPUs that are 4th gen and newer.

## Disclaimers & Cautions
- If you intend to use Uniwill Control Utility in a video/text post online (e.g. YouTube, Reddit), please credit the project by linking to the GitHub release page! We ask this so that viewers/readers can download the software from a trusted source, and so the developers get the proper recognition for their work.
- The Uniwill Control Utility Team is not liable for any damages that may occur from using this software. Please use it at your own risk!
- "AMD", "APU", "Ryzen", and "AMD Ryzen" are trademarked by and belong to Advanced Micro Devices, Inc. This project makes no claims to these assets and uses them for informational purposes only.

## Installation
- Head over to the [releases page](https://github.com/jgudec/Uniwill-Control-Utility/releases)
- Click on the download hyperlink
- Once downloaded, open the .msi installer and follow the instructions presented
- The application should now be installed
- Finally, find the shortcut on your desktop and double-click on it to open UCU
- UCU should now open, have fun!!!!

## Getting Started
_New to the Uniwill Control Utility?_ _No worries!_ _This quickstart guide covers everything needed to get started._

#### Launching UCU
* After successful installation, locate the UCU icon on your desktop and double click to launch the application.

#### Navigating the Interface
* The UCU UI has several sections including Presets, Adaptive Mode, Overlay, Automations, and hardware-specific controls.
* These sections allow you to tune your device however you prefer.
* Hardware-specific sections (Fan Control, Keyboard, Light Bar, Battery) appear when supported Uniwill EC hardware is detected.

#### Presets
* Uniwill Control Utility offers premade presets specifically designed for Zen-based processors.
* These presets are preconfigured settings tailored for specific use cases, providing convenience and efficiency.
* Presets serve as excellent starting points for customization and experimentation while tuning.
* Simply click on a desired preset to apply it to your device.

#### Adaptive Mode
* UCU features an Adaptive Mode, offering an intelligent approach to optimizing processor performance.
* Adaptive Mode implements an adaptive TDP (Thermal Design Power) algorithm, dynamically adjusting power limits to optimize performance while maintaining stability.
* By continuously monitoring processor temperatures, Adaptive Mode intelligently balances power limits to achieve the most stable performance settings.
* Turn on Adaptive Mode with the "Start Adaptive Mode" button, and adjust the polling rate to your preference.

#### Other Sections
* Overlay: Configure hardware monitoring overlay settings.
* Automations: Choose to automatically apply settings during specified events.
* System Info: View device specifications and information.
* Fan Control: Customize fan curves (requires supported hardware).
* Keyboard: Control keyboard lighting and effects (requires supported hardware).
* Light Bar: Control RGB light bars (requires supported hardware).
* Battery: Manage battery charging modes and telemetry (requires supported hardware).

#### Tips for Successful Tuning
* Make gradual changes instead of drastic ones to maintain stability and longevity.
* Be cautious of the recommended maximum temperature and TDP for your hardware.

## Credits

UCU builds on the work of many open-source projects and reverse-engineering efforts. We thank the authors of the following projects for their contributions:

### Core & Inspiration
- [G-Helper (GitHub)](https://github.com/seerge/g-helper) — Lightweight laptop tuning utility that inspired UCU's design philosophy
- [WPF UI (GitHub)](https://github.com/lepoco/wpfui) — Modern Fluent Design UI library used for the UCU interface
- [Magpie (GitHub)](https://github.com/Blinue/Magpie) — Translation management library used for localization

### Reverse Engineering References
- [reverse_engineering (GitHub)](https://github.com/zllovesuki/reverse_engineering) — DSDT and WMI reverse engineering references
- [Laptops (GitHub)](https://github.com/ahahahahahMtnf/Laptops/tree/main/Asus/WMI) — ASUS WMI method documentation
- [ADLX SDK Wrapper (GitHub)](https://github.com/jgudec/ADLX-SDK-Wrapper) — AMD ADLX SDK wrapper for GPU controls

### Device Protocol References
- [THRM (GitHub)](https://github.com/TIANLI0/THRM) by TIANLI0 — Primary reference for the Flydigi `5A A5` cooler protocol. Provided the most complete documentation of the frame format, RGB upload sequence, and gear RPM table.
- [watercooler-manager (GitHub)](https://github.com/tomups/watercooler-manager) by tomups — Primary reference for the LCT watercooler BLE protocol. The Python implementation confirmed the Nordic UART UUIDs, frame format, and command byte values.
- [UCC (GitHub)](https://github.com/nanomatters/ucc) by nanomatters — Reference for the LCT watercooler protocol (BLE connection lifecycle, error recovery, state management) and for the ITE 8291 keyboard HID controller protocol (HID device enumeration, ACPI interface, zone-based control commands).
- [tuxedo-drivers (GitLab)](https://gitlab.com/tuxedocomputers/development/packages/tuxedo-drivers) by TUXEDO Computers — Authoritative reference for the ITE 8291 per-key RGB protocol. The Linux kernel driver (`src/ite_8291/ite_8291.c`) provided the exact byte layouts for UserMode entry, row announcement, and the 65-byte BGR-planar output report format. Also provided the EC register definitions (`src/uniwill_interfaces.h`) for NVIDIA GPU power control on Schenker/XMG laptops (cTGP/TPP/DB offset registers and the `0x0743` enable register).
- [LenovoLegionToolkit (GitHub)](https://github.com/LenovoLegionToolkit/LenovoLegionToolkit) — Reference for the color picker UI pattern, fan curve editor approach, and keyboard visualizer layout. **Not** used for keyboard protocol details (Lenovo uses different hardware).
- **MechControl.dll** (decompiled, closed-source) — Schenker/Mechrevo utility. Source for the global keyboard effects protocol (zone-based 9-byte HID feature reports).
- **XMG Control Center / GCUService.exe** (closed-source) — Official Schenker/XMG utility. Studied for HID device communication patterns, handle acquisition, and GPU power control EC register sequences (via `GPUInfoDLL.dll` and `ACPIDriverDll.dll` binary analysis).

### Earlier Projects by the Team
- [Ryzen Controller (GitLab)](https://gitlab.com/ryzen-controller-team/ryzen-controller) — Desktop Ryzen tuning utility
- [Renoir Mobile Tuning (GitHub)](https://github.com/sbski/Renoir-Mobile-Tuning) — Early mobile Ryzen tuning experiment
- [Power Control Panel (GitHub)](https://github.com/project-sbc/Power-Control-Panel-v2) — Power limit control utility
