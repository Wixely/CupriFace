using CupriFace.Dom;
using Xunit;

namespace CupriFace.Tests;

/// <summary>
/// A flex item that refuses to shrink all the way. The row distributes a size, but LayoutNode then
/// clamps it — min-width, max-width, an intrinsic floor — and the item ends up a different size
/// than the row planned for. The pen has to advance by what the item ACTUALLY became.
/// </summary>
public class FlexClampTests
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

    [Fact]
    public void Items_that_refuse_to_shrink_do_not_overlap_each_other()
    {
        // Six 150px cards asked to fit in 300px. They cannot, and min-width says they must not try:
        // the row overflows. Before this fix each card kept its 150px WIDTH while the pen advanced
        // by the 50px it had been allotted, so they were drawn stacked on top of one another.
        using var doc = CupriDocument.Load(
            """
            <body><div class='row'>
              <div class='card'>0</div><div class='card'>1</div><div class='card'>2</div>
              <div class='card'>3</div><div class='card'>4</div><div class='card'>5</div>
            </div></body>
            """,
            """
            body { margin:0 }
            .row  { width:300px; height:80px; display:flex; }
            .card { width:150px; min-width:150px; height:60px; }
            """);
        doc.BuildFrame(400, 200);

        var cards = Cards(doc);
        Assert.Equal(6, cards.Count);
        for (var i = 0; i < cards.Count; i++)
        {
            Assert.Equal(150f, cards[i].Width, 1);
            Assert.Equal(i * 150f, cards[i].X, 1);          // laid end to end, not on top of each other
        }
        for (var i = 1; i < cards.Count; i++)
            Assert.True(cards[i].X >= cards[i - 1].X + cards[i - 1].Width - 0.5f,
                $"card {i} starts at {cards[i].X:F0}, inside card {i - 1} which ends at {cards[i - 1].X + cards[i - 1].Width:F0}");
    }

    [Fact]
    public void Items_that_shrink_normally_are_unaffected()
    {
        // The ordinary path: no floor, so the row shrinks them to fit and they tile exactly.
        using var doc = CupriDocument.Load(
            "<body><div class='row'><div class='card'>a</div><div class='card'>b</div></div></body>",
            "body{margin:0} .row{width:300px;height:80px;display:flex} .card{width:200px;height:60px}");
        doc.BuildFrame(400, 200);

        var cards = Cards(doc);
        Assert.Equal(150f, cards[0].Width, 1);
        Assert.Equal(150f, cards[1].Width, 1);
        Assert.Equal(0f, cards[0].X, 1);
        Assert.Equal(150f, cards[1].X, 1);
    }
}
