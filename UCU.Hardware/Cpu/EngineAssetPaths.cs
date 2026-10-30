using System;
using System.IO;

namespace UCU.Hardware.Cpu
{
    /// <summary>
    /// Payload paths the engine's CPU services load from. The installed layout
    /// puts the engine binary in <c>current\Service\</c> and the shared assets
    /// (PawnIO modules, SKU data) in <c>current\Assets\</c> — siblings. A dev
    /// console run has no Service directory, so the walk-up search ends at the
    /// repository layout (bin\...\net10.0-windows → repository root's Assets,
    /// when one is checked out beside the binaries).
    /// </summary>
    public static class EngineAssetPaths
    {
        public static string AmdPawnIoModule =>
            Resolve("Assets", "AMD", "PawnIO", "RyzenSMU.bin");

        public static string IntelMsrModule =>
            Resolve("Assets", "Intel", "PawnIO", "IntelMSR.bin");

        public static string IntelMchbarModule =>
            Resolve("Assets", "Intel", "PawnIO", "IntelMCHBAR.bin");

        /// <summary>
        /// Search for the asset relative to the base directory and each parent
        /// up to three levels; installed (Service → ..\Assets) is the first
        /// hit. Throws FileNotFound when nothing exists: the caller reports the
        /// service as unavailable, and the app's local route answers.
        /// </summary>
        private static string Resolve(params string[] segments)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            for (int level = 0; level <= 3 && dir != null; level++, dir = dir.Parent)
            {
                string candidate = Path.Combine(new[] { dir.FullName }.Concat(segments).ToArray());
                if (File.Exists(candidate))
                    return candidate;
            }

            throw new FileNotFoundException(
                "Engine asset not found: " + Path.Combine(segments) + " (base: " + AppContext.BaseDirectory + ")");
        }
    }
}
