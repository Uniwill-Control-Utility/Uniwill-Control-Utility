using System.IO;
using Microsoft.Win32;
using UCU.Core.Ec;

namespace UCU.Engine
{
    /// <summary>
    /// Resolves the CPU family the EC encodings depend on.
    ///
    /// The UI resolves the same value from <c>Family.TYPE</c> (CPUID plus WMI); the
    /// service reads the vendor identifier the kernel published for the boot processor,
    /// which is the same fact with no WMI dependency to load in a service. Only the
    /// GPU MUX bytes depend on it, so the fallback (AMD) is the mapping this product
    /// shipped with for years.
    /// </summary>
    public static class CpuPlatformDetector
    {
        public const string CentralProcessorKey = @"HARDWARE\DESCRIPTION\System\CentralProcessor\0";

        public static EcPlatform Detect() => FromVendorIdentifier(ReadVendorIdentifier());

        public static EcPlatform FromVendorIdentifier(string? vendorIdentifier) =>
            vendorIdentifier?.IndexOf("Intel", StringComparison.OrdinalIgnoreCase) >= 0
                ? EcPlatform.Intel
                : EcPlatform.Amd;

        private static string? ReadVendorIdentifier()
        {
            try
            {
                using RegistryKey? key = RegistryKey
                    .OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Default)
                    .OpenSubKey(CentralProcessorKey);

                return key?.GetValue("VendorIdentifier") as string;
            }
            catch (Exception)
            {
                // A registry read is not the product's critical path; an unresolved
                // platform means AMD, which is the historical default.
                return null;
            }
        }
    }

    /// <summary>Where the engine writes its log.</summary>
    public static class EnginePaths
    {
        /// <summary>
        /// Service logs go to ProgramData: the service runs as LocalSystem, and a log
        /// under the user's profile is unreadable for support of a machine where the
        /// user never started the app.
        /// </summary>
        public static string ServiceLogDirectory =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "UCU", "engine");

        /// <summary>--console logs go to the user's local app data, so a dev run needs no elevation.</summary>
        public static string ConsoleLogDirectory =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UCU", "engine");

        /// <summary>
        /// The app's graceful-exit flag, in the install root's logs folder (the
        /// engine ships in the Service\ subfolder, so the install root is one
        /// level up — the same path the app writes on OnExit and the guardian
        /// reads). The supervisor distinguishes "the user closed the app" from
        /// "the app died" through this file.
        /// </summary>
        public static string GracefulExitFlagPath =>
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "logs", "graceful-exit.flag"));

        public static string ResolveLogDirectory(EngineRuntimeOptions options, bool isService) =>
            options.LogDirectory
                ?? (isService ? ServiceLogDirectory : ConsoleLogDirectory);
    }
}
