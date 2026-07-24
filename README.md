# Universal x86 Tuning Utility

[![Download Latest](https://img.shields.io/github/downloads/JamesCJ60/Universal-x86-Tuning-Utility/latest/total?style=flat-square&color=orange&label=Download%20Latest)](https://github.com/JamesCJ60/Universal-x86-Tuning-Utility/releases/latest)
[![Total Downloads](https://img.shields.io/github/downloads/JamesCJ60/Universal-x86-Tuning-Utility/total?style=flat-square&color=orange&label=Download%20Total)](https://github.com/JamesCJ60/Universal-x86-Tuning-Utility/releases/latest)
[![discord](https://img.shields.io/discord/772105072720871435?color=orange&label=Discord&logo=discord&logoColor=white&style=flat-square)](https://discord.gg/9FeYVcbbUQ)
[![Donations](https://img.shields.io/badge/PayPal-00457C?style=flat-square&color=orange&label=Donations&logo=paypal&logoColor=white)](https://www.paypal.com/paypalme/JamesCJ60)
[![Support us on Patreon](https://img.shields.io/endpoint.svg?url=https%3A%2F%2Fshieldsio-patreon.vercel.app%2Fapi%3Fusername%3Duxtusoftware%26type%3Dpatrons&style=flat-square&color=orange&label=Patreon&logoColor=white)](https://patreon.com/uxtusoftware)

<img width="8996" height="1944" alt="Banner" src="https://github.com/user-attachments/assets/ab1623bb-8e16-484b-ac9f-c5a7cee88767" />


This project is still very much a WORK IN PROGRESS!

PLEASE READ THIS: THE SOFTWARE IS PROVIDED “AS IS” WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. MISUSE OF THIS SOFTWARE COULD CAUSE SYSTEM INSTABILITY OR MALFUNCTION.

If you come across any issues or errors with UXTU, please open an issue or ping `@jamescj` in our [discord community server](https://discord.gg/9FeYVcbbUQ). 

If you would like to support the development of Universal x86 Tuning Utility by donating, you can do so via [Patreon](https://www.patreon.com/uxtusoftware) or [PayPal](https://www.paypal.me/JamesCJ60)

## What is UXTU?
- It's a tuning utility aimed to help you tune your device as you wish.
- It's created by the developers of [Ryzen Controller](https://gitlab.com/ryzen-controller-team/ryzen-controller), [Renoir Mobile Tuning](https://github.com/sbski/Renoir-Mobile-Tuning), and [Power Control Panel](https://github.com/project-sbc/Power-Control-Panel-v2).
- It's a little lightweight Ryzen Master/XTU alternative for x86 laptops and computers that allows fine control over your device's processor and GPU/s.
- Works best on Zen-based CPUs/APUs or Intel CPUs that are 4th gen and newer.

## Disclaimers & Cautions
- If you intend to use Universal x86 Tuning Utility in a video/text post online (e.g. YouTube, Reddit), please credit the Universal x86 Tuning Utility team by linking to the Universal x86 Tuning Utility GitHub release page! We ask this so that viewers/readers can download the software from a trusted source, and so the developers get the proper recognition for their work.
- Universal x86 Tuning Utility Team is not liable for any damages that may occur from using Universal x86 Tuning Utility. Please use it at your own risk!
- "AMD", "APU", "Ryzen", and "AMD Ryzen" are trademarked by and belong to Advanced Micro Devices, Inc. Universal x86 Tuning Utility Team makes no claims to these assets and uses them for informational purposes only.
- If you wish to gain developer access to Universal x86 Tuning Utility, ping `@jamescj` in our [discord community server](https://discord.gg/9FeYVcbbUQ). 

## Installation
- Head over to the [releases page](https://github.com/JamesCJ60/Universal-x86-Tuning-Utility/releases)
- Click on the download hyperlink
- Once downloaded, open the .msi installer and follow the instructions presented
- The application should now be installed
- Finally, find the shortcut on your desktop and double-click on it to open UXTU
- UXTU should now open, have fun!!!!

## Getting Started
_New to the Universal x86 Tuning Utility?_ _No worries!_ _This quickstart guide covers everything needed to get started._

#### Launching UXTU
* After successful installation, locate the UXTU icon on your desktop and double click to launch the application.

#### Navigating the Interface
* The UXTU UI has three main sections - Premade Presets, Custom Presets, and Adaptive Mode.
* These three sections allow you to tune your device however you prefer.
* Other sections include the Game Library, System Info, and Automations.

#### Premade Presets
* Universal x86 Tuning Utility offers premade presets specifically designed for Zen-based processors.
* These presets are preconfigured settings tailored for specific use cases, providing convenience and efficiency.
* Premade presets serve as excellent starting points for customization and experimentation while tuning.
* Simply click on a desired preset to apply it to your device.

#### Custom Presets
* The Custom Presets section allows you to create advanced tuning configurations.
* Depending on your system, there are various settings you can modify according to your preferences.
* After configuring your desired settings, there is an option to apply them and/or save them for future use.
* Custom Presets provide a high level of flexibility, allowing users to tailor their device's performance to meet specific needs.

#### Adaptive Mode
* UXTU features an Adaptive Mode, offering an intelligent approach to optimizing processor performance.
* Adaptive Mode implements an adaptive TDP (Thermal Design Power) algorithm, dynamically adjusting power limits to optimize performance while maintaining stability.
* By continuously monitoring processor temperatures, Adaptive Mode intelligently balances power limits to achieve the most stable performance settings.
* Turn on Adaptive Mode with the "Start Adaptive Mode" button, and adjust the polling rate to your preference.

#### Other Sections
* Game Library: View and launch installed games.
* System Info: View device specifications and information.
* Automations: Choose to automatically apply settings during specified events.

#### Tips for Successful Tuning
* Make gradual changes instead of drastic ones to maintain stability and longevity.
* Be cautious of the recommended maximum temperature and TDP for your hardware.

## Credits

UXTU builds on the work of many open-source projects and reverse-engineering efforts. We thank the authors of the following projects for their contributions:

### Core & Inspiration
- [G-Helper (GitHub)](https://github.com/seerge/g-helper) — Lightweight laptop tuning utility that inspired UXTU's design philosophy
- [WPF UI (GitHub)](https://github.com/lepoco/wpfui) — Modern Fluent Design UI library used for the UXTU interface
- [Magpie (GitHub)](https://github.com/Blinue/Magpie) — Translation management library used for localization

### Reverse Engineering References
- [reverse_engineering (GitHub)](https://github.com/zllovesuki/reverse_engineering) — DSDT and WMI reverse engineering references
- [Laptops (GitHub)](https://github.com/ahahahahahMtnf/Laptops/tree/main/Asus/WMI) — ASUS WMI method documentation
- [ADLX SDK Wrapper (GitHub)](https://github.com/JamesCJ60/ADLX-SDK-Wrapper) — AMD ADLX SDK wrapper for GPU controls

### Device Protocol References
- [THRM (GitHub)](https://github.com/TIANLI0/THRM) by TIANLI0 — Primary reference for the Flydigi `5A A5` cooler protocol. Provided the most complete documentation of the frame format, RGB upload sequence, and gear RPM table.
- [watercooler-manager (GitHub)](https://github.com/tomups/watercooler-manager) by tomups — Primary reference for the LCT watercooler BLE protocol. The Python implementation confirmed the Nordic UART UUIDs, frame format, and command byte values.
- [UCC (GitHub)](https://github.com/nanomatters/ucc) by nanomatters — Secondary reference for the LCT watercooler protocol. The C++/Qt implementation provided additional insight into the BLE connection lifecycle, error recovery, and state management.
- [LenovoLegionToolkit (GitHub)](https://github.com/LenovoLegionToolkit/LenovoLegionToolkit) — Reference for the color picker UI pattern and fan curve editor approach.

### Earlier Projects by the UXTU Team
- [Ryzen Controller (GitLab)](https://gitlab.com/ryzen-controller-team/ryzen-controller) — Desktop Ryzen tuning utility
- [Renoir Mobile Tuning (GitHub)](https://github.com/sbski/Renoir-Mobile-Tuning) — Early mobile Ryzen tuning experiment
- [Power Control Panel (GitHub)](https://github.com/project-sbc/Power-Control-Panel-v2) — Power limit control utility
