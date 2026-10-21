using Uniwill_Control_Utility.Services;

namespace Uniwill_Control_Utility.Scripts.Misc
{
    internal static class ToastNotification
    {
        /// <summary>
        /// Presents status feedback as an in-app OSD card (see ToastOverlay) rather than
        /// a Windows toast: as an unpackaged app we never won the shell's identity
        /// resolution game — every rebuild minted a fresh Settings > Notifications
        /// category, and bare WinRT toast posting throws 0x80070490 in this process.
        /// The name is kept so all call sites (and their localization keys) stay put.
        /// <paramref name="accent"/> is the exact substring of the rendered title that
        /// names the profile (e.g. "Turbo"); it is drawn bold in an AccentTextFillColor-
        /// Tertiary-style accent text color, while the rest of the title stays plain. Build composite names with
        /// AccentFor() so they survive localized word order.
        /// </summary>
        public static void ShowToastNotification(string title = "", string body = "", string? accent = null, ToastCategory category = ToastCategory.Presets)
        {
            ToastOverlay.Show(
                LocalizationService.Get(title),
                LocalizationService.Get(body),
                accent, category);
        }

        /// <summary>
        /// Builds the accent span for a "Prefix {0} suffix!" style localized title:
        /// everything in the localized template before the first placeholder, plus the
        /// inserted value. That span is always contiguous in the rendered string, so
        /// phrases like "Custom 3" stay whole even when a language reorders them
        /// ("Prilagođeno 3 primijenjeno!" → accent "Prilagođeno 3").
        /// </summary>
        public static string AccentFor(string titleTemplate, string insertedValue)
        {
            var localized = LocalizationService.Get(titleTemplate);
            int placeholder = localized.IndexOf("{0", System.StringComparison.Ordinal);
            return placeholder >= 0
                ? localized[..placeholder] + insertedValue
                : insertedValue;
        }
    }
}
