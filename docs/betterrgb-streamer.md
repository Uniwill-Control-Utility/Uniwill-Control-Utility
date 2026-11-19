# BetterRGB streamer engine

UCU's keyboard page can drive the backlight either through the keyboard's own firmware effects card or through a software renderer that streams frames to the ITE controller. The streamer is opt-in and is a port of the open-source [BetterRGB-ITE8291-Adaptive](https://github.com/ZavierChen/BetterRGB-ITE8291-Adaptive) engine.

## Transport

Frames go out on the dedicated ITE `0xFF03` RGB interface, using the reference implementation's interleaved 512-byte framing: one `SetFeature` followed by eight 65-byte writes per frame. Frame rate is button-selected at 15 / 30 / 45 / 60 FPS, default 30, and the pacer self-degrades when the device reports lag.

## Effects

All 14 effects are rendered in software: breathing, wave, sparkle, reactive, wheel, lightning, flame, raindrop, matrix, static, ripple, rainbow, quicksand, current. (Touch Current and Screen Ambient from the reference's 16 were dropped.)

The streamer's own Effects card carries the full reference control set per effect — speeds, smoothness, widths, densities, multi-color palettes capped per effect (up to 5 for Raindrop, 3 for Matrix/Quicksand/Current, 7 for Static, where the colour count *is* the zone count), random-colour toggles, reactive duration, matrix styles, and Wave direction arrow buttons at 45° increments. The firmware Effects card stays separate and unchanged.

## Runtime behaviour

- **Brightness** — 3–100, software-applied.
- **Reactive / ripple** — driven by the system key-state hook, so they respond to real keypresses rather than to a timer.
- **AC auto-policy** — plugged in, the streamer runs; on battery it hands back to the firmware effects and drops to 0% CPU.
- **Sleep / wake** — the streamer resumes on wake.
- **Watchdog** — three failed attempts fall the page back to Hardware MCU.
- **Status line** — live running FPS and frame counter.
- **Exit handling** — on a normal UCU exit the engine hands the keyboard a static white firmware effect at the second-lowest brightness (in memory only, never persisted) so the keyboard never goes black.

Off by default: the hardware path is byte-identical whether or not the streamer has ever been enabled.
