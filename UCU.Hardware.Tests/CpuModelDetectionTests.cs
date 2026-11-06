using UCU.Hardware.Cpu;

namespace UCU.Hardware.Tests;

/// <summary>
/// The SMU gate must read the family/model pair from the CPU key's Identifier
/// ("AMD64 Family 26 Model 68 Stepping 0"), not from the brand string.
///
/// Field 2026-11-02 (XMG Neo 16, Ryzen 9 9955HX3D): the brand string carries a
/// real product name with no "Family"/"Model" words, so the old parse failed,
/// the gate answered false, the engine registered ZERO SMU commands, and every
/// Core-Optimizer / power-limit command in a preset apply threw "Command 'x'
/// not found" - 16 stack traces per apply, and no tuning reached the hardware.
/// </summary>
public sealed class CpuModelDetectionTests
{
    [Theory]
    [InlineData("AMD64 Family 26 Model 68 Stepping 0", true)]   // FireRange Identifier
    [InlineData("AMD64 Family 25 Model 97 Stepping 0", true)]   // Zen 4 - parses, just not FireRange
    [InlineData("AMD64 Family 26 Model 44 Stepping 0", true)]   // Zen 5, not FireRange
    [InlineData("AMD Ryzen 9 9955HX3D 16-Core Processor", false)] // brand string carries no pair
    [InlineData("", false)]
    [InlineData(null, false)]
    public void FamilyAndModelAreParsedOnlyWhenTheWordsArePresent(string? text, bool hasPair)
    {
        bool parsed = CpuModelDetection.TryParseFamilyModel(text, out int family, out int model);

        Assert.Equal(hasPair, parsed);
        if (!parsed)
        {
            Assert.Equal(0, family);
            Assert.Equal(0, model);
        }
    }

    [Fact]
    public void FireRangeIdentifierParsesToFamily26Model68()
    {
        Assert.True(CpuModelDetection.TryParseFamilyModel(
            "AMD64 Family 26 Model 68 Stepping 0", out int family, out int model));
        Assert.Equal(26, family);
        Assert.Equal(68, model);
    }
}
