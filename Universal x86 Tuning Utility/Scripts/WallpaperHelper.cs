using Microsoft.Win32;
using System.IO;

namespace Universal_x86_Tuning_Utility.Scripts
{
    internal static class WallpaperHelper
    {
        /// <summary>
        /// Returns the current desktop wallpaper file path from the registry,
        /// or null if the key doesn't exist or the file is missing.
        /// </summary>
        public static string? GetWallpaperPath()
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop");
            string? path = key?.GetValue("Wallpaper") as string;

            if (!string.IsNullOrEmpty(path) && File.Exists(path))
                return path;

            return null;
        }
    }
}
