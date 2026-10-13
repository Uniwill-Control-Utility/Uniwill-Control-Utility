# ITE 8291 Keyboard HID Protocol — Complete Reference

This document describes how to control the ITE 8291 keyboard RGB controller on XMG/TUXEDO laptops (e.g., XMG Neo 16 A25) from Windows. It covers both **global effects** (single-color, rainbow, wave, etc.) and **per-key RGB** (individual color per zone).

## Hardware

- **Controller**: ITE 8291 (PID `0xCE00`, `0x6004`, `0x600A`, `0x600B`)
- **Vendor ID**: `0x048D`
- **Interface**: USB HID (not EC registers)
- **Zones**: 6 rows × 21 columns = 126 addressable zones
- **Row numbering**: Row 0 = bottom (modifier row), Row 5 = top (Esc/F-row)

> **Important**: The keyboard backlight is **not** controlled through EC registers (e.g., `0x078C`). The EC only controls the logo LED. All keyboard lighting goes through the ITE HID controller.

## References

This protocol was reverse-engineered from multiple sources:

### Open-Source References

1. **[tuxedo-drivers](https://gitlab.com/tuxedocomputers/development/packages/tuxedo-drivers)** (`src/ite_8291/ite_8291.c`) — Linux kernel HID driver by TUXEDO Computers. This is the **authoritative reference** for the per-key RGB protocol. The `ite8291_write_rows()` function provided the exact byte layouts for UserMode entry, row announcement, and the 65-byte BGR-planar output report format.
2. **[UCC (Uniwill Control Center)](https://github.com/nanomatters/ucc)** — XMG Control Center reverse-engineering project by nanomatters. Provided insight into the HID device enumeration, ACPI interface, and zone-based control commands used by the official XMG Control Center.
3. **[LenovoLegionToolkit](https://github.com/LenovoLegionToolkit/LenovoLegionToolkit)** — Used for keyboard visualizer UI layout patterns, color picker UX, and zone index mapping approach. **Not** used for protocol details (Lenovo uses different hardware).
4. **[BetterRGB-ITE8291-Adaptive](https://github.com/ZavierChen/BetterRGB-ITE8291-Adaptive)** by ZavierChen — Authoritative reference for the software streamer engine (Section 3): the 0xFF03/MI_01 interface-selection scoring, the `enter_custom_mode`/`send_frame` interleaved 512-byte framing, the 102-entry KEYMAP layout, and all 16 effect algorithms.

### Closed-Source References

5. **MechControl.dll** (decompiled) — Schenker/Mechrevo utility. Source for the global effects protocol (zone-based 9-byte feature reports: CMD_SET_COLOR `0x14`, CMD_MODE_BRIGHTNESS `0x08`, CMD_ZONE_ON_OFF `0x1A`).
6. **XMG Control Center (GCUService.exe)** — Official Schenker/XMG utility. Studied for device communication patterns and HID handle acquisition.

## Communication Methods

All communication with the ITE controller uses two Windows APIs:

| Operation | Windows API | Linux Equivalent | Purpose |
|-----------|-------------|------------------|---------|
| **Feature reports** (control commands) | `HidD_SetFeature` | `hid_hw_raw_request(..., HID_FEATURE_REPORT, HID_REQ_SET_REPORT)` | Set mode, brightness, effect, announce row |
| **Output reports** (color data) | `WriteFile` | `hdev->ll_driver->output_report()` | Send per-key color data per row |

### Critical: Output Reports Must Use `WriteFile`

On Windows, `IOCTL_HID_SET_OUTPUT_REPORT` goes through the HID class driver's report descriptor translation. The firmware receives **corrupted data** when using this path for per-key color data.

**Use `WriteFile`** on the HID device handle instead. This sends the raw buffer directly to the USB interrupt-out endpoint, bypassing HID translation — exactly what Linux `hdev->ll_driver->output_report()` does.

```csharp
// P/Invoke declarations (kernel32.dll and hid.dll are Windows system libraries)
[DllImport("kernel32.dll", SetLastError = true)]
static extern bool WriteFile(SafeFileHandle hFile, byte[] lpBuffer,
    uint nNumberOfBytesToWrite, out uint lpNumberOfBytesWritten, nint lpOverlapped);

[DllImport("hid.dll", SetLastError = true)]
static extern bool HidD_SetFeature(SafeFileHandle handle, byte[] report, int length);
```

### Opening the Device

```csharp
[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
static extern SafeFileHandle CreateFile(string path, uint desiredAccess,
    uint shareMode, nint securityAttributes, uint creationDisposition,
    uint flagsAndAttributes, nint templateFile);

// Open with read+write access for WriteFile to work on output reports
var handle = CreateFile(hidPath,
    GENERIC_READ | GENERIC_WRITE,  // 0x80000000 | 0x40000000
    FILE_SHARE_READ | FILE_SHARE_WRITE,
    nint.Zero, OPEN_EXISTING, 0, nint.Zero);
```

Find the HID device path using SetupAPI (`SetupDiGetClassDevs`, `SetupDiEnumDeviceInterfaces`) filtering on `VID_048D`.

---

## 1. Global Effects (Zone-Based Control)

Global effects apply a single color and animation to the entire keyboard. These use **9-byte feature reports** with **report ID 0x00**.

### Report Format

```
Byte[0] = 0x00          // Report ID
Byte[1] = command       // Command type
Byte[2..8] = params     // Command-specific parameters
```

### Commands

| Command | Byte[1] | Purpose |
|---------|----------|---------|
| `CMD_SET_COLOR` | `0x14` | Set RGB color for a zone |
| `CMD_MODE_BRIGHTNESS` | `0x08` | Set effect mode, speed, brightness |
| `CMD_ZONE_ON_OFF` | `0x1A` | Enable/disable a zone |
| `CMD_KEYBOARD_OFF` | `0x09` | Special keyboard-only off command |
| `CMD_ZONE_RESET` | `0x12` | Reset zone state |

### Zone IDs

| Zone | ID | Zone Mask |
|------|-----|-----------|
| Keyboard | `0` | `0x02` |
| Light Bar | `2` | `0x22` |
| Logo | `3` | `0x23` |

### Setting a Static Color

```csharp
// Step 1: Enable zone
SendReport(new byte[] { 0x00, 0x1A, 0x00, 0x01, 0x04, 0x00, 0x00, 0x00, 0x01 });
//             reportID  CMD    zone  on   ?    pad  pad  pad  persist

// Step 2: Set RGB color
SendReport(new byte[] { 0x00, 0x14, 0x00, 0x01, R, G, B, 0x00, 0x00 });
//             reportID  CMD    zone  ?    R    G  B   pad  pad

// Step 3: Set effect mode + brightness + direction
SendReport(new byte[] { 0x00, 0x08, 0x02, effect, speed, brightness, 0x08, direction, 0x00 });
//             reportID  CMD    mask   eff   spd   bri    ?      dir    pad
```

Where `SendReport` calls `HidD_SetFeature(handle, report, 9)`.

### Effect Modes (byte[3] of CMD_MODE_BRIGHTNESS)

| Value | Effect | Notes |
|-------|--------|-------|
| `0x01` | Static (solid color) | |
| `0x02` | Breathing | Multi-color (7 palette) |
| `0x03` | Wave | Supports direction (byte[7]) |
| `0x04` | Random | Non-touch random keys (XCC: "Reactive"). Touch-reactive variant via byte[7]=0x01 |
| `0x05` | Rainbow | Static rainbow gradient |
| `0x06` | Ripple | Non-touch ripple. Touch-reactive variant via byte[7]=0x01 |
| `0x07` | TouchRipple | Ripple triggered by keypress (separate firmware effect) |
| `0x09` | Marquee | |
| `0x0A` | Raindrop | Backlight off, random keys breathe (XCC: "Raindrop") |
| `0x0E` | Aurora | Non-touch Aurora. Touch-reactive variant via byte[7]=0x01 |
| `0x0F` | TouchAurora | Aurora triggered by keypress (separate firmware effect) |
| `0x10` | TouchSpark | Spark triggered by keypress (separate firmware effect) |
| `0x11` | Spark | Non-touch Spark. Touch-reactive variant via byte[7]=0x01 |
| `0x15` | GamingMode | WASD + arrows lit, rest dark (XCC: "Gaming Mode") |
| `0x20` | GamingModeFull | GamingMode + static color on rest |

**Dropped effects:**
- `0x33` (Music / audio-reactive): Requires GCUBridge/GCUService to run for audio capture.
  This service conflicts with direct EC fan control (GCUBridge overrides fan curves), so this
  effect is not available in UXTU. Discovered 2026-08-09.

### Speed (byte[4] of CMD_MODE_BRIGHTNESS)

Range `0x00` (fastest) to `0x0B` (frozen — effect active but no movement). Higher = slower.

#### Slider-to-HID Speed Conversion

UI slider values (1–10, where 1=fastest, 10=slowest) must be **inverted** before sending to
the HID controller:

```
HID_speed = 10 - slider_value    // slider 1→9 (slow in HID), slider 10→0 (fast in HID)
```

This inversion is implemented as `SliderToHidSpeed()` in `Keyboard.xaml.cs`. The Adaptive Mode
page must apply the same conversion when reading from its speed slider before passing the value
to `ApplyKeyboardFromPreset()`.

**Pitfall** (discovered 2026-08-03): If the slider value is sent directly without inversion,
the speed appears to not work at all or behaves in reverse, because the HID controller
interprets slider value 1 as HID speed 1 (very slow) instead of the intended fast speed.

### Brightness (byte[5] of CMD_MODE_BRIGHTNESS)

Range `0x00` to `0x64` (0–100 decimal). For per-key mode, the range is `0x00` to `0x32` (0–50).

### Direction / Touch-Reactive (byte[7] of CMD_MODE_BRIGHTNESS)

This byte has **dual purpose** depending on the effect:

1. **Animation direction** — For effects like Wave (0x03), byte[7] controls the travel direction.
2. **Touch-reactive modifier** — For Random (0x04), Ripple (0x06), Aurora (0x0E), and Spark (0x11),
   byte[7] acts as a variant switch: `0x00` = auto (non-touch), `0x01` = keypress-reactive.

#### Animation Direction (Wave)

Values `0x00` and `0x01` both produce the default Left→Right direction. Values `0x07` and above
wrap back to Left→Right.

| Value | Direction (Wave) | Notes |
|-------|-------------------|-------|
| `0x00` | Left → Right | Default |
| `0x01` | Left → Right | Same as `0x00` |
| `0x02` | Right → Left | |
| `0x03` | Down → Up | |
| `0x04` | Up → Down | |
| `0x05` | Diagonal: bottom-right → top-left | |
| `0x06` | Diagonal: bottom-left → top-right | Not used by GCUService |
| `0x07`+ | Left → Right | Wraps back to default |

**Discovered**: 2026-08-03 via systematic HID report experimentation (HidCapture direction tool).
The GCUService direction enum (LeftRight=0, RightLeft=1, DownUp=2, UpDown=3, OnKeyPressed=4, Sync=5)
does **not** map to the actual hardware directions — the enum names are misleading. The correct
mapping (verified on Wave) is `0x02` = Right→Left, `0x03` = Down→Up, `0x04` = Up→Down, `0x05` = diagonal BR→TL.

#### Touch-Reactive Modifier (Random, Ripple, Aurora, Spark)

For these effects, byte[7] controls whether the effect runs automatically or triggers on keypress:

| Value | Behavior |
|-------|----------|
| `0x00` | Auto — effect runs without user interaction |
| `0x01` | Reactive — effect triggers on keypress |

**Critical**: For Random, Ripple, Aurora, and Spark, the global primer report (byte[2]=0x00)
interferes with the zone-specific apply. Send **only** the zone-specific report
(byte[2]=0x02) with byte[7] set to the desired variant. Do NOT send the primer.

**Discovered**: 2026-08-09. The original XMG CC "Reactive" label in the UI actually mapped to
effect 0x04 with byte[7]=0x00 (non-touch random). The true keypress-reactive variant was
accidentally discovered by sending a nonzero direction byte to effect 0x04.

#### Wave Direction Primer+Delay Requirement (discovered 2026-08-03)

The ITE firmware requires a **two-step primer+delay sequence** for the direction byte to take
effect on Wave. Simply sending `CMD_MODE_BRIGHTNESS` with the desired direction in byte[7] is
not enough — the firmware must first see Wave "armed" with a default direction before it accepts
direction changes.

**Required sequence for Wave with non-default direction:**
```
1. CMD_MODE_BRIGHTNESS: effect=Wave(0x03), direction=LeftRight(0x00)   // primer
2. Sleep(100ms)
3. CMD_MODE_BRIGHTNESS: effect=Wave(0x03), direction=<desired>          // apply
```

Without this sequence, the firmware applies the Wave effect but ignores the direction byte,
defaulting to Left→Right. This is especially noticeable on app startup where `TurnOn()`
initializes the effect engine with Static+LeftRight, and the first `SetEffect(Wave, direction)`
is ignored for the direction byte.

**Root cause hypothesis**: The firmware caches the direction from the first
`CMD_MODE_BRIGHTNESS` report it receives after initialization (which comes from `TurnOn()`
with `byte[7]=0x00`). Subsequent reports update the effect byte but not the direction byte
until Wave has been "armed" by a primer report.

**Note**: This primer+delay sequence is **NOT** used for Random, Ripple, Aurora, or Spark —
those effects skip the primer entirely (see Touch-Reactive Modifier section above).

### Turning Off

```csharp
SendReport(new byte[] { 0x00, 0x09, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 }); // keyboard off
SendReport(new byte[] { 0x00, 0x12, 0x00, 0x03, 0x00, 0x00, 0x00, 0x00, 0x00 }); // zone reset
SendReport(new byte[] { 0x00, 0x08, 0x05, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 }); // mode clear
SendReport(new byte[] { 0x00, 0x1A, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01 }); // zone off
```

### BIOS Persistence

To save settings across reboots, set `byte[3] = 0x09` in the color report and `byte[8] = 0x01`:

```csharp
SendReport(new byte[] { 0x00, 0x14, 0x00, 0x09, R, G, B, 0x00, 0x01 });
```

---

## 2. Per-Key RGB Control

Per-key control allows setting individual RGB colors for each of the 126 zones. The protocol requires a **three-step sequence**:

1. **Enter UserMode** — Feature report tells firmware to accept per-key data
2. **Announce row** — Feature report selects which row (0–5) to update
3. **Send color data** — 65-byte output report via `WriteFile`

### Step 1: Enter UserMode

Send a feature report with **report ID 0x08**:

```
Report ID: 0x08
Data (8 bytes): 08 02 33 00 [brightness] 00 00 00
```

| Byte | Value | Meaning |
|------|-------|---------|
| 0 | `0x08` | Command identifier (duplicates report ID) |
| 1 | `0x02` | Power state: `0x01` = off, `0x02` = on |
| 2 | `0x33` | Animation mode: `0x33` = User/per-key mode |
| 3 | `0x00` | Speed (ignored in UserMode) |
| 4 | `0x00–0x32` | Brightness (0–50 decimal) |
| 5–7 | `0x00` | Reserved |

```csharp
// Build 9-byte report: [reportID] + 8 bytes of data
byte[] report = new byte[] {
    0x08,                          // Report ID
    0x08, 0x02, 0x33, 0x00,       // cmd, power ON, UserMode, speed
    (byte)brightness,              // 0-50
    0x00, 0x00, 0x00
};
HidD_SetFeature(handle, report, report.Length);
```

### Step 2: Announce Row

For each row (0 = bottom, 5 = top), send a feature report with **report ID 0x16**:

```
Report ID: 0x16
Data (8 bytes): 16 00 [row] 00 00 00 00 00
```

| Byte | Value | Meaning |
|------|-------|---------|
| 0 | `0x16` | Command identifier (duplicates report ID) |
| 1 | `0x00` | Reserved |
| 2 | `0x00–0x05` | Row number (0 = bottom, 5 = top) |
| 3–7 | `0x00` | Reserved |

```csharp
byte[] report = new byte[] {
    0x16,                          // Report ID
    0x16, 0x00, (byte)row,         // cmd, pad, row index
    0x00, 0x00, 0x00, 0x00
};
HidD_SetFeature(handle, report, report.Length);
```

### Step 3: Send Color Data (Output Report)

Send a **65-byte output report** via `WriteFile`:

```
Byte 0–1:   Padding (0x00, 0x00)
Byte 2–22:  Blue channel for columns 0–20
Byte 23–43: Green channel for columns 0–20
Byte 44–64: Red channel for columns 0–20
```

The color data is **channel-planar** (all blues, then all greens, then all reds), **not** interleaved per-key (BGR-BGR-BGR).

```csharp
byte[] rowData = new byte[65];
// rowData[0..1] = 0x00 (padding)
// For each column 0..20:
rowData[2 + col]       = blue[col];    // Byte 2-22
rowData[23 + col]      = green[col];   // Byte 23-43
rowData[44 + col]      = red[col];     // Byte 44-64

// CRITICAL: Use WriteFile, NOT IOCTL_HID_SET_OUTPUT_REPORT
WriteFile(handle, rowData, 65, out _, nint.Zero);
```

### Complete Sequence

```csharp
// 1. Enter UserMode
HidD_SetFeature(handle, new byte[] { 0x08, 0x08, 0x02, 0x33, 0x00, brightness, 0x00, 0x00, 0x00 });

// 2-3. For each row 0..5:
for (int row = 0; row < 6; row++)
{
    // Announce row
    HidD_SetFeature(handle, new byte[] { 0x16, 0x16, 0x00, (byte)row, 0x00, 0x00, 0x00, 0x00, 0x00 });

    // Build and send 65-byte color data
    byte[] rowData = BuildRowData(rowColors[row]);
    WriteFile(handle, rowData, 65, out _, nint.Zero);
}
```

### Zone-to-Row/Column Mapping

The keyboard has 126 zones mapped to a 6×21 grid. Zone indices from UI layouts (e.g., KeyboardZone models) do **not** follow row-major order. You need a lookup table:

| Physical Row | ITE Row | Keys (examples) |
|-------------|---------|-----------------|
| Esc / F1-F12 / PgDn | 5 (top) | Esc=col0, F1=col1, ..., PgDn=col19 |
| ` 1-0 / Bksp / Numpad top | 4 | `=col0, 1=col1, ..., Num=col16 |
| Tab / QWERTY / Enter / Numpad | 3 | Tab=col0-1, Q=col2, ..., Enter=col14 |
| Caps / ASDF / Numpad | 2 | Caps=col0-1, A=col2, S=col3, ..., 6(numpad)=col18 |
| LShift / ZXCV / RShift / Numpad | 1 | LShift=col0, Z=col2, ..., RShift=col12-14 |
| Modifiers / Space / Arrows / Numpad | 0 (bottom) | Ctrl=col0, Space=col4-8, ↑=col12, 0=col16-17 |

Some keys span multiple columns (e.g., Space spans cols 4–8, Enter spans 2 rows). Only the first column of a multi-column key needs a color assignment (the firmware handles the rest).

### Turning Off Per-Key Mode

To exit per-key mode and return to global effects, send a UserMode command with a different effect:

```csharp
// Switch back to Static mode (0x01)
HidD_SetFeature(handle, new byte[] { 0x00, 0x08, 0x02, 0x01, speed, brightness, 0x08, 0x00, 0x00 });
```

Or turn the keyboard off entirely using the global off sequence.

---

## 3. BetterRGB Streamer Engine (Software Effects)

The **BetterRGB** engine (UCU's `BetterRgbStreamService` + `BetterRgbFrameRenderer` + `BetterRgbFrameState`) is a byte-level port of the open-source BetterRGB-ITE8291-Adaptive engine (`better_rgb.c`): instead of delegating animation to the keyboard MCU's firmware effects, UCU renders one 512-byte interleaved frame per tick in software and streams it over the **dedicated RGB runtime interface** (Section 3.0) at an adaptive 15–60 FPS (default 30). The wire bytes follow the reference's `enter_custom_mode` / `send_frame` framing exactly.

### 3.0 The 0xFF03 RGB data interface (interface selection)

The ITE device exposes several SetupAPI collection paths; the RGB runtime mode lives on its OWN interface, not the per-key UserMode collection. `resolve_device_path` (better_rgb.c:968–1004) scores every ITE VID `0x048D` path whose `FeatureReportByteLength == 9`:

| Rule | Score |
|------|-------|
| `caps.UsagePage == 0xFF03` | +3 |
| else interface number `MI_01` (parsed from the path) | +2 |
| PID `0x600B` in the path | +1 |
| `MI_00` | never selectable |

UCU's `KeyboardHidService.Open()` enumerates every candidate (`[KBD-HID] enum: usage_page=… iface=… pid=… score=…`), opens the best-scoring path into `_streamHandle`, and exposes `HasRgbStreamInterface`. If no path scores, the streamer refuses to start and the watchdog falls back to the Hardware MCU engine. The firmware usage-1/usage-2 mapping (Sections 1–2) is untouched. **On the XMG NEO A25 the best-scoring path is the SAME HID endpoint as the firmware usage-1 handle** (`hid#vid_048d&pid_600b&mi_01`, usage `0x0001`, usage page `0xFF03`, score 4) — so when `streamPath == keyboardPath`, `Open()` sets `_streamOnKeyboardHandle` and the streamer reuses the single keyboard handle instead of opening a second one on the same path (one live consumer per HID endpoint; two handles on one path would fight over the runtime mode).

### Handshake (before the first frame) — `enter_custom_mode`, verbatim

| Step | Report | Purpose |
|------|--------|---------|
| 1 | `0x12` feature, `00 12 00 03 00 00 00 00 00` | Enter custom/RGB runtime mode |
| 2 | 65-byte `WriteFile`, all zero | Clear the RGB frame buffer |
| 3 | `0x08` feature, `00 08 02 33 00 32 00 00 00` | RGB-mode control — byte[5] is the **constant 0x32**; brightness is NEVER sent to the device (software multiplier only) |

Per frame, `send_frame` (better_rgb.c:1708–1745) ships NINE synchronous HID calls under one device lock: one feature report `00 12 00 00 08 00 00 00 00` followed by 8 × 65-byte `WriteFile` (leading `0x00` + 64 payload bytes of the 512-byte interleaved frame). The frame is RGB-triple-per-key at `offset = col·24 + 1 + 4·(5−row)` per the reference's 102-entry `KEYMAP` (Esc offset 21, CtrlL 1, ShiftL 5, Enter 321, End 477; `set_key` mirrors wide ShiftL→AUX 29 and Enter→AUX 345). The call time feeds the pacer. The old announce-`0x16` + BGR-planar row scheme (Section 2) remains ONLY for firmware per-key mode; the streamer no longer uses it.

### Frame pacing and drop-frame policy

- `periodMs = 1000/fps`; the loop targets a monotonic deadline (`DateTime.UtcNow.Ticks / 10_000L`), never wall-clock drift. If the deadline falls behind by more than two periods it resyncs to `now` (the C pacer's catch-up guard) instead of firing a burst of backlogged frames — that burst was what made 30/60 FPS "reset visually".
- **Adaptive effective FPS** (port of the C pacer's `set_effective_fps` / `record_transport_result`): the loop paces at `effectiveFps`, not the target.
- **Timeline is WALL CLOCK** — C computes `seconds = (GetTickCount64() - startedAt)/1000` fresh every frame inside each effect loop; UCU passes `(monotonic now - streamStartMs)/1000` per frame. Animation speed is therefore constant real-time and the frame rate only changes sampling density. An accumulated `Σ 1/fps` timeline drifts slow/fast exactly when the pacer degrades/recovers — that drift was the residual smoothness gap; a multiplicative `frameIndex/fps` timeline additionally jumps on every pacer step. `frameIndex` stays as the frame-count timeline for stochastic effects and the noise seed.
- A failed send or a send call taking ≥90 % of the frame budget for 3 consecutive frames drops `effectiveFps` by 5 (floor: the static keepalive cap); `effectiveFps × 10` consecutive healthy frames raise it back by 1 toward the target. Clamped to `[15, target]`. A user FPS change snaps it (`set_effective_fps(value, "target changed by user")`).
- **FPS is a set of four UI buttons — 15/30/45/60, default 30** (`btnFps15/30/45/60`; the reference's `TARGET_FPS_MIN`/`MAX`/`DEFAULT` still bounds the range), persisted as `BetterRgbFps` (snapped to the nearest 15 on apply). The pacing artifacts that forced the old 15-FPS hardcode were on the WRONG interface (announce framing on the per-key collection); on the 0xFF03/MI_01 RGB interface the reference's framing paces cleanly at 30/60. The pacer self-degrades (−5 steps) if the device cannot sustain the target, so exposing 60 is safe.
- **Brightness** is a dedicated **2–100 continuous slider** (`BrightnessPercentSlider`, `Minimum="2"`; the C engine's `g_brightness` range is 0–100, the UI floor is raised to 2) — applied as a `BrightnessPercent/100` multiplier on every final pixel of the frame (HSV effects included), exactly like the C engine, not baked into the palette. The firmware `Brightness` field (1–7 levels) stays for the hardware path only. The UserMode ctrl report still caps at 50 (`ITE_MAX_PERKEY_BRIGHTNESS`).
- **The BetterRGB engine has its own control card** (`BetterRgbEffectsCardExpander`, shown only while the engine is on — the firmware Effects card is hidden then; the two engines no longer share controls). Every reference control is a first-class UI control with the reference's range/default: per-effect sliders (breath speed 1–40 def 12, smoothness 2–150 def 60, sparkle rate 1–10 + density 3–20, lightning speed/smoothness/width/concurrent, flame, rain speed/density, matrix speed/density, ripple spread/width, quicksand speed/scale, current speed/length 2–8), **wave/wheel speed 1–10 and rainbow speed 1–20**, **wave angle as 8 direction arrow buttons at 45° increments** (0–315, mirroring the firmware Wave direction buttons), **multi-color palettes as the Flydigi-style `MultiColorPickerControl`** (one picker + "+" button; "-" appears once more than one color is set and enters delete mode, which auto-exits at 1 color — never 0) with per-effect `MaxColors` caps (5 for Raindrop, 3 for Matrix/Quicksand/Current), static layout buttons + zones + zone swatches, single-color pickers (breathing/reactive/ripple), random-color toggles, and reactive fade-duration buttons (Short 10 / Medium 20 / Long 45 ms). The firmware Speed slider keeps its 1–10 firmware convention for the hardware path only.
- Static/per-key sources cap the keepalive at ~10 FPS (identical frames; no point burning 720 reports/s).
- Logging only on failures and every Nth frame — per-call logging at 60 FPS × 12 reports would emit ~720 lines/s.

### Effect model (1:1 with the BetterRGB C engine)

All **14 shipped effects** are ported 1:1 (`BetterRgbEffect` enum in reference `MODE_*` order — the reference's Touch Current and Screen Ambient were dropped from UCU), each with the reference's per-effect globals and slider semantics:

| Effect | C function | Notes |
|--------|-----------|-------|
| Breathing | `effect_breathing` | `t += speed/100` per frame, quantized smooth levels, random-cycle color option |
| Wave | `effect_wave` | `hue = t + (x·cosA + y·sinA)·15`, `t += speed` per frame |
| Sparkle | `effect_sparkle` | age/duration per key, sin² envelope, random HSV spawn, life `max(18, 70−5·speed)` |
| Reactive | `effect_reactive` | `GetAsyncKeyState` keypress hook; held keys full color, fade `fadeLife/duration`; random = per-channel bytes |
| Wheel | `effect_wheel` | hue = `t·dirMul + atan2` about the keyboard centroid |
| Lightning | `effect_lightning` | bolts: `speed=0.05+0.08s`, band/xSigma widths, phosphor EMA attack/release, up to 4 concurrent |
| Flame | `effect_flame` | heat EMA, quantized update interval `max(1, 11−speed)`, ramp palette |
| Raindrop | `effect_rain` | gaussian splats, `speed=0.05+0.03s`, drops = density·3 ≤ 20 |
| Matrix | `effect_matrix` | 4 styles: diagonal streaks (5), columns (24), lasers (3), rings (3) |
| Static | `effect_static` | horizontal zone table / vertical x-rank split, up to 7 zone colors |
| Ripple | `effect_ripple` | keypress-spawned rings, `0.006+0.0015s` key-units/ms, lifetime `2300−120s` ms |
| Rainbow | `effect_rainbow` | wall-clock hue `+= (4+2s) deg/s` |
| Quicksand | `effect_quicksand` | `0.22+0.16s`, wavelength `1.15+0.42·scale`, linear-light palette, 4-sample AA |
| Current | `effect_current` | per-row comets, `0.50+0.40s`, cycle `22+width`, 8-sample AA |

The keypress hook polls `GetAsyncKeyState` over the reference's tracked VK list (letters/digits + the specials table) and maps VK → KEYMAP index → physical (x, y) via the reference's `get_key_x`/`get_key_y` geometry (one-key pitches incl. numpad and row stagger).

### Determinism and reconnect semantics

Animation state lives in an explicit **`BetterRgbFrameState`** (the C effect threads' locals: spawn lists, phosphor EMA, fire heights, matrix columns, wall-clock hue accumulators) owned by the service and **kept across in-place reconnects** — the animation resumes mid-motion with no timeline reset, matching the reference's thread-local continuity. Thread-prologue inits (lightning head/bolt seeds, flame heat, rain/matrix spawn seeds) mirror the C thread bodies. Randomness uses a splitmix64 stream seeded from the clock (same role as C's `srand(time(NULL))` + `rand()`: spawn positions/colors only, deterministic per session and testable).

### Watchdog and fallback semantics

| Trigger | Policy |
|---------|--------|
| 3 failed start attempts (open+handshake+first frame) | Mark streamer unsupported, stop, one-shot hardware apply at same params |
| Send-failure streak ≥ 3 | In-place reconnect keeping `frameIndex`; give up after the ladder |
| AC unplugged (`PowerModeChangedEventArgs`) | Stop streamer + one-shot hardware apply (0% CPU); replug → `StartEngine("ac-restore")` |
| Sleep/wake | `ResumeAfterWake()` — 8 open attempts (controller may be slow after wake) |
| Adaptive override | `SuspendForOverride()` / `ResumeFromOverride()` |
| Adaptive BetterRGB preset | `DeviceApplier.ApplyBetterRgbFromPreset` forces `Engine=BetterRGB` for the override duration, routes the preset's effect/colors/FPS/brightness into the BetterRgb* fields, then `SetParams` + `StartEngine`; firmware presets force `Engine=Hardware` so the HID apply is visible. The user's engine is restored on override lift |

### Shutdown fallback (normal app exit)

On a normal UCU exit (`App.xaml` `Exit="OnExit"`), `OnExit` synchronously calls
`BetterRgbStreamService.StopEngine("shutdown")` **before** the fire-and-forget host
`StopAsync`, so the push happens while the process is still alive:

1. **Release the HID endpoint first** — `ExitUserModeStream()` (CMD_KEYBOARD_OFF / CMD_ZONE_RESET /
   CMD_MODE_BRIGHTNESS / CMD_ZONE_ON_OFF on the 0xFF03 handle) then `_hid.Dispose()`. A fresh
   `KeyboardHidService.Open()` for the fallback would fail while the stream handle still holds
   the shared endpoint (one live consumer per HID endpoint).
2. **Push a firmware effect, in memory only** — ALWAYS Static white at firmware brightness
   level 2 (second-lowest on the 1–7 scale; `ApplyHardwareFallback` maps it to
   `2 × 100 / 7 ≈ 28`), so the keyboard never goes black or keeps a frozen BetterRGB frame.
   The saved settings are never touched — next startup still boots BetterRGB.
3. **Once-guard** — `_shutdownFallbackDone` makes the push run at most once per process even
   though `StopEngine("shutdown")` is reached twice (OnExit + host `StopAsync` teardown); it
   runs regardless of whether the stream loop was running.

The abrupt-close case (process killed, no `OnExit` handler runs) is out of scope — nothing
can push a replacement effect.

### One live consumer rule

While streaming, `BetterRgbStreamService` owns the **only** keyboard-zone HID consumer. The Keyboard page and `DeviceApplier` never send keyboard-zone reports while `IsRunning` — concurrent traffic would corrupt the announce→`WriteFile` row pairing. Param changes route through `SetParams` (atomic snapshot swap). The light bar (usage page 2) is untouched.

### OpenRevo concept mapping

| OpenRevo (Rust/Tauri) | UCU (C#) |
|----------------------|----------|
| `src/driver/streamer/effects.rs` (ports BetterRGB's `source/better_rgb.c`) | `Models/BetterRgbFrameRenderer.cs` |
| streamer loop + watchdog + reconnect tracker | `Services/BetterRgbStreamService.cs` |
| BRFX Rhai script effects | **future work** — not ported |
| low-level keypress hook (touch-reactive effects) | `GetAsyncKeyState` hook over the reference VK list (VK → KEYMAP → physical x,y) |

---

## Common Pitfalls

### 1. HID Apply Lifecycle — `Page_Loaded` Is the Single Source of Truth

The Keyboard page is `Scoped` in DI and reused across navigations. The constructor opens the
HID handle once. **Do NOT call `ApplySettingsToHid()` from the constructor** — let `Page_Loaded`
be the single source of truth for HID applies.

**Why**: The constructor runs when the page is first instantiated (which may be before the user
navigates to it). `Page_Loaded` fires on every navigate-in. If the constructor also applies,
you get:
- Race with Adaptive Mode's HID applies (both use the same HID device)
- Double-apply on first navigation (constructor + `Page_Loaded`)
- No re-apply on subsequent navigations (constructor only runs once for Scoped pages)

**Fix**: Constructor opens HID and syncs UI from settings only. `Page_Loaded` applies to HID
on a background `Task.Run()` with a 200ms initial delay (firmware settle time).

**Discovered**: 2026-08-03. The `_startupApplyDone` flag approach was insufficient because it
prevented ALL future `Page_Loaded` applies, not just the duplicate.

### 2. Firmware Settle Time After HID Open

After opening the HID device, the ITE firmware needs ~200ms to settle before accepting
direction bytes in CMD_MODE_BRIGHTNESS reports. Without this delay, the direction byte is
silently ignored and defaults to Left→Right.

**Fix**: Add `Thread.Sleep(200)` in the background `Task.Run()` before calling
`ApplySettingsToHid()` or `ApplyPerKeyColorsToHid()`.

### 3. Using IOCTL_HID_SET_OUTPUT_REPORT for Color Data

`IOCTL_HID_SET_OUTPUT_REPORT` goes through the Windows HID class driver, which translates the buffer through the HID report descriptor. The firmware receives **corrupted data**. Use `WriteFile` instead.

**Symptom**: Output report "succeeds" (returns 64 bytes) but no keys light up.

### 2. Wrong Report ID for Control Commands

The UserMode command uses report ID `0x08`, and the row announce uses `0x16`. Using report ID `0x00` (which works for global effects) will silently fail for per-key commands.

**Symptom**: Feature report sends successfully but per-key colors don't apply.

### 3. Row Index at Wrong Byte Position

The row index is at **byte[2]** of the 8-byte data buffer (`16 00 row 00 00 00 00 00`), not byte[3].

**Symptom**: All color data goes to row 0 (bottom row only).

### 4. Brightness Out of Range

Per-key brightness range is `0x00–0x32` (0–50), not `0–100` or `0–255`. Values above `0x32` may be clipped or cause undefined behavior.

### 5. Assuming Row-Major Zone Indices

Zone indices from UI layouts (e.g., Esc=105, S=45) are **not** in row-major order. Using `row = index / 21, col = index % 21` will place colors on wrong keys. Use an explicit mapping table.

### 6. Channel Order

The output report uses **BGR-planar** format (all blues first, then all greens, then all reds), not per-key BGR triples. Sending RGB or BGR-interleaved data will produce wrong colors.

---

## Libraries Used

This implementation uses only **Windows system APIs** via P/Invoke:

| Library | Purpose |
|---------|---------|
| `kernel32.dll` | `CreateFile` (open HID device), `WriteFile` (output reports), `DeviceIoControl` (fallback), `CloseHandle` |
| `hid.dll` | `HidD_SetFeature` (feature reports), `HidD_GetFeature` (read feature reports), `HidD_GetPreparsedData`, `HidD_GetHidGuid` |
| `setupapi.dll` | `SetupDiGetClassDevs`, `SetupDiEnumDeviceInterfaces`, `SetupDiGetDeviceInterfaceDetail` (enumerate HID devices) |

No third-party libraries are required. The HID device is accessed directly through Windows HID APIs.
