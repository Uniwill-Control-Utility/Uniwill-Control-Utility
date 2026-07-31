using System.Windows;
using System.Windows.Media;

namespace Universal_x86_Tuning_Utility.Models
{
    /// <summary>
    /// Represents a single addressable zone on the per-key RGB keyboard.
    /// Zone indices 0-125 map to physical keys. Some indices are unused/unknown.
    /// Layout based on LenovoLegionToolkit LampArray keyboard with XMG Neo 16 A25 zone indices.
    /// Canvas: 735 x 280, keys at 36px with 2px gaps.
    /// Right alignment axis (Del/Bksp/Enter/RShift): X=578.
    /// Numpad columns: 580 / 618 / 656 / 694.
    /// </summary>
    public record KeyboardZone
    {
        /// <summary>
        /// Zone index 0-125 sent to the HID controller.
        /// </summary>
        public int Index { get; init; }

        /// <summary>
        /// Display label for this key (e.g., "A", "Space", "F1").
        /// </summary>
        public string Label { get; init; } = string.Empty;

        /// <summary>
        /// Canvas X position in pixels.
        /// </summary>
        public double X { get; init; }

        /// <summary>
        /// Canvas Y position in pixels.
        /// </summary>
        public double Y { get; init; }

        /// <summary>
        /// Key width in pixels (default 36).
        /// </summary>
        public double Width { get; init; } = 36;

        /// <summary>
        /// Key height in pixels (default 36).
        /// </summary>
        public double Height { get; init; } = 36;

        /// <summary>
        /// Current color for this zone. Default is white.
        /// </summary>
        public Color Color { get; set; } = Colors.White;

        /// <summary>
        /// Returns the full set of keyboard zones with Canvas positions.
        /// Canvas size: 735 x 280.
        /// </summary>
        public static KeyboardZone[] GetAllZones()
        {
            // Right alignment axis for Del / Bksp / Enter / RShift
            const int RIGHT_EDGE = 578;

            return new KeyboardZone[]
            {
                // ===== Row 0: Esc + F1-F12 + PrtSc/Ins/Del + Home/End/PgUp/PgDn (Y=16, H=20) =====
                new() { Index = 105, Label = "Esc",   X = 0,    Y = 16, Width = 34, Height = 20 },
                new() { Index = 106, Label = "F1",    X = 36,   Y = 16, Width = 34, Height = 20 },
                new() { Index = 107, Label = "F2",    X = 72,   Y = 16, Width = 34, Height = 20 },
                new() { Index = 108, Label = "F3",    X = 108,  Y = 16, Width = 34, Height = 20 },
                new() { Index = 109, Label = "F4",    X = 144,  Y = 16, Width = 34, Height = 20 },
                new() { Index = 110, Label = "F5",    X = 180,  Y = 16, Width = 34, Height = 20 },
                new() { Index = 111, Label = "F6",    X = 216,  Y = 16, Width = 34, Height = 20 },
                new() { Index = 112, Label = "F7",    X = 252,  Y = 16, Width = 34, Height = 20 },
                new() { Index = 113, Label = "F8",    X = 288,  Y = 16, Width = 34, Height = 20 },
                new() { Index = 114, Label = "F9",    X = 324,  Y = 16, Width = 34, Height = 20 },
                new() { Index = 115, Label = "F10",   X = 360,  Y = 16, Width = 34, Height = 20 },
                new() { Index = 116, Label = "F11",   X = 396,  Y = 16, Width = 34, Height = 20 },
                new() { Index = 117, Label = "F12",   X = 432,  Y = 16, Width = 34, Height = 20 },
                new() { Index = 118, Label = "PrtSc", X = 468,  Y = 16, Width = 34, Height = 20 },
                new() { Index = 119, Label = "Ins",   X = 504,  Y = 16, Width = 32, Height = 20 },
                // Del: ends at RIGHT_EDGE (571)
                new() { Index = 120, Label = "Del",   X = 538,  Y = 16, Width = 32, Height = 20 },
                // Navigation cluster above numpad — DO NOT TOUCH
                new() { Index = 121, Label = "Home",  X = 576,  Y = 16, Width = 36, Height = 20 },
                new() { Index = 122, Label = "End",   X = 614,  Y = 16, Width = 36, Height = 20 },
                new() { Index = 123, Label = "PgUp",  X = 652,  Y = 16, Width = 36, Height = 20 },
                new() { Index = 124, Label = "PgDn",  X = 690,  Y = 16, Width = 36, Height = 20 },

                // ===== Row 1: ` 1-0 - = Backspace + Numpad top (Y=46) =====
                new() { Index = 84, Label = "`",    X = 0,    Y = 46, Width = 28 },
                new() { Index = 85, Label = "1",    X = 30,   Y = 46 },
                new() { Index = 86, Label = "2",    X = 68,   Y = 46 },
                new() { Index = 87, Label = "3",    X = 106,  Y = 46 },
                new() { Index = 88, Label = "4",    X = 144,  Y = 46 },
                new() { Index = 89, Label = "5",    X = 182,  Y = 46 },
                new() { Index = 90, Label = "6",    X = 220,  Y = 46 },
                new() { Index = 91, Label = "7",    X = 258,  Y = 46 },
                new() { Index = 92, Label = "8",    X = 296,  Y = 46 },
                new() { Index = 93, Label = "9",    X = 334,  Y = 46 },
                new() { Index = 94, Label = "0",    X = 372,  Y = 46 },
                new() { Index = 95, Label = "-",    X = 410,  Y = 46 },
                new() { Index = 96, Label = "=",    X = 448,  Y = 46 },
                // Backspace: ends at RIGHT_EDGE (571)
                new() { Index = 98, Label = "⌫",    X = 487,  Y = 46, Width = 84 },

                // Numpad row 1
                new() { Index = 36, Label = "Num",  X = 576,  Y = 46 },
                new() { Index = 37, Label = "/",    X = 614,  Y = 46 },
                new() { Index = 38, Label = "*",    X = 652,  Y = 46 },
                new() { Index = 39, Label = "-",    X = 690,  Y = 46 },

                // ===== Row 2: Tab + QWERTY + Enter(top) + Numpad (Y=84) =====
                new() { Index = 63, Label = "Tab",  X = 0,    Y = 84, Width = 48 },
                new() { Index = 65, Label = "Q",    X = 50,   Y = 84 },
                new() { Index = 66, Label = "W",    X = 88,   Y = 84 },
                new() { Index = 67, Label = "E",    X = 126,  Y = 84 },
                new() { Index = 68, Label = "R",    X = 164,  Y = 84 },
                new() { Index = 69, Label = "T",    X = 202,  Y = 84 },
                new() { Index = 70, Label = "Y",    X = 240,  Y = 84 },
                new() { Index = 71, Label = "U",    X = 278,  Y = 84 },
                new() { Index = 72, Label = "I",    X = 316,  Y = 84 },
                new() { Index = 73, Label = "O",    X = 354,  Y = 84 },
                new() { Index = 74, Label = "P",    X = 392,  Y = 84 },
                new() { Index = 75, Label = "[",    X = 430,  Y = 84 },
                new() { Index = 76, Label = "]",    X = 468,  Y = 84 },
                // Enter top half: ~1.8 keys (66px), ends at RIGHT_EDGE (571), spans 2 rows
                new() { Index = 77, Label = "↵",    X = 511,  Y = 84, Width = 60, Height = 74 },

                // Numpad row 2
                new() { Index = 40, Label = "7",    X = 576,  Y = 84 },
                new() { Index = 41, Label = "8",    X = 614,  Y = 84 },
                new() { Index = 56, Label = "9",    X = 652,  Y = 84 },
                new() { Index = 64, Label = "+",    X = 690,  Y = 84, Height = 74 },

                // ===== Row 3: Caps + ASDF + ; ' \ + Numpad (Y=122) =====
                // Enter bottom half is covered by the tall Enter from row 2.
                new() { Index = 42, Label = "Caps", X = 0,    Y = 122, Width = 60 },
                new() { Index = 44, Label = "A",    X = 62,   Y = 122 },
                new() { Index = 45, Label = "S",    X = 100,  Y = 122 },
                new() { Index = 46, Label = "D",    X = 138,  Y = 122 },
                new() { Index = 47, Label = "F",    X = 176,  Y = 122 },
                new() { Index = 48, Label = "G",    X = 214,  Y = 122 },
                new() { Index = 49, Label = "H",    X = 252,  Y = 122 },
                new() { Index = 50, Label = "J",    X = 290,  Y = 122 },
                new() { Index = 51, Label = "K",    X = 328,  Y = 122 },
                new() { Index = 52, Label = "L",    X = 366,  Y = 122 },
                new() { Index = 53, Label = ";",    X = 404,  Y = 122 },
                // ' and \ keys: 30px each, fit between ; and Enter with 2px gaps
                new() { Index = 54, Label = "'",    X = 442,  Y = 122, Width = 32 },
                new() { Index = 55, Label = "\\",   X = 476,  Y = 122, Width = 32 },

                // Numpad row 3
                new() { Index = 57, Label = "4",    X = 576,  Y = 122 },
                new() { Index = 58, Label = "5",    X = 614,  Y = 122 },
                new() { Index = 59, Label = "6",    X = 652,  Y = 122 },

                // ===== Row 4: LShift + \ ZXCV + RShift + Numpad (Y=160) =====
                // LShift: 1.3x normal (46px) so X aligns with LAlt
                new() { Index = 22, Label = "⇧",    X = 0,    Y = 160, Width = 46 },
                new() { Index = 23, Label = "\\",   X = 48,   Y = 160 },
                new() { Index = 24, Label = "Z",    X = 86,   Y = 160 },
                new() { Index = 25, Label = "X",    X = 124,  Y = 160 },
                new() { Index = 26, Label = "C",    X = 162,  Y = 160 },
                new() { Index = 27, Label = "V",    X = 200,  Y = 160 },
                new() { Index = 28, Label = "B",    X = 238,  Y = 160 },
                new() { Index = 29, Label = "N",    X = 276,  Y = 160 },
                new() { Index = 30, Label = "M",    X = 314,  Y = 160 },
                new() { Index = 31, Label = ",",    X = 352,  Y = 160 },
                new() { Index = 32, Label = ".",    X = 390,  Y = 160 },
                new() { Index = 33, Label = "/",    X = 428,  Y = 160 },
                // RShift: ends at RIGHT_EDGE (571), wider to absorb LShift shrink
                new() { Index = 35, Label = "⇧",    X = 466,  Y = 160, Width = 104 },

                // Numpad row 4
                new() { Index = 60, Label = "1",    X = 576,  Y = 160 },
                new() { Index = 61, Label = "2",    X = 614,  Y = 160 },
                new() { Index = 62, Label = "3",    X = 652,  Y = 160 },
                new() { Index = 97, Label = "↵",    X = 690,  Y = 160, Height = 74 },

                // ===== Row 5: Modifiers + Space + Arrows + Numpad (Y=198) =====
                new() { Index = 0,  Label = "Ctrl", X = 0,    Y = 198, Width = 46 },
                new() { Index = 2,  Label = "Fn",   X = 48,   Y = 198, Width = 36 },
                new() { Index = 3,  Label = "⊞",    X = 86,   Y = 198 },
                new() { Index = 4,  Label = "Alt",  X = 124,  Y = 198, Width = 36 },
                // Space: ends where M ends (M at X=314, W=36 → right edge 350)
                new() { Index = 7,  Label = "Space",X = 162,  Y = 198, Width = 188 },
                new() { Index = 10, Label = "Alt",  X = 352,  Y = 198, Width = 36 },
                // Right Ctrl: ends at 449 so arrows start right after
                new() { Index = 12, Label = "Ctrl", X = 390,  Y = 198, Width = 59 },
                // Arrow Up: aligned with Arrow Down (X=493)
                new() { Index = 14, Label = "↑",    X = 492,  Y = 204, Width = 38 },

                // Numpad row 5
                new() { Index = 99, Label = "0",    X = 576,  Y = 198, Width = 74 },
                new() { Index = 100, Label = ".",   X = 652,  Y = 198 },

                // ===== Row 6: Arrow cluster (Y=236) =====
                // ← starts after RCtrl (ends 449), → ends at RIGHT_EDGE (571)
                new() { Index = 13, Label = "←",    X = 452,  Y = 242, Width = 38 },
                new() { Index = 18, Label = "↓",    X = 492,  Y = 242, Width = 38 },
                new() { Index = 15, Label = "→",    X = 532,  Y = 242, Width = 38 },
            };
        }
    }
}
