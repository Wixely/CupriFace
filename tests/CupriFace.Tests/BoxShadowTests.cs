using CupriFace.Style;
using SkiaSharp;
using Xunit;

namespace CupriFace.Tests;

/// <summary>CSS box-shadow: parsing (offset/blur/spread/colour/inset, multiple layers) + rendering.</summary>
public class BoxShadowTests
{
    [Fact]
    public void Parses_offset_blur_spread_colour_and_inset_layers()
    {
        using var t = new TestDoc(
            "<body>" +
            "<div class='a' style='width:20px;height:20px;box-shadow:2px 4px 8px 1px #11223344'></div>" +
            "<div class='b' style='width:20px;height:20px;box-shadow:inset 0 1px 3px #000, 0 6px 12px #0002'></div>" +
            "</body>", "", null, width: 200, height: 200);

        var a = t.FindClass("a").Style.BoxShadow!;
        Assert.Single(a);
        Assert.Equal(2, a[0].Dx, 1);
        Assert.Equal(4, a[0].Dy, 1);
        Assert.Equal(8, a[0].Blur, 1);
        Assert.Equal(1, a[0].Spread, 1);
        Assert.False(a[0].Inset);
        Assert.Equal(0x44, a[0].Color.Alpha);   // 8-digit hex alpha

        var b = t.FindClass("b").Style.BoxShadow!;
        Assert.Equal(2, b.Count);                // two comma-separated layers
        Assert.True(b[0].Inset);                 // "inset 0 1px 3px #000"
        Assert.False(b[1].Inset);
    }

    [Fact]
    public void None_clears_the_shadow()
    {
        using var t = new TestDoc(
            "<body><div class='x' style='width:20px;height:20px;box-shadow:none'></div></body>", "", null);
        Assert.Null(t.FindClass("x").Style.BoxShadow);
    }

    [Fact]
    public void A_drop_shadow_darkens_the_pixels_below_the_box()
    {
        // A white box on white; the shadow below it is the only thing that can darken those pixels.
        const string box = "<div style='margin:30px;width:80px;height:40px;background:white{S}'></div>";
        using var shadow = new TestDoc($"<body style='background:white'>{box.Replace("{S}", ";box-shadow:0 8px 16px #000000cc")}</body>", "", null, width: 220, height: 200);
        using var plain = new TestDoc($"<body style='background:white'>{box.Replace("{S}", "")}</body>", "", null, width: 220, height: 200);

        var withShadow = shadow.Render(SKColors.White).GetPixel(70, 84); // just below the box
        var without = plain.Render(SKColors.White).GetPixel(70, 84);
        Assert.Equal(255, without.Red);                                   // no shadow → white
        Assert.True(withShadow.Red < 245, $"shadow should darken; got {withShadow}");
    }
}
