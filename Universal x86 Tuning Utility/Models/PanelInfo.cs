namespace Universal_x86_Tuning_Utility.Models
{
    /// <summary>
    /// Known panel database for XMG/Schenker laptops. Used to map panel
    /// hardware IDs and user-friendly names to panel type and max refresh rate.
    /// </summary>
    public static class PanelDatabase
    {
        public const string PanelTypeMiniLED = "MiniLED";
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

        private static readonly System.Collections.Generic.Dictionary<string, PanelInfo> PanelsByHardwareId = new()
        {
            { "BOE0D5A", new PanelInfo("NE160QDM-NM9", "BOE0D5A", PanelTypeMiniLED, 300) },
            { "AUO30A5", new PanelInfo("B160QAN03.K", "AUO30A5", PanelTypeMiniLED, 165) },
            { "BOE0AF0", new PanelInfo("NE160QDM-NZ1", "BOE0AF0", PanelTypeIPS, 240) },
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
