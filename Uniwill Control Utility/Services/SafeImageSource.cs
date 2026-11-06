using System;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Uniwill_Control_Utility.Services
{
    /// <summary>
    /// Image loading that can never block the UI thread. Field-proven need:
    /// a plain <c>new BitmapImage(new Uri(...))</c> decodes on the calling
    /// thread, and WPF's decoder resolves the image's ICC color context
    /// through a synchronous FileWebRequest on
    /// C:\Windows\System32\spool\drivers\color. On a machine whose security
    /// product guards system directories (observed: Emsisoft), that open's
    /// IRP is held indefinitely, and because DashboardPage built itself on
    /// the UI thread, the splash froze at 92% "Initializing" forever -
    /// rebooting changes nothing, every launch hits the same hung open.
    /// This loader decodes on a pool thread (so a held open parks one pool
    /// thread, never the dispatcher), skips the color-context file open
    /// (IgnoreColorProfile; the app's PNGs are sRGB, the visual difference
    /// is nil), caches on load and freezes the bitmap (cross-thread safe),
    /// and bounds the wait: past the budget the image is declared absent and
    /// the caller hides it - a missing laptop photo is a cosmetic loss, a
    /// frozen pre-loader is a dead product.
    /// </summary>
    internal static class SafeImageSource
    {
        private static readonly TimeSpan DefaultBudget = TimeSpan.FromSeconds(5);

        /// <summary>
        /// Decode a pack/file URI off the UI thread. Completes with the
        /// frozen image, or null on any failure (bad URI, unreadable file,
        /// decoder exception, budget exceeded). Never throws, never blocks
        /// the caller beyond the budget.
        /// </summary>
        internal static async Task<ImageSource?> DecodeAsync(string uri, TimeSpan? budget = null)
        {
            TimeSpan limit = budget ?? DefaultBudget;

            var decode = Task.Run(() =>
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                bitmap.UriSource = new Uri(uri, UriKind.RelativeOrAbsolute);
                bitmap.EndInit();
                bitmap.Freeze();
                return (ImageSource)bitmap;
            });

            var winner = await Task.WhenAny(decode, Task.Delay(limit));
            if (winner != decode)
                return null;

            try
            {
                return await decode;
            }
            catch (Exception)
            {
                // A decoder failure is a missing picture, never a reason to
                // surface anything to the user mid-startup.
                return null;
            }
        }
    }
}
