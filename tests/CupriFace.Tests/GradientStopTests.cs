using SkiaSharp;
using Xunit;
using Xunit.Abstractions;

namespace CupriFace.Tests;

/// <summary>
/// A gradient stop's position can be a length, resolved against the gradient line of the box the
/// gradient fills — the tile, under <c>background-size</c> (#273).
///
/// <para>Only a <c>%</c> position was ever read; a px one was dropped and the stop left
/// unpositioned, so <c>#000 1.5px, transparent 2px</c> was a fade from the centre to the far
/// corner. Invisible while the gradient box was the whole element, and a field of blobs once
/// 0.35.0 tiled it: a 1.5px dot on a 36px grid painted 38.6% of its box dark, where a browser
/// paints about half a percent.</para>
/// </summary>
public class GradientStopTests(ITestOutputHelper output)
{
    private static SKBitmap Render(string css, int size = 360)
    {
        using var t = new TestDoc("<body><div class='p'></div></body>",
            "body{margin:0;background:#fff} .p{width:" + size + "px;height:" + size + "px;background-color:#fff;" + css + "}",
            width: size, height: size);
        return t.Render();
    }

    /// <summary>The issue's measure: the share of the box darker than mid-grey.</summary>
    private double DarkShare(string css, int size = 360)
    {
        using var bmp = Render(css, size);
        long dark = 0;
        for (var y = 0; y < bmp.Height; y++)
            for (var x = 0; x < bmp.Width; x++)
                if (bmp.GetPixel(x, y).Red < 128) dark++;
        var share = dark * 100.0 / (bmp.Width * (double)bmp.Height);
        output.WriteLine($"{css,-90} -> {share:F2}% dark");
        return share;
    }

    // ---- the reported table --------------------------------------------------------------------

    /// <summary>Each row used to read 38.6% / 38.6% / 39.2%, unmoved by the dot or the tile. The
    /// expected shares are π·r² over the tile area, plus a little antialiasing at the rim.</summary>
    [Theory]
    [InlineData(36, 1.5, 0.3, 1.2)]
    [InlineData(36, 3.0, 1.8, 3.2)]
    [InlineData(120, 3.0, 0.15, 0.45)]
    public void A_px_dot_on_a_tiled_radial_gradient_is_that_size(int tile, double r, double lo, double hi)
    {
        var share = DarkShare($"background-image:radial-gradient(circle, #000 {r}px, transparent {r + 0.5}px);background-size:{tile}px {tile}px");
        Assert.InRange(share, lo, hi);
    }

    /// <summary>The grid behind every code block in the corpus: a 1px line per 64px tile is one
    /// row in sixty-four, not a band a third of the tile tall.</summary>
    [Fact]
    public void A_px_line_on_a_tiled_linear_gradient_is_one_pixel()
    {
        var share = DarkShare("background-image:linear-gradient(#000 1px, transparent 1px);background-size:64px 64px");
        Assert.InRange(share, 1.3, 2.0);                     // 1/64 = 1.56%
        using var bmp = Render("background-image:linear-gradient(#000 1px, transparent 1px);background-size:64px 64px");
        Assert.True(bmp.GetPixel(100, 0).Red < 128, "the line is the tile's first row");
        Assert.Equal(SKColors.White, bmp.GetPixel(100, 2));
        Assert.True(bmp.GetPixel(100, 64).Red < 128, "and the next tile's");
    }

    // ---- the length is measured along the gradient line of the box the gradient fills ----------

    [Fact]
    public void A_px_stop_on_an_untiled_linear_gradient_is_measured_from_the_start_edge()
    {
        using var bmp = Render("background-image:linear-gradient(90deg, #000 50px, #fff 50px)", 200);
        Assert.Equal(SKColors.Black, bmp.GetPixel(25, 100));
        Assert.Equal(SKColors.Black, bmp.GetPixel(48, 100));
        Assert.Equal(SKColors.White, bmp.GetPixel(52, 100));
        Assert.Equal(SKColors.White, bmp.GetPixel(150, 100));
    }

    /// <summary>The same stops over a different tile: the dot stays the size it was written as.</summary>
    [Fact]
    public void The_dot_does_not_scale_with_the_tile()
    {
        using var small = Render("background-image:radial-gradient(circle, #000 4px, transparent 4.5px);background-size:40px 40px;background-repeat:no-repeat", 40);
        using var large = Render("background-image:radial-gradient(circle, #000 4px, transparent 4.5px);background-size:200px 200px;background-repeat:no-repeat", 200);
        int Dark(SKBitmap b) { var n = 0; for (var y = 0; y < b.Height; y++) for (var x = 0; x < b.Width; x++) if (b.GetPixel(x, y).Red < 128) n++; return n; }
        var (a, b) = (Dark(small), Dark(large));
        output.WriteLine($"4px dot: {a} px in a 40px tile, {b} px in a 200px tile");
        Assert.InRange(a, 40, 70);                            // π·4² ≈ 50
        Assert.InRange(b, 40, 70);
    }

    // ---- the CSS fix-up ------------------------------------------------------------------------

    /// <summary>Unpositioned stops between positioned ones spread evenly: with black at 0 and
    /// white at 100px, an unpositioned grey sits at 50px.</summary>
    [Fact]
    public void An_unpositioned_stop_sits_between_its_positioned_neighbours()
    {
        using var bmp = Render("background-image:linear-gradient(90deg, #000 0px, #808080, #fff 100px)", 200);
        var mid = bmp.GetPixel(50, 100).Red;
        output.WriteLine($"at 50px: {mid}");
        Assert.InRange(mid, 0x78, 0x88);
        Assert.Equal(SKColors.White, bmp.GetPixel(150, 100));
    }

    /// <summary>A stop written before the one above it is pulled up to it, as CSS says — a hard
    /// edge rather than a reversed segment.</summary>
    [Fact]
    public void A_stop_that_runs_backwards_is_pulled_up_to_the_previous_one()
    {
        using var bmp = Render("background-image:linear-gradient(90deg, #000 100px, #fff 20px)", 200);
        Assert.Equal(SKColors.Black, bmp.GetPixel(90, 100));
        Assert.Equal(SKColors.White, bmp.GetPixel(110, 100));
    }

    /// <summary>Percentages, unchanged: the control that showed the tile geometry was never the problem.</summary>
    [Fact]
    public void Percentage_stops_still_resolve_against_the_tile()
    {
        var share = DarkShare("background-image:radial-gradient(circle, #000 4%, transparent 6%);background-size:36px 36px");
        Assert.InRange(share, 0.2, 0.6);
    }
}
