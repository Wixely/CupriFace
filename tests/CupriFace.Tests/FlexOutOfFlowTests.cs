using CupriFace.Dom;
using CupriFace.Interaction;
using Xunit;

namespace CupriFace.Tests;

/// <summary>A position:fixed child (a popup/overlay) is out of flow, so opening one inside a flex
/// container must not shove its in-flow siblings — it takes no slot in the flex line. Regression for
/// a context menu opening over a centred region and jumping the region's text aside.</summary>
public class FlexOutOfFlowTests
{
    // A flex:1 item whose text wraps only after the fixed sibling takes its share of the row must grow to
    // fit the wrapped lines — its cross size is measured at its *reduced* width, not the full container's.
    // Regression for kanban/reorder cards whose 2-line text was crammed into a 1-line-tall box.
    [Fact]
    public void A_flex_item_that_wraps_at_its_reduced_width_grows_to_fit_the_text()
    {
        const string css = ".row { display:flex; width:300px; align-items:center; } .side { width:200px; } .txt { flex:1; }";
        using var t = new TestDoc(
            "<body><div class='row'><div class='side'>side</div><div class='txt'>one two three four five six seven</div></div></body>",
            css, width: 500, height: 300);

        var box = t.FindClass("txt");                                  // the flex:1 item (~100px wide → text wraps)
        var text = TestDoc.Find(box, n => n.IsText && n.Lines is { Count: > 0 })!;
        Assert.True(text.Lines!.Count >= 2, $"text should wrap at the reduced width ({text.Lines.Count} line)");
        Assert.True(box.Height + 0.5f >= text.Height, $"the box (h={box.Height:0.0}) must contain its wrapped text (h={text.Height:0.0})");
    }

    private const string Css =
        ".card { display:flex; justify-content:center; width:300px; height:60px; }" +
        ".txt { width:40px; }" +
        ".pop { position:fixed; width:120px; height:40px; display:none; }" +
        ".pop.open { display:block; }";

    private static float TextX(string popClass)
    {
        using var t = new TestDoc(
            $"<body><div class='card'><span class='txt'>Hi</span><div class='pop {popClass}'></div></div></body>",
            Css, width: 400, height: 200);
        return HitTesting.AbsoluteBox(t.FindClass("txt")).X;
    }

    [Fact]
    public void Opening_a_fixed_popup_inside_a_flex_row_does_not_move_its_siblings()
    {
        var closed = TextX("");        // popup display:none
        var open = TextX("open");      // popup display:block, position:fixed
        Assert.Equal(closed, open, 0.5); // the span stays centred; the fixed popup takes no flex slot
    }
}
