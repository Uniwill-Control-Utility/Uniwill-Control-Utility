using MahApps.Metro.IconPacks;
using Wpf.Ui.Controls;

namespace Uniwill_Control_Utility.Views.Controls;

/// <summary>
/// A PackIcon-based IconElement for the Flydigi cooler navigation icon.
/// Uses MahApps.Metro.IconPacks Lucide AirVent glyph (no Geometry.Parse() overhead).
/// The Foreground inherits from the parent so it follows theme changes.
/// </summary>
public sealed class FlydigiIconElement : IconElement
{
    private readonly double _glyphSize;

    /// <param name="glyphSize">Inner glyph size; the nav uses the 20 default,
    /// larger hosts (e.g. the toast overlay) pass their own.</param>
    // Explicit parameterless ctor: the XAML sites need one (optional args don't
    // satisfy the XAML parser).
    public FlydigiIconElement() => _glyphSize = 20;

    public FlydigiIconElement(double glyphSize) => _glyphSize = glyphSize;

    protected override System.Windows.UIElement InitializeChildren()
    {
        return new PackIconLucide
        {
            Kind = PackIconLucideKind.AirVent,
            Width = _glyphSize,
            Height = _glyphSize,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = System.Windows.VerticalAlignment.Center,
        };
    }
}
