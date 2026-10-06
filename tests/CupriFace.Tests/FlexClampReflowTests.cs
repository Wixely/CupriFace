using CupriFace.Dom;
using Xunit;
using Xunit.Abstractions;

namespace CupriFace.Tests;

/// <summary>
/// A <c>min-height</c>/<c>max-height</c> clamp that bites on a flex container lays its items out
/// again against the height the box ends up with (#274).
///
/// <para>The items were measured against the DECLARED height and the clamp applied to the box
/// afterwards, so when the clamp bit the two disagreed and only the items were wrong. The
/// re-layout added for #251 covered one of the four cases: its guard excluded every box with a
/// declared <c>height</c> and every clamp that shrank one. The expensive case was the one it
/// excluded — a <c>max-height</c> bounding a fixed-size panel by the window, where the container's
/// own box came out honest (so a test asking "does the card fit?" passed) while a <c>flex:1</c>
/// child kept the size it was given and hung 162px out of the bottom.</para>
/// </summary>
public class FlexClampReflowTests(ITestOutputHelper output)
{
    // The issue's document: a column card, a fixed head, and a flex:1 body holding something tall.
    private const string Html = """
        <body><div class="card">
          <div class="head">h</div>
          <div class="fields"><div class="tall">x</div></div>
        </div></body>
        """;

    private const string Css = "body{margin:0} .head{height:100px} .fields{flex:1;overflow:scroll} .tall{height:900px}";

    private static RenderNode Find(RenderNode n, string cls) =>
        TestDoc.Find(n, x => x.Element?.ClassList.Contains(cls) == true)!;

    private (float Card, float Fields) Measure(string cardCss)
    {
        using var t = new TestDoc(Html, Css + " .card{display:flex;flex-direction:column;" + cardCss + "}",
            width: 400, height: 700);
        var card = Find(t.Root, "card");
        var fields = Find(t.Root, "fields");
        output.WriteLine($".card{{{cardCss}}} -> card {card.Height:0}, fields {fields.Height:0}");
        return (card.Height, fields.Height);
    }

    // ---- the reported table --------------------------------------------------------------------

    /// <summary>
    /// A clamp applied to a DECLARED height: the body gets what is left of the box, 358px. Both
    /// rows were wrong — 520 (162px past the parent) when the clamp shrank, 100 when it grew.
    ///
    /// <para>Both numbers are Chrome's for the same document. The issue's table has a third row,
    /// <c>min-height: 458px</c> alone, which it marks correct at 458/358: a browser gives 1015/915
    /// there, because with no declared height the content is 1015 tall and a 458 minimum never
    /// bites. The engine reaches 458 only by way of a separate gap in intrinsic sizing (see
    /// <see cref="A_flex_item_contributes_nothing_to_an_auto_height_column"/>), so the row is
    /// deliberately not pinned here — it would lock in one bug to prove another fixed.</para>
    /// </summary>
    [Theory]
    [InlineData("height:620px;max-height:458px")]         // the clamp shrank a declared height
    [InlineData("height:200px;min-height:458px")]         // …and grew one
    public void A_clamped_column_gives_its_flex_child_the_height_that_is_left(string cardCss)
    {
        var (card, fields) = Measure(cardCss);
        Assert.Equal(458f, card, 0.5);
        Assert.Equal(358f, fields, 0.5);
    }

    /// <summary>
    /// The gap the issue noticed alongside, recorded rather than asserted: a <c>flex: 1</c> item
    /// contributes nothing to an auto-height column, so the card measures its head alone.
    ///
    /// <para>Chrome gives 1015 and 915 for this document (900 plus a scrollbar the engine draws as
    /// an overlay, so 1000/900 is the engine's equivalent). It is why <c>max-height</c> ALONE on
    /// such a container changes nothing — the content height never reaches the ceiling — and it is
    /// independent of the clamp this class is about. Pinned at the current numbers so that fixing
    /// it is a deliberate act with this test to update, rather than a silent change.</para>
    /// </summary>
    [Fact]
    public void A_flex_item_contributes_nothing_to_an_auto_height_column()
    {
        var (card, fields) = Measure("");
        Assert.Equal(100f, card, 0.5);        // Chrome: 1015
        Assert.Equal(0f, fields, 0.5);        // Chrome: 915
    }

    /// <summary>The whole point: the child is inside its parent. Asserted as a relationship rather
    /// than a number, because that is what was broken — the box measured as fitting while its
    /// content did not.</summary>
    [Theory]
    [InlineData("height:620px;max-height:458px")]
    [InlineData("min-height:458px")]
    [InlineData("height:200px;min-height:458px")]
    [InlineData("height:620px")]
    [InlineData("max-height:9999px")]
    public void The_items_never_overflow_the_box_they_are_in(string cardCss)
    {
        using var t = new TestDoc(Html, Css + " .card{display:flex;flex-direction:column;" + cardCss + "}",
            width: 400, height: 700);
        var card = Find(t.Root, "card");
        var last = card.Children[^1];
        var bottom = last.Y + last.Height;
        output.WriteLine($".card{{{cardCss}}} -> card {card.Height:0}, content reaches {bottom:0}");
        Assert.True(bottom <= card.Height + 0.5f, $"content reaches {bottom:0} in a {card.Height:0} box");
    }

    /// <summary>A clamp that does not bite changes nothing — the second pass is a correction, not a
    /// behaviour of its own.</summary>
    [Theory]
    [InlineData("height:620px")]
    [InlineData("height:620px;max-height:9999px")]
    [InlineData("height:620px;min-height:10px")]
    public void An_unclamped_column_is_untouched(string cardCss)
    {
        var (card, fields) = Measure(cardCss);
        Assert.Equal(620f, card, 0.5);
        Assert.Equal(520f, fields, 0.5);
    }

    // ---- the axis and the alignment the earlier fixes were about -------------------------------

    /// <summary>#251's case still holds: a box grown by min-height centres in the height it ended
    /// up with rather than the one its content asked for.</summary>
    [Fact]
    public void A_grown_column_still_centres_in_the_height_it_ends_up_with()
    {
        using var t = new TestDoc("<body><div class='n'><div class='k'>x</div></div></body>",
            "body{margin:0} .n{display:flex;flex-direction:column;justify-content:center;width:200px;min-height:200px}"
            + " .k{height:40px}", width: 300, height: 400);
        var k = Find(t.Root, "k");
        output.WriteLine($"child at y={k.Y:0} in a 200px box");
        Assert.Equal(80f, k.Y, 0.5);                       // (200 - 40) / 2
    }

    /// <summary>A shrunk column aligns in the clamped height too — the same correction at the other
    /// end, which is what used to be missing.</summary>
    [Fact]
    public void A_shrunk_column_centres_in_the_clamped_height()
    {
        using var t = new TestDoc("<body><div class='n'><div class='k'>x</div></div></body>",
            "body{margin:0} .n{display:flex;flex-direction:column;justify-content:center;width:200px;height:400px;max-height:200px}"
            + " .k{height:40px}", width: 300, height: 400);
        var n = Find(t.Root, "n");
        var k = Find(t.Root, "k");
        output.WriteLine($"box {n.Height:0}, child at y={k.Y:0}");
        Assert.Equal(200f, n.Height, 0.5);
        Assert.Equal(80f, k.Y, 0.5);
    }

    /// <summary>A row's items are laid out against the width, which no height clamp touches.</summary>
    [Fact]
    public void A_row_is_unaffected_by_a_height_clamp()
    {
        using var t = new TestDoc("<body><div class='r'><div class='a'>a</div><div class='b'>b</div></div></body>",
            "body{margin:0} .r{display:flex;width:300px;height:400px;max-height:120px} .a{flex:1} .b{width:50px}",
            width: 400, height: 500);
        var a = Find(t.Root, "a");
        output.WriteLine($"row item a: {a.Width:0} wide");
        Assert.Equal(250f, a.Width, 0.5);
    }

    // ---- what the container reports afterwards -------------------------------------------------

    /// <summary>The scrollable extent is what the items NOW occupy. Taking the pre-clamp figure
    /// would report a box as scrollable whose content had since been compressed to fit it.</summary>
    [Fact]
    public void A_shrunk_scroll_container_reports_the_extent_its_items_ended_at()
    {
        using var t = new TestDoc("<body><div class='card'><div class='head'>h</div><div class='fields'><div class='tall'>x</div></div></div></body>",
            Css + " .card{display:flex;flex-direction:column;overflow:scroll;height:620px;max-height:458px}",
            width: 400, height: 700);
        var card = Find(t.Root, "card");
        output.WriteLine($"card {card.Height:0}, scroll extent {card.ScrollContentHeight:0}");
        Assert.Equal(458f, card.Height, 0.5);
        Assert.Equal(458f, card.ScrollContentHeight, 0.5);
        Assert.Equal(0f, card.MaxScrollY, 0.5);            // it fits: nothing to scroll
    }

    /// <summary>The unclamped height is still what a <c>transition: height</c> animates to, since
    /// the natural height is recorded before the clamp.</summary>
    [Fact]
    public void The_natural_height_is_still_the_unclamped_one()
    {
        using var t = new TestDoc(Html, Css + " .card{display:flex;flex-direction:column;height:620px;max-height:458px}",
            width: 400, height: 700);
        var card = Find(t.Root, "card");
        output.WriteLine($"card {card.Height:0}, natural {card.ContentNaturalHeight:0}");
        Assert.Equal(458f, card.Height, 0.5);
        Assert.Equal(620f, card.ContentNaturalHeight, 0.5);
    }
}
