# Display panel control

UCU's Display page talks to the **laptop panel only** — never to an external monitor — for refresh rate and colour calibration. Both controls address the panel by device name, so a write lands on it whether it is primary or secondary, and Windows' per-display registry carries the setting across primary/secondary switches.

Both controls share one rule: if no selection is offered the setting is hidden rather than disabled — rates the panel does not report are hidden and the visible buttons spread evenly across the row.

## Display refresh rate

Switching goes through the Windows display-mode pathway (`EnumDisplaySettings` / `ChangeDisplaySettingsEx`) rather than a vendor register.

When only an external monitor is in use and the panel is detached (GDI hides it), the panel's known rate set is still offered and the last known rate stays selected. A new selection in that state is deferred and applied once the panel reattaches.

## Colour calibration

Associates a Windows ICC profile with the panel: **Default** (uncalibrated), **sRGB**, **P3** (Display P3), or **Adobe RGB**.

The apply path is the genuine Windows colour-engine switch — `mscms.dll` `InstallColorProfile` + `WcsSetDefaultColorProfile` at `SYSTEM_WIDE` and `CURRENT_USER` scope, which is the exact sequence XMG Control Center's `GCUUtil.exe` runs — against the built-in spool profiles (`sRGB.icm`, `DCIP3.icm`, `AdobeRGB.icm`). A bare `ICM\ProfileAssociations` registry write only relabels the selection; this path forces the colour engine to reload and actually repaint the panel.

When the panel is detached (external-only), the last known profile stays selected and a new selection is deferred until it reattaches.

The profiles ship with the OEM Control Center, not with Windows: see the README's *Optional: ICC colour profiles* section for how to get them onto a machine that never had it installed.

## GPU display mode and power saving

The MUX switch writes the mode to UEFI NVRAM (through the `OemMagicVariable` gatekeeper handshake) so it survives restarts; the physical MUX change needs a reboot. GPU Power Saving is the runtime equivalent for Hybrid mode: it toggles the discrete GPU through PnP hotswap without a reboot, leaving **Auto** to Windows/NVIDIA Optimus.
