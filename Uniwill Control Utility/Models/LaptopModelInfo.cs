namespace Uniwill_Control_Utility.Models
{
    /// <summary>
    /// Describes a known laptop model for detection and display purposes.
    /// </summary>
    /// <param name="DisplayName">Human-readable name shown in the dashboard header (e.g. "XMG NEO 16 (A25)").</param>
    /// <param name="SystemManufacturer">Exact Win32_ComputerSystem.Manufacturer value to match (e.g. "SchenkerTechnologiesGmbH").</param>
    /// <param name="BaseBoardProductPrefix">First 4 characters of Win32_BaseBoard.Product to match (e.g. "X6FR").</param>
    /// <param name="ImagePath">Default pack URI for the laptop image.</param>
    /// <param name="AmdImagePath">Optional override pack URI when the CPU is AMD (null to fall back to ImagePath).</param>
    /// <param name="IntelImagePath">Optional override pack URI when the CPU is Intel (null to fall back to ImagePath).</param>
    public record LaptopModelInfo(
        string DisplayName,
        string SystemManufacturer,
        string BaseBoardProductPrefix,
        string ImagePath,
        string? AmdImagePath = null,
        string? IntelImagePath = null)
    {
        /// <summary>
        /// Returns the image path for the given CPU vendor.
        /// </summary>
        public string GetImagePath(bool isIntel) =>
            isIntel
                ? IntelImagePath ?? ImagePath
                : AmdImagePath ?? ImagePath;
    }
}
