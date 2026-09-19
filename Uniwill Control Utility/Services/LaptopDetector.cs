using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using Uniwill_Control_Utility.Models;

namespace Uniwill_Control_Utility.Services
{
    /// <summary>
    /// Detects the current laptop model by matching Win32_ComputerSystem.Manufacturer
    /// and the first 4 characters of Win32_BaseBoard.Product against a known catalog.
    /// 
    /// This approach is resilient to custom BIOS modifications that may alter the
    /// System Model value — the baseboard product identifier is harder to change
    /// and uniquely identifies the chassis.
    /// </summary>
    public static class LaptopDetector
    {
        /// <summary>
        /// Number of leading characters from Win32_BaseBoard.Product used for matching.
        /// E.g. "X6FR559Y" → "X6FR" (AMD TongFeng chassis), "X6AR558Y" → "X6AR" (Intel variant).
        /// </summary>
        private const int BaseBoardPrefixLength = 4;

        /// <summary>
        /// Catalog of known laptop models. Ordered by brand; each entry maps a
        /// (SystemManufacturer, BaseBoardProductPrefix) pair to display metadata.
        /// Add new entries here when supporting additional laptop brands that share
        /// the same underlying hardware.
        /// </summary>
        private static readonly List<LaptopModelInfo> Catalog = new()
        {
            // --- XMG / Schenker ---
            new LaptopModelInfo(
                DisplayName: "XMG NEO 16 (A25)",
                SystemManufacturer: "SchenkerTechnologiesGmbH",
                BaseBoardProductPrefix: "X6FR",
                ImagePath: "pack://application:,,,/Assets/Laptops/XMG/Neo-25.png"),

            new LaptopModelInfo(
                DisplayName: "XMG NEO 16 (E25)",
                SystemManufacturer: "SchenkerTechnologiesGmbH",
                BaseBoardProductPrefix: "X6AR",
                ImagePath: "pack://application:,,,/Assets/Laptops/XMG/Neo-25.png"),

            // --- PCSpecialist ---
            new LaptopModelInfo(
                DisplayName: "PCSpecialist Recoil III 16 (AMD)",
                SystemManufacturer: "PCSpecialist",
                BaseBoardProductPrefix: "X6FR",
                ImagePath: "pack://application:,,,/Assets/Laptops/PCSpecialist/Recoil-3-16-AMD.png"),

            new LaptopModelInfo(
                DisplayName: "PCSpecialist Recoil III 16 (Intel)",
                SystemManufacturer: "PCSpecialist",
                BaseBoardProductPrefix: "X6AR",
                ImagePath: "pack://application:,,,/Assets/Laptops/PCSpecialist/Recoil-3-16-Intel.png"),

            // --- Mifcom ---
            new LaptopModelInfo(
                DisplayName: "Mifcom 16 (AMD)",
                SystemManufacturer: "AiStone",
                BaseBoardProductPrefix: "X6FR",
                ImagePath: "pack://application:,,,/Assets/Laptops/Mifcom/16.png"),

            new LaptopModelInfo(
                DisplayName: "Mifcom 16 (Intel)",
                SystemManufacturer: "AiStone",
                BaseBoardProductPrefix: "X6AR",
                ImagePath: "pack://application:,,,/Assets/Laptops/Mifcom/16.png"),

            // --- TUXEDO ---
            // TUXEDO computers use the same TongFeng chassis. Add entries when images are available.
            // e.g. new LaptopModelInfo("TUXEDO Pulse 15", "TUXEDO", "X6FR", "..."),

            // --- Future brands ---
            // Add entries for any other rebrand that ships TongFeng NB05/NB02 chassis laptops.
        };

        private static LaptopModelInfo? _detectedModel;
        private static bool _initialized;

        /// <summary>
        /// The currently detected laptop model, or null if no match was found.
        /// </summary>
        public static LaptopModelInfo? Detected => _detectedModel;

        /// <summary>
        /// Runs the detection once at application startup.
        /// Call after Family.setCpuFamily() so the CPU vendor is known.
        /// </summary>
        public static void Initialize()
        {
            if (_initialized)
                return;

            _initialized = true;

            try
            {
                string? manufacturer = GetWmiStringValue("Win32_ComputerSystem", "Manufacturer");
                string? baseBoardProduct = GetWmiStringValue("Win32_BaseBoard", "Product");

                if (string.IsNullOrEmpty(manufacturer) || string.IsNullOrEmpty(baseBoardProduct))
                    return;

                string prefix = baseBoardProduct.Length >= BaseBoardPrefixLength
                    ? baseBoardProduct.Substring(0, BaseBoardPrefixLength)
                    : baseBoardProduct;

                _detectedModel = Catalog
                    .FirstOrDefault(m =>
                        m.SystemManufacturer.Equals(manufacturer, StringComparison.OrdinalIgnoreCase) &&
                        m.BaseBoardProductPrefix.Equals(prefix, StringComparison.OrdinalIgnoreCase));
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                // WMI not available — detection fails silently.
            }
        }

        /// <summary>
        /// Returns the display name for the detected laptop, or "DESKTOP SYSTEM" if unknown.
        /// </summary>
        public static string GetDisplayName() =>
            Detected?.DisplayName ?? "DESKTOP SYSTEM";

        /// <summary>
        /// Returns the appropriate image pack URI for the detected laptop and CPU vendor,
        /// or null if no model is detected.
        /// </summary>
        /// <param name="isIntel">Whether the current CPU is Intel.</param>
        public static string? GetImagePath(bool isIntel) =>
            Detected?.GetImagePath(isIntel);

        /// <summary>
        /// Reads the first value of a named property from a WMI class.
        /// </summary>
        private static string? GetWmiStringValue(string wmiClass, string propertyName)
        {
            using var searcher = new ManagementObjectSearcher(
                "root\\CIMV2",
                $"SELECT {propertyName} FROM {wmiClass}");

            foreach (ManagementObject mo in searcher.Get())
            {
                return mo[propertyName]?.ToString()?.Trim();
            }

            return null;
        }
    }
}
