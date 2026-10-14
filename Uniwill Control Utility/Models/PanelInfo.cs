using System;
using System.Text;

namespace Uniwill_Control_Utility.Models
{
    /// <summary>
    /// Known panel database for XMG/Schenker laptops. Used to map panel
    /// hardware IDs and user-friendly names to panel type and max refresh rate.
    /// </summary>
    public static class PanelDatabase
    {
        public const string PanelTypeMiniLED = "MiniLED";
        public const string PanelTypeOLED = "OLED";
        public const string PanelTypeIPS = "IPS";

        /// <summary>
        /// Looks up panel info by hardware ID (e.g., "BOE0D5A"). Returns null if not found.
        /// </summary>
        public static PanelInfo? LookupByHardwareId(string hardwareId)
        {
            if (string.IsNullOrEmpty(hardwareId))
                return null;

            return PanelsByHardwareId.TryGetValue(hardwareId.ToUpperInvariant(), out var info) ? info : null;
        }

        /// <summary>
        /// Looks up panel info by user-friendly model name (e.g., "NE160QDM-NM9"). Returns null if not found.
        /// </summary>
        public static PanelInfo? LookupByModel(string model)
        {
            if (string.IsNullOrEmpty(model))
                return null;

            return PanelsByModel.TryGetValue(model.ToUpperInvariant(), out var info) ? info : null;
        }

        /// <summary>
        /// Detects the current panel type by querying WmiMonitorID for hardware ID and model name.
        /// Iterates all active monitors and returns true if any match a known MiniLED panel.
        /// This handles laptops with docks, external displays, or virtual monitors that may
        /// appear before the internal panel in the WMI enumeration order.
        /// </summary>
        public static bool IsMiniLED()
        {
            try
            {
                using var searcher = new System.Management.ManagementObjectSearcher(
                    "root\\wmi", "SELECT * FROM WmiMonitorID");

                foreach (System.Management.ManagementBaseObject monitor in searcher.Get())
                {
                    if (!(bool)monitor["Active"])
                        continue;

                    // Try to identify this monitor
                    string? panelModel = ExtractFriendlyName(monitor);
                    string? hardwareId = ExtractHardwareId(monitor);

                    // Try hardware ID first, then model name.
                    PanelInfo? info = null;

                    if (!string.IsNullOrEmpty(hardwareId))
                        info = LookupByHardwareId(hardwareId);

                    if (info == null && !string.IsNullOrEmpty(panelModel))
                        info = LookupByModel(panelModel);

                    // If this monitor matched a known MiniLED panel, return true immediately.
                    if (info.HasValue && info.Value.PanelType == PanelTypeMiniLED)
                        return true;
                }
            }
            catch
            {
                // WMI not available
            }

            return false;
        }

        /// <summary>
        /// The internal panel's model name (WmiMonitorID UserFriendlyName), e.g.
        /// "NE160QDM-NM9". Prefers the active monitor that matches the known panel
        /// database — that match IS the laptop panel, so externals are ignored —
        /// falling back to the first active monitor with a friendly name.
        /// Null when WMI is unavailable or no monitor reports a name.
        /// </summary>
        public static string? CurrentPanelModel()
        {
            try
            {
                using var searcher = new System.Management.ManagementObjectSearcher(
                    "root\\wmi", "SELECT * FROM WmiMonitorID");

                string? firstNamed = null;
                foreach (System.Management.ManagementBaseObject monitor in searcher.Get())
                {
                    if (!(bool)monitor["Active"])
                        continue;

                    string? name = ExtractFriendlyName(monitor);
                    string? hardwareId = ExtractHardwareId(monitor);

                    PanelInfo? info = null;
                    if (!string.IsNullOrEmpty(hardwareId))
                        info = LookupByHardwareId(hardwareId);
                    if (info == null && !string.IsNullOrEmpty(name))
                        info = LookupByModel(name);

                    // The database match IS the laptop panel; its Model is the friendly name
                    // even when the monitor's WMI UserFriendlyName is empty (BOE panels omit it).
                    if (info != null)
                        return info.Value.Model;

                    if (firstNamed is null && !string.IsNullOrEmpty(name))
                        firstNamed = name;
                }
                return firstNamed;
            }
            catch
            {
                // WMI not available
                return null;
            }
        }

        private static string? ExtractFriendlyName(System.Management.ManagementBaseObject monitor)
        {
            byte[]? friendlyNameBytes = monitor["UserFriendlyName"] as byte[];
            int nameLen = Convert.ToInt32(monitor["UserFriendlyNameLength"]);
            if (friendlyNameBytes != null && nameLen > 0)
            {
                string name = System.Text.Encoding.ASCII.GetString(friendlyNameBytes)
                    .TrimEnd('\0', '\r', '\n', ' ');
                if (!string.IsNullOrEmpty(name))
                    return name;
            }
            return null;
        }

        private static string? ExtractHardwareId(System.Management.ManagementBaseObject monitor)
        {
            string instanceName = Convert.ToString(monitor["InstanceName"]) ?? "";
            if (string.IsNullOrEmpty(instanceName))
                return null;

            int bs1 = instanceName.IndexOf('\\');
            int bs2 = instanceName.IndexOf('\\', bs1 + 1);
            if (bs1 >= 0 && bs2 > bs1)
                return instanceName.Substring(bs1 + 1, bs2 - bs1 - 1);

            return null;
        }

        private static readonly System.Collections.Generic.Dictionary<string, PanelInfo> PanelsByHardwareId = new()
        {
            { "BOE0D5A", new PanelInfo("NE160QDM-NM9", "BOE0D5A", PanelTypeMiniLED, 300) },
            { "AUO30A5", new PanelInfo("B160QAN03.K", "AUO30A5", PanelTypeMiniLED, 165) },
            { "BOE0AF0", new PanelInfo("NE160QDM-NZ1", "BOE0AF0", PanelTypeIPS, 240) },
            { "SDC420B", new PanelInfo("SDC420B", "SDC420B", PanelTypeOLED, 240) },
        };

        private static readonly System.Collections.Generic.Dictionary<string, PanelInfo> PanelsByModel = new()
        {
            { "NE160QDM-NM9", new PanelInfo("NE160QDM-NM9", "BOE0D5A", PanelTypeMiniLED, 300) },
            { "B160QAN03.K",   new PanelInfo("B160QAN03.K", "AUO30A5", PanelTypeMiniLED, 165) },
            { "NE160QDM-NZL",  new PanelInfo("NE160QDM-NZL", null, PanelTypeIPS, 300) },
            { "NE160QDM-NZA",  new PanelInfo("NE160QDM-NZA", null, PanelTypeIPS, 240) },
            { "NE160QDM-NZ1",  new PanelInfo("NE160QDM-NZ1", "BOE0AF0", PanelTypeIPS, 240) },
            { "NE160QDM-NY3",  new PanelInfo("NE160QDM-NY3", null, PanelTypeIPS, 165) },
            { "NE160QDM-NYC",  new PanelInfo("NE160QDM-NYC", null, PanelTypeIPS, 165) },
            { "B160QAN02.Q",   new PanelInfo("B160QAN02.Q", null, PanelTypeIPS, 165) },
            { "B160QAN02.Y",   new PanelInfo("B160QAN02.Y", null, PanelTypeIPS, 165) },
            { "MNG007DA1-9",   new PanelInfo("MNG007DA1-9", null, PanelTypeIPS, 165) },
            { "MNG007DA1-8",   new PanelInfo("MNG007DA1-8", null, PanelTypeIPS, 165) },
            { "MNG007DA1-Q",   new PanelInfo("MNG007DA1-Q", null, PanelTypeIPS, 165) },
            { "N160GLE-GT1",   new PanelInfo("N160GLE-GT1", null, PanelTypeIPS, 165) },
            { "SDC420B",       new PanelInfo("SDC420B", "SDC420B", PanelTypeOLED, 240) },
        };
    }

    public readonly struct PanelInfo
    {
        public string Model { get; }
        public string? HardwareId { get; }
        public string PanelType { get; }
        public int MaxRefreshRateHz { get; }

        public PanelInfo(string model, string? hardwareId, string panelType, int maxRefreshRateHz)
        {
            Model = model;
            HardwareId = hardwareId;
            PanelType = panelType;
            MaxRefreshRateHz = maxRefreshRateHz;
        }
    }
}
