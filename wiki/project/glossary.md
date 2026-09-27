# Glossary

| Term | Meaning in this project |
| --- | --- |
| Adaptive Mode | Per-game profile engine that detects running applications and overrides selected device and tuning domains. |
| BLE | Bluetooth Low Energy, used for Hydro UI and Flydigi BS1 communication. |
| cTGP | Configurable total graphics power for an NVIDIA laptop GPU. |
| Dynamic Boost / DB | NVIDIA mechanism that shifts additional platform power to the GPU; UCU also programs related EC fields. |
| EC | Embedded controller responsible for laptop platform functions such as fans, battery modes, lighting, and power fields. |
| GATT | BLE service/characteristic model used by peripheral services. |
| HID | Human Interface Device transport used by the ITE keyboard and Flydigi USB coolers. |
| Hydro UI | The product name used by UCU for supported LCT/XMG liquid-cooling devices. |
| LHM | LibreHardwareMonitor, the telemetry library used for CPU/GPU/memory/battery sensor access. |
| MCHBAR | Intel memory-mapped chipset register area accessed through a PawnIO module. |
| MSR | Model-specific register used for low-level Intel processor control and telemetry. |
| NVAPI | NVIDIA driver API. UCU uses both a managed wrapper and selected private calls. |
| NVML | NVIDIA Management Library, loaded by P/Invoke for selected data and controls. |
| PawnIO | Kernel-backed module system used by UCU for AMD SMU and Intel register access. |
| PL1 / PL2 | Intel sustained and short-duration processor power limits. |
| Preset | A named collection of CPU, GPU, and related settings; built-in profiles are Office, Gaming, and Turbo. |
| RTSS | RivaTuner Statistics Server. UCU can read its shared memory as the preferred FPS source. |
| SKU | A resolved laptop CPU/GPU/device combination with model-specific profile values from `SkuData/skus.json`. |
| SMU | AMD System Management Unit, addressed through mailbox commands for power and Curve Optimizer operations. |
| SPL / FPL | Sustained and fast power limits used by AMD profile controls. |
| TCC / TjMax | Processor thermal-control parameters. |
| UCU | Uniwill Control Utility. |
| UXTU | Universal x86 Tuning Utility, the historical foundation of this repository. |
| V-F curve | NVIDIA voltage-frequency curve; UCU stores point-index-to-offset mappings and applies them through a private NVAPI path. |
| WMI | Windows Management Instrumentation, used for system discovery and selected OEM controls. |

[Back to documentation index](../README.md)

