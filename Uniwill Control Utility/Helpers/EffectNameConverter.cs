using System;
using System.ComponentModel;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows.Data;
using Uniwill_Control_Utility.Services;

namespace Uniwill_Control_Utility.Helpers
{
    /// <summary>
    /// Increments <see cref="Version"/> whenever the UI culture changes. Effect-name
    /// bindings include it as a MultiBinding input so they re-evaluate on language
    /// switch — ComboBox selected-value presenters cache their converter output and
    /// do not rebuild when the selection is restored to an equal value, so a
    /// self-refreshing binding is the only reliable refresh path.
    /// </summary>
    internal sealed class CultureToken : INotifyPropertyChanged
    {
        public static readonly CultureToken Instance = new();

        private static readonly PropertyChangedEventArgs s_versionArgs = new(nameof(Version));

        public int Version { get; private set; }

        public event PropertyChangedEventHandler? PropertyChanged;

        private CultureToken()
        {
            LocalizationService.CultureChanged += (sender, e) =>
            {
                Version++;
                PropertyChanged?.Invoke(this, s_versionArgs);
            };
        }
    }

    /// <summary>
    /// Converts an effect enum value (KeyboardEffect / BetterRgbEffect) to a localized
    /// display name. PascalCase enum names are split into words ("GamingMode" becomes
    /// "Gaming Mode", matching the Adaptive page's labels) and looked up in the
    /// localization catalog. The second MultiBinding input is the culture version
    /// token; it is not read — it makes the binding re-evaluate on language switch.
    /// </summary>
    internal sealed class EffectNameConverter : IMultiValueConverter
    {
        private static readonly Regex s_wordBoundary = new("([a-z0-9])([A-Z])", RegexOptions.Compiled);

        public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
        {
            if (values.Length < 1 || values[0] is not Enum effect)
            {
                return string.Empty;
            }

            return LocalizationService.Get(s_wordBoundary.Replace(effect.ToString()!, "$1 $2"));
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
