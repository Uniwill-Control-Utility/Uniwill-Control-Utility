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

### Closed-Source References

4. **MechControl.dll** (decompiled) — Schenker/Mechrevo utility. Source for the global effects protocol (zone-based 9-byte feature reports: CMD_SET_COLOR `0x14`, CMD_MODE_BRIGHTNESS `0x08`, CMD_ZONE_ON_OFF `0x1A`).
5. **XMG Control Center (GCUService.exe)** — Official Schenker/XMG utility. Studied for device communication patterns and HID handle acquisition.

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

// Step 3: Set effect mode + brightness
SendReport(new byte[] { 0x00, 0x08, 0x02, effect, speed, brightness, 0x08, 0x00, 0x00 });
//             reportID  CMD    mask   eff   spd   bri    ?      pad  pad
```

Where `SendReport` calls `HidD_SetFeature(handle, report, 9)`.

### Effect Modes (byte[3] of CMD_MODE_BRIGHTNESS)

| Value | Effect |
|-------|--------|
| `0x01` | Static (solid color) |
| `0x02` | Breathing |
| `0x03` | Wave |
| `0x04` | Reactive |
| `0x05` | Rainbow |
| `0x06` | Ripple |
| `0x09` | Marquee |
| `0x0A` | Raindrop |
| `0x0E` | Aurora |
| `0x11` | Spark |
| `0x33` | **Per-key / User mode** |

### Speed (byte[4] of CMD_MODE_BRIGHTNESS)

Range `0x00` (fastest) to `0x0B` (frozen — effect active but no movement). Higher = slower.

### Brightness (byte[5] of CMD_MODE_BRIGHTNESS)

Range `0x00` to `0x64` (0–100 decimal). For per-key mode, the range is `0x00` to `0x32` (0–50).

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

## Common Pitfalls

### 1. Using IOCTL_HID_SET_OUTPUT_REPORT for Color Data

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
