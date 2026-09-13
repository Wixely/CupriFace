using CupriFace.Interaction;
using Xunit;

namespace CupriFace.Tests;

public class GridTests
{
    /// <summary>
    /// A grid row is sized by its items' MARGIN boxes. It used to be sized by their border boxes
    /// while the margin was still applied to the item's position, so an item with a vertical margin
    /// overflowed its row — and the grid — by exactly that margin, eating whatever padding sat below.
    ///
    /// <para>Reported from the colour picker, whose neutral ramp is the last row of the same grid
    /// and is set apart by a 9px top margin: the popup declares 10px of padding and 1px of it
    /// survived under the greys.</para>
    /// </summary>
    [Fact]
    public void A_row_is_as_tall_as_its_items_margin_box()
    {
        const string css = """
            body { margin:0 }
            .grid { display:grid; width:200px; grid-template-columns: repeat(2, 1fr); padding:10px;
                    background:#eee }
            .cell { height:20px }
            .spaced { margin-top:9px }
            """;
        using var t = new TestDoc(
            "<body><div class='grid'><div class='cell'>a</div><div class='cell'>b</div>"
            + "<div class='cell spaced' id='last'>c</div><div class='cell spaced'>d</div></div></body>",
            css, width: 300, height: 200);

        var grid = t.FindClass("grid");
        var last = TestDoc.Find(t.Root, n => n.Element?.GetAttribute("id") == "last")!;

        // Two rows of 20, the second pushed down 9 by its margin, plus 10 of padding top and bottom.
        Assert.Equal(20f + 9f + 20f + 20f, grid.Height, 1);
        // …and the item ends exactly one padding above the grid's bottom edge, rather than in it.
        Assert.Equal(grid.Height - 10f, last.Y + last.Height, 1);
    }

    /// <summary>The other half: a stretched item leaves room for its own margins instead of
    /// overflowing the cell by them.</summary>
    [Fact]
    public void A_stretched_item_fits_inside_its_cell_with_its_margins()
    {
        const string css = """
            body { margin:0 }
            .grid { display:grid; width:200px; height:100px; grid-template-columns: 1fr;
                    grid-template-rows: 100px; align-items:stretch; justify-items:stretch }
            .cell { margin:12px }
            """;
        using var t = new TestDoc("<body><div class='grid'><div class='cell' id='c'>x</div></div></body>",
                                  css, width: 300, height: 200);
        var cell = TestDoc.Find(t.Root, n => n.Element?.GetAttribute("id") == "c")!;

        Assert.Equal(200f - 24f, cell.Width, 1);
        Assert.Equal(100f - 24f, cell.Height, 1);
        Assert.Equal(12f, cell.X, 1);
        Assert.Equal(12f, cell.Y, 1);
    }

    /// <summary>An item with no margins is unchanged — the common case, and the one a fix like this
    /// can most easily disturb.</summary>
    [Fact]
    public void An_item_without_margins_is_unchanged()
    {
        const string css = "body{margin:0} .grid{display:grid;width:200px;grid-template-columns:repeat(2,1fr)} .cell{height:20px}";
        using var t = new TestDoc(
            "<body><div class='grid'><div class='cell'>a</div><div class='cell' id='b'>b</div></div></body>",
            css, width: 300, height: 200);

        Assert.Equal(20f, t.FindClass("grid").Height, 1);
        Assert.Equal(0f, TestDoc.Find(t.Root, n => n.Element?.GetAttribute("id") == "b")!.Y, 1);
    }

    [Fact]
    public void Named_lines_place_items_by_name()
    {
        const string css = """
            body { margin:0; }
            .grid { display:grid; width:520px;
                    grid-template-columns: [side-start] 120px [side-end main-start] 1fr [main-end]; }
            .s { grid-column: side-start / side-end; }
            .m { grid-column: main-start / main-end; }
            """;
        using var t = new TestDoc("<body><div class='grid'><div class='s'>side</div><div class='m'>main</div></div></body>", css);

        var s = t.FindClass("s");
        var m = t.FindClass("m");
        Assert.Equal(120f, s.Width, 1);                     // the 120px named column
        Assert.Equal(400f, m.Width, 1);                     // the 1fr column (520 − 120)
        Assert.Equal(120f, HitTesting.AbsoluteBox(m).X, 1); // starts after the sidebar column
    }

    [Fact]
    public void Item_can_span_multiple_rows()
    {
        const string css = """
            body { margin:0; }
            .grid { display:grid; grid-template-columns: 1fr 1fr; grid-auto-rows: 50px; gap: 10px; }
            .tall { grid-row: span 2; }
            """;
        using var t = new TestDoc(
            "<body><div class='grid'><div class='tall'>t</div><div class='a'>a</div><div class='b'>b</div></div></body>", css);

        var tall = t.FindClass("tall");
        Assert.Equal(110f, tall.Height, 1);                 // two 50px rows + the 10px gap between them
    }
}
