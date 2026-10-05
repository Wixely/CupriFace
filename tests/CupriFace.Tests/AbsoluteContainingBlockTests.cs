using CupriFace.Dom;
using Xunit;
using Xunit.Abstractions;

namespace CupriFace.Tests;

/// <summary>
/// An absolutely positioned element resolves against its nearest POSITIONED ancestor, or the
/// root when there is none (#259), and its margins move it the way they move a block in flow (#260).
///
/// <para>Both are the centring idioms every composition uses. The engine positioned an absolute
/// child against its direct parent whether or not that parent was positioned, so <c>top: 50%</c>
/// inside an unsized static wrapper was 50% of nothing; and it resolved the margins and then never
/// read them, so <c>margin-top: -187.5px</c> did nothing at all.</para>
/// </summary>
public class AbsoluteContainingBlockTests(ITestOutputHelper output)
{
    // The issue's document, scaled: a 200x100 card in a 400x300 body.
    private const string Html = "<body><div id='root'><div class='card'></div></div></body>";
    private const string Card = ".card{position:absolute;top:50%;left:50%;width:200px;height:100px;background:#15202b}";

    private (float X, float Y, float W, float H) CardIn(string rootCss, string cardCss = Card, string html = Html)
    {
        using var t = new TestDoc(html, "body{margin:0;width:400px;height:300px} #root{" + rootCss + "} " + cardCss,
            width: 400, height: 300);
        var n = t.FindClass("card");
        var b = Interaction.HitTesting.AbsoluteBox(n);
        output.WriteLine($"#root{{{rootCss}}} -> card at {b.X:0},{b.Y:0} {b.W:0}x{b.H:0}");
        return (b.X, b.Y, b.W, b.H);
    }

    // ---- #259: the containing block ------------------------------------------------------------

    /// <summary>The issue's table. Every wrapper gives the same answer a browser gives: the card's
    /// top edge at half the body's height. The first row is the one that was 0.</summary>
    [Theory]
    [InlineData("")]                                                    // static, auto height
    [InlineData("width:400px;height:300px")]                            // static, sized
    [InlineData("position:relative;width:400px;height:300px")]
    [InlineData("position:absolute;inset:0")]
    public void Top_50_percent_resolves_against_the_nearest_positioned_ancestor_or_the_root(string rootCss)
    {
        var c = CardIn(rootCss);
        Assert.Equal(200f, c.X, 0.5);
        Assert.Equal(150f, c.Y, 0.5);
    }

    [Fact]
    public void No_wrapper_at_all_gives_the_same_answer()
    {
        var c = CardIn("", html: "<body><div class='card'></div></body>");
        Assert.Equal((200f, 150f), (c.X, c.Y));
    }

    /// <summary>A positioned wrapper that is NOT the parent is the one that counts, through any
    /// number of static ones; its own padding box is what the offsets refer to.</summary>
    [Fact]
    public void Static_wrappers_in_between_are_walked_through()
    {
        using var t = new TestDoc(
            "<body><div class='frame'><div class='a'><div class='b'><div class='card'></div></div></div></div></body>",
            "body{margin:0} .frame{position:relative;margin:20px;width:300px;height:200px}"
            + " .a{padding:10px} .b{padding:10px}"
            + " .card{position:absolute;right:0;bottom:0;width:50px;height:40px}",
            width: 400, height: 300);
        var b = Interaction.HitTesting.AbsoluteBox(t.FindClass("card"));
        output.WriteLine($"card at {b.X:0},{b.Y:0}");
        // Bottom-right of the frame's box (at 20,20 300x200), not of .b's padded box.
        Assert.Equal(20 + 300 - 50, b.X, 0.5);
        Assert.Equal(20 + 200 - 40, b.Y, 0.5);
    }

    /// <summary>With no offset on an axis the box keeps its static position — the start of its own
    /// parent's content box — so a badge that only says <c>right: 0</c> still sits at the top of
    /// the row it was written in.</summary>
    [Fact]
    public void An_axis_with_no_offset_keeps_the_static_position_in_its_own_parent()
    {
        using var t = new TestDoc(
            "<body><div class='frame'><div class='spacer'></div><div class='row'><div class='card'></div></div></div></body>",
            "body{margin:0} .frame{position:relative;width:300px;height:200px}"
            + " .spacer{height:60px} .row{padding:8px}"
            + " .card{position:absolute;right:0;width:50px;height:40px}",
            width: 400, height: 300);
        var b = Interaction.HitTesting.AbsoluteBox(t.FindClass("card"));
        output.WriteLine($"card at {b.X:0},{b.Y:0}");
        Assert.Equal(250f, b.X, 0.5);        // right:0 against the frame
        Assert.Equal(60 + 8, b.Y, 0.5);      // the row's content top: no `top`, so where it was
    }

    /// <summary>A positioned descendant is a containing block of its own: its absolute children
    /// do not leak up to the outer one.</summary>
    [Fact]
    public void A_positioned_descendant_keeps_its_own_absolute_children()
    {
        using var t = new TestDoc(
            "<body><div class='outer'><div class='inner'><div class='card'></div></div></div></body>",
            "body{margin:0} .outer{position:relative;width:300px;height:200px}"
            + " .inner{position:relative;margin:50px;width:100px;height:80px}"
            + " .card{position:absolute;top:0;left:0;width:10px;height:10px}",
            width: 400, height: 300);
        var b = Interaction.HitTesting.AbsoluteBox(t.FindClass("card"));
        Assert.Equal((50f, 50f), (b.X, b.Y));
    }

    // ---- #260: margins -------------------------------------------------------------------------

    /// <summary>The issue's table: a negative half-size margin centres the card; a positive one
    /// pushes it in. Both used to leave it at 200,150.</summary>
    [Theory]
    [InlineData("margin-top:-50px;margin-left:-100px", 100, 100)]
    [InlineData("margin-top:30px;margin-left:30px", 230, 180)]
    [InlineData("margin:10px 0 0 20px", 220, 160)]
    public void Margins_move_an_absolutely_positioned_box(string margin, float x, float y)
    {
        var c = CardIn("position:relative;width:400px;height:300px",
            ".card{position:absolute;top:50%;left:50%;width:200px;height:100px;" + margin + "}");
        Assert.Equal(x, c.X, 0.5);
        Assert.Equal(y, c.Y, 0.5);
    }

    [Fact]
    public void A_right_or_bottom_offset_takes_the_margin_on_that_side()
    {
        var c = CardIn("position:relative;width:400px;height:300px",
            ".card{position:absolute;right:0;bottom:0;width:200px;height:100px;margin-right:10px;margin-bottom:20px}");
        Assert.Equal((400 - 200 - 10f, 300 - 100 - 20f), (c.X, c.Y));
    }

    [Fact]
    public void Auto_margins_between_two_pinned_edges_centre_a_sized_box()
    {
        var c = CardIn("position:relative;width:400px;height:300px",
            ".card{position:absolute;inset:0;margin:auto;width:200px;height:100px}");
        Assert.Equal((100f, 100f), (c.X, c.Y));
    }

    /// <summary>The stretch case from #200 still works, and the margins come out of the stretched
    /// size as they always did.</summary>
    [Fact]
    public void Inset_zero_with_margins_still_fills_the_box_minus_the_margins()
    {
        var c = CardIn("position:relative;width:400px;height:300px",
            ".card{position:absolute;inset:0;margin:10px}");
        Assert.Equal((10f, 10f, 380f, 280f), c);
    }
}
