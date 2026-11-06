# Uniwill EC Battery Charge Control

Register-level notes for the EC battery charge-control family, field-verified on the lab
box 2026-10-30 with the read-only probe `docs\acpi\BatteryDiagnostic` (report:
[Battery Charge-Control Field Probe](../runtime-tests/0.9.4.2-u103-battery-charge-control-probe.md)).
The engine owns these registers; the app reaches them only through the `battery` verbs.

## Charge mode — `0x07A6` (write + read)

The register carries a **mode field plus other firmware-owned bits**. Two chassis families
encode the mode at **different bits**, and the two claims collide at bit 3:

| Family | Mode encoding | Bit 3 means |
|---|---|---|
| **UCU / MechControl IDY_7Y** (this product's chassis) | field at bits 3-5 (`mask 0x38`): `0x08` = High Capacity (100 %), `0x18` = Balanced (80 %), `0x28` = Stationary (60 %) | part of the mode field |
| **OpenRevo registry chassis** (`ec_registry.json`) | mode bytes `0x01` LONGLIFE / `0x11` BALANCE / `0x21` STATION, written read-modify-write preserving `TOUCHPAD_BITMASK 0x08` | touchpad state |

Lab evidence: `0x07A6 = 0x28` decodes cleanly as Stationary in the UCU family and matches
no OpenRevo mode byte. `UCU.Core\Ec\EcBatteryCodec.cs` implements the UCU family:
`EncodeMode` writes the field literal (the OEM tool's literal write, not read-modify-write),
`DecodeMode` masks the field and falls back to High Capacity on an unrecognised value so
the UI always shows a mode. The collision is why nothing from the sibling family is
written on this chassis.

## Charge thresholds — **no registers on this family** (derived view)

The OpenRevo registry exposes `ADDR_BATTERY_LIMIT_UP = 0x07C9` and
`ADDR_BATTERY_LIMIT_DOWN = 0x07D0` (percent; its driver writes `limit` and `limit-5`, and
reads up masked `& 0x7F`). On the lab box those bytes read **`0x07C9 = 0`, `0x07D0 = 95`** —
not a percent pair, failing the registry's own `60..100 / down<up` sanity contract. They are
**not** thresholds here, and UCU writes neither address (the constants exist in `EcEngine`
for the next probe, with no write path).

What this chassis does expose is the mode's capacity ceiling, so the threshold pair the UI
shows is **derived** (read-only) in `EcBatteryCodec`:

- `Up` = the mode's ceiling (100 / 80 / 60 %),
- `Down` = `Up - 5` (the OpenRevo driver's hysteresis convention; pair floor `55 %`),
- `ValidateThresholds(up, down)` pins the contract: up in `60..100`, down in `55..100`,
  down strictly below up. Every derived pair satisfies it by construction.

## Charge gate — `0x0742` bit 4 (read-only)

Bit 4 (`0x10`) marks an active charge gate: charging is held at the ceiling. The same
register/bit is `FAN_TURBO_SUPPORTED` on the fan rows — the EC byte is
**feature-multiplexed**, so each row reads its own bit's meaning and never the other's.
The lab box idles at `0x02` with the battery at 99 % on AC (bit 4 clear).
`EcBatteryCodec.IsChargeGateActive` masks the bit; the Battery page renders it live on the
existing 5 s poll (`battery.get_gate_status`).

## Platform override — `0x0770` (read-only)

`0xFF` is the documented **reset** value — the lab box idles there, and the 0x0770-0x077F
scan reads all `FF` (a `55 AA` marker sits at `0x077E/0x077F`; semantics not claimed).
The pin value is `5` (legacy builds also wrote `4`); **any** value other than `0xFF` means
an override is in effect, i.e. the EC applies charge modes non-naively because a
firmware-side or third-party tool spoofed the platform. `battery.get_platform_override`
surfaces it and the Battery page shows the notice row only while it is set. UCU never
writes this register.

## Capability surface

`battery.get_mode` / `battery.set_mode` (the legacy 100/80/60 path), plus the U-103
read-only verbs `battery.get_gate_status`, `battery.get_thresholds`,
`battery.get_platform_override`. An engine predating them answers `UnsupportedVerb`, and
the app hides the status rows; EC-absent machines keep the page's unavailable card.
Refusals: a mode outside 0-2 is `InvalidValue` (the codec validator) with the register
untouched.
