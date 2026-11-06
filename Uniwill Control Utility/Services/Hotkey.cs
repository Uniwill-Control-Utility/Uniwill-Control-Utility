using System;
using System.Windows.Forms;

namespace Uniwill_Control_Utility.Services
{
    /// <summary>
    /// A parsed chord ("Ctrl+Shift+F5") and its match predicate. Extracted
    /// from the overlay service so the user shortcut map (U-204) parses and
    /// matches chords exactly the way the two overlay toggles always have -
    /// one grammar, one matcher, no drift between the two consumers.
    ///
    /// Grammar: optional Ctrl/Control, Alt, Shift tokens plus one primary key
    /// (a <see cref="Keys"/> name, case-insensitive). Modifier-only and
    /// empty chords are rejected: a chord must be pressable.
    /// </summary>
    public readonly record struct Hotkey(
        bool Control,
        bool Alt,
        bool Shift,
        Keys Key)
    {
        public bool Matches(KeyEventArgs e)
        {
            return e.Control == Control &&
                   e.Alt == Alt &&
                   e.Shift == Shift &&
                   e.KeyCode == Key;
        }

        public static bool TryParse(
            string? text,
            out Hotkey hotkey)
        {
            hotkey = default;

            if (string.IsNullOrWhiteSpace(text))
                return false;

            bool control = false;
            bool alt = false;
            bool shift = false;
            bool hasPrimaryKey = false;

            Keys key = Keys.None;

            string[] tokens = text.Split(
                '+',
                StringSplitOptions.TrimEntries |
                StringSplitOptions.RemoveEmptyEntries);

            foreach (string token in tokens)
            {
                if (token.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) ||
                    token.Equals("Control", StringComparison.OrdinalIgnoreCase))
                {
                    control = true;
                    continue;
                }

                if (token.Equals("Alt", StringComparison.OrdinalIgnoreCase))
                {
                    alt = true;
                    continue;
                }

                if (token.Equals("Shift", StringComparison.OrdinalIgnoreCase))
                {
                    shift = true;
                    continue;
                }

                if (hasPrimaryKey)
                    return false;

                if (!Enum.TryParse(token, true, out key))
                    return false;

                hasPrimaryKey = true;
            }

            if (!hasPrimaryKey ||
                key == Keys.None ||
                key is
                    Keys.ControlKey or
                    Keys.LControlKey or
                    Keys.RControlKey or
                    Keys.Menu or
                    Keys.LMenu or
                    Keys.RMenu or
                    Keys.ShiftKey or
                    Keys.LShiftKey or
                    Keys.RShiftKey)
            {
                return false;
            }

            hotkey = new Hotkey(control, alt, shift, key);

            return true;
        }
    }
}
