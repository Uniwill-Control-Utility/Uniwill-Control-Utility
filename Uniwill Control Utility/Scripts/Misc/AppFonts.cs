using System;
using System.Windows.Media;

namespace Uniwill_Control_Utility.Scripts;

internal static class AppFonts
{
    private static readonly Uri ResourceAssemblyUri =
        new("pack://application:,,,/Uniwill-Control-Utility;component/");

    internal static FontFamily SegoeFluentIcons { get; } =
        new(ResourceAssemblyUri, "./Assets/Fonts/#Segoe Fluent Icons");
}
