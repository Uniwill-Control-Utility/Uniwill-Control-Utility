# Device protocol references

What each upstream project documented, and which part of UCU it produced. The README keeps the short list; the detail lives here.

## Open-source

### THRM — TIANLI0

[GitHub](https://github.com/TIANLI0/THRM) (user and repository gone at the time of writing, October 4th 2026).

Primary reference for the Flydigi `5A A5` cooler protocol. Provided the most complete documentation of the frame format, the RGB upload sequence, and the gear RPM table.

### watercooler-manager — tomups

[GitHub](https://github.com/tomups/watercooler-manager).

Primary reference for the LCT watercooler BLE protocol. The Python implementation confirmed the Nordic UART UUIDs, the frame format, and the command byte values.

### UCC — nanomatters

[GitHub](https://github.com/nanomatters/ucc).

Reference for the LCT watercooler protocol (BLE connection lifecycle, error recovery, state management) and for the ITE 8291 keyboard HID controller protocol (HID device enumeration, ACPI interface, zone-based control commands).

### tuxedo-drivers — TUXEDO Computers

[GitLab](https://gitlab.com/tuxedocomputers/development/packages/tuxedo-drivers).

Authoritative reference for the ITE 8291 per-key RGB protocol. The Linux kernel driver (`src/ite_8291/ite_8291.c`) provided the exact byte layouts for UserMode entry, row announcement, and the 65-byte BGR-planar output report format. It also provided the EC register definitions (`src/uniwill_interfaces.h`) for NVIDIA GPU power control on Schenker/XMG laptops — the cTGP/TPP/DB offset registers and the `0x0743` enable register.

### BetterRGB-ITE8291-Adaptive — ZavierChen

[GitHub](https://github.com/ZavierChen/BetterRGB-ITE8291-Adaptive).

Primary reference for UCU's BetterRGB software streamer engine (see [betterrgb-streamer.md](betterrgb-streamer.md)). Its `better_rgb.c` established the RGB runtime-mode negotiation on the ITE `0xFF03` / `MI_01` HID interface, the synchronous interleaved 512-byte frame transport (one SetFeature + eight 65-byte writes per frame), the 102-entry KEYMAP frame layout, and the per-effect animation math.

## Closed-source (decompiled)

### MechControl — Okashira (defty1234), fork by Heavyfail

[Heavyfail](https://github.com/Heavyfail). Schenker/Mechrevo utility.

Source for the global keyboard effects protocol (zone-based 9-byte HID feature reports), MiniLED Local Dimming control (the WMI `AcpiTest_MULong` / `GetSetULong` invocation pattern), and the `UWACPIDriver.sys` IOCTL codes (`0x9C40A488` / `0x9C40A48C`) used for direct EC access.

### XMG Control Center / GCUService.exe

The official Schenker/XMG utility. Studied for HID device communication patterns, handle acquisition, GPU power control EC register sequences (binary analysis of `GPUInfoDLL.dll` and `ACPIDriverDll.dll`), and the NVRAM GUID/variable names behind GPU mode persistence.

### OpenRevo — faintonce

[GitHub](https://github.com/faintonce/open-revo). Mechrevo control center (Rust/Tauri), a sibling of UCU's target on the same Uniwill EC family.

- **NVRAM probing** — runtime probing established the correct offset (98) within `OemMagicVariable` for BIOS-visible GPU MUX mode switching. Without it, UCU's NVRAM writes would not have been recognised by the BIOS. Its gatekeeper handshake — the `OemMagicDoor` = 0x01 unlock that must precede every `OemMagicVariable` write on the 50-series chassis generation (`driver\uefi_nvram.rs`) — is ported into UCU's NVRAM write path (U-106).
- **EC OEM mode engine** — its mode-apply path was decompiled for the per-mode `0x0751` fan-control literals that recolour the Q-key LED, the `0x0787` commit write, the `0x0741` manual-control gate, and the fact that stock modes write **no** power registers at all. That is what UCU's stock presets assert through, and what the Performance-button intercept architecture behind UCU's own button handling is modelled on.
- **Power-contract healing** — the `heal_gpu_power_contract` / `heal_cpu_power_contract` commands in `commands\tuning_cmd.rs` are re-implemented (Rust not copied) as UCU's opt-in Power Tuning Guardian (U-107): app-side drift detection against the EC's power-truth registers behind per-feature toggles, with the engine replaying the preset pipeline's own activation sequence.
- **Device-switch registry** — `config\ec_registry.json` (AC-recovery bit on `0x0726`, Fn lock on `0x074E`, win-lock status/trigger pair on `0x0768`/`0x0767`, and the trigger-fire driver calls in `driver\wmi_ec.rs`) is re-implemented as UCU's Windows key lock, Fn key lock and AC power recovery rows in Settings → Hardware management (U-108). Every register's semantics was field-probed on the lab box (`docs\acpi\SwitchDiagnostic`) before a write path shipped, including the discovery that the win-lock trigger is a debounced toggle rather than a state write.
