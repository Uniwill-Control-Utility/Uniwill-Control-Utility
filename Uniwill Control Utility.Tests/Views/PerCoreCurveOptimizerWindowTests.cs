using Uniwill_Control_Utility.Views.Windows;

namespace Uniwill_Control_Utility.Tests.Views;

/// <summary>
/// The per-core dialog parses each box's text rather than its value, because the control
/// clamps an out-of-range entry silently on focus loss. A positive offset asks for more
/// voltage and is the classic way to wedge a core, so it must read as invalid, not as 0.
/// </summary>
public sealed class PerCoreCurveOptimizerWindowTests
{
    [Theory]
    [InlineData("0", 0)]
    [InlineData("-50", -50)]
    [InlineData("-7", -7)]
    [InlineData("-12.4", -12)]
    public void InRangeText_IsAccepted(string text, int expected)
    {
        Assert.True(PerCoreCurveOptimizerWindow.TryParseOffset(text, out int value));
        Assert.Equal(expected, value);
    }

    [Theory]
    [InlineData("5")]
    [InlineData("1")]
    [InlineData("-51")]
    [InlineData("")]
    [InlineData("abc")]
    public void OutOfRangeOrJunkText_IsRejected(string text)
    {
        Assert.False(PerCoreCurveOptimizerWindow.TryParseOffset(text, out int value));
        Assert.Equal(0, value);
    }
}
