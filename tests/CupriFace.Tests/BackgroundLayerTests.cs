using CupriFace.Diagnostics;
using SkiaSharp;
using Xunit;
using Xunit.Abstractions;

namespace CupriFace.Tests;

/// <summary>
/// The <c>background</c> shorthand keeps its image when a keyword follows it (#265), and the image
/// layer is sized, placed and tiled by <c>background-size</c>, <c>-position</c> and <c>-repeat</c> (#267).
///
/// <para>Before: <c>background: linear-gradient(…) no-repeat</c> matched neither the gradient parser
/// nor the colour parser and the whole declaration was dropped — the element painted nothing, with
/// no diagnostic. And the three geometry longhands were reported unsupported and ignored, so a
/// gradient told to cover half its box as a progress bar filled all of it.</para>
/// </summary>
public class BackgroundLayerTests(ITestOutputHelper output)
{
    private const string Base = "body{margin:0;background:#fff} .p{width:200px;height:80px}";
    private const string Grad = "linear-gradient(90deg, #d9642a, #3f6fd8)";
    private const string Solid = "linear-gradient(90deg, #d9642a, #d9642a)";   // a gradient with one colour: easy to count

    private static SKBitmap Render(string css)
    {
        using var t = new TestDoc("<body><div class='p'></div></body>", Base + " .p{" + css + "}", width: 200, height: 80);
        return t.Render();
    }

    private static int Painted(SKBitmap bmp, int x0 = 0, int y0 = 0, int? x1 = null, int? y1 = null)
    {
        var n = 0;
        for (var y = y0; y < (y1 ?? bmp.Height); y++)
            for (var x = x0; x < (x1 ?? bmp.Width); x++)
                if (bmp.GetPixel(x, y) != SKColors.White) n++;
        return n;
    }

    private static bool Orange(SKColor c) => c.Red > 180 && c.Green < 140 && c.Blue < 100;

    // ---- #265: the shorthand -------------------------------------------------------------------

    /// <summary>The issue's table: the bare gradient paints, the longhands paint, and the shorthand
    /// with a keyword after the image painted NOTHING. All three are now the same pixels.</summary>
    [Theory]
    [InlineData("background:" + Grad + " no-repeat")]
    [InlineData("background:" + Grad + " repeat")]
    [InlineData("background:" + Grad + " no-repeat scroll")]
    [InlineData("background:" + Grad + " 0 0 / 100% 100% no-repeat")]
    [InlineData("background-image:" + Grad + ";background-repeat:no-repeat")]
    public void A_keyword_after_the_image_keeps_the_image(string decl)
    {
        using var control = Render("background:" + Grad);
        using var bmp = Render(decl);
        output.WriteLine($"{decl} -> {Painted(bmp)} px (control {Painted(control)})");
        Assert.True(Painted(control) > 15000, "the control did not draw");
        Assert.True(ImageDiff.Compare(control, bmp).IsIdentical, decl + " did not paint like the bare gradient");
    }

    [Fact]
    public void The_shorthand_takes_a_colour_and_an_image_together()
    {
        using var bmp = Render("background:#000 " + Solid + " no-repeat left / 50% 100%");
        Assert.True(Orange(bmp.GetPixel(50, 40)), $"left half {bmp.GetPixel(50, 40)} should be the gradient");
        Assert.Equal(SKColors.Black, bmp.GetPixel(150, 40));     // the colour shows where the tile is not
    }

    [Fact]
    public void A_later_shorthand_resets_the_earlier_layer()
    {
        using var bmp = Render("background:" + Solid + " no-repeat left / 50% 100%; background:#000");
        Assert.Equal(SKColors.Black, bmp.GetPixel(50, 40));
    }

    // ---- #267: size ----------------------------------------------------------------------------

    /// <summary>The issue's document: a gradient sized to half its box, and the control that used to
    /// render identically to it.</summary>
    [Fact]
    public void Background_size_confines_a_gradient_to_part_of_its_box()
    {
        using var control = Render("background-image:" + Solid + ";background-repeat:no-repeat");
        using var half = Render("background-image:" + Solid + ";background-repeat:no-repeat;background-size:50% 100%");
        output.WriteLine($"control {Painted(control)} px, half {Painted(half)} px");
        Assert.False(ImageDiff.Compare(control, half).IsIdentical, "background-size changed nothing");
        Assert.InRange(Painted(half, 0, 0, 100, 80), 7900, 8000);   // the left half, fully painted
        Assert.Equal(0, Painted(half, 100, 0, 200, 80));           // the right half, untouched
    }

    [Theory]
    [InlineData("background-size:40px 20px", 40, 20)]
    [InlineData("background-size:40px", 40, 80)]      // one value: the height stays auto, the box for a gradient
    [InlineData("background-size:25% auto", 50, 80)]
    [InlineData("background-size:cover", 200, 80)]    // no intrinsic ratio: cover and contain are the box
    [InlineData("background-size:contain", 200, 80)]
    public void A_gradient_tile_takes_the_size_it_is_given(string decl, int w, int h)
    {
        using var bmp = Render("background-image:" + Solid + ";background-repeat:no-repeat;" + decl);
        var painted = Painted(bmp);
        output.WriteLine($"{decl} -> {painted} px");
        Assert.InRange(painted, w * h - (w + h) * 2, w * h + (w + h) * 2);   // the tile's area, give or take its edges
        Assert.True(Orange(bmp.GetPixel(w / 2, h / 2)));
    }

    // ---- #267: position ------------------------------------------------------------------------

    [Theory]
    [InlineData("right", 150, 40)]
    [InlineData("center", 100, 40)]
    [InlineData("100% 0", 150, 40)]
    [InlineData("50px 0", 75, 40)]
    [InlineData("left bottom", 50, 40)]
    public void Background_position_places_the_tile(string pos, int cx, int cy)
    {
        using var bmp = Render("background-image:" + Solid + ";background-repeat:no-repeat;background-size:100px 80px;background-position:" + pos);
        output.WriteLine($"{pos}: at {cx},{cy} = {bmp.GetPixel(cx, cy)}");
        Assert.True(Orange(bmp.GetPixel(cx, cy)), $"the tile is not at {cx},{cy}");
        Assert.InRange(Painted(bmp), 7800, 8200);
    }

    // ---- #267: repeat --------------------------------------------------------------------------

    /// <summary>A small tile sized explicitly — dots, grid lines — repeats across the box, and
    /// stops repeating when told to.</summary>
    [Fact]
    public void A_sized_tile_repeats_across_the_box_unless_told_not_to()
    {
        using var tiled = Render("background-image:" + Solid + ";background-size:20px 80px");
        using var once = Render("background-image:" + Solid + ";background-size:20px 80px;background-repeat:no-repeat");
        using var acrossX = Render("background-image:" + Solid + ";background-size:20px 40px;background-repeat:repeat-x");
        output.WriteLine($"repeat {Painted(tiled)} px, once {Painted(once)} px, repeat-x {Painted(acrossX)} px");
        Assert.InRange(Painted(tiled), 15800, 16000);                 // the whole box
        Assert.InRange(Painted(once), 1500, 1700);                    // one 20x80 tile
        Assert.InRange(Painted(acrossX), 7800, 8200);                 // the top half, tiled along x only
        Assert.True(Orange(acrossX.GetPixel(190, 20)));
        Assert.Equal(SKColors.White, acrossX.GetPixel(190, 60));
    }

    /// <summary>A tile that repeats is laid from its position, so a half-tile offset shows at the
    /// left edge rather than the tiling starting at the box's corner.</summary>
    [Fact]
    public void Tiling_starts_at_the_position()
    {
        const string stripes = "linear-gradient(90deg, #d9642a 50%, #ffffff 50%)";   // a 20px tile: 10 orange, 10 white
        using var fromZero = Render("background-image:" + stripes + ";background-size:20px 80px");
        using var fromTen = Render("background-image:" + stripes + ";background-size:20px 80px;background-position:10px 0");
        Assert.True(Orange(fromZero.GetPixel(5, 40)));
        Assert.Equal(SKColors.White, fromZero.GetPixel(15, 40));
        Assert.Equal(SKColors.White, fromTen.GetPixel(5, 40));
        Assert.True(Orange(fromTen.GetPixel(15, 40)));
    }

    // ---- #267: a raster image ------------------------------------------------------------------

    /// <summary>A 40×20 image, inline as a data URI — so it has an intrinsic size and a ratio.</summary>
    private static string TwoByOne()
    {
        using var bmp = new SKBitmap(40, 20);
        bmp.Erase(new SKColor(0xd9, 0x64, 0x2a));
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Png, 100);
        return "data:image/png;base64," + Convert.ToBase64String(data.ToArray());
    }

    [Fact]
    public void A_url_image_tiles_at_its_own_size_by_default()
    {
        var url = "url(\"" + TwoByOne() + "\")";
        using var tiled = Render("background-image:" + url);
        using var once = Render("background-image:" + url + ";background-repeat:no-repeat");
        output.WriteLine($"tiled {Painted(tiled)} px, once {Painted(once)} px");
        Assert.InRange(Painted(tiled), 15800, 16000);
        Assert.InRange(Painted(once), 780, 820);     // 40x20
    }

    /// <summary>cover scales the image up, keeping its ratio, until the box is covered; contain
    /// until it fits. On a 200×80 box a 2:1 image contains at 160×80 and covers at 200×100.</summary>
    [Fact]
    public void Cover_and_contain_scale_a_raster_image_by_its_ratio()
    {
        var url = "url(" + TwoByOne() + ")";
        using var contain = Render("background:" + url + " no-repeat center / contain");
        using var cover = Render("background:" + url + " no-repeat center / cover");
        output.WriteLine($"contain {Painted(contain)} px, cover {Painted(cover)} px");
        Assert.InRange(Painted(contain), 12600, 13000);      // 160x80, centred: 20px bare at each side
        Assert.Equal(SKColors.White, contain.GetPixel(10, 40));
        Assert.True(Orange(contain.GetPixel(100, 40)));
        Assert.InRange(Painted(cover), 15800, 16000);        // the whole box
    }

    // ---- the doctor ----------------------------------------------------------------------------

    [Fact]
    public void The_doctor_no_longer_reports_the_geometry_properties()
    {
        var report = CupriDoctor.Check("<body><div class='p'></div></body>",
            Base + " .p{background:" + Grad + " no-repeat center / cover; background-size:50% 100%; background-position:right; background-repeat:repeat-x}");
        output.WriteLine(report.ToString());
        Assert.True(report.IsClean, report.ToString());
    }
}
