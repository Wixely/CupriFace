using CupriFace.Diagnostics;
using SkiaSharp;
using Xunit;
using Xunit.Abstractions;

namespace CupriFace.Tests;

/// <summary>
/// A radial gradient's prelude is read: <c>at &lt;position&gt;</c>, the ending shape and every size
/// keyword (#278).
///
/// <para>All of it was skipped. The parser stepped over the prelude and the rasteriser filled in a
/// centred circle reaching the farthest corner, so every positioned gradient in a document piled
/// onto the middle of its box — a composition lit from two corners came out as one blob in the
/// centre. 63 declarations across 28 of 165 corpus compositions write one.</para>
///
/// <para>The centred case looked correct and was not: CSS's default ending shape is an ELLIPSE,
/// and the engine drew a circle. Every expectation below is the CSS value, computed from the
/// spec's rules for the ending shape.</para>
/// </summary>
public class RadialGradientTests(ITestOutputHelper output)
{
    // A 400x200 box on black. A hard stop makes the ending shape measurable: `#fff 0, #fff X,
    // transparent X` paints a solid shape of exactly X, and a `50%` stop paints half the ray.
    private (int X0, int Y0, int X1, int Y1, int W, int H) Ink(string gradient)
    {
        using var t = new TestDoc("<body><div class='p'></div></body>",
            "body{margin:0;background:#000} .p{width:400px;height:200px;background-image:" + gradient + "}",
            width: 400, height: 200);
        using var bmp = t.Render(SKColors.Black);
        int x0 = int.MaxValue, y0 = int.MaxValue, x1 = -1, y1 = -1;
        for (var y = 0; y < bmp.Height; y++)
            for (var x = 0; x < bmp.Width; x++)
                if (bmp.GetPixel(x, y).Red > 128) { x0 = Math.Min(x0, x); y0 = Math.Min(y0, y); x1 = Math.Max(x1, x); y1 = Math.Max(y1, y); }
        output.WriteLine($"{gradient,-78} -> {(x1 < 0 ? "nothing" : $"({x0},{y0})-({x1},{y1})  {x1 - x0 + 1} x {y1 - y0 + 1}")}");
        return (x0, y0, x1, y1, x1 - x0 + 1, y1 - y0 + 1);
    }

    /// <summary>A 40px disc, so the centre is the midpoint of what was painted.</summary>
    private (int X, int Y, int R) Disc(string prelude)
    {
        var (x0, y0, x1, y1, w, _) = Ink($"radial-gradient(circle {prelude}, #fff 0, #fff 40px, transparent 40px)");
        return ((x0 + x1) / 2, (y0 + y1) / 2, w / 2);
    }

    // ---- the reported position table -----------------------------------------------------------

    /// <summary>Every row drew at (199, 99) before. The disc is 40px, so its centre is where the
    /// gradient was told to sit, within the half pixel the bounding box rounds by.</summary>
    [Theory]
    [InlineData("at 50% 50%", 200, 100)]
    [InlineData("at 18% 20%", 72, 40)]
    [InlineData("at 75% 18%", 300, 36)]
    [InlineData("at 25% 75%", 100, 150)]
    [InlineData("at 100px 50px", 100, 50)]
    [InlineData("at 30% 70%", 120, 140)]
    public void A_positioned_gradient_is_drawn_where_it_was_put(string prelude, int cx, int cy)
    {
        var (x, y, r) = Disc(prelude);
        Assert.Equal(cx, x, 1.0);
        Assert.Equal(cy, y, 1.0);
        Assert.Equal(40, r, 1.5);
    }

    /// <summary>Keyword positions, including the corner the issue reported. A disc at the corner is
    /// clipped to the quarter inside the box, which is what a corner glow is.</summary>
    [Fact]
    public void Keyword_positions_place_the_centre_on_the_edge()
    {
        var topLeft = Ink("radial-gradient(circle at left top, #fff 0, #fff 40px, transparent 40px)");
        Assert.Equal((0, 0), (topLeft.X0, topLeft.Y0));
        Assert.Equal(39, topLeft.X1, 1.5);
        Assert.Equal(39, topLeft.Y1, 1.5);

        var bottomRight = Ink("radial-gradient(circle at right bottom, #fff 0, #fff 40px, transparent 40px)");
        Assert.Equal(399, bottomRight.X1);
        Assert.Equal(199, bottomRight.Y1);
        Assert.Equal(360, bottomRight.X0, 1.5);

        // A lone keyword centres the other axis, as in CSS.
        var left = Disc("at left");
        Assert.Equal(100, left.Y, 1.0);
    }

    [Fact]
    public void Two_glows_land_in_two_different_corners()
    {
        var a = Disc("at 18% 20%");
        var b = Disc("at 75% 18%");
        output.WriteLine($"glow a at ({a.X},{a.Y}), glow b at ({b.X},{b.Y})");
        Assert.True(b.X - a.X > 150, "the two glows should be far apart, not piled on the centre");
    }

    // ---- the reported shape and size table -----------------------------------------------------

    /// <summary>
    /// Each row is the CSS ending shape, measured at a 50% hard stop (so half of each radius).
    ///
    /// <para>Centred in a 400x200 box the distances are 200 to each side and 100 to the top and
    /// bottom, so: a farthest-corner CIRCLE has r=√(200²+100²)=223.6; a farthest-corner ELLIPSE
    /// keeps the farthest-side ratio (200:100) scaled to meet the corner, giving 200√2 by 100√2;
    /// closest-side is 100 for a circle and 200 by 100 for an ellipse.</para>
    /// </summary>
    [Theory]
    [InlineData("circle at 50% 50%", 224, 200)]            // r 223.6, halved to 111.8; height clipped by the box
    [InlineData("ellipse at 50% 50%", 283, 141)]           // 282.8 x 141.4, halved
    [InlineData("circle closest-side", 100, 100)]          // r 100, halved to 50
    [InlineData("circle farthest-side", 200, 200)]         // r 200, halved to 100
    [InlineData("ellipse closest-side", 200, 100)]         // 200 x 100, halved
    [InlineData("ellipse farthest-side", 200, 100)]        // the same, centred
    [InlineData("circle 60px at 50% 50%", 60, 60)]         // an explicit radius
    [InlineData("ellipse 80px 40px at 50% 50%", 80, 40)]   // two explicit radii
    public void The_ending_shape_is_the_one_that_was_written(string prelude, int w, int h)
    {
        var ink = Ink($"radial-gradient({prelude}, #fff 0, #fff 50%, transparent 50%)");
        Assert.Equal(w, ink.W, 2.0);
        Assert.Equal(h, ink.H, 2.0);
    }

    /// <summary>CSS's default ending shape is an ellipse, not a circle: Chrome normalises the
    /// keyword away in a computed value while keeping <c>circle</c>. A bare gradient in a
    /// non-square box is therefore wider than it is tall, where the engine used to draw a circle.
    /// <b>This changes documents that were already written.</b></summary>
    [Fact]
    public void A_bare_radial_gradient_is_an_ellipse()
    {
        var bare = Ink("radial-gradient(#fff 0, #fff 50%, transparent 50%)");
        var ellipse = Ink("radial-gradient(ellipse, #fff 0, #fff 50%, transparent 50%)");
        var circle = Ink("radial-gradient(circle, #fff 0, #fff 50%, transparent 50%)");
        Assert.Equal(ellipse.W, bare.W, 2.0);
        Assert.Equal(ellipse.H, bare.H, 2.0);
        Assert.True(bare.W > bare.H + 100, "an ellipse in a 2:1 box is clearly wider than it is tall");
        Assert.NotEqual(circle.W, bare.W, 2.0);
    }

    /// <summary>A corner extent keeps the matching side extent's aspect ratio and scales it to meet
    /// that corner, so it is always at least as big as the side one.</summary>
    [Fact]
    public void Corner_extents_reach_past_the_side_ones()
    {
        var closestSide = Ink("radial-gradient(ellipse closest-side at 25% 50%, #fff 0, #fff 50%, transparent 50%)");
        var closestCorner = Ink("radial-gradient(ellipse closest-corner at 25% 50%, #fff 0, #fff 50%, transparent 50%)");
        var farthestSide = Ink("radial-gradient(ellipse farthest-side at 25% 50%, #fff 0, #fff 50%, transparent 50%)");
        output.WriteLine($"closest-side {closestSide.W}, closest-corner {closestCorner.W}, farthest-side {farthestSide.W}");
        Assert.True(closestCorner.W > closestSide.W, "a corner is further than the closest side");
        Assert.True(farthestSide.W > closestSide.W);
    }

    // ---- the size keywords move with the centre ------------------------------------------------

    /// <summary>The centre and the size are one calculation: an extent measures FROM the centre, so
    /// moving the centre changes the radius too. This is what made the old constants self-consistent
    /// and still wrong.</summary>
    [Fact]
    public void An_extent_is_measured_from_the_centre_not_the_box()
    {
        var centred = Ink("radial-gradient(circle closest-side at 50% 50%, #fff 0, #fff 100%, transparent 100%)");
        var offset = Ink("radial-gradient(circle closest-side at 10% 50%, #fff 0, #fff 100%, transparent 100%)");
        output.WriteLine($"closest-side centred {centred.W}, at 10% {offset.W}");
        Assert.Equal(200, centred.W, 2.0);                 // min(200,200,100,100) = 100 radius
        Assert.Equal(80, offset.W, 2.0);                   // 10% of 400 = 40 to the left edge
    }

    // ---- it composes with the rest of the background machinery ---------------------------------

    /// <summary>Under <c>background-size</c> the gradient box is the TILE, so a positioned gradient
    /// is positioned within each tile (#267, #273).</summary>
    [Fact]
    public void A_tiled_gradient_positions_within_its_tile()
    {
        using var t = new TestDoc("<body><div class='p'></div></body>",
            "body{margin:0;background:#000} .p{width:400px;height:200px;"
            + "background-image:radial-gradient(circle at 0 0, #fff 0, #fff 10px, transparent 10px);"
            + "background-size:100px 100px}", width: 400, height: 200);
        using var bmp = t.Render(SKColors.Black);
        // A dot at the top-left of every 100px tile: eight tiles, so eight quarter-discs.
        var corners = 0;
        for (var ty = 0; ty < 200; ty += 100)
            for (var tx = 0; tx < 400; tx += 100)
                if (bmp.GetPixel(tx + 2, ty + 2).Red > 128) corners++;
        output.WriteLine($"tiles with a dot in the corner: {corners}");
        Assert.Equal(8, corners);
    }

    [Fact]
    public void The_doctor_is_clean_on_every_prelude_form()
    {
        var report = CupriDoctor.Check("<body><div class='p'></div></body>",
            "body{margin:0} .p{width:400px;height:200px;background-image:radial-gradient(circle at 18% 20%, #fff, transparent)}"
            + " .a{background-image:radial-gradient(ellipse farthest-corner at left top, #fff, transparent)}"
            + " .b{background-image:radial-gradient(60px 30px at 10px 20px, #fff, transparent)}"
            + " .c{background-image:radial-gradient(closest-side, #fff, transparent)}");
        output.WriteLine(report.ToString());
        Assert.True(report.IsClean, report.ToString());
    }
}
