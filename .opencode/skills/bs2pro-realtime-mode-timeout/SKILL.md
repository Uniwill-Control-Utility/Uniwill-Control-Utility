---
name: bs2pro-realtime-mode-timeout
description: Fix Flydigi BS2 Pro realtime RPM commands reverting to gear preset after a few seconds - firmware exits realtime mode (0x23) without fresh RPM commands (0x21); add a keepalive re-sending the last RPM every ≤5s
---

# BS2 Pro Realtime Mode Timeout & Keepalive

## Problem Pattern

The Flydigi BS2 Pro (and related BS series) cooling pad enters **realtime RPM mode** (0x23) to accept arbitrary RPM commands (0x21). However, the device firmware **exits realtime mode after a period without receiving new RPM commands**, falling back to the current gear preset (~1900-2200 RPM depending on gear settings).

**Classic symptom:** Manual mode RPM control appears to work momentarily (fan drops to commanded RPM), then reverts to 1900-2200 RPM after a few seconds. Auto mode works fine because it sends RPM commands every second.

## Protocol Background

The BS2 Pro uses a state machine with two modes:

| Mode | WorkMode byte | Entry | Exit |
|---|---|---|---|
| Gear mode | even (e.g. 0x04) | 0x24 (ExitRealtimeMode), or timeout | N/A (default) |
| Realtime mode | odd (e.g. 0x05) | 0x23 (EnterRealtimeMode) | 0x24 or firmware timeout |

The device reports its current mode in every 0xEF status notification: `IsRealtimeMode = (WorkMode & 0x01) == 1`

## Root Cause

The device firmware has a **realtime mode timeout** — if it doesn't receive periodic RPM commands (0x21), it exits realtime mode and reverts to gear mode. The timeout duration is not documented but appears to be on the order of 5-10 seconds.

## How the Service Tracks State

`FlydigiCoolerService` (singleton, DI-registered) maintains:

```csharp
private bool _realtimeMode;       // Whether 0x23 was sent without 0x24
private ushort _lastCommandedRpm; // Last RPM we commanded
private bool _hasCommandedRpm;    // Whether _lastCommandedRpm is meaningful
```

State transitions:

| Action | Effect |
|---|---|
| `WriteRealtimeRpmAsync(rpm)` | Enters realtime mode if needed, sends RPM |
| `WriteGearAsync(gear)` | Calls `ResetRealtimeControlState()` |
| 0xEF notification with `!IsRealtimeMode` | Calls `ResetRealtimeControlState()` (device left realtime mode) |
| `ResetRealtimeControlState()` | Sets `_realtimeMode=false`, `_hasCommandedRpm=false`, `_lastCommandedRpm=0` |

## Fix: Periodic Keepalive

Any code path that sets a realtime RPM **must** periodically re-send the same RPM to keep the device in realtime mode. The interval should be **≤5 seconds** to stay ahead of the firmware timeout.

### Pattern from FlydigiSmartControl (Auto mode) — the reference implementation

```csharp
private int _tickCounter;

private void Tick(object? state)
{
    _tickCounter++;

    // Periodic keepalive: re-send the last RPM every 5 ticks (~5 seconds)
    if (_tickCounter % 5 == 0 && _lastCommandedRpm.HasValue && _lastCommandedRpm.Value > 0)
    {
        _ = _coolerService.WriteRealtimeRpmAsync(_lastCommandedRpm.Value);
    }

    // ... normal temperature-driven RPM computation ...
}
```

### Manual mode keepalive (what was missing)

```csharp
// In FlydigiCooler.xaml.cs
private System.Threading.Timer? _rpmKeepaliveTimer;
private ushort _lastManualRpm;

private async void ApplyRpmAsync()
{
    // ... existing debounce disposal ...
    var rpm = (ushort)nudRpm.Value;
    await _coolerService.WriteRealtimeRpmAsync(rpm);
    _lastManualRpm = rpm;

    // Start/refresh keepalive timer
    _rpmKeepaliveTimer?.Dispose();
    _rpmKeepaliveTimer = new System.Threading.Timer(
        _ => Application.Current.Dispatcher.Invoke(async () =>
        {
            if (_coolerService?.IsConnected == true)
            {
                await _coolerService.WriteRealtimeRpmAsync(_lastManualRpm);
            }
        }),
        null,
        TimeSpan.FromSeconds(4),  // fire every 4s to stay ahead of timeout
        TimeSpan.FromSeconds(4));
}
```

Dispose the keepalive timer in `Page_Unloaded` and when switching away from Manual mode.

## Why the Same-Value Skip Matters

`WriteRealtimeRpmAsync` has an optimization:

```csharp
// Skip redundant writes when RPM hasn't changed and we're already in realtime mode
if (_hasCommandedRpm && _lastCommandedRpm == rpm && _realtimeMode)
    return true;
```

This is safe for keepalive because the keepalive's purpose is to send the 0x21 command to the device. If the service detects the device is still in realtime mode (from 0xEF notifications), the skip prevents unnecessary writes. But if the device has silently timed out (and hasn't sent 0xEF yet), the next 0xEF notification will reset the state, and the subsequent keepalive will re-enter realtime mode.

## Diagnostics Checklist

- [ ] Check 0xEF notifications: is `IsRealtimeMode` flipping between true/false?
- [ ] Check `_realtimeMode` state in the service after the RPM reverts
- [ ] Verify no other code path (Adaptive page, gear buttons) is sending gear commands that reset the state
- [ ] Confirm the keepalive timer fires at a frequency shorter than the device's timeout
- [ ] Check whether `CheckAdaptiveModeState` on the Flydigi page is correctly detecting override scenarios

## Related Files

- `Services/Bs2ProService.cs` — HID communication layer, realtime mode state tracking
- `Views/Pages/FlydigiCooler.xaml.cs` — Flydigi page UI, Manual/Auto/Gear mode switching
- `Views/Pages/Adaptive.xaml.cs` — Adaptive page, can override BS2 Pro control via presets
- `Models/Bs2ProProtocol.cs` — Protocol constants, `Bs2ProStatusNotification.IsRealtimeMode`
- `Services/FlydigiSmartControl.cs` — Auto mode keepalive reference implementation

## Prevention Rules

1. **Always add a keepalive** when commanding realtime RPM from a non-periodic code path
2. **Dispose keepalive timers** when switching fan modes or navigating away from the page
3. **Never assume the device holds an RPM command** — the firmware will timeout
4. **Watch for state resets** — gear commands, disconnections, and 0xEF notifications all reset `_realtimeMode`
5. **The Adaptive page uses a static service reference** — it persists across page navigation and can override RPM commands from the Flydigi page
