using CupriFace.Diagnostics;
using CupriFace.Paint;
using CupriFace.Style;
using SkiaSharp;
using Xunit;
using Xunit.Abstractions;

namespace CupriFace.Tests;

/// <summary>
/// Borders that differ from edge to edge: <c>border-left</c> and its three siblings, the longhand
/// widths and colours, and the one-to-four-value forms of <c>border-width</c> and
/// <c>border-color</c> (#170).
///
/// <para>Both halves failed, and they failed differently, which is what made this worth a test file
/// of its own. <c>border-left</c> was not in the property switch at all, so it was thrown away and
/// CF0050 said so. <c>border-width: 1px 0 0 0</c> WAS in the switch, and handed the whole string to
/// the single-length parser, which failed and fell back to zero — so the box lost its border
/// entirely and nothing reported anything, because the property name was known.</para>
///
/// <para>A header underline, a column separator, the coloured rule down the side of a quoted reply:
/// the engine has always laid out and painted per-side widths. There was simply no property that
/// reached them.</para>
///
/// <para><b>Style stays whole-box on purpose.</b> Width and colour are per side; a dashed left with
/// a solid top is not expressible, and the last style parsed wins. That is asserted below so the
/// limit is a decision rather than a surprise.</para>
/// </summary>
public class BorderSidesTests(ITestOutputHelper output)
{
    private const string Css = "body{margin:0}.b{width:60px;height:40px;background:#fff}";

    /// <summary>The widths the engine resolved, clockwise from the top.</summary>
    private static (float T, float R, float B, float L) Widths(string style)
    {
        using var t = new TestDoc($"<body><div class='b' style='{style}'></div></body>", Css, width: 100, height: 80);
        var n = t.FindClass("b");
        return (n.BorderTopW, n.BorderRightW, n.BorderBottomW, n.BorderLeftW);
    }

    /// <summary>The one border command the box produced.</summary>
    private static BorderRect Border(string style)
    {
        using var t = new TestDoc($"<body><div class='b' style='{style}'></div></body>", Css, width: 100, height: 80);
        return Assert.Single(t.Doc.BuildFrame(100, 80).Commands.OfType<BorderRect>());
    }

    // ---- one edge at a time ---------------------------------------------------------------------

    [Theory]
    [InlineData("border-left:3px solid #fbbf24", 0, 0, 0, 3)]
    [InlineData("border-top:3px solid #fbbf24", 3, 0, 0, 0)]
    [InlineData("border-right:3px solid #fbbf24", 0, 3, 0, 0)]
    [InlineData("border-bottom:2px solid #fbbf24", 0, 0, 2, 0)]
    public void The_per_edge_shorthand_sets_only_its_own_edge(string style, float top, float right, float bottom, float left)
    {
        var w = Widths(style);
        output.WriteLine($"{style} -> {w}");
        Assert.Equal((top, right, bottom, left), w);
    }

    [Theory]
    [InlineData("border-top-width:4px", 4, 0, 0, 0)]
    [InlineData("border-left-width:4px", 0, 0, 0, 4)]
    public void The_width_longhands_set_one_edge(string style, float top, float right, float bottom, float left)
        => Assert.Equal((top, right, bottom, left), Widths(style));

    /// <summary>A per-edge shorthand on top of the whole-box one narrows a single edge rather than
    /// replacing all four — the cascade order authors rely on for "a box, but heavier underneath".</summary>
    [Fact]
    public void A_per_edge_shorthand_overrides_only_that_edge_of_the_whole_box_one()
    {
        Assert.Equal((1f, 1f, 4f, 1f), Widths("border:1px solid #333; border-bottom:4px solid #333"));
    }

    // ---- the one-to-four-value forms ------------------------------------------------------------

    /// <summary>The mirroring every CSS box shorthand uses: one value is every edge, two are
    /// top/bottom then left/right, three add the bottom, four go clockwise from the top.</summary>
    [Theory]
    [InlineData("1px 0 0 0", 1, 0, 0, 0)]        // the reported case: a top rule only
    [InlineData("2px", 2, 2, 2, 2)]
    [InlineData("2px 4px", 2, 4, 2, 4)]
    [InlineData("2px 4px 6px", 2, 4, 6, 4)]
    [InlineData("1px 2px 3px 4px", 1, 2, 3, 4)]
    public void Border_width_takes_one_to_four_values(string value, float top, float right, float bottom, float left)
    {
        var w = Widths($"border:solid #333;border-width:{value}");
        output.WriteLine($"border-width:{value} -> {w}");
        Assert.Equal((top, right, bottom, left), w);
    }

    [Fact]
    public void Border_color_takes_one_to_four_values()
    {
        var b = Border("border:2px solid;border-color:#ff0000 #00ff00 #0000ff #ffff00");
        Assert.Equal(new SKColor(0xff, 0, 0), b.ColorOf(0));
        Assert.Equal(new SKColor(0, 0xff, 0), b.ColorOf(1));
        Assert.Equal(new SKColor(0, 0, 0xff), b.ColorOf(2));
        Assert.Equal(new SKColor(0xff, 0xff, 0), b.ColorOf(3));
    }

    /// <summary>A colour whose own value contains spaces must not be split by the value splitter —
    /// <c>rgb(255 0 0)</c> is one colour, not three.</summary>
    [Fact]
    public void A_functional_colour_is_one_value_not_three()
    {
        var b = Border("border:2px solid;border-color:rgb(255, 0, 0)");
        Assert.Equal(new SKColor(0xff, 0, 0), b.ColorOf(0));
        Assert.True(b.UniformColor);
    }

    [Fact]
    public void The_colour_longhands_set_one_edge()
    {
        var b = Border("border:2px solid #333333;border-bottom-color:#ff0000");
        Assert.Equal(new SKColor(0x33, 0x33, 0x33), b.ColorOf(0));
        Assert.Equal(new SKColor(0xff, 0, 0), b.ColorOf(2));
        Assert.False(b.UniformColor);
    }

    // ---- what the rasteriser is handed ----------------------------------------------------------

    /// <summary>An ordinary single-colour box must still report a uniform colour, because that is
    /// what lets the rasteriser stroke one rounded rectangle instead of filling four sides. Losing
    /// it would round every plain bordered box slightly differently.</summary>
    [Fact]
    public void An_ordinary_border_is_still_uniform_so_it_keeps_the_stroked_path()
    {
        Assert.True(Border("border:2px solid #334455").UniformColor);
    }

    /// <summary>A box whose only border is one edge still emits a command — the whole-box colour
    /// test used to answer for an edge that was not there.</summary>
    [Fact]
    public void A_single_edge_still_produces_a_border_command()
    {
        var b = Border("border-left:3px solid #fbbf24");
        Assert.Equal(3f, b.Left);
        Assert.Equal(0f, b.Top);
    }

    /// <summary>A border with a width but no visible colour paints nothing, and must not emit a
    /// command either — transparent is a real answer, not a missing one.</summary>
    [Fact]
    public void A_transparent_border_emits_nothing()
    {
        using var t = new TestDoc("<body><div class='b' style='border:2px solid transparent'></div></body>",
                                  Css, width: 100, height: 80);
        Assert.Empty(t.Doc.BuildFrame(100, 80).Commands.OfType<BorderRect>());
    }

    // ---- and what someone actually sees ---------------------------------------------------------

    private static SKBitmap Paint(string style)
    {
        var t = new TestDoc($"<body><div class='b' style='{style}'></div></body>",
                            // border-box, so 60x40 IS the outer box and the edge pixels are where
                            // the arithmetic below says they are rather than 8px further out.
                            "body{margin:0;background:#fff}"
                            + ".b{box-sizing:border-box;width:60px;height:40px;background:#fff}",
                            width: 80, height: 60);
        var bmp = t.Render(SKColors.White);
        t.Dispose();
        return bmp;
    }

    /// <summary>The reported case, in pixels: a coloured rule down the left edge and nothing down
    /// the right. Before the fix the declaration was discarded and the box was blank.</summary>
    [Fact]
    public void A_left_border_paints_on_the_left_and_nowhere_else()
    {
        using var bmp = Paint("border-left:3px solid #ff0000");

        var left = bmp.GetPixel(1, 20);
        var right = bmp.GetPixel(58, 20);
        output.WriteLine($"left={left} right={right}");

        Assert.True(left.Red > 200 && left.Green < 60, $"the left edge should be red, got {left}");
        Assert.True(right is { Red: > 200, Green: > 200, Blue: > 200 },
                    $"the right edge should be untouched background, got {right}");
    }

    /// <summary>Each edge in its own colour. This is the assertion the four-colour display-list
    /// command exists for: one shared paint colour drew all four edges in whichever one it held.</summary>
    [Fact]
    public void Each_edge_paints_in_its_own_colour()
    {
        using var bmp = Paint("border:4px solid;border-color:#ff0000 #00ff00 #0000ff #ffff00");

        var top = bmp.GetPixel(30, 1);
        var right = bmp.GetPixel(58, 20);
        var bottom = bmp.GetPixel(30, 38);
        var left = bmp.GetPixel(1, 20);
        output.WriteLine($"top={top} right={right} bottom={bottom} left={left}");

        Assert.True(top.Red > 200 && top.Blue < 60, $"top should be red, got {top}");
        Assert.True(right.Green > 200 && right.Red < 60, $"right should be green, got {right}");
        Assert.True(bottom.Blue > 200 && bottom.Red < 60, $"bottom should be blue, got {bottom}");
        Assert.True(left.Red > 200 && left.Green > 200 && left.Blue < 60, $"left should be yellow, got {left}");
    }

    /// <summary>A per-side border takes room the way a whole-box one does: the content moves right
    /// by the left border's width. Layout always handled this; nothing could reach it.</summary>
    [Fact]
    public void A_left_border_pushes_the_content_across()
    {
        using var t = new TestDoc(
            "<body><div class='b' style='border-left:6px solid #333'><span id='in'>x</span></div></body>",
            Css, width: 100, height: 80);
        var inner = TestDoc.Find(t.Root, n => n.Element?.GetAttribute("id") == "in")!;
        Assert.Equal(6f, inner.X, 1);
    }

    // ---- the diagnostics ------------------------------------------------------------------------

    /// <summary>The doctor derives CF0050 from what the resolver threw away, so a property it now
    /// understands stops being reported without the doctor being told anything.</summary>
    [Theory]
    [InlineData("border-left:3px solid #fbbf24")]
    [InlineData("border-top-width:2px")]
    [InlineData("border-bottom-color:#333")]
    public void A_per_side_border_is_no_longer_reported_as_unsupported(string decl)
    {
        var report = CupriDoctor.Check("<body><div class='b'></div></body>", $".b{{{decl}}}", width: 100, height: 80);
        Assert.DoesNotContain(report.Findings, f => f.Code == "CF0050");
    }

    /// <summary>Style is whole-box, and the last one parsed wins. Stated as a test because a silent
    /// limit is the thing this whole file exists to prevent.</summary>
    [Fact]
    public void Style_stays_whole_box_and_the_last_one_wins()
    {
        var b = Border("border:2px solid #333;border-left:2px dashed #333");
        Assert.Equal(BorderLineStyle.Dashed, b.Style);
    }
}
