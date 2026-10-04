using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using Uniwill_Control_Utility.Models;
using Uniwill_Control_Utility.Scripts;

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
        /// Brands that support driver page URLs.
        /// XMG and TUXEDO use fixed URLs by model, PCSpecialist requires the System SKU number.
        /// </summary>
        private static readonly HashSet<string> DriverSupportedManufacturers = new(StringComparer.OrdinalIgnoreCase)
        {
            "SchenkerTechnologiesGmbH",
            "PCSpecialist",
            "TUXEDO"
        };

        private static string? _systemSkuNumber;

        /// <summary>
        /// The Win32_ComputerSystem.SystemSKUNumber value, or null if not available.
        /// Used for PCSpecialist driver URLs.
        /// </summary>
        public static string? SystemSkuNumber => _systemSkuNumber;

        /// <summary>
        /// True when the detected laptop is one of the brands that has a known driver download page.
        /// Returns false for "unicorn" PCSpecialist machines (custom BIOS with cleared identifiers)
        /// and for unsupported brands.
        /// </summary>
        public static bool HasDriverPage => Detected != null && DriverSupportedManufacturers.Contains(Detected.SystemManufacturer);

        /// <summary>
        /// Builds the driver download URL for the detected laptop.
        /// Returns null if the laptop doesn't have a supported driver page.
        ///
        /// XMG Neo 16 A25: https://download.schenker-tech.de/package/xmg-neo-16-a25-xne16a25/
        /// XMG Neo 16 E25: https://download.schenker-tech.de/package/xmg-neo-16-e25-xne16e25/
        /// PCSpecialist Recoil III: https://www.pcspecialist.ie/drivers/{SkuNumber}/w11/
        /// TUXEDO Stellaris 16 Gen7 (AMD): https://mytuxedo.de/index.php/s/ZeB8FTf8CrpEtJr?dir=/Stellaris_Series/Stellaris_Gen7_AMD
        /// TUXEDO Stellaris 16 Gen7 (Intel): https://mytuxedo.de/index.php/s/ZeB8FTf8CrpEtJr?dir=/Stellaris_Series/Stellaris_Gen7_Intel
        /// </summary>
        public static string? GetDriverUrl()
        {
            var model = Detected;
            if (model == null || !DriverSupportedManufacturers.Contains(model.SystemManufacturer))
                return null;

            // XMG / Schenker — fixed URL by model name
            if (model.SystemManufacturer.Equals("SchenkerTechnologiesGmbH", StringComparison.OrdinalIgnoreCase))
            {
                if (model.DisplayName.Contains("A25", StringComparison.OrdinalIgnoreCase))
                    return "https://download.schenker-tech.de/package/xmg-neo-16-a25-xne16a25/";
                if (model.DisplayName.Contains("E25", StringComparison.OrdinalIgnoreCase))
                    return "https://download.schenker-tech.de/package/xmg-neo-16-e25-xne16e25/";
                if (model.DisplayName.Contains("M26", StringComparison.OrdinalIgnoreCase))
                    return "https://download.schenker-tech.de/package/xmg-neo-16-m26-xne16m26/";
                return null;
            }

            // PCSpecialist — requires System SKU number from WMI
            if (model.SystemManufacturer.Equals("PCSpecialist", StringComparison.OrdinalIgnoreCase))
            {
                var sku = _systemSkuNumber?.Trim();
                if (string.IsNullOrEmpty(sku))
                    return null; // Unicorn BIOS with cleared SKU — can't build URL

                return $"https://www.pcspecialist.ie/drivers/{sku}/w11/";
            }

            // TUXEDO — CPU-vendor-aware URL
            if (model.SystemManufacturer.Equals("TUXEDO", StringComparison.OrdinalIgnoreCase))
            {
                bool isIntel = Family.TYPE == Family.ProcessorType.Intel;
                return isIntel
                    ? "https://mytuxedo.de/index.php/s/ZeB8FTf8CrpEtJr?dir=/Stellaris_Series/Stellaris_Gen7_Intel"
                    : "https://mytuxedo.de/index.php/s/ZeB8FTf8CrpEtJr?dir=/Stellaris_Series/Stellaris_Gen7_AMD";
            }

            return null;
        }

        /// <summary>
        /// Catalog of known laptop models. Ordered by brand; each entry maps a
        /// (SystemManufacturer, BaseBoardProductPrefix) pair to display metadata.
        /// Add new entries here when supporting additional laptop brands that share
        /// the same underlying hardware.
        /// </summary>
        private static readonly List<LaptopModelInfo> Catalog = new()
        {
            // --- XMG / Schenker ---
            // NB02 chassis — Win32_BaseBoard.Manufacturer = "NB02"
            // X6FR = AMD, X6AR = Intel, XxAF = Intel 290HX Plus (M26)
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

            // M26 generation uses XxAF baseboard product: "XxAF5xxx (NB02)"
            // EC register map is identical to X6FR/X6AR.
            new LaptopModelInfo(
                DisplayName: "XMG NEO 16 (M26)",
                SystemManufacturer: "SchenkerTechnologiesGmbH",
                BaseBoardProductPrefix: "XxAF",
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
            new LaptopModelInfo(
                DisplayName: "Tuxedo Stellaris 16 Gen7 (AMD)",
                SystemManufacturer: "TUXEDO",
                BaseBoardProductPrefix: "X6FR",
                ImagePath: "pack://application:,,,/Assets/Laptops/TuxedoComputers/Stellaris-16-Gen7.png"),

            new LaptopModelInfo(
                DisplayName: "Tuxedo Stellaris 16 Gen7 (Intel)",
                SystemManufacturer: "TUXEDO",
                BaseBoardProductPrefix: "X6AR",
                ImagePath: "pack://application:,,,/Assets/Laptops/TuxedoComputers/Stellaris-16-Gen7.png"),

            // --- MECHREVO ---
            // Mechrevo baseboard products use "YAOSHI Series-X6FR..." / "YAOSHI Series-X6AR..."
            // The chassis code (X6FR/X6AR) appears after the "YAOSHI Series-" prefix.
            // Matching is substring-based, so "X6FR" and "X6AR" match anywhere in the string.
            new LaptopModelInfo(
                DisplayName: "Mechrevo Dragon 16 Ultra",
                SystemManufacturer: "MECHREVO",
                BaseBoardProductPrefix: "X6FR",
                ImagePath: "pack://application:,,,/Assets/Laptops/Mechrevo/Dragon-16-Ultra.png"),

            new LaptopModelInfo(
                DisplayName: "Mechrevo Aurora 16 Ultra",
                SystemManufacturer: "MECHREVO",
                BaseBoardProductPrefix: "X6AR",
                ImagePath: "pack://application:,,,/Assets/Laptops/Mechrevo/Aurora-16-Ultra.png"),

            // --- TUXEDO ---
            // TUXEDO computers use the same TongFeng chassis. Add entries when images are available.
            // e.g. new LaptopModelInfo("TUXEDO Pulse 15", "TUXEDO", "X6FR", "..."),

            // --- Future brands ---
            // Add entries for any other rebrand that ships TongFeng NB02 chassis laptops.
        };

        /// <summary>
        /// Pack URI for the default Uniwill IDY laptop image, used when the chassis
        /// is detected as TongFeng but the brand isn't in the catalog yet.
        /// </summary>
        public const string DefaultImagePath = "pack://application:,,,/Assets/Laptops/Uniwill/IDY.png";

        private static LaptopModelInfo? _detectedModel;
        private static bool? _isTongFengChassis;
        private static bool _initialized;

        /// <summary>
        /// The currently detected laptop model, or null if no catalog match was found.
        /// </summary>
        public static LaptopModelInfo? Detected => _detectedModel;

        /// <summary>
        /// True when the baseboard product contains a known TongFeng prefix (X6FR, X6AR, XxAF),
        /// regardless of whether the brand is in the catalog.
        /// </summary>
        public static bool IsTongFengChassis => _isTongFengChassis ?? false;

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

                if (string.IsNullOrEmpty(baseBoardProduct))
                    return;

                // Check for any TongFeng chassis prefix.
                // All models report Win32_BaseBoard.Manufacturer = "NB02".
                // X6FR (AMD), X6AR (Intel) — e.g. "X6AR558Y"
                // XxAF (Intel M26) — e.g. "XxAF5xxx (NB02)"
                // The chassis code may appear at position 0 or embedded (e.g. Mechrevo "YAOSHI Series-X6AR55xY").
                _isTongFengChassis = baseBoardProduct.IndexOf("X6", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                     baseBoardProduct.IndexOf("XA", StringComparison.OrdinalIgnoreCase) >= 0;

                if (string.IsNullOrEmpty(manufacturer))
                    return;

                _detectedModel = Catalog
                    .FirstOrDefault(m =>
                        m.SystemManufacturer.Equals(manufacturer, StringComparison.OrdinalIgnoreCase) &&
                        baseBoardProduct.Contains(m.BaseBoardProductPrefix, StringComparison.OrdinalIgnoreCase));
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                // WMI not available — detection fails silently.
            }

            // SystemSKUNumber is optional (used for PCSpecialist driver URLs) — read separately
            // so a failure here doesn't break main laptop detection.
            try { _systemSkuNumber = GetWmiStringValue("Win32_ComputerSystem", "SystemSKUNumber"); }
            catch (System.Runtime.InteropServices.COMException) { }
        }

        /// <summary>
        /// Returns the display name for the detected laptop, or "DESKTOP SYSTEM" if unknown.
        /// </summary>
        public static string GetDisplayName() =>
            Detected?.DisplayName ?? "DESKTOP SYSTEM";

        /// <summary>
        /// Returns the appropriate image pack URI for the detected laptop and CPU vendor.
        /// Falls back to the default Uniwill IDY image when the chassis is TongFeng but
        /// the brand isn't in the catalog yet.
        /// </summary>
        /// <param name="isIntel">Whether the current CPU is Intel.</param>
        public static string? GetImagePath(bool isIntel) =>
            Detected?.GetImagePath(isIntel) ?? (IsTongFengChassis ? DefaultImagePath : null);

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
