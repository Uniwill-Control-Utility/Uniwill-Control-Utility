using System.Windows;
using System.Windows.Media;
using System.Windows.Resources;

namespace Uniwill_Control_Utility.Tests.UI;

public sealed class BundledFluentIconFontTests
{
    private static readonly int[] RequiredGlyphs =
    [
        0xE713, // Settings
        0xE721, // Link
        0xE768, // Play
        0xE7C3, // Temperature
        0xE80F, // Home
        0xE945, // Flash
        0xE946, // Info
        0xEB42, // Water
        0xED39, // Light
        0xF167, // Status info
        0xF404  // Gauge
    ];

    [Fact]
    public void PackagedFontResolvesGlyphsUsedByFontIcons()
    {
        var resourceUri = new Uri(
            "/Uniwill-Control-Utility;component/Assets/Fonts/Segoe%20Fluent%20Icons.ttf",
            UriKind.Relative);
        StreamResourceInfo? resource = Application.GetResourceStream(resourceUri);
        Assert.NotNull(resource);

        string temporaryDirectory = Path.Combine(Path.GetTempPath(), "Uniwill-Control-Utility.Tests");
        string temporaryFontPath = Path.Combine(temporaryDirectory, "Segoe Fluent Icons.ttf");
        Directory.CreateDirectory(temporaryDirectory);

        using (resource.Stream)
        using (FileStream target = File.Create(temporaryFontPath))
        {
            resource.Stream.CopyTo(target);
        }

        var glyphTypeface = new GlyphTypeface(new Uri(temporaryFontPath));
        Assert.Contains("Segoe Fluent Icons", glyphTypeface.Win32FamilyNames.Values);

        foreach (int codePoint in RequiredGlyphs)
        {
            Assert.True(
                glyphTypeface.CharacterToGlyphMap.ContainsKey(codePoint),
                $"Bundled Segoe Fluent Icons is missing U+{codePoint:X4}.");
        }
    }
}
