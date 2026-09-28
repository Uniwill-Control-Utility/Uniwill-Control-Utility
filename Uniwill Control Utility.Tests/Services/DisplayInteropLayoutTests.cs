using System.Runtime.InteropServices;
using Uniwill_Control_Utility.Scripts.Misc;

namespace Uniwill_Control_Utility.Tests.Services;

public sealed class DisplayInteropLayoutTests
{
    [Fact]
    public void UnicodeDisplayStructures_MatchWin32AbiSizes()
    {
        Assert.Equal(840, Marshal.SizeOf<Display.DISPLAY_DEVICE>());
        Assert.Equal(220, Marshal.SizeOf<Display.DEVMODE>());
    }
}
