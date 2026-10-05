using CupriFace.Diagnostics;
using CupriFace.Style;
using SkiaSharp;
using Xunit;
using Xunit.Abstractions;

namespace CupriFace.Tests;

/// <summary>
/// <c>clip-path</c> clips the element's paint to a basic shape in its own box (#268), and the
/// shape's numbers animate between keyframes.
///
/// <para>It was reported unsupported in every form and the element painted unclipped. 40 of 165
/// designed compositions in one corpus use it, and rarely decoratively: a wipe is an
/// <c>inset()</c> animated from one edge, an iris a <c>circle()</c> growing from a point, a lower
/// third's mask reveal an <c>inset()</c> that opens, a diagonal cut a <c>polygon()</c>. Every one
/// showed its final state from the first frame.</para>
/// </summary>
public class ClipPathTests(ITestOutputHelper output)
{
    // A 200×100 orange box filling the frame.
    private const string Base = "body{margin:0;background:#fff} .p{width:200px;height:100px;background:#d9642a}";

    private static SKBitmap Render(string css, string inner = "")
    {
        using var t = new TestDoc("<body><div class='p'>" + inner + "</div></body>", Base + " " + css, width: 200, height: 100);
        return t.Render();
    }

    private static int Painted(SKBitmap bmp)
    {
        var n = 0;
        for (var y = 0; y < bmp.Height; y++)
            for (var x = 0; x < bmp.Width; x++)
                if (bmp.GetPixel(x, y) != SKColors.White) n++;
        return n;
    }

    private static bool Orange(SKColor c) => c.Red > 180 && c.Green < 140 && c.Blue < 100;

    private int Ink(string css)
    {
        using var bmp = Render(css);
        var n = Painted(bmp);
        output.WriteLine($"{css,-60} -> {n} px");
        return n;
    }

    // ---- the reported table --------------------------------------------------------------------

    [Fact]
    public void Inset_clips_the_edges_it_names()
    {
        using var bmp = Render(".p{clip-path:inset(0 40% 0 0)}");
        var n = Painted(bmp);
        output.WriteLine($"inset(0 40% 0 0) -> {n} px");
        Assert.InRange(n, 11900, 12100);                    // the left 60%: 120×100
        Assert.True(Orange(bmp.GetPixel(100, 50)));
        Assert.Equal(SKColors.White, bmp.GetPixel(150, 50));
    }

    /// <summary>A percentage radius is of the diagonal over √2: 30% of 158.1 is a 47.4px disc.</summary>
    [Fact]
    public void Circle_clips_to_a_disc()
    {
        using var bmp = Render(".p{clip-path:circle(30%)}");
        var n = Painted(bmp);
        output.WriteLine($"circle(30%) -> {n} px");
        Assert.InRange(n, 6800, 7300);                       // π·47.4²
        Assert.True(Orange(bmp.GetPixel(100, 50)));
        Assert.Equal(SKColors.White, bmp.GetPixel(10, 10));
    }

    [Fact]
    public void Polygon_clips_to_the_points()
    {
        using var bmp = Render(".p{clip-path:polygon(0 0, 100% 0, 50% 100%)}");
        var n = Painted(bmp);
        output.WriteLine($"polygon -> {n} px");
        Assert.InRange(n, 9800, 10200);                      // half the box
        Assert.True(Orange(bmp.GetPixel(100, 90)));
        Assert.Equal(SKColors.White, bmp.GetPixel(10, 90));
        Assert.Equal(SKColors.White, bmp.GetPixel(190, 90));
    }

    // ---- the other forms -----------------------------------------------------------------------

    [Fact]
    public void Inset_round_rounds_the_corners_of_what_is_left()
    {
        using var square = Render(".p{clip-path:inset(0)}");
        using var rounded = Render(".p{clip-path:inset(0 round 40px)}");
        Assert.True(Orange(square.GetPixel(2, 2)));
        Assert.Equal(SKColors.White, rounded.GetPixel(2, 2));
        Assert.True(Orange(rounded.GetPixel(100, 50)));
    }

    [Fact]
    public void Circle_at_a_position_sits_there()
    {
        using var bmp = Render(".p{clip-path:circle(20px at 20px 20px)}");
        Assert.True(Orange(bmp.GetPixel(20, 20)));
        Assert.Equal(SKColors.White, bmp.GetPixel(100, 50));
        Assert.InRange(Painted(bmp), 1200, 1320);            // π·20²
    }

    /// <summary>No radius means closest-side: on a 200×100 box that is a 50px disc.</summary>
    [Fact]
    public void A_bare_circle_reaches_the_closest_side()
    {
        var n = Ink(".p{clip-path:circle()}");
        Assert.InRange(n, 7700, 8100);                       // π·50², plus the antialiased rim
        // farthest-side is the 100px half-width: the disc spans the box but misses its corners
        // (the corner is 111.8px from the centre): ∫ 2√(100²−y²) dy over the box = 19,132.
        Assert.InRange(Ink(".p{clip-path:circle(farthest-side)}"), 19000, 19400);
    }

    [Fact]
    public void Ellipse_takes_two_radii()
    {
        var n = Ink(".p{clip-path:ellipse(50% 50%)}");
        Assert.InRange(n, 15500, 16000);                     // π·100·50, plus the rim
    }

    [Fact]
    public void None_leaves_the_box_whole()
    {
        Assert.InRange(Ink(".p{clip-path:none}"), 19900, 20000);
    }

    /// <summary>The children are the element's paint too: a child that would overflow the shape is
    /// cut by it.</summary>
    [Fact]
    public void Children_are_clipped_with_the_element()
    {
        using var bmp = Render(".p{clip-path:inset(0 50% 0 0)} .k{width:200px;height:100px;background:#000}", "<div class='k'></div>");
        Assert.Equal(SKColors.Black, bmp.GetPixel(50, 50));
        Assert.Equal(SKColors.White, bmp.GetPixel(150, 50));
    }

    /// <summary>The shape turns with the element: an inset that keeps the top half, under a
    /// half-turn, keeps the bottom half of the frame.</summary>
    [Fact]
    public void The_shape_follows_the_transform()
    {
        using var bmp = Render(".p{clip-path:inset(0 0 50% 0);transform:rotate(180deg)}");
        Assert.Equal(SKColors.White, bmp.GetPixel(100, 20));
        Assert.True(Orange(bmp.GetPixel(100, 80)));
    }

    // ---- animation -----------------------------------------------------------------------------

    private static SKBitmap At(string css, double t)
    {
        using var doc = new TestDoc("<body><div class='p'></div></body>", Base + " " + css, width: 200, height: 100);
        doc.Doc.Animate(t);
        return doc.Render();
    }

    /// <summary>A wipe: the right inset animates from 100% to 0, so the box reveals from the left.</summary>
    [Fact]
    public void An_inset_wipe_reveals_the_box_over_time()
    {
        const string css = "@keyframes wipe{from{clip-path:inset(0 100% 0 0)}to{clip-path:inset(0 0 0 0)}} .p{animation:wipe 1s linear forwards}";
        using var start = At(css, 0.0);
        using var half = At(css, 0.5);
        using var end = At(css, 1.0);
        output.WriteLine($"wipe: {Painted(start)} / {Painted(half)} / {Painted(end)} px");
        Assert.Equal(0, Painted(start));
        Assert.InRange(Painted(half), 9900, 10100);
        Assert.True(Orange(half.GetPixel(50, 50)));
        Assert.Equal(SKColors.White, half.GetPixel(150, 50));
        Assert.InRange(Painted(end), 19900, 20000);
    }

    /// <summary>An iris: a circle growing from a point.</summary>
    [Fact]
    public void A_circle_iris_grows_from_a_point()
    {
        const string css = "@keyframes iris{from{clip-path:circle(0%)}to{clip-path:circle(80%)}} .p{animation:iris 1s linear forwards}";
        using var start = At(css, 0.0);
        using var quarter = At(css, 0.25);
        using var half = At(css, 0.5);
        var (a, b, c) = (Painted(start), Painted(quarter), Painted(half));
        output.WriteLine($"iris: {a} / {b} / {c} px");
        Assert.Equal(0, a);
        Assert.True(a < b && b < c, "the iris did not grow");
        Assert.InRange(b, 3000, 3300);                       // 20% of 158.1 = 31.6px: π·31.6²
    }

    /// <summary>A polygon reveal: every point moves. Here a slit at the centre opens to the full box.</summary>
    [Fact]
    public void A_polygon_interpolates_point_by_point()
    {
        const string css = "@keyframes open{from{clip-path:polygon(50% 0, 50% 0, 50% 100%, 50% 100%)}to{clip-path:polygon(0 0, 100% 0, 100% 100%, 0 100%)}} .p{animation:open 1s linear forwards}";
        using var half = At(css, 0.5);
        var n = Painted(half);
        output.WriteLine($"half-open polygon: {n} px");
        Assert.InRange(n, 9900, 10100);                      // the middle half: 100×100
        Assert.Equal(SKColors.White, half.GetPixel(25, 50));
        Assert.True(Orange(half.GetPixel(100, 50)));
    }

    /// <summary>Two shapes of different kinds are not interpolable: the pair flips at the midpoint,
    /// as CSS says, rather than producing something that is neither.</summary>
    [Fact]
    public void Different_kinds_flip_at_the_midpoint()
    {
        const string css = "@keyframes mix{from{clip-path:inset(0 50% 0 0)}to{clip-path:circle(20px)}} .p{animation:mix 1s linear forwards}";
        using var early = At(css, 0.4);
        using var late = At(css, 0.6);
        Assert.InRange(Painted(early), 9900, 10100);
        Assert.InRange(Painted(late), 1200, 1320);
    }

    // ---- the doctor ----------------------------------------------------------------------------

    [Fact]
    public void The_doctor_accepts_the_basic_shapes_and_reports_the_rest()
    {
        var clean = CupriDoctor.Check("<body><div class='p'></div></body>",
            Base + " .p{clip-path:inset(0 40% 0 0)} .q{clip-path:circle(30% at center)} .r{clip-path:polygon(0 0, 100% 0, 50% 100%)} .s{clip-path:ellipse(40% 30%) border-box}");
        output.WriteLine(clean.ToString());
        Assert.True(clean.IsClean, clean.ToString());

        var masked = CupriDoctor.Check("<body><div class='p'></div></body>", Base + " .p{clip-path:url(#mask)} .q{clip-path:path('M0 0H10V10Z')}");
        output.WriteLine(masked.ToString());
        Assert.Contains(masked.Findings, f => f.Code == "CF0050" && f.Message.Contains("clip-path"));
    }

    [Fact]
    public void Shapes_compare_by_value_so_a_rebuild_does_not_look_like_a_change()
    {
        var a = new ClipShape { Kind = ClipShapeKind.Inset, Right = new Length(LengthUnit.Percent, 40f) };
        var b = new ClipShape { Kind = ClipShapeKind.Inset, Right = new Length(LengthUnit.Percent, 40f) };
        Assert.Equal(a, b);
        Assert.NotEqual(a, new ClipShape { Kind = ClipShapeKind.Inset, Right = new Length(LengthUnit.Percent, 41f) });
    }
}
