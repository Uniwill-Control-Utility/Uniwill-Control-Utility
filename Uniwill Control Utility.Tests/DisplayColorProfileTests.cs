using Uniwill_Control_Utility.Scripts.Misc;

namespace Uniwill_Control_Utility.Tests;

/// <summary>
/// The ICC delete guard (U-205). Only user-imported profiles are the app's to
/// delete: Windows' shipped set lives in its own spool folder, and removing or
/// "deleting" those is not a feature - it is a way to break every colour lookup
/// on the machine. The rule is pinned here so the Delete control's visibility
/// and the delete call itself cannot drift apart.
/// </summary>
public sealed class DisplayColorProfileTests
{
    [Theory]
    [InlineData("Default.icm")]
    [InlineData("sRGB.icm")]
    [InlineData("DCIP3.icm")]
    [InlineData("AdobeRGB.icm")]
    [InlineData("srgb.icm")] // case-insensitive: registry/file identity
    public void ShippedProfiles_AreNotImported(string fileName)
    {
        Assert.False(Display.IsImportedColorProfile(fileName));
    }

    [Theory]
    [InlineData("MyCalibration.icc")]
    [InlineData("Dell U2720Q.icm")]
    [InlineData("custom.icc")]
    public void UserProfiles_AreImported(string fileName)
    {
        Assert.True(Display.IsImportedColorProfile(fileName));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void EmptyNames_AreNotImported(string? fileName)
    {
        Assert.False(Display.IsImportedColorProfile(fileName));
    }

    [Fact]
    public void Delete_RefusesShippedProfiles_WithoutTouchingAnything()
    {
        Assert.False(Display.DeleteImportedColorProfile("sRGB.icm", out string? error));
        Assert.False(string.IsNullOrEmpty(error));
    }
}
