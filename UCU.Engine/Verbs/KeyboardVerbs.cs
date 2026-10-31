using UCU.Contracts;

namespace UCU.Engine
{
    /// <summary>Keyboard-domain verbs: colour, brightness, effects.</summary>
    internal static class KeyboardVerbs
    {
        public static void Register(Action<string, VerbHandler> add, EcEngineGate gate)
        {
            add(EngineVerbs.Keyboard.SetColor, (p, ct) =>
            {
                KeyboardColorDto dto = EngineSerializer.BindParams<KeyboardColorDto>(p);
                gate.Acquire().SetKeyboardColor(dto.Red, dto.Green, dto.Blue);
                return Task.FromResult<object?>(null);
            });

            add(EngineVerbs.Keyboard.SetBrightness, (p, ct) =>
            {
                KeyboardBrightnessDto dto = EngineSerializer.BindParams<KeyboardBrightnessDto>(p);
                gate.Acquire().SetKeyboardBrightness(dto.Brightness);
                return Task.FromResult<object?>(null);
            });

            add(EngineVerbs.Keyboard.TurnOn, (p, ct) =>
            {
                gate.Acquire().TurnOnKeyboard();
                return Task.FromResult<object?>(null);
            });

            add(EngineVerbs.Keyboard.TurnOff, (p, ct) =>
            {
                gate.Acquire().TurnOffKeyboard();
                return Task.FromResult<object?>(null);
            });

            add(EngineVerbs.Keyboard.SetChinaMode, (p, ct) =>
            {
                gate.Acquire().SetChinaMode();
                return Task.FromResult<object?>(null);
            });

            add(EngineVerbs.Keyboard.TriggerLogoEffect, (p, ct) =>
            {
                gate.Acquire().TriggerLogoEffect();
                return Task.FromResult<object?>(null);
            });

            add(EngineVerbs.Keyboard.TriggerRainbowEffect, (p, ct) =>
            {
                gate.Acquire().TriggerRainbowEffect();
                return Task.FromResult<object?>(null);
            });

            add(EngineVerbs.Keyboard.IsRgbKeyboard, (p, ct) =>
                Task.FromResult<object?>(gate.Acquire().IsRgbKeyboard()));

            add(EngineVerbs.Keyboard.IsWhiteOnlyKeyboard, (p, ct) =>
                Task.FromResult<object?>(gate.Acquire().IsWhiteOnlyKeyboard()));

            // Device locks (U-108). Win-lock rides the trigger byte, so the
            // handler runs on this domain: the fire's RMW of 0x0767 is
            // ordered against the effect writes, and the status byte is the
            // truth the UI reflects (it survives app restarts in the EC —
            // the app re-reads it, never re-applies it).
            add(EngineVerbs.Keyboard.GetWinLock, (p, ct) =>
                Task.FromResult<object?>(gate.Acquire().GetWinLockLocked()));

            add(EngineVerbs.Keyboard.SetWinLock, (p, ct) =>
            {
                DeviceToggleDto dto = EngineSerializer.BindParams<DeviceToggleDto>(p);
                return Task.FromResult<object?>(gate.Acquire().SetWinLockLocked(dto.Enabled));
            });

            add(EngineVerbs.Keyboard.GetFnLock, (p, ct) =>
                Task.FromResult<object?>(gate.Acquire().GetFnLockLocked()));

            add(EngineVerbs.Keyboard.SetFnLock, (p, ct) =>
            {
                DeviceToggleDto dto = EngineSerializer.BindParams<DeviceToggleDto>(p);
                return Task.FromResult<object?>(gate.Acquire().SetFnLockLocked(dto.Enabled));
            });
        }
    }
}
