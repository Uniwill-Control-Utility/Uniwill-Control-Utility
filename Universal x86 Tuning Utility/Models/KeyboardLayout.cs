using System.Collections.Generic;
using System.Globalization;
using System.Windows.Input;

namespace Universal_x86_Tuning_Utility.Models;

/// <summary>
/// Keyboard layout variants supported by the per-key RGB visualizer.
/// </summary>
public enum KeyboardLayoutType
{
    UsQwerty,
    UkQwerty,
    DeQwertz,
    FrAzerty,
    BeAzerty,
    ItQwerty,
    EsQwerty,
    PtQwerty,
    PlQwerty,
    CzQwertz,
    SkQwertz,
    HuQwertz,
    TrQwerty,
    SeQwerty,
    NoQwerty,
    DkQwerty,
    FiQwerty,
    NlQwerty,
    GrQwerty,
    EeQwerty,
    RuQwerty,
    UaQwerty,
    ChDeQwertz,
    ChFrQwertz,
    HrQwertz,
    DvorakUs,
    DvorakDe,
    UsIntl,
    UsArabic,
}

/// <summary>
/// Detects the current Windows input keyboard layout and maps it to a KeyboardLayoutType.
/// Falls back to UsQwerty for unrecognized layouts.
/// </summary>
public static class KeyboardLayoutDetector
{
    /// <summary>
    /// Maps Windows input culture names to keyboard layout types.
    /// </summary>
    private static readonly Dictionary<string, KeyboardLayoutType> CultureMap = new()
    {
        // US variants
        { "en-US", KeyboardLayoutType.UsQwerty },
        { "en",    KeyboardLayoutType.UsQwerty },
        { "en-US-dv-r-ph", KeyboardLayoutType.DvorakUs },  // Dvorak programmatic ID

        // UK
        { "en-GB", KeyboardLayoutType.UkQwerty },

        // German
        { "de-DE", KeyboardLayoutType.DeQwertz },
        { "de",    KeyboardLayoutType.DeQwertz },

        // French
        { "fr-FR", KeyboardLayoutType.FrAzerty },
        { "fr",    KeyboardLayoutType.FrAzerty },

        // Belgian
        { "fr-BE", KeyboardLayoutType.BeAzerty },
        { "nl-BE", KeyboardLayoutType.BeAzerty },

        // Italian
        { "it-IT", KeyboardLayoutType.ItQwerty },
        { "it",    KeyboardLayoutType.ItQwerty },

        // Spanish
        { "es-ES", KeyboardLayoutType.EsQwerty },
        { "es",    KeyboardLayoutType.EsQwerty },

        // Portuguese
        { "pt-PT", KeyboardLayoutType.PtQwerty },
        { "pt-BR", KeyboardLayoutType.PtQwerty },
        { "pt",    KeyboardLayoutType.PtQwerty },

        // Polish
        { "pl-PL", KeyboardLayoutType.PlQwerty },
        { "pl",    KeyboardLayoutType.PlQwerty },

        // Czech (QWERTZ)
        { "cs-CZ", KeyboardLayoutType.CzQwertz },
        { "cs",    KeyboardLayoutType.CzQwertz },

        // Slovak (QWERTZ)
        { "sk-SK", KeyboardLayoutType.SkQwertz },
        { "sk",    KeyboardLayoutType.SkQwertz },

        // Hungarian (QWERTZ)
        { "hu-HU", KeyboardLayoutType.HuQwertz },
        { "hu",    KeyboardLayoutType.HuQwertz },

        // Turkish
        { "tr-TR", KeyboardLayoutType.TrQwerty },
        { "tr",    KeyboardLayoutType.TrQwerty },

        // Scandinavian
        { "sv-SE", KeyboardLayoutType.SeQwerty },
        { "sv",    KeyboardLayoutType.SeQwerty },
        { "nb-NO", KeyboardLayoutType.NoQwerty },
        { "nn-NO", KeyboardLayoutType.NoQwerty },
        { "no",    KeyboardLayoutType.NoQwerty },
        { "da-DK", KeyboardLayoutType.DkQwerty },
        { "da",    KeyboardLayoutType.DkQwerty },
        { "fi-FI", KeyboardLayoutType.FiQwerty },
        { "fi",    KeyboardLayoutType.FiQwerty },

        // Dutch
        { "nl-NL", KeyboardLayoutType.NlQwerty },
        { "nl",    KeyboardLayoutType.NlQwerty },

        // Greek
        { "el-GR", KeyboardLayoutType.GrQwerty },
        { "el",    KeyboardLayoutType.GrQwerty },

        // Estonian
        { "et-EE", KeyboardLayoutType.EeQwerty },
        { "et",    KeyboardLayoutType.EeQwerty },

        // Russian
        { "ru-RU", KeyboardLayoutType.RuQwerty },
        { "ru",    KeyboardLayoutType.RuQwerty },

        // Ukrainian
        { "uk-UA", KeyboardLayoutType.UaQwerty },
        { "uk",    KeyboardLayoutType.UaQwerty },

        // Swiss
        { "de-CH", KeyboardLayoutType.ChDeQwertz },
        { "fr-CH", KeyboardLayoutType.ChFrQwertz },

        // Croatian/Slovenian (QWERTZ)
        { "hr-HR", KeyboardLayoutType.HrQwertz },
        { "sl-SI", KeyboardLayoutType.HrQwertz },
    };

    /// <summary>
    /// Returns the detected keyboard layout type based on the current Windows input language.
    /// Falls back to UsQwerty if the layout cannot be determined.
    /// </summary>
    public static KeyboardLayoutType Detect()
    {
        // Try input language first (CurrentInputLanguage returns CultureInfo directly)
        var inputCulture = InputLanguageManager.Current.CurrentInputLanguage;
        if (TryMapCulture(inputCulture.Name, out var layout))
            return layout;

        // Fallback: current UI culture
        var uiCulture = CultureInfo.CurrentUICulture;
        if (TryMapCulture(uiCulture.Name, out layout))
            return layout;

        // Fallback: two-letter ISO code
        if (TryMapCulture(inputCulture.TwoLetterISOLanguageName, out layout))
            return layout;

        return KeyboardLayoutType.UsQwerty;
    }

    private static bool TryMapCulture(string name, out KeyboardLayoutType layout)
    {
        // Exact match
        if (CultureMap.TryGetValue(name, out layout))
            return true;

        // Try without subculture (e.g., "en-AU" → "en")
        var baseName = name.Split('-')[0];
        if (CultureMap.TryGetValue(baseName, out layout))
            return true;

        return false;
    }
}
