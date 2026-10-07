using CupriFace.Diagnostics;
using CupriFace.Style;
using SkiaSharp;
using Xunit;
using Xunit.Abstractions;

namespace CupriFace.Tests;

/// <summary>
/// A background paints every image layer it was given, first on top (#279).
///
/// <para>Only the first was painted, so a background built from two or three glows over a base
/// colour — the normal way a designed background is made, and 38 of 165 corpus compositions — kept
/// one of its lights and lost the rest. It was a model limit rather than a painter one: the
/// computed style held a single gradient and a single image source.</para>
/// </summary>
public class BackgroundLayerStackTests(ITestOutputHelper output)
{
    private const string Red = "linear-gradient(90deg,#f00,transparent 70%)";
    private const string Blue = "linear-gradient(270deg,#00f,transparent 70%)";

    private SKBitmap Render(string decl, int w = 400, int h = 200)
    {
        using var t = new TestDoc("<body><div class='p'></div></body>",
            "body{margin:0;background:#000} .p{width:" + w + "px;height:" + h + "px;" + decl + "}", width: w, height: h);
        return t.Render(SKColors.Black);
    }

    private (int R, int G, int B) Mean(string decl)
    {
        using var bmp = Render(decl);
        long r = 0, g = 0, b = 0;
        for (var y = 0; y < bmp.Height; y++)
            for (var x = 0; x < bmp.Width; x++) { var c = bmp.GetPixel(x, y); r += c.Red; g += c.Green; b += c.Blue; }
        var n = bmp.Width * (long)bmp.Height;
        var mean = ((int)(r / n), (int)(g / n), (int)(b / n));
        output.WriteLine($"{decl,-96} -> {mean}");
        return mean;
    }

    // ---- the reported table --------------------------------------------------------------------

    /// <summary>Two layers used to render identically to whichever was listed first. Now both
    /// contribute, so the frame carries red AND blue however they are ordered.</summary>
    [Fact]
    public void Both_layers_are_painted()
    {
        var red = Mean($"background-image:{Red}");
        var blue = Mean($"background-image:{Blue}");
        var redFirst = Mean($"background-image:{Red},{Blue}");
        var blueFirst = Mean($"background-image:{Blue},{Red}");

        Assert.True(red.R > 40 && red.B == 0);
        Assert.True(blue.B > 40 && blue.R == 0);
        foreach (var both in new[] { redFirst, blueFirst })
        {
            Assert.True(both.R > 40, "the red layer is missing");
            Assert.True(both.B > 40, "the blue layer is missing");
        }
    }

    /// <summary>Three layers, each a hard-edged band in its own third of the box: all three show,
    /// which a mean cannot prove on its own.</summary>
    [Fact]
    public void Every_layer_in_a_longer_list_is_painted()
    {
        using var bmp = Render(
            "background-image:"
            + "linear-gradient(90deg,#f00 0,#f00 33%,transparent 33%),"
            + "linear-gradient(90deg,transparent 33%,#0f0 33%,#0f0 66%,transparent 66%),"
            + "linear-gradient(90deg,transparent 66%,#00f 66%)");
        var (left, middle, right) = (bmp.GetPixel(60, 100), bmp.GetPixel(200, 100), bmp.GetPixel(340, 100));
        output.WriteLine($"left {left}, middle {middle}, right {right}");
        Assert.True(left.Red > 200, "the first layer is missing");
        Assert.True(middle.Green > 200, "the second layer is missing");
        Assert.True(right.Blue > 200, "the third layer is missing");
    }

    /// <summary>A solid colour under the layers still paints, as it always did.</summary>
    [Fact]
    public void The_background_colour_stays_underneath()
    {
        var m = Mean($"background-color:#004000;background-image:{Red}");
        Assert.True(m.G > 20, "the colour under the layers is missing");
        Assert.True(m.R > 40, "the image layer is missing");
    }

    // ---- order: first is on top ----------------------------------------------------------------

    /// <summary>CSS lists the topmost layer FIRST. Two opaque layers covering the whole box, so
    /// whichever is written first is the one seen.</summary>
    [Theory]
    [InlineData("linear-gradient(#f00,#f00),linear-gradient(#00f,#00f)", 255, 0)]
    [InlineData("linear-gradient(#00f,#00f),linear-gradient(#f00,#f00)", 0, 255)]
    public void The_first_layer_is_on_top(string images, int red, int blue)
    {
        using var bmp = Render("background-image:" + images);
        var c = bmp.GetPixel(200, 100);
        output.WriteLine($"{images} -> {c}");
        Assert.Equal(red, c.Red, 2.0);
        Assert.Equal(blue, c.Blue, 2.0);
    }

    // ---- the geometry longhands pair by index --------------------------------------------------

    /// <summary>`background-size: a, b` sizes the layers independently, pairing by index.</summary>
    [Fact]
    public void Background_size_pairs_with_the_layers_by_index()
    {
        using var bmp = Render(
            "background-image:linear-gradient(#f00,#f00),linear-gradient(#00f,#00f);"
            + "background-repeat:no-repeat;background-size:50% 100%,100% 100%");
        // The red layer covers the left half only; the blue one is underneath across the whole box.
        Assert.True(bmp.GetPixel(100, 100).Red > 200, "the first layer should cover the left half");
        Assert.True(bmp.GetPixel(300, 100).Blue > 200, "the second layer should show where the first stops");
    }

    /// <summary>A shorter list repeats to cover the layers, as in CSS: one position, two layers.</summary>
    [Fact]
    public void A_shorter_longhand_list_repeats()
    {
        using var t = new TestDoc("<body><div class='p'></div></body>",
            "body{margin:0} .p{width:400px;height:200px;background-image:"
            + "linear-gradient(#f00,#f00),linear-gradient(#00f,#00f);background-size:40px 40px,80px 80px;background-position:right}",
            width: 400, height: 200);
        var s = t.FindClass("p").Style;
        Assert.Equal(2, s.BackgroundLayers!.Count);
        Assert.Equal(2, s.BackgroundGeometries!.Count);
        // Two sizes, one position: the position repeats across both layers.
        Assert.Equal(40f, s.GeometryFor(0).Width.Value, 0.5);
        Assert.Equal(80f, s.GeometryFor(1).Width.Value, 0.5);
        Assert.Equal(100f, s.GeometryFor(0).PosX.Value, 0.5);
        Assert.Equal(100f, s.GeometryFor(1).PosX.Value, 0.5);
    }

    /// <summary>The shorthand carries a size per layer too, after each layer's slash.</summary>
    [Fact]
    public void The_shorthand_carries_geometry_per_layer()
    {
        using var t = new TestDoc("<body><div class='p'></div></body>",
            "body{margin:0} .p{width:400px;height:200px;background:"
            + "linear-gradient(#f00,#f00) no-repeat left / 25% 100%,"
            + "linear-gradient(#00f,#00f) no-repeat right / 50% 100%}",
            width: 400, height: 200);
        var s = t.FindClass("p").Style;
        Assert.Equal(2, s.BackgroundLayers!.Count);
        Assert.Equal(25f, s.GeometryFor(0).Width.Value, 0.5);
        Assert.Equal(50f, s.GeometryFor(1).Width.Value, 0.5);
        Assert.Equal(0f, s.GeometryFor(0).PosX.Value, 0.5);
        Assert.Equal(100f, s.GeometryFor(1).PosX.Value, 0.5);
    }

    /// <summary>A later shorthand replaces the whole stack, as a shorthand does.</summary>
    [Fact]
    public void A_later_shorthand_resets_the_layers()
    {
        using var t = new TestDoc("<body><div class='p'></div></body>",
            "body{margin:0} .p{width:400px;height:200px;background:" + Red + "," + Blue + ";background:#000}",
            width: 400, height: 200);
        var s = t.FindClass("p").Style;
        Assert.True(s.BackgroundLayers is null or { Count: 0 });
        Assert.Equal(SKColors.Black, s.Background);
    }

    [Fact]
    public void Background_image_none_clears_every_layer()
    {
        using var t = new TestDoc("<body><div class='p'></div></body>",
            "body{margin:0} .p{width:400px;height:200px;background-image:" + Red + "," + Blue + ";background-image:none}",
            width: 400, height: 200);
        Assert.True(t.FindClass("p").Style.BackgroundLayers is null or { Count: 0 });
    }

    // ---- the two issues together ---------------------------------------------------------------

    /// <summary>
    /// The case the issue was written from: four corner glows in one declaration. Three were
    /// dropped here and the fourth was drawn in the centre by #278, so fixing either alone left the
    /// background wrong. All four now light their own corner.
    /// </summary>
    [Fact]
    public void Four_corner_glows_light_four_corners()
    {
        using var bmp = Render(
            "background-image:"
            + "radial-gradient(circle 60px at 0% 0%, #f00, transparent),"
            + "radial-gradient(circle 60px at 100% 0%, #0f0, transparent),"
            + "radial-gradient(circle 60px at 0% 100%, #00f, transparent),"
            + "radial-gradient(circle 60px at 100% 100%, #ff0, transparent)");
        var tl = bmp.GetPixel(8, 8); var tr = bmp.GetPixel(391, 8);
        var bl = bmp.GetPixel(8, 191); var br = bmp.GetPixel(391, 191);
        output.WriteLine($"TL {tl}  TR {tr}  BL {bl}  BR {br}");
        Assert.True(tl.Red > 120 && tl.Green < 80, "the top-left glow is missing");
        Assert.True(tr.Green > 120 && tr.Red < 80, "the top-right glow is missing");
        Assert.True(bl.Blue > 120 && bl.Red < 80, "the bottom-left glow is missing");
        Assert.True(br.Red > 120 && br.Green > 120, "the bottom-right glow is missing");
        // …and the middle is not a pile of all four.
        Assert.True(bmp.GetPixel(200, 100).Red < 90, "the glows should not pile onto the centre");
    }

    [Fact]
    public void The_doctor_is_clean_on_a_stacked_background()
    {
        var report = CupriDoctor.Check("<body><div class='p'></div></body>",
            "body{margin:0} .p{width:400px;height:200px;background:"
            + "radial-gradient(circle at 18% 20%, #f0d3a6, transparent 31%),"
            + "radial-gradient(circle at 75% 18%, #a6d3f0, transparent 31%), #101418}");
        output.WriteLine(report.ToString());
        Assert.True(report.IsClean, report.ToString());
    }
}
