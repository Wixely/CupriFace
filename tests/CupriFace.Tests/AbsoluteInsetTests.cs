using CupriFace.Diagnostics;
using Xunit;
using Xunit.Abstractions;

namespace CupriFace.Tests;

/// <summary>
/// An absolutely positioned box pinned on opposite edges takes the size between them (#200).
///
/// <para><c>position:absolute; top:0; right:0; bottom:0; left:0</c> is the canonical spelling of
/// "fill your parent" and predates <c>inset</c> by twenty years. It was accepted with no diagnostic
/// and sized to nothing, so the full-bleed overlay, backdrop or end card it describes covered
/// nothing — and the composition still rendered, which left nothing to notice. In one reported case
/// a closing card drew its text over the scene with no card behind it; rewriting that single
/// declaration took the final frame from 5.4% to 97.3% matching a browser.</para>
///
/// <para><c>inset: 0</c> is the same thing in modern spelling, and was reported as an unsupported
/// property. It is 61% of one surveyed corpus, 273 of 292 uses being exactly <c>inset: 0</c>.</para>
/// </summary>
public class AbsoluteInsetTests(ITestOutputHelper output)
{
    private const string Parent = ".box{position:relative;width:160px;height:100px}";

    private (float W, float H, float X, float Y) Child(string rule)
    {
        using var t = new TestDoc("<body><div class='box'><div class='p'></div></div></body>",
            "body{margin:0}" + Parent + rule, width: 300, height: 200);
        var n = t.FindClass("p");
        output.WriteLine($"{rule}\n   -> {n.Width:0}x{n.Height:0} at {n.X:0},{n.Y:0}");
        return (n.Width, n.Height, n.X, n.Y);
    }

    // ---- the reported table --------------------------------------------------------------------

    /// <summary>The three spellings the issue tabulated. C was the workaround and already worked;
    /// A and B both came out 160x0.</summary>
    [Theory]
    [InlineData(".p{position:absolute;inset:0;background:#d9642a}")]
    [InlineData(".p{position:absolute;top:0;right:0;bottom:0;left:0;background:#d9642a}")]
    [InlineData(".p{position:absolute;top:0;left:0;width:100%;height:100%;background:#d9642a}")]
    public void Every_spelling_of_fill_your_parent_fills_it(string rule)
    {
        var c = Child(rule);
        Assert.Equal(160f, c.W, 0.5);
        Assert.Equal(100f, c.H, 0.5);
    }

    /// <summary>`inset: 0` is no longer reported as unsupported, because it is supported.</summary>
    [Fact]
    public void Inset_is_no_longer_an_unsupported_property()
    {
        var r = CupriDoctor.Check("<div class='box'><div class='p'></div></div>",
            Parent + ".p{position:absolute;inset:0}", width: 300, height: 200);
        output.WriteLine(r.ToString());
        Assert.DoesNotContain(r.Findings, f => f.Code == "CF0050" && f.Message.Contains("inset"));
    }

    // ---- the shorthand mirrors like every other box shorthand ----------------------------------

    /// <summary>One to four values, mirrored the way `margin` and `padding` mirror: one is every
    /// edge, two are top/bottom then left/right, three add the bottom, four go clockwise.</summary>
    [Theory]
    [InlineData("inset:10px", 140, 80, 10, 10)]                     // all four
    [InlineData("inset:10px 20px", 120, 80, 20, 10)]                // top/bottom, left/right
    [InlineData("inset:10px 20px 30px", 120, 60, 20, 10)]           // …and an explicit bottom
    [InlineData("inset:10px 20px 30px 40px", 100, 60, 40, 10)]      // clockwise from the top
    public void The_shorthand_mirrors(string decl, float w, float h, float x, float y)
    {
        var c = Child($".p{{position:absolute;{decl}}}");
        Assert.Equal(w, c.W, 0.5);
        Assert.Equal(h, c.H, 0.5);
        Assert.Equal(x, c.X, 0.5);
        Assert.Equal(y, c.Y, 0.5);
    }

    /// <summary>Percentages resolve against the containing block, not the viewport.</summary>
    [Fact]
    public void Percentage_offsets_resolve_against_the_parent()
    {
        var c = Child(".p{position:absolute;inset:25%}");
        Assert.Equal(80f, c.W, 0.5);    // 160 - 25% - 25%
        Assert.Equal(50f, c.H, 0.5);    // 100 - 25% - 25%
    }

    // ---- the half that says I did not over-reach ------------------------------------------------

    /// <summary>A declared size still wins: with a width AND both offsets, CSS keeps the width and
    /// ignores `right`. Stretching here would silently resize boxes that were already correct.</summary>
    [Fact]
    public void A_declared_size_wins_over_the_opposite_offset()
    {
        var c = Child(".p{position:absolute;left:0;right:0;top:0;bottom:0;width:40px;height:30px}");
        Assert.Equal(40f, c.W, 0.5);
        Assert.Equal(30f, c.H, 0.5);
    }

    /// <summary>One edge alone still positions and does not size — the old behaviour, which was
    /// correct and must survive.</summary>
    [Fact]
    public void A_single_offset_positions_without_sizing()
    {
        var c = Child(".p{position:absolute;left:20px;top:10px;width:30px;height:20px}");
        Assert.Equal(30f, c.W, 0.5);
        Assert.Equal(20f, c.H, 0.5);
        Assert.Equal(20f, c.X, 0.5);
        Assert.Equal(10f, c.Y, 0.5);
    }

    /// <summary>Only one axis may stretch. A box pinned left/right but with a height keeps that
    /// height, and vice versa.</summary>
    [Fact]
    public void The_two_axes_are_independent()
    {
        var wide = Child(".p{position:absolute;left:0;right:0;top:0;height:20px}");
        Assert.Equal(160f, wide.W, 0.5);
        Assert.Equal(20f, wide.H, 0.5);

        var tall = Child(".p{position:absolute;top:0;bottom:0;left:0;width:20px}");
        Assert.Equal(20f, tall.W, 0.5);
        Assert.Equal(100f, tall.H, 0.5);
    }

    /// <summary>Padding and border come out of the stretched size rather than being added to it —
    /// the box still spans exactly the distance between its edges.</summary>
    [Fact]
    public void Padding_and_border_fit_inside_the_stretched_box()
    {
        var c = Child(".p{position:absolute;inset:0;padding:8px;border:2px solid #000}");
        Assert.Equal(160f, c.W, 0.5);
        Assert.Equal(100f, c.H, 0.5);
    }

    /// <summary>Margins are part of the same arithmetic: the box plus its margins spans the gap.</summary>
    [Fact]
    public void Margins_come_out_of_the_stretched_size()
    {
        var c = Child(".p{position:absolute;inset:0;margin:10px}");
        Assert.Equal(140f, c.W, 0.5);
        Assert.Equal(80f, c.H, 0.5);
    }

    /// <summary>Offsets bigger than the parent clamp at zero rather than going negative — a negative
    /// width is the kind of value that produces NaN geometry three layers away.</summary>
    [Fact]
    public void Impossible_offsets_clamp_to_zero()
    {
        var c = Child(".p{position:absolute;left:200px;right:200px;top:200px;bottom:200px}");
        Assert.Equal(0f, c.W, 0.5);
        Assert.Equal(0f, c.H, 0.5);
    }

    /// <summary>The children of a stretched box lay out inside the size it got, which is the whole
    /// point: an overlay is a container, not a coloured rectangle.</summary>
    [Fact]
    public void Content_inside_a_stretched_box_uses_the_new_size()
    {
        using var t = new TestDoc(
            "<body><div class='box'><div class='p'><div class='fill'></div></div></div></body>",
            "body{margin:0}" + Parent + ".p{position:absolute;inset:0}.fill{width:50%;height:50%}",
            width: 300, height: 200);
        var fill = t.FindClass("fill");
        output.WriteLine($"half of the overlay: {fill.Width:0}x{fill.Height:0}");
        Assert.Equal(80f, fill.Width, 0.5);
        Assert.Equal(50f, fill.Height, 0.5);
    }

    /// <summary>The reported consequence, as a picture: a full-bleed card over a scene must actually
    /// cover it. Asserted on paint rather than geometry, because "covered nothing" was the symptom.</summary>
    [Fact]
    public void A_full_bleed_card_covers_what_is_behind_it()
    {
        const string html = "<body><div class='box'><div class='scene'>scene text</div>"
                          + "<div class='card'></div></div></body>";
        const string css = "body{margin:0}" + Parent
                         + ".scene{width:160px;height:100px;background:#1f6feb}"
                         + ".card{position:absolute;inset:0;background:#ffffff}";

        using var doc = CupriDocument.Load(html, css);
        doc.Refresh();
        using (doc.RenderToImage(160, 100)) { }
        using var img = doc.RenderToImage(160, 100, SkiaSharp.SKColors.Black);
        using var bmp = SkiaSharp.SKBitmap.FromImage(img);

        // Every pixel of the parent should be the card's white, with nothing showing through.
        var showing = 0;
        for (var y = 0; y < 100; y++)
            for (var x = 0; x < 160; x++)
                if (bmp.GetPixel(x, y) != SkiaSharp.SKColors.White) showing++;

        output.WriteLine($"{showing} of {160 * 100} pixels not covered by the card");
        Assert.Equal(0, showing);
    }
}
