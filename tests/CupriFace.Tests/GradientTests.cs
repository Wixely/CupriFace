using CupriFace.Style;
using SkiaSharp;
using Xunit;

namespace CupriFace.Tests;

/// <summary>CSS linear-gradient() / radial-gradient() backgrounds: parsing + rendering.</summary>
public class GradientTests
{
    [Fact]
    public void Parses_angle_keyword_stops_and_radial()
    {
        using var t = new TestDoc(
            "<body>" +
            "<div class='a' style='width:20px;height:20px;background:linear-gradient(90deg,#ff0000,#0000ff)'></div>" +
            "<div class='b' style='width:20px;height:20px;background:linear-gradient(to right,#111111 25%,#222222 75%)'></div>" +
            "<div class='c' style='width:20px;height:20px;background:radial-gradient(#ffffff,#000000)'></div>" +
            "</body>", "", null, width: 200, height: 200);

        var a = t.FindClass("a").Style.BackgroundGradient!;
        Assert.Equal(GradientKind.Linear, a.Kind);
        Assert.Equal(90, a.AngleDeg, 1);
        Assert.Equal(2, a.Stops.Count);
        Assert.Equal(0xff, a.Stops[0].Color.Red);
        Assert.Equal(0xff, a.Stops[1].Color.Blue);

        var b = t.FindClass("b").Style.BackgroundGradient!;
        Assert.Equal(90, b.AngleDeg, 1);                 // "to right" → 90°
        Assert.Equal(0.25f, b.Stops[0].Position, 2);     // explicit stop positions
        Assert.Equal(0.75f, b.Stops[1].Position, 2);

        Assert.Equal(GradientKind.Radial, t.FindClass("c").Style.BackgroundGradient!.Kind);
    }

    [Fact]
    public void A_solid_colour_after_a_gradient_clears_it()
    {
        using var t = new TestDoc(
            "<body><div class='x' style='width:20px;height:20px;background:linear-gradient(#fff,#000);background:#123456'></div></body>",
            "", null);
        var x = t.FindClass("x").Style;
        Assert.Null(x.BackgroundGradient);
        Assert.Equal(0x12, x.Background.Red);
    }

    [Fact]
    public void A_horizontal_gradient_varies_from_left_to_right()
    {
        using var t = new TestDoc(
            "<body><div style='margin:20px;width:100px;height:40px;background:linear-gradient(90deg,#000000,#ffffff)'></div></body>",
            "", null, width: 200, height: 100);
        var bmp = t.Render(SKColors.White);
        var left = bmp.GetPixel(24, 40);    // near the box's left edge (90deg → black there)
        var right = bmp.GetPixel(116, 40);  // near the right edge (white)
        Assert.True(left.Red < 60, $"left should be dark; got {left}");
        Assert.True(right.Red > 195, $"right should be light; got {right}");
    }
}
