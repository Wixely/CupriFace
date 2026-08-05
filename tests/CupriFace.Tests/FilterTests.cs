using SkiaSharp;
using Xunit;

namespace CupriFace.Tests;

public class FilterTests
{
    // Render a box (colour boxColor) with `filter` on a white page; sample a pixel inside the box.
    private static SKColor Center(string filter, SKColor boxColor)
    {
        var hex = $"#{boxColor.Red:x2}{boxColor.Green:x2}{boxColor.Blue:x2}";
        var css = $"body{{background:#ffffff}} .b{{width:80px;height:60px;margin:30px;background:{hex}; filter:{filter};}}";
        using var t = new TestDoc("<body><div class='b'></div></body>", css, width: 200, height: 160);
        using var bmp = t.Render(SKColors.White);
        return bmp.GetPixel(70, 60); // box at 30,30 size 80x60
    }

    [Fact]
    public void Grayscale_makes_red_grey()
    {
        var g = Center("grayscale(1)", new SKColor(0xff, 0, 0));
        Assert.True(System.Math.Abs(g.Red - g.Green) < 6 && System.Math.Abs(g.Green - g.Blue) < 6 && g.Green > 20, $"{g}");
    }

    [Fact]
    public void Invert_of_white_is_black()
    {
        var c = Center("invert(1)", SKColors.White);
        Assert.True(c.Red < 12 && c.Green < 12 && c.Blue < 12, $"{c}");
    }

    [Theory]
    [InlineData("brightness(0.5)", true)]   // darker
    [InlineData("brightness(1.5)", false)]  // lighter
    public void Brightness_scales_luminance(string filter, bool darker)
    {
        var mid = new SKColor(0x80, 0x80, 0x80);
        var r = Center(filter, mid).Red;
        Assert.True(darker ? r < mid.Red - 40 : r > mid.Red + 40, $"R={r}");
    }

    [Fact]
    public void Sepia_tints_toward_brown()
    {
        var c = Center("sepia(1)", SKColors.White);
        Assert.True(c.Red >= c.Green && c.Green > c.Blue, $"{c}");
    }

    [Fact]
    public void Blur_bleeds_colour_past_the_box_edge()
    {
        var css = "body{background:#ffffff} .b{width:80px;height:60px;margin:30px;background:#ff0000; filter:blur(5px);}";
        using var t = new TestDoc("<body><div class='b'></div></body>", css, width: 200, height: 160);
        using var bmp = t.Render(SKColors.White);
        var edge = bmp.GetPixel(28, 60);                 // just left of the box (x=30)
        Assert.True(edge.Green is > 20 and < 240 && edge.Red > 240, $"edge={edge}"); // red bleeds, G/B drop
    }

    [Fact]
    public void DropShadow_casts_a_pixel_past_the_box()
    {
        var css = "body{background:#ffffff} .b{width:80px;height:60px;margin:30px;background:#ff0000; filter:drop-shadow(10px 10px 4px #000000);}";
        using var t = new TestDoc("<body><div class='b'></div></body>", css, width: 200, height: 160);
        using var bmp = t.Render(SKColors.White);
        var shadow = bmp.GetPixel(118, 98);              // down-right of the box, over white
        Assert.True(shadow.Red < 240 && shadow.Green < 240, $"shadow={shadow}");
    }
}
