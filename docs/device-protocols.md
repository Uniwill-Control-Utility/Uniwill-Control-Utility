# Device Communication Protocols

This document describes the communication protocols between UXTU and the connected devices: the XMG Oasis watercooler (LCT) and Flydigi BS series cooling pads (BS1, BS2, BS2 Pro, BS3, BS3 Pro).

It is intended as a reference for developers who want to implement their own communication with these devices.

---

## Table of Contents

- [XMG Oasis Watercooler (LCT)](#xmg-oasis-watercooler-lct)
  - [BLE Transport](#ble-transport)
  - [Device Discovery](#device-discovery-watercooler)
  - [Frame Format](#frame-format-watercooler)
  - [Commands](#commands-watercooler)
  - [Status Response](#status-response-watercooler)
- [Flydigi BS Series Cooling Pads](#flydigi-bs-series-cooling-pads)
  - [Shared Protocol](#shared-protocol)
  - [Frame Format](#frame-format-flydigi)
  - [HID Transport (BS2+)](#hid-transport-bs2)
  - [BLE Transport (BS1)](#ble-transport-bs1)
  - [Commands](#commands-flydigi)
  - [Fan Speed Control](#fan-speed-control)
  - [RGB Control](#rgb-control-bs2)
  - [Status Notification](#status-notification-flydigi)
  - [BS1-Specific Differences](#bs1-specific-differences)
- [Reference Projects](#reference-projects)

---

## XMG Oasis Watercooler (LCT)

The XMG Oasis watercooler uses an **LCT (Liquid Cooling Technology)** module that communicates over **BLE GATT** using the standard Nordic nRF52 UART profile. The device is found in laptops from XMG (Schenker), TUXEDO, PC Specialist, and Eluktronics.

There are two hardware generations:
- **LCT21001** (Mk1) — Original generation
- **LCT22002** (Mk2) — Second generation

### BLE Transport

The device uses the Nordic nRF52 UART over BLE GATT profile:

| Role | UUID |
|------|------|
| Service | `6E400001-B5A3-F393-E0A9-E50E24DCCA9E` |
| TX (write commands to device) | `6E400002-B5A3-F393-E0A9-E50E24DCCA9E` |
| RX (notifications from device) | `6E400003-B5A3-F393-E0A9-E50E24DCCA9E` |

All commands use **WriteWithResponse** (reliable, acknowledged writes). The device may drop commands sent via WriteWithoutResponse if they arrive too quickly.

### Device Discovery {#device-discovery-watercooler}

Discovery is **advertising-name-based**, not VID/PID-based. Scan for BLE devices whose advertising name contains (case-insensitive):

- `"lct"`
- `"be quiet!"`
- `"oasis"`

The device model is detected from the BLE name:
- Name contains `"22002"` → LCT22002 (Mk2)
- Name contains `"21001"` → LCT21001 (Mk1)

### Frame Format {#frame-format-watercooler}

All commands use a **fixed 8-byte frame** with no checksum:

```
Byte 0: 0xFE (FrameStart)
Byte 1: Command byte
Byte 2: Enable flag (0x01 = enabled, 0x00 = disabled)
Byte 3: Parameter A (meaning varies by command)
Byte 4: Parameter B
Byte 5: Parameter C
Byte 6: 0x00 (padding, always zero)
Byte 7: 0xEF (FrameEnd)
```

The device parses fixed byte offsets. There is no length field and no checksum.

### Commands {#commands-watercooler}

| Command | Byte | Description |
|---------|------|-------------|
| Reset | `0x19` | Reset device to default state |
| Status | `0x1A` | Query device status |
| Fan | `0x1B` | Set fan speed |
| Pump | `0x1C` | Set pump voltage |
| RGB | `0x1E` | Set RGB mode/color |

Note: `0x1D` is undefined/unused.

#### Reset

```
FE 19 00 01 00 00 00 EF
```

Sent during disconnect to cleanly reset the device state.

#### Fan

**Fan on:**
```
FE 1B 01 [duty_cycle%] 00 00 00 EF
```

Byte 3 is the duty cycle percentage (0–100). Common values: 25, 50, 75, 90, 95, 100.

**Fan off:**
```
FE 1B 00 00 00 00 00 EF
```

#### Pump

**Pump on:**
```
FE 1C 01 [duty_cycle%] [voltage_code] 00 00 EF
```

- Byte 3: Duty cycle (typically 60)
- Byte 4: Voltage code

| Voltage Code | Voltage |
|-------------|---------|
| `0x00` | 11V |
| `0x02` | 7V |
| `0x03` | 8V |

**Pump off:**
```
FE 1C 00 00 00 00 00 EF
```

> **Note:** 12V pump operation (`0x01`) is intentionally excluded across all known reference implementations for pump longevity reasons.

#### RGB

**RGB on:**
```
FE 1E 01 [R] [G] [B] [mode] EF
```

- Byte 3: Red component (0–255)
- Byte 4: Green component (0–255)
- Byte 5: Blue component (0–255)
- Byte 6: Animation mode

| Mode Code | Animation |
|-----------|-----------|
| `0x00` | Static |
| `0x01` | Breathe |
| `0x02` | Colorful (rainbow cycle) |
| `0x03` | Breathe Color |

**RGB off:**
```
FE 1E 00 00 00 00 00 EF
```

### Status Response {#status-response-watercooler}

After sending a Status query (`FE 1A 01 00 00 00 00 EF`), the device responds on the RX characteristic with:

```
FE 1A [enable] [pump_status] [fan_rpm_lo] [fan_rpm_hi] [temperature] [checksum] EF
```

- `payload[0]` — Pump status byte
- `payload[1..2]` — Fan RPM (little-endian uint16)
- `payload[3]` — Temperature (byte, likely Celsius)

---

## Flydigi BS Series Cooling Pads

The Flydigi BS series uses a unified **`5A A5` frame protocol** across all devices. The inner protocol is identical between HID (BS2+) and BLE (BS1) transports — only the transport wrapper differs.

### Shared Protocol

All Flydigi devices share:
- The same `5A A5` magic header
- The same checksum algorithm (`sum & 0xFF`)
- The same command byte identifiers for overlapping commands
- The same `0xEF` status notification format

### Frame Format {#frame-format-flydigi}

```
Byte 0: 0x5A (Magic)
Byte 1: 0xA5 (Magic)
Byte 2: Command byte (CMD)
Byte 3: Length (LEN) = 2 + payload_length
Byte 4..N: Payload bytes (0 to N bytes)
Byte N+1: Checksum = (CMD + LEN + sum(PAYLOAD[])) & 0xFF
```

**Frame total size:** `2 (magic) + LEN + 1 (checksum) = LEN + 3`

**Example** — Set RPM to 1700:
```
5A A5 21 04 A4 06 CF
CMD=0x21, LEN=0x04, PAYLOAD=A4 06 (0x06A4 = 1700 LE), CHECKSUM=(0x21+0x04+0xA4+0x06)&0xFF = 0xCF
```

### HID Transport (BS2+) {#hid-transport-bs2}

The BS2, BS2 Pro, BS3, and BS3 Pro communicate over **HID**.

**Vendor ID:** `0x37D7`

| Product ID | Model |
|-----------|-------|
| `0x1001` | BS2 |
| `0x1002` | BS2 Pro |
| `0x1003` | BS3 |
| `0x1004` | BS3 Pro |

**HID Report IDs:**
- Input (device → host): `0x01`
- Output (host → device): `0x02`

**Report lengths:**
- Control commands: **25 bytes** (1 report ID + 24 payload, zero-padded)
- RGB light commands: **65 bytes** (1 report ID + 64 payload, zero-padded)

To send a command, wrap the `5A A5` frame in a HID output report: prepend `0x02`, then zero-pad to the report length.

### BLE Transport (BS1) {#ble-transport-bs1}

The BS1 communicates over **BLE GATT** using custom Flydigi UUIDs (not Nordic UART):

| Role | UUID |
|------|------|
| Service | `0000fff0-0000-1000-8000-00805f9b34fb` |
| TX (write commands) | `0000fff2-0000-1000-8000-00805f9b34fb` |
| RX (notifications) | `0000fff1-0000-1000-8000-00805f9b34fb` |

Frames are sent as raw bytes (no Report ID prefix). Use **WriteWithoutResponse** for fan commands, **WriteWithResponse** for heartbeat.

**Heartbeat required:** The BS1 disconnects if no heartbeat (`0x04` command, payload none → `5A A5 04 02 06`) is received within approximately 3 seconds. Send a heartbeat every 2.5 seconds.

**BS1-specific command additions:**
- `0x04` Heartbeat — keepalive (not used on BS2+)

### Commands {#commands-flydigi}

#### Fan Speed Control

**Gear Mode (0x08):**
```
5A A5 08 03 [gear] [checksum]
```

| Gear | Byte | Default RPM (BS2+) | Default RPM (BS1) |
|------|------|-------------------|-------------------|
| Quiet | `0x01` | 1300 | 1300 |
| Standard | `0x02` | 2100 | 2000 |
| Strong | `0x03` | 2800 | 2500 |
| Overclock | `0x04` | 3500 | 3000 |

**Set Gear RPM (0x26):** — BS2+ only
```
5A A5 26 05 [gear_idx] [rpm_lo] [rpm_hi] [checksum]
```

Set a custom RPM for a gear slot. `gear_idx`: 0–3. RPM: little-endian.

**Realtime RPM Mode:**

Enter realtime mode (0x23):
```
5A A5 23 02 25
```

Set RPM (0x21):
```
5A A5 21 04 [rpm_lo] [rpm_hi] [checksum]
```

RPM range: **1300–4000** (BS2+), **1300–3000** (BS1). Value 0 = fan off.

Exit realtime mode (0x24):
```
5A A5 24 02 26
```

**Realtime mode sequence (first call):**
1. Send `0x23` (enter), wait 50ms
2. Send `0x21 [rpm_lo] [rpm_hi]`
3. For fan off (RPM=0): send `0x21 00 00`, wait 50ms, send `0x24` (exit)

**Subsequent RPM changes:** Send `0x21` directly without re-entering mode.

> **Important:** The device exits realtime mode if it doesn't receive RPM commands regularly. Re-send the last RPM every ~5 seconds to maintain the mode.

#### Device Settings — BS2+ only

**Power-On Start (0x0C):**
```
5A A5 0C 03 [0x01=on, 0x02=off] [checksum]
```

**Smart Start/Stop (0x0D):**
```
5A A5 0D 03 [0x00=off, 0x01=immediate, 0x02=delayed] [checksum]
```

**Gear Light (0x48):**
```
5A A5 48 03 [0x00=off, 0x01=on] [checksum]
```

#### Query Commands

**Query Work Mode (0x25):** Response payload contains the current work mode byte (even = manual, odd = realtime).

**Query Gear Table (0x27):** Response contains 8 bytes — 4 gear entries, each 2 bytes little-endian RPM.

### RGB Control — BS2+ only {#rgb-control-bs2}

The BS2+ devices have a built-in RGB light strip. The BS1 does **not** have RGB.

**Full upload sequence:**

```
0x46 01    RGB enable on (sent twice, 5ms apart)
0x45 02    Heartbeat query
0x45 03 01 Heartbeat ack
0x41 02    Upload init
0x41 03 01 Upload init confirm
0x47 00 [10 bytes of f0 header]    Frame 0 (header)
0x47 01 [10 bytes]                 Frame 1
...
0x47 1E [10 bytes]                 Frame 30
0x43 01    Commit/apply
```

Each `0x47` frame uses the **65-byte HID report** (not 25).

**Frame 0 (header) structure** — 10 bytes:
```
[00] [02] [00] [mode_code] [speed] [brightness] [R] [G] [B] [00]
```

- `mode_code`: `0x00` = static, `0x01` = breathing, `0x05` = rotation/flowing
- `speed`: `0x05` = fast, `0x0A` = medium, `0x0F` = slow

**Smart-temp mode** (no frame upload, temperature-reactive lighting):
```
0x46 01 x2 → 0x45 02 → 0x45 03 01 → 0x41 02 → 0x41 03 01 → 0x44 01 → 0x43 01
```

### Status Notification {#status-notification-flydigi}

The device pushes periodic `0xEF` status notifications:

```
5A A5 EF 0B [gear_settings] [work_mode] [reserved] [current_rpm_lo] [current_rpm_hi] [target_rpm_lo] [target_rpm_hi] [extra...]
```

| Byte Offset | Field |
|------------|-------|
| 0 | Gear settings (high nibble = max gear, low nibble = selected gear) |
| 1 | Work mode (even = manual/gear, odd = realtime/auto) |
| 2 | Reserved |
| 3–4 | Current RPM (little-endian) |
| 5–6 | Target RPM (little-endian) |

**Gear codes:**
- Max gear: `0x2` = Standard, `0x4` = Performance, `0x6` = Extreme
- Selected gear: `0x8` = Quiet, `0xA` = Standard, `0xC` = Performance, `0xE` = Extreme

### BS1-Specific Differences

| Aspect | BS2+ (HID) | BS1 (BLE) |
|--------|-----------|-----------|
| Transport | HID (Report ID 0x02) | BLE GATT (no Report ID) |
| Max RPM | 4000 | 3000 |
| RGB | Full (0x41–0x48) | None |
| Sub-gear levels | 3 per gear | None (4 fixed gears) |
| Device settings | Power-On Start, Smart Start/Stop, Gear Light | None |
| Heartbeat | Not required | Required every ~3s |
| Gear default RPMs | 1300/2100/2800/3500 | 1300/2000/2500/3000 |

---

## Reference Projects

The protocol implementations in UXTU were derived from the following reference projects:

- **[THRM](https://github.com/TIANLI0/THRM)** by TIANLI0 — Primary reference for the Flydigi `5A A5` protocol. Contains the most complete documentation of the frame format, RGB upload sequence, and gear RPM table. UXTU's protocol tests verify against THRM's byte-level frame outputs.

- **[watercooler-manager](https://github.com/antlxd/watercooler-manager)** by antlxd — Primary reference for the LCT watercooler BLE protocol. The Python implementation confirmed the Nordic UART UUIDs, frame format, and command byte values.

- **[UCC (Uniwill Control Center)](https://github.com/antlxd/ucc)** by antlxd — Secondary reference for the LCT watercooler protocol. The C++/Qt implementation provided additional insight into the BLE connection lifecycle, error recovery, and state management.

- **[LenovoLegionToolkit](https://github.com/LenovoLegionToolkit/LenovoLegionToolkit)** — Reference for the color picker UI pattern and fan curve editor approach. The slider-based curve editor and popup-based color picker influenced UXTU's Flydigi curve editor design.
