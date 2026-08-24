using CupriFace.Dom;
using Xunit;

namespace CupriFace.Tests;

/// <summary>
/// <c>repeat(auto-fill|auto-fit, …)</c> grid templates (issue #51) — the count comes from the
/// container width and the pattern's minimum, resolved at layout time. Before this, the repeat
/// expander only took a numeric count: an auto-fill template fell through the track parser as one
/// bogus 0px track, so every card collapsed to its padding and stacked in a single column — the
/// standard responsive-card idiom, silently and confidently wrong.
/// </summary>
public class GridAutoRepeatTests
{
    private static List<RenderNode> Cards(CupriDocument doc)
    {
        var found = new List<RenderNode>();
        void Walk(RenderNode n)
        {
            if (n.Element?.ClassList.Contains("card") == true) found.Add(n);
            foreach (var c in n.Children) Walk(c);
        }
        Walk(doc.Root);
        return found;
    }

    private static CupriDocument Load(string template, int cardCount, string extra = "")
    {
        var cards = string.Concat(Enumerable.Range(0, cardCount).Select(i => $"<div class='card'>c{i}</div>"));
        return CupriDocument.Load(
            $"<body><div class='cards'>{cards}</div></body>",
            $"body {{ margin:0 }} .cards {{ display:grid; gap:8px; grid-template-columns: {template}; }} " +
            $".card {{ padding:16px }} {extra}");
    }

    [Fact]
    public void Auto_fill_computes_the_count_from_the_container_width()
    {
        // 800px container: three 200px-minimum columns fit (3×200 + 2×8 = 616 ≤ 800; four would
        // need 824), and the 1fr maxima share the remainder: (800 − 16) / 3 = 261.33 each.
        using var doc = Load("repeat(auto-fill, minmax(200px, 1fr))", 3);
        doc.BuildFrame(800, 220);

        var cards = Cards(doc);
        Assert.Equal(3, cards.Count);
        Assert.All(cards, c => Assert.Equal(261.3f, c.Width, 0));
        Assert.Equal(cards[0].Y, cards[1].Y, 1);            // one row, not a vertical stack
        Assert.Equal(cards[1].Y, cards[2].Y, 1);
        Assert.True(cards[1].X > cards[0].X && cards[2].X > cards[1].X);
    }

    [Fact]
    public void A_narrow_container_gets_one_full_width_column()
    {
        // 320px container: one 200px-minimum track fits, two would need 408.
        using var doc = Load("repeat(auto-fill, minmax(200px, 1fr))", 3);
        doc.BuildFrame(320, 400);

        var cards = Cards(doc);
        Assert.All(cards, c => Assert.Equal(320f, c.Width, 0));   // the 1fr max takes the full width
        Assert.True(cards[0].Y < cards[1].Y && cards[1].Y < cards[2].Y, "cards should stack");
    }

    [Fact]
    public void Auto_fit_collapses_the_tracks_no_item_reaches()
    {
        // 800px container: auto-FILL would make three tracks and leave one empty, freezing a third
        // of the row as blank space. auto-FIT collapses it: two items → two tracks →
        // (800 − 8) / 2 = 396 each.
        using var doc = Load("repeat(auto-fit, minmax(200px, 1fr))", 2);
        doc.BuildFrame(800, 220);

        var cards = Cards(doc);
        Assert.Equal(2, cards.Count);
        Assert.All(cards, c => Assert.Equal(396f, c.Width, 0));
    }

    [Fact]
    public void A_numeric_repeat_with_a_nested_minmax_no_longer_mangles()
    {
        // The second bug in the same expander: the old regex captured `[^)]+`, so a NUMERIC repeat
        // whose pattern contained minmax() was cut at the inner ')' and mis-parsed. Same markup as
        // the auto-fill case, explicit count — identical three columns.
        using var doc = Load("repeat(3, minmax(200px, 1fr))", 3);
        doc.BuildFrame(800, 220);

        var cards = Cards(doc);
        Assert.Equal(3, cards.Count);
        Assert.All(cards, c => Assert.Equal(261.3f, c.Width, 0));
        Assert.Equal(cards[0].Y, cards[2].Y, 1);
    }

    [Fact]
    public void A_plain_numeric_repeat_still_expands()
    {
        // Regression guard for the path that always worked: repeat(2, 100px 50px) → 4 tracks.
        using var doc = Load("repeat(2, 100px 50px)", 4);
        doc.BuildFrame(800, 220);

        var cards = Cards(doc);
        Assert.Equal(100f, cards[0].Width, 0);
        Assert.Equal(50f, cards[1].Width, 0);
        Assert.Equal(100f, cards[2].Width, 0);
        Assert.Equal(50f, cards[3].Width, 0);
        Assert.Equal(cards[0].Y, cards[3].Y, 1);
    }

    [Fact]
    public void Fixed_tracks_and_an_auto_repeat_share_a_template()
    {
        // 600px viewport, margin 0 (no padding): 100px fixed + N×minmax(150px,1fr), gap 10.
        // N=3: 100 + 450 + 3×10 = 580 ≤ 600; N=4 needs 740. The fr tracks then share
        // (600 − 100 − 30) / 3 = 156.67.
        using var doc = CupriDocument.Load(
            "<body><div class='cards'>" +
            "<div class='card'>a</div><div class='card'>b</div><div class='card'>c</div><div class='card'>d</div>" +
            "</div></body>",
            "body { margin:0 } .cards { display:grid; gap:10px; " +
            "grid-template-columns: 100px repeat(auto-fill, minmax(150px, 1fr)); }");
        doc.BuildFrame(600, 220);

        var cards = Cards(doc);
        Assert.Equal(100f, cards[0].Width, 0);              // the fixed track, first
        Assert.Equal(156.7f, cards[1].Width, 0);
        Assert.Equal(156.7f, cards[3].Width, 0);
        Assert.Equal(cards[0].Y, cards[3].Y, 1);            // all four on the one row
    }

    [Fact]
    public void A_minimum_wider_than_the_container_still_yields_one_track()
    {
        // CSS: always at least one repetition, even overflowing — the floor is a floor.
        using var doc = CupriDocument.Load(
            "<body><div class='cards'><div class='card'>a</div></div></body>",
            "body { margin:0 } .cards { display:grid; grid-template-columns: repeat(auto-fill, minmax(200px, 1fr)); }");
        doc.BuildFrame(150, 220);

        Assert.Equal(200f, Cards(doc)[0].Width, 0);         // minmax floor wins over the too-small fr share
    }
}
