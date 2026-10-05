using CupriFace.Diagnostics;
using CupriFace.Svg;
using SkiaSharp;
using Xunit;
using Xunit.Abstractions;

namespace CupriFace.Tests;

/// <summary>
/// The cascade and the animation engine reach the elements INSIDE an inline <c>&lt;svg&gt;</c> (#262).
///
/// <para>Only the <c>&lt;svg&gt;</c> root took stylesheet CSS; a rule written against a
/// <c>&lt;rect&gt;</c> did nothing, a <c>@keyframes</c> on it parsed, ran and moved nothing, and a
/// presentation attribute beat a stylesheet rule that said otherwise. 16 of 165 designed
/// compositions in one surveyed corpus animate an element inside an svg by id — a heart that fills,
/// a path that draws on, a ring that scales — and every one was inert.</para>
/// </summary>
public class SvgCascadeTests(ITestOutputHelper output)
{
    // The issue's document: a 300×300 svg, viewBox 0 0 24 24, holding one pink rect.
    private const string Rect = "<svg viewBox='0 0 24 24'><rect id='r' x='4' y='4' width='16' height='16' fill='#f91880'/></svg>";
    private const string Base = "body{margin:0;background:#fff} svg{width:120px;height:120px}";

    private static CupriDocument Doc(string svg, string css)
    {
        var doc = CupriDocument.Load($"<body>{svg}</body>", Base + css);
        doc.UseSvg();
        doc.Refresh();
        using (doc.RenderToImage(120, 120)) { }
        return doc;
    }

    private static SKBitmap Render(CupriDocument doc, double? t = null)
    {
        if (t is { } time) doc.Animate(time);
        using (doc.RenderToImage(120, 120)) { }
        using var img = doc.RenderToImage(120, 120, SKColors.White);
        return SKBitmap.FromImage(img);
    }

    private static int Painted(SKBitmap bmp, Func<SKColor, bool>? match = null)
    {
        match ??= c => c != SKColors.White;
        var n = 0;
        for (var y = 0; y < bmp.Height; y++)
            for (var x = 0; x < bmp.Width; x++)
                if (match(bmp.GetPixel(x, y))) n++;
        return n;
    }

    private static bool Pink(SKColor c) => c.Red > 200 && c.Green < 80 && c.Blue > 90;

    private int Ink(string svg, string css, double? t = null)
    {
        using var doc = Doc(svg, css);
        using var bmp = Render(doc, t);
        var n = Painted(bmp);
        output.WriteLine($"{css,-70} -> {n} px");
        return n;
    }

    // ---- the reported table --------------------------------------------------------------------

    [Fact]
    public void A_stylesheet_rule_on_a_shape_applies()
    {
        Assert.True(Ink(Rect, "") > 1000, "the control did not draw");
        Assert.Equal(0, Ink(Rect, "#r{opacity:0}"));
    }

    /// <summary>A presentation attribute is a declaration of specificity zero: the stylesheet wins.</summary>
    [Fact]
    public void A_stylesheet_rule_beats_a_presentation_attribute()
    {
        var hiddenByAttr = Ink(Rect.Replace("fill='#f91880'", "fill='#f91880' opacity='0'"), "");
        var restored = Ink(Rect.Replace("fill='#f91880'", "fill='#f91880' opacity='0'"), "#r{opacity:1}");
        Assert.Equal(0, hiddenByAttr);
        Assert.True(restored > 1000, "the rule did not beat the attribute");
    }

    [Fact]
    public void A_keyframes_opacity_on_a_shape_animates()
    {
        const string css = "@keyframes fade{from{opacity:1}to{opacity:0}} #r{animation:fade 1s linear forwards}";
        using var doc = Doc(Rect, css);
        using (var start = Render(doc, 0.0)) Assert.True(Painted(start) > 1000);
        using (var mid = Render(doc, 0.5))
        {
            var p = mid.GetPixel(60, 60);
            output.WriteLine($"mid-fade pixel {p}");
            Assert.InRange(p.Green, 100, 200);     // half-way between pink and white
        }
        using (var end = Render(doc, 1.0)) Assert.Equal(0, Painted(end));
    }

    /// <summary>The issue's fourth row: 600 user units is twenty-five viewBoxes to the right, so
    /// the rect leaves the frame entirely.</summary>
    [Fact]
    public void A_keyframes_transform_on_a_shape_moves_it()
    {
        const string css = "@keyframes go{from{transform:translateX(0)}to{transform:translateX(600px)}} #r{animation:go 1s linear forwards}";
        using var doc = Doc(Rect, css);
        using (var start = Render(doc, 0.0)) Assert.True(Painted(start) > 1000);
        using (var end = Render(doc, 1.0)) Assert.Equal(0, Painted(end));
        // Part-way it is part-way: 300 units right at t=0.5 is still off-frame, so sample early.
        using var early = Render(doc, 0.01);     // 6 units: the rect's left edge moves from x=4 to x=10
        var leftmost = int.MaxValue;
        for (var y = 0; y < early.Height; y++)
            for (var x = 0; x < early.Width; x++)
                if (early.GetPixel(x, y) != SKColors.White) { leftmost = Math.Min(leftmost, x); }
        output.WriteLine($"leftmost ink at t=0.01: x={leftmost}");
        Assert.InRange(leftmost, 45, 55);        // 10 units of 5px each
    }

    // ---- what the corpus animates ----------------------------------------------------------------

    /// <summary>A heart that fills: <c>fill</c> from the stylesheet, and interpolated by a keyframe.</summary>
    [Fact]
    public void Fill_from_the_stylesheet_and_from_a_keyframe()
    {
        using (var doc = Doc(Rect, "#r{fill:#1f6feb}"))
        using (var bmp = Render(doc))
        {
            var p = bmp.GetPixel(60, 60);
            output.WriteLine($"stylesheet fill: {p}");
            Assert.Equal(new SKColor(0x1f, 0x6f, 0xeb), p);
        }
        using (var doc = Doc(Rect, "@keyframes like{from{fill:#ffffff}to{fill:#f91880}} #r{animation:like 1s linear forwards}"))
        {
            using var start = Render(doc, 0.0);
            using var end = Render(doc, 1.0);
            Assert.Equal(0, Painted(start, Pink));
            Assert.True(Painted(end, Pink) > 1000, "the heart did not fill");
        }
    }

    /// <summary>A path that draws on: a dashed stroke whose offset animates from the full length
    /// to zero. At the start nothing of the line shows; at the end all of it does.</summary>
    [Fact]
    public void Stroke_dashoffset_from_a_keyframe_draws_a_path_on()
    {
        const string line = "<svg viewBox='0 0 24 24'><path id='p' d='M2 12 L22 12' stroke='#000' stroke-width='2' fill='none' stroke-dasharray='20'/></svg>";
        const string css = "@keyframes draw{from{stroke-dashoffset:20}to{stroke-dashoffset:0}} #p{animation:draw 1s linear forwards}";
        using var doc = Doc(line, css);
        using var start = Render(doc, 0.0);
        using var half = Render(doc, 0.5);
        using var end = Render(doc, 1.0);
        var (a, b, c) = (Painted(start), Painted(half), Painted(end));
        output.WriteLine($"drawn: start {a} px, half {b} px, end {c} px");
        Assert.True(a < b && b < c, "the dash offset did not animate");
        Assert.True(c > 300, "the line never fully drew");
    }

    /// <summary>A ring that scales about its own centre — the idiom is <c>transform-box: fill-box</c>
    /// with <c>transform-origin: center</c>, and without it a shape pivots about the viewBox corner.</summary>
    [Fact]
    public void A_shape_scales_about_its_own_centre_with_fill_box()
    {
        const string ring = "<svg viewBox='0 0 24 24'><circle id='c' cx='12' cy='12' r='4' fill='#000'/></svg>";
        using var plain = Doc(ring, "");
        using var scaled = Doc(ring, "#c{transform:scale(2);transform-box:fill-box;transform-origin:center}");
        using var corner = Doc(ring, "#c{transform:scale(2)}");   // the SVG default origin: 0 0 of the viewBox
        using var a = Render(plain); using var b = Render(scaled); using var d = Render(corner);
        var (pa, pb, pd) = (Painted(a), Painted(b), Painted(d));
        output.WriteLine($"plain {pa} px, scaled about centre {pb} px, scaled about the corner {pd} px");
        Assert.InRange(pb, pa * 3.5f, pa * 4.5f);                // twice the radius: four times the area
        Assert.Equal(SKColors.Black, b.GetPixel(60, 60));         // still centred
        Assert.True(pd < pb, "scaling about the corner should push part of the disc off the frame");
        Assert.Equal(SKColors.White, d.GetPixel(60, 60));        // the centre moved to 120,120
    }

    /// <summary>Group opacity from the stylesheet multiplies into every shape under the group.</summary>
    [Fact]
    public void A_group_rule_reaches_its_children()
    {
        const string grouped = "<svg viewBox='0 0 24 24'><g class='dim'><rect x='4' y='4' width='16' height='16' fill='#000'/></g></svg>";
        using var doc = Doc(grouped, ".dim{opacity:0.5}");
        using var bmp = Render(doc);
        var p = bmp.GetPixel(60, 60);
        output.WriteLine($"through a half-opaque group: {p}");
        Assert.InRange(p.Red, 100, 160);
    }

    /// <summary>Inherited paint: a stylesheet <c>fill</c> on the group colours a child that says
    /// nothing, and a child's own attribute still wins over it, as in SVG.</summary>
    [Fact]
    public void Fill_inherits_from_a_styled_group_and_a_childs_attribute_still_wins()
    {
        const string grouped = "<svg viewBox='0 0 24 24'><g class='g'>"
            + "<rect x='0' y='0' width='12' height='24'/>"
            + "<rect x='12' y='0' width='12' height='24' fill='#000'/></g></svg>";
        using var doc = Doc(grouped, ".g{fill:#f91880}");
        using var bmp = Render(doc);
        Assert.True(Pink(bmp.GetPixel(30, 60)), $"left half {bmp.GetPixel(30, 60)} should be the group's pink");
        Assert.Equal(SKColors.Black, bmp.GetPixel(90, 60));
    }

    /// <summary>A hover rule on a shape works through the same path, which is the point of using
    /// the cascade rather than a side channel.</summary>
    [Fact]
    public void A_hover_rule_on_a_shape_applies_on_hover()
    {
        using var t = new TestDoc("<body><div class='host'>" + Rect + "</div></body>",
            Base + " .host{width:120px;height:120px} .host:hover #r{opacity:0}", width: 120, height: 120);
        t.Doc.UseSvg(); t.Layout();
        using (var idle = t.Render()) Assert.True(Painted(idle) > 1000);
        t.HoverClass("host");
        using var hovered = t.Render();
        Assert.Equal(0, Painted(hovered));
    }

    // ---- nothing else changed --------------------------------------------------------------------

    /// <summary>Attributes alone, no stylesheet: the drawing is what it was before any of this.</summary>
    [Fact]
    public void Attributes_alone_paint_as_before()
    {
        using var doc = Doc(Rect.Replace("fill='#f91880'", "fill='#f91880' fill-opacity='0.5' stroke='#000' stroke-width='2'"), "");
        using var bmp = Render(doc);
        var centre = bmp.GetPixel(60, 60);
        output.WriteLine($"centre {centre}, edge {bmp.GetPixel(20, 60)}");
        Assert.InRange(centre.Green, 100, 160);                  // half-opaque pink over white
        Assert.True(bmp.GetPixel(20, 60).Red < 60, "the stroke did not draw");
    }

    [Fact]
    public void Visibility_hidden_stays_hidden_even_when_a_rule_sets_opacity()
    {
        Assert.Equal(0, Ink(Rect.Replace("fill='#f91880'", "fill='#f91880' visibility='hidden'"), "#r{opacity:1}"));
    }

    [Fact]
    public void The_doctor_is_clean_on_a_styled_and_animated_svg()
    {
        var report = CupriDoctor.Check("<body>" + Rect + "</body>",
            Base + " @keyframes fade{from{opacity:1}to{opacity:0}} #r{fill:#000;stroke-dashoffset:4;animation:fade 1s}",
            configure: d => d.UseSvg());
        output.WriteLine(report.ToString());
        Assert.True(report.IsClean, report.ToString());
    }
}
