using CupriFace.Diagnostics;
using CupriFace.Paint;
using CupriFace.Svg;
using SkiaSharp;
using Xunit;
using Xunit.Abstractions;

namespace CupriFace.Tests;

/// <summary>
/// Inline <c>&lt;svg&gt;</c>, drawn by the optional <c>CupriFace.Svg</c> package (#203).
///
/// <para>It used to lay out and stay empty — 32% of one surveyed corpus, after
/// <c>letter-spacing</c> and <c>inset</c> the largest remaining gap, and unlike those two a whole
/// sub-language rather than one declaration.</para>
///
/// <para>The slice here is what that corpus actually used SVG for: logos, icons in a UI mock, a
/// progress ring, and connecting lines on a diagram. The shapes are turned into path data and
/// painted by the engine as real vectors, so they stay sharp at any size and compose with the
/// transform, clip and opacity stack — see <c>SVG-SUPPORT.md</c> for what is deliberately left
/// out.</para>
/// </summary>
public class SvgTests(ITestOutputHelper output)
{
    private static CupriDocument Doc(string svg, string css = "")
    {
        var doc = CupriDocument.Load($"<body><div class='host'>{svg}</div></body>",
            // The svg is sized the way an author sizes one — from a stylesheet rule, not from the
            // parent. Without a size of its own it takes its intrinsic one (width/height, else the
            // viewBox), exactly as an image does.
            "body{margin:0;background:#fff}.host{width:80px;height:80px}svg{width:80px;height:80px}" + css);
        doc.UseSvg();
        doc.Refresh();
        using (doc.RenderToImage(120, 120)) { }
        return doc;
    }

    private static IVectorDrawing? Drawing(CupriDocument doc) => doc.Vectors.Get("svg:0");

    private static SKBitmap Render(string svg, string css = "", int w = 120, int h = 120)
    {
        using var doc = Doc(svg, css);
        using (doc.RenderToImage(w, h)) { }
        using var img = doc.RenderToImage(w, h, SKColors.White);
        return SKBitmap.FromImage(img);
    }

    /// <summary>Anything drawn at all, measured rather than eyeballed: how many pixels stopped being
    /// the white background.</summary>
    private static int Painted(SKBitmap bmp)
    {
        var n = 0;
        for (var y = 0; y < bmp.Height; y++)
            for (var x = 0; x < bmp.Width; x++)
                if (bmp.GetPixel(x, y) != SKColors.White) n++;
        return n;
    }

    // ---- it draws at all -------------------------------------------------------------------------

    /// <summary>The headline: an inline SVG paints. Before the package it laid out and stayed
    /// blank.</summary>
    [Fact]
    public void An_inline_svg_paints_something()
    {
        using var bmp = Render("<svg viewBox='0 0 10 10'><rect x='0' y='0' width='10' height='10' fill='#d9642a'/></svg>");
        var painted = Painted(bmp);
        output.WriteLine($"{painted} px painted");
        Assert.True(painted > 1000, $"only {painted} px changed — the svg did not draw");
    }

    /// <summary>…and without the package it still does not, so the opt-in is real rather than
    /// decorative.</summary>
    [Fact]
    public void Without_the_package_nothing_is_drawn()
    {
        using var doc = CupriDocument.Load(
            "<body><div class='host'><svg viewBox='0 0 10 10'><rect x='0' y='0' width='10' height='10' fill='#d9642a'/></svg></div></body>",
            "body{margin:0;background:#fff}.host{width:80px;height:80px}svg{width:80px;height:80px}");
        doc.Refresh();
        using (doc.RenderToImage(120, 120)) { }
        using var img = doc.RenderToImage(120, 120, SKColors.White);
        using var bmp = SKBitmap.FromImage(img);
        Assert.Equal(0, Painted(bmp));
    }

    // ---- every shape in the slice -----------------------------------------------------------------

    /// <summary>Each supported shape becomes geometry. Asserted on pixels, because "the parser
    /// produced a shape" and "the frame shows it" are different claims.</summary>
    [Theory]
    [InlineData("<rect x='1' y='1' width='8' height='8' fill='#000'/>")]
    [InlineData("<rect x='1' y='1' width='8' height='8' rx='2' fill='#000'/>")]
    [InlineData("<circle cx='5' cy='5' r='4' fill='#000'/>")]
    [InlineData("<ellipse cx='5' cy='5' rx='4' ry='2' fill='#000'/>")]
    [InlineData("<line x1='1' y1='1' x2='9' y2='9' stroke='#000' stroke-width='1'/>")]
    [InlineData("<polygon points='5,1 9,9 1,9' fill='#000'/>")]
    [InlineData("<polyline points='1,9 5,1 9,9' stroke='#000' stroke-width='1' fill='none'/>")]
    [InlineData("<path d='M1 1 L9 1 L9 9 Z' fill='#000'/>")]
    [InlineData("<path d='M1 5 A4 4 0 1 1 9 5' stroke='#000' stroke-width='1' fill='none'/>")]
    public void Every_shape_in_the_slice_draws(string shape)
    {
        using var bmp = Render($"<svg viewBox='0 0 10 10'>{shape}</svg>");
        var painted = Painted(bmp);
        output.WriteLine($"{shape[..Math.Min(40, shape.Length)],-42} {painted} px");
        Assert.True(painted > 20, $"nothing drawn for {shape}");
    }

    // ---- the four things the corpus used SVG for ---------------------------------------------------

    /// <summary>An icon: a filled shape with a stroked path over it, the shape of nearly every
    /// checkmark, chevron and glyph in a UI mock.</summary>
    [Fact]
    public void An_icon_draws_its_fill_and_its_stroke()
    {
        using var bmp = Render(
            "<svg viewBox='0 0 24 24'>"
            + "<circle cx='12' cy='12' r='10' fill='#d9642a'/>"
            + "<path d='M7 12 L11 16 L17 8' stroke='#ffffff' stroke-width='2' fill='none'/></svg>");

        var orange = 0; var white = 0;
        for (var y = 0; y < bmp.Height; y++)
            for (var x = 0; x < bmp.Width; x++)
            {
                var p = bmp.GetPixel(x, y);
                if (p.Red > 180 && p.Green is > 70 and < 140 && p.Blue < 80) orange++;
                // white INSIDE the disc is the stroke, not the background
                else if (p == SKColors.White && Inside(x, y)) white++;
            }
        output.WriteLine($"disc {orange} px, stroke-inside-disc {white} px");
        Assert.True(orange > 500, "the filled circle did not draw");
        Assert.True(white > 30, "the stroked checkmark did not draw over it");

        static bool Inside(int x, int y)
        {
            float dx = x - 40, dy = y - 40;      // the 80px host box, centred
            return dx * dx + dy * dy < 30 * 30;
        }
    }

    /// <summary>The progress ring: a dashed circle whose offset is what animates. Both the dash
    /// pattern and a <c>rotate()</c> about a point have to work, or it is a full circle in the wrong
    /// place.</summary>
    [Fact]
    public void A_progress_ring_draws_an_arc_rather_than_a_full_circle()
    {
        const string ring =
            "<svg viewBox='0 0 36 36'><circle cx='18' cy='18' r='15' fill='none' stroke='#10ac84'"
            + " stroke-width='4' stroke-dasharray='{0}' stroke-dashoffset='0'"
            + " transform='rotate(-90 18 18)'/></svg>";

        using var quarter = Render(string.Format(ring, "23.5 70.6"));   // a quarter of ~94.2
        using var whole = Render(string.Format(ring, "none"));

        var q = Painted(quarter); var w = Painted(whole);
        output.WriteLine($"quarter arc {q} px, full ring {w} px");
        Assert.True(q > 20, "the arc did not draw");
        Assert.True(q < w * 0.6f, $"the dash pattern was ignored — arc {q} vs full ring {w}");
    }

    /// <summary>Diagram lines: a <c>&lt;g&gt;</c> that carries the stroke for the children inside
    /// it. Inheritance is the thing to get right — without it a logo comes out entirely black.</summary>
    [Fact]
    public void A_group_passes_its_paint_to_its_children()
    {
        using var doc = Doc("<svg viewBox='0 0 10 10'><g stroke='#1f6feb' stroke-width='2' fill='none'>"
                          + "<line x1='1' y1='9' x2='5' y2='1'/><line x1='5' y1='1' x2='9' y2='9'/></g></svg>");
        var d = Drawing(doc);
        Assert.NotNull(d);
        Assert.Equal(2, d!.Shapes.Count);
        foreach (var shape in d.Shapes)
        {
            Assert.Equal(new SKColor(0x1f, 0x6f, 0xeb), shape.Stroke);
            Assert.Equal(2f, shape.StrokeWidth, 0.01);
            Assert.Equal(SKColors.Transparent, shape.Fill);
        }
    }

    /// <summary>A child overrides what the group gave it, rather than the group always winning.</summary>
    [Fact]
    public void A_child_overrides_its_group()
    {
        using var doc = Doc("<svg viewBox='0 0 10 10'><g fill='#ff0000'>"
                          + "<rect x='0' y='0' width='4' height='4'/>"
                          + "<rect x='5' y='5' width='4' height='4' fill='#00ff00'/></g></svg>");
        var d = Drawing(doc)!;
        Assert.Equal(SKColors.Red, d.Shapes[0].Fill);
        Assert.Equal(SKColors.Lime, d.Shapes[1].Fill);
    }

    // ---- the details that go wrong quietly -----------------------------------------------------------

    /// <summary>The viewBox is fitted with ONE uniform scale and centred — SVG's default
    /// `xMidYMid meet`. Scaling the axes independently is what makes a squashed logo, and it is the
    /// thing naive SVG drawing gets wrong most often.</summary>
    [Fact]
    public void A_square_drawing_in_a_wide_box_stays_square()
    {
        using var bmp = Render("<svg viewBox='0 0 10 10'><circle cx='5' cy='5' r='5' fill='#000'/></svg>",
            ".host{width:160px;height:80px}", w: 200, h: 120);

        // Measure the painted extent: a circle scaled to fit must be as wide as it is tall.
        int minX = int.MaxValue, maxX = -1, minY = int.MaxValue, maxY = -1;
        for (var y = 0; y < bmp.Height; y++)
            for (var x = 0; x < bmp.Width; x++)
                if (bmp.GetPixel(x, y) != SKColors.White)
                { minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); minY = Math.Min(minY, y); maxY = Math.Max(maxY, y); }

        var w = maxX - minX; var h = maxY - minY;
        output.WriteLine($"painted extent {w}x{h} in a 160x80 box");
        Assert.True(Math.Abs(w - h) <= 2, $"the circle was squashed: {w}x{h}");
    }

    /// <summary>A viewBox with a non-zero origin shifts the drawing, rather than being ignored.</summary>
    [Fact]
    public void A_view_box_origin_is_honoured()
    {
        using var doc = Doc("<svg viewBox='10 20 30 40'><rect x='10' y='20' width='30' height='40' fill='#000'/></svg>");
        var d = Drawing(doc)!;
        Assert.Equal(new SKRect(10, 20, 40, 60), d.ViewBox);
    }

    /// <summary>`fill='none'` is nothing, not black — the difference between an outlined icon and a
    /// solid blob.</summary>
    [Fact]
    public void Fill_none_is_not_black()
    {
        using var doc = Doc("<svg viewBox='0 0 10 10'><circle cx='5' cy='5' r='4' fill='none' stroke='#000' stroke-width='1'/></svg>");
        Assert.Equal(SKColors.Transparent, Drawing(doc)!.Shapes[0].Fill);
    }

    /// <summary>A gradient reference is left UNPAINTED rather than guessed at. Drawing it black
    /// would be worse than not drawing it, and SVG-SUPPORT.md says so.</summary>
    [Fact]
    public void An_unresolvable_paint_server_draws_nothing_rather_than_black()
    {
        using var doc = Doc("<svg viewBox='0 0 10 10'><rect x='0' y='0' width='10' height='10' fill='url(#grad)'/></svg>");
        Assert.Equal(SKColors.Transparent, Drawing(doc)!.Shapes[0].Fill);
    }

    /// <summary>Presentation attributes and an inline `style` both work — export tools emit both,
    /// often in the same file.</summary>
    [Fact]
    public void An_inline_style_is_read_like_an_attribute()
    {
        using var doc = Doc("<svg viewBox='0 0 10 10'><rect x='0' y='0' width='10' height='10' style='fill:#00ff00;stroke-width:3'/></svg>");
        var shape = Drawing(doc)!.Shapes[0];
        Assert.Equal(SKColors.Lime, shape.Fill);
        Assert.Equal(3f, shape.StrokeWidth, 0.01);
    }

    /// <summary>`&lt;defs&gt;` holds definitions, not drawings — walking into it would paint a
    /// symbol that was never placed.</summary>
    [Fact]
    public void Defs_are_not_drawn()
    {
        using var doc = Doc("<svg viewBox='0 0 10 10'>"
                          + "<defs><rect x='0' y='0' width='10' height='10' fill='#f00'/></defs>"
                          + "<circle cx='5' cy='5' r='3' fill='#000'/></svg>");
        Assert.Single(Drawing(doc)!.Shapes);
    }

    /// <summary>Transforms compose down the tree rather than each replacing the last.</summary>
    [Fact]
    public void Nested_transforms_compose()
    {
        using var doc = Doc("<svg viewBox='0 0 10 10'><g transform='translate(2 0)'>"
                          + "<rect x='0' y='0' width='2' height='2' transform='translate(3 0)' fill='#000'/></g></svg>");
        var m = Drawing(doc)!.Shapes[0].Transform;
        output.WriteLine($"composed translation: {m.TransX}, {m.TransY}");
        Assert.Equal(5f, m.TransX, 0.01);
    }

    /// <summary>`display:none` inside the drawing hides a subtree, as it does everywhere else.</summary>
    [Fact]
    public void Display_none_hides_a_subtree()
    {
        using var doc = Doc("<svg viewBox='0 0 10 10'>"
                          + "<g display='none'><rect x='0' y='0' width='9' height='9' fill='#f00'/></g>"
                          + "<circle cx='5' cy='5' r='2' fill='#000'/></svg>");
        Assert.Single(Drawing(doc)!.Shapes);
    }

    /// <summary>Path data is written with the invariant culture. A comma decimal separator turns
    /// <c>M1.5 2</c> into <c>M1,5 2</c>, which parses as different coordinates entirely — a bug that
    /// only shows up on someone else's machine.</summary>
    [Fact]
    public void Fractional_coordinates_survive_a_comma_decimal_culture()
    {
        var original = System.Threading.Thread.CurrentThread.CurrentCulture;
        try
        {
            System.Threading.Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            using var doc = Doc("<svg viewBox='0 0 10 10'><rect x='1.5' y='2.5' width='6.25' height='5' fill='#000'/></svg>");
            var d = Drawing(doc)!.Shapes[0].PathData;
            output.WriteLine(d);
            Assert.DoesNotContain(",", d, StringComparison.Ordinal);
            Assert.Contains("1.5", d, StringComparison.Ordinal);
        }
        finally { System.Threading.Thread.CurrentThread.CurrentCulture = original; }
    }

    // ---- lifetime -------------------------------------------------------------------------------------

    /// <summary>A rebuild does not lose the drawing — the DOM is rebuilt on every model change, and
    /// re-registering has to happen each time.</summary>
    [Fact]
    public void A_drawing_survives_a_rebuild()
    {
        using var doc = Doc("<svg viewBox='0 0 10 10'><rect x='0' y='0' width='10' height='10' fill='#000'/></svg>");
        Assert.NotNull(Drawing(doc));
        doc.Refresh();
        doc.Refresh();
        Assert.NotNull(Drawing(doc));
    }

    // ---- diagnostics ------------------------------------------------------------------------------------

    /// <summary>With the package wired, the checker stops calling an inline svg undrawable.</summary>
    [Fact]
    public void The_doctor_is_quiet_when_the_package_is_configured()
    {
        var html = "<div class='host'><svg viewBox='0 0 10 10'><rect x='0' y='0' width='10' height='10' fill='#000'/></svg></div>";
        var r = CupriDoctor.Check(html, ".host{width:40px;height:40px}", configure: d => d.UseSvg());
        output.WriteLine(r.ToString());
        Assert.DoesNotContain(r.Findings, f => f.Code == "CF0030");
    }

    /// <summary>…and without it, still reports — naming the package, so the reader knows what to do
    /// rather than being told SVG does not exist.</summary>
    [Fact]
    public void The_doctor_names_the_package_when_it_is_missing()
    {
        var html = "<div class='host'><svg viewBox='0 0 10 10'><rect x='0' y='0' width='10' height='10' fill='#000'/></svg></div>";
        var r = CupriDoctor.Check(html, ".host{width:40px;height:40px}");
        var f = Assert.Single(r.Findings, x => x.Code == "CF0030");
        output.WriteLine(f.Fix);
        Assert.Contains("CupriFace.Svg", f.Fix, StringComparison.Ordinal);
    }

    /// <summary>An svg with nothing drawable in it is left unclaimed, so it is still reported rather
    /// than silently painting an empty box.</summary>
    [Fact]
    public void An_empty_svg_is_still_reported()
    {
        var r = CupriDoctor.Check("<div class='host'><svg viewBox='0 0 10 10'></svg></div>",
            ".host{width:40px;height:40px}", configure: d => d.UseSvg());
        Assert.Contains(r.Findings, f => f.Code is "CF0030" or "CF0031");
    }
}
