using System.Linq;
using CupriFace.Paint;
using SkiaSharp;
using Xunit;

namespace CupriFace.Tests;

/// <summary>Accent theming: a single <c>--cupri-accent</c> CSS variable, overridden by a body theme class
/// and inherited down, recolours everything that reads <c>var(--cupri-accent, …)</c>.</summary>
public class ThemeTests
{
    private const string Css =
        "body { --cupri-accent:#B87333; }" +
        "body.theme-ocean { --cupri-accent:#2f7ed8; }" +
        ".x { background:var(--cupri-accent, #B87333); width:20px; height:20px; }";

    private static bool Paints(CupriDocument doc, SKColor c) =>
        doc.BuildFrame(100, 100).Commands.OfType<FillRect>().Any(f => f.Color == c);

    [Fact]
    public void The_default_accent_is_the_variables_value()
    {
        using var doc = CupriDocument.Load("<body><div class='x'></div></body>", Css);
        Assert.True(Paints(doc, new SKColor(0xB8, 0x73, 0x33)));   // copper
    }

    [Fact]
    public void A_theme_class_overrides_the_accent_for_descendants()
    {
        using var doc = CupriDocument.Load("<body class='theme-ocean'><div class='x'></div></body>", Css);
        Assert.True(Paints(doc, new SKColor(0x2f, 0x7e, 0xd8)));   // ocean, inherited from the body class
        Assert.False(Paints(doc, new SKColor(0xB8, 0x73, 0x33)));  // no longer copper
    }
}
