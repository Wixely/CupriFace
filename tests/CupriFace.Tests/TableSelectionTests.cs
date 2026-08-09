using System.Collections.Generic;
using CupriFace.Dom;
using CupriFace.Style;
using Xunit;

namespace CupriFace.Tests;

/// <summary>
/// <c>&lt;cupri-table select="{{Set}}"&gt;</c>: clicking a body row toggles its index in the bound comma-set
/// (multi-select) and flags the row <c>data-selected</c> for the highlight; the header row is sticky.
/// </summary>
public class TableSelectionTests
{
    private sealed class Model { public string Sel { get; set; } = ""; }

    private const string Html =
        "<body><cupri-table select=\"{{Sel}}\">" +
          "<cupri-row header><cupri-cell>Name</cupri-cell></cupri-row>" +
          "<cupri-row><cupri-cell>Ada</cupri-cell></cupri-row>" +
          "<cupri-row><cupri-cell>Grace</cupri-cell></cupri-row>" +
          "<cupri-row><cupri-cell>Linus</cupri-cell></cupri-row>" +
        "</cupri-table></body>";

    private static List<RenderNode> BodyRows(TestDoc t)
    {
        var outp = new List<RenderNode>();
        void W(RenderNode n) { if (n.Element?.HasAttribute("data-toggle-value") == true) outp.Add(n); foreach (var c in n.Children) W(c); }
        W(t.Root);
        return outp;
    }

    [Fact]
    public void Clicking_rows_multi_selects_them_and_highlights()
    {
        var m = new Model();
        using var t = new TestDoc(Html, "", m, width: 320, height: 300, components: true);

        t.ClickNode(BodyRows(t)[1]);                 // select Grace (index 1)
        Assert.Equal("1", m.Sel);
        Assert.True(BodyRows(t)[1].Element!.HasAttribute("data-selected")); // highlighted

        t.ClickNode(BodyRows(t)[0]);                 // also select Ada (index 0)
        var set = new HashSet<string>(m.Sel.Split(','));
        Assert.Contains("0", set);
        Assert.Contains("1", set);

        t.ClickNode(BodyRows(t)[1]);                 // click Grace again → deselect
        Assert.DoesNotContain("1", new HashSet<string>(m.Sel.Split(',')));
        Assert.False(BodyRows(t)[1].Element!.HasAttribute("data-selected"));
    }

    [Fact]
    public void The_header_row_is_sticky()
    {
        using var t = new TestDoc(Html, "", new Model(), width: 320, height: 300, components: true);
        var header = t.Find(n => n.Element?.ClassList.Contains("header") == true)!;
        Assert.Equal(PositionType.Sticky, header.Style.Position);
    }
}
