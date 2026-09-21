using MahApps.Metro.IconPacks;
using Wpf.Ui.Controls;

namespace Uniwill_Control_Utility.Views.Controls;

/// <summary>
/// A PackIcon-based IconElement for the Fan Control navigation icon.
/// Uses MahApps.Metro.IconPacks Lucide fan glyph (no Geometry.Parse() overhead).
/// The Foreground inherits from the parent so it follows theme changes.
/// </summary>
public sealed class FanIconElement : IconElement
{
    protected override System.Windows.UIElement InitializeChildren()
    {
        return new PackIconLucide
        {
            Kind = PackIconLucideKind.Fan,
            Width = 20,
            Height = 20,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = System.Windows.VerticalAlignment.Center,
        };
    }
}
