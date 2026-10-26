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
        }
    }
}
