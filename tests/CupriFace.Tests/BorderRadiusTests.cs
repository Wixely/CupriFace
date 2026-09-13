using CupriFace.Paint;
using CupriFace.Style;
using SkiaSharp;
using Xunit;
using Xunit.Abstractions;

namespace CupriFace.Tests;

/// <summary>
/// <c>border-radius</c> beyond a single length: the per-corner shorthand (#163) and percentages
/// (#162).
///
/// <para>Both used to be handed whole to a single-number parser, which failed and fell back to zero.
/// The failure mode is what made them worth fixing together: an author who asked for a rounded top
/// edge got NO rounding, and the canonical circular avatar — <c>border-radius: 50%</c> — rendered as
/// a square. In both cases the single-value control right beside it worked, which makes the cause
/// look like anything but the value.</para>
///
/// <para>Asserted on the display list where the question is "what did the engine decide", and on
/// PIXELS where the question is "what did someone see" — a corner is rounded when the fill does not
/// reach the corner of its own box.</para>
/// </summary>
public class BorderRadiusTests(ITestOutputHelper output)
{
    private const string Css = "body{margin:0}.b{width:56px;height:56px;background:#d9642a}";

    /// <summary>The resolved radii of the one filled box, in px.</summary>
    private static CornerRadii Radii(string style)
    {
        using var t = new TestDoc($"<body><div class='b' style='{style}'></div></body>", Css, width: 80, height: 80);
        var fills = t.Doc.BuildFrame(80, 80).Commands.OfType<FillRect>().Where(f => f.W > 50).ToList();
        return Assert.Single(fills).Radius;
    }

    [Fact]
    public void One_value_rounds_every_corner()
    {
        var r = Radii("border-radius:14px");
        Assert.Equal(14f, r.Uniform);
    }

    /// <summary>The shorthand's mirroring, which is the same rule every CSS box shorthand uses: one
    /// value is every corner, two are TL/BR then TR/BL, three add the bottom left, four go clockwise
    /// from the top left.</summary>
    [Theory]
    [InlineData("14px 14px 0 0", 14, 14, 0, 0)]      // a card with a rounded top — the reported case
    [InlineData("14px 0", 14, 0, 14, 0)]             // two values: TL/BR, then TR/BL
    [InlineData("0 0 14px 14px", 0, 0, 14, 14)]
    [InlineData("14px 0 0 0", 14, 0, 0, 0)]          // one corner only
    [InlineData("14px 8px 4px", 14, 8, 4, 8)]        // three values: the fourth mirrors the second
    public void Each_corner_takes_its_own_value(string value, float tl, float tr, float br, float bl)
    {
        var r = Radii($"border-radius:{value}");
        output.WriteLine(r.ToString());
        Assert.Equal((tl, tr, br, bl), (r.TopLeft.X, r.TopRight.X, r.BottomRight.X, r.BottomLeft.X));
        Assert.Equal((tl, tr, br, bl), (r.TopLeft.Y, r.TopRight.Y, r.BottomRight.Y, r.BottomLeft.Y));
    }

    /// <summary>A percentage is a fraction of the BOX, so it cannot become a number until there is a
    /// box — which is why it survives into the computed style instead of being parsed to a float.</summary>
    [Fact]
    public void A_percentage_resolves_against_the_box()
    {
        var r = Radii("border-radius:50%");
        Assert.Equal(28f, r.Uniform);          // half of 56
    }

    /// <summary>…and on a rectangle it is an ELLIPSE: horizontal radii come from the width, vertical
    /// from the height. That is the whole reason the radii are kept per axis.</summary>
    [Fact]
    public void A_percentage_on_a_rectangle_is_an_ellipse_not_a_circle()
    {
        using var t = new TestDoc(
            "<body><div class='wide'></div></body>",
            "body{margin:0}.wide{width:120px;height:40px;background:#d9642a;border-radius:50%}",
            width: 160, height: 80);
        var r = Assert.Single(t.Doc.BuildFrame(160, 80).Commands.OfType<FillRect>(), f => f.W > 100).Radius;

        Assert.Equal(60f, r.TopLeft.X);        // half the width
        Assert.Equal(20f, r.TopLeft.Y);        // half the height
        Assert.Null(r.Uniform);                // …so it is not one number, and must not be drawn as one
    }

    /// <summary>The two-axis form: everything before the slash is horizontal, everything after is
    /// vertical.</summary>
    [Fact]
    public void The_slash_form_gives_the_two_axes_separately()
    {
        var r = Radii("border-radius:30px / 10px");
        Assert.Equal(30f, r.TopLeft.X);
        Assert.Equal(10f, r.TopLeft.Y);
    }

    [Theory]
    [InlineData("border-top-left-radius:12px", 12, 0, 0, 0)]
    [InlineData("border-bottom-right-radius:12px", 0, 0, 12, 0)]
    public void The_longhands_set_one_corner(string style, float tl, float tr, float br, float bl)
    {
        var r = Radii(style);
        Assert.Equal((tl, tr, br, bl), (r.TopLeft.X, r.TopRight.X, r.BottomRight.X, r.BottomLeft.X));
    }

    /// <summary>A value the parser cannot read must not silently round the box by some other amount:
    /// an unknown unit is no radius, which is what every other ignored declaration does.</summary>
    [Theory]
    [InlineData("border-radius:banana")]
    [InlineData("border-radius:12qq")]
    public void An_unreadable_value_is_no_radius_rather_than_a_guess(string style)
    {
        Assert.True(Radii(style).IsZero);
    }

    // ---- and what someone actually sees ---------------------------------------------------------

    /// <summary>A corner is rounded when the fill does not reach the corner pixel of its own box.
    /// Four separate answers per element, which is the claim the shorthand makes.</summary>
    private static (bool TL, bool TR, bool BR, bool BL) RoundedCorners(string style, int w = 56, int h = 56)
    {
        using var t = new TestDoc(
            $"<body><div class='b' style='{style}'></div></body>",
            $"body{{margin:0;background:#000}}.b{{width:{w}px;height:{h}px;background:#d9642a}}",
            width: w + 8, height: h + 8);
        using var bmp = t.Render(SKColors.Black);
        bool Fill(int x, int y) { var p = bmp.GetPixel(x, y); return p.Red > 150 && p.Green is > 60 and < 140; }
        return (!Fill(1, 1), !Fill(w - 2, 1), !Fill(w - 2, h - 2), !Fill(1, h - 2));
    }

    [Fact]
    public void A_card_rounded_only_at_the_top_paints_that_way()
    {
        Assert.Equal((true, true, false, false), RoundedCorners("border-radius:14px 14px 0 0"));
    }

    /// <summary>The avatar: <c>50%</c> has to paint a circle, and did paint a square.</summary>
    [Fact]
    public void Fifty_percent_paints_a_circle()
    {
        Assert.Equal((true, true, true, true), RoundedCorners("border-radius:50%", 40, 40));
    }

    /// <summary>A radius larger than the box does not distort it — Skia scales the corners down the
    /// way CSS does, which is why <c>999px</c> and <c>50%</c> agree on a square box.</summary>
    [Fact]
    public void An_enormous_radius_is_the_same_capsule_as_fifty_percent()
    {
        Assert.Equal(RoundedCorners("border-radius:50%", 40, 40), RoundedCorners("border-radius:999px", 40, 40));
    }

    /// <summary>Hit testing follows the painted shape per corner too: a box rounded only at the top
    /// must still take a click at its square bottom corner, and refuse one outside the rounded top.</summary>
    [Fact]
    public void A_click_is_refused_at_a_rounded_corner_and_taken_at_a_square_one()
    {
        using var t = new TestDoc(
            "<body><div class='clip'><div class='target'></div></div></body>",
            "body{margin:0}.clip{position:relative;width:100px;height:100px;overflow:hidden;border-radius:40px 0 0 0}"
            + ".target{position:absolute;left:0;top:0;width:100px;height:100px}",
            width: 140, height: 140);
        var clicks = 0;
        t.Doc.OnClick(".target", _ => clicks++);

        t.Click(3, 3);        // the ROUNDED corner: outside the painted shape
        Assert.Equal(0, clicks);

        t.Click(97, 97);      // the square one: inside it
        Assert.Equal(1, clicks);
    }
}
