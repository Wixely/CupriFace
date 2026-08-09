using System.Collections.Generic;
using System.Linq;
using CupriFace.Dom;
using CupriFace.Interaction;
using Xunit;

namespace CupriFace.Tests;

/// <summary>A <c>&lt;cupri-table resize="{{Cols}}"&gt;</c> lets you drag a header cell's right boundary to
/// resize that column; the width is written to the bound list and applied to every row's matching cell, so
/// columns stay aligned. The last column is left flexible (no handle).</summary>
public class ColumnResizeTests
{
    private sealed class Model { public string Cols { get; set; } = ""; }

    private const string Html =
        "<body><div style='padding:10px'>" +
        "<cupri-table resize=\"{{Cols}}\" style=\"width:360px\">" +
        "<cupri-row header><cupri-cell>Name</cupri-cell><cupri-cell>Role</cupri-cell><cupri-cell>City</cupri-cell></cupri-row>" +
        "<cupri-row><cupri-cell>Ada</cupri-cell><cupri-cell>Admin</cupri-cell><cupri-cell>London</cupri-cell></cupri-row>" +
        "<cupri-row><cupri-cell>Linus</cupri-cell><cupri-cell>Owner</cupri-cell><cupri-cell>Oslo</cupri-cell></cupri-row>" +
        "</cupri-table></div></body>";

    private static RenderNode HeaderCell(TestDoc t, int col) => t.Find(n =>
        n.Element?.LocalName == "cupri-cell" && n.Element.GetAttribute("data-col") == col.ToString()
        && n.Parent?.Element?.ClassList.Contains("header") == true)!;

    private static List<RenderNode> BodyCells(TestDoc t, int col)
    {
        var outp = new List<RenderNode>();
        void W(RenderNode n)
        {
            if (n.Element?.LocalName == "cupri-cell" && n.Element.GetAttribute("data-col") == col.ToString()
                && n.Parent?.Element?.ClassList.Contains("header") != true) outp.Add(n);
            foreach (var c in n.Children) W(c);
        }
        W(t.Root);
        return outp;
    }

    [Fact]
    public void Dragging_a_header_boundary_widens_the_whole_column_and_keeps_it_aligned()
    {
        var m = new Model();
        using var t = new TestDoc(Html, "", m, components: true, width: 420, height: 240);

        var h0 = HeaderCell(t, 0);
        var startW = h0.Width;                                       // border-box width before the drag
        var b = HitTesting.AbsoluteBox(h0);
        float gx = b.X + b.W - 3, y = b.Y + b.H / 2;                 // just inside the right boundary

        t.Click(gx, y);                                             // grab
        t.Move(gx + 60, y);                                         // drag right 60px (writes the list, rebuilds)
        t.Up(gx + 60, y);

        Assert.False(string.IsNullOrEmpty(m.Cols));                 // a width was written back
        var h0b = HeaderCell(t, 0);
        Assert.True(h0b.Width > startW + 40, $"column should have widened (was {startW}, now {h0b.Width})");

        // Every cell in column 0 (header + body) shares the new width → columns line up.
        foreach (var cell in BodyCells(t, 0))
            Assert.Equal(h0b.Width, cell.Width, 0.5);
    }

    [Fact]
    public void The_last_column_has_no_handle()
    {
        var m = new Model();
        using var t = new TestDoc(Html, "", m, components: true, width: 420, height: 240);

        var h2 = HeaderCell(t, 2);                                  // the last column
        var b = HitTesting.AbsoluteBox(h2);
        t.Click(b.X + b.W - 3, b.Y + b.H / 2);                      // press at its right edge…
        t.Move(b.X + b.W + 57, b.Y + b.H / 2);                      // …and try to drag
        t.Up(b.X + b.W + 57, b.Y + b.H / 2);

        Assert.Equal("", m.Cols);                                   // nothing grabbed, nothing written
    }

    [Fact]
    public void A_resized_width_survives_a_rebuild()
    {
        var m = new Model { Cols = "150" };                         // column 0 preset to 150px content
        using var t = new TestDoc(Html, "", m, components: true, width: 420, height: 240);

        var before = HeaderCell(t, 0).Width;
        t.Doc.Refresh();                                            // force a full rebuild
        t.Layout();
        Assert.Equal(before, HeaderCell(t, 0).Width, 0.5);         // same width (it lives in the model)
        // and it is a fixed ~150px content column, not the default even share
        Assert.True(HeaderCell(t, 0).ContentBoxWidth is > 148 and < 152);
    }
}
