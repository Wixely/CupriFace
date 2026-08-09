using System.Collections.Generic;
using System.Linq;
using CupriFace.Dom;
using Xunit;

namespace CupriFace.Tests;

/// <summary>
/// <c>&lt;cupri-virtual&gt;</c> windows a <c>data-repeat</c> to just the rows in view (plus a buffer), with
/// spacer divs preserving the full scroll extent — so a 1000-row list keeps only ~a screenful in the DOM,
/// and scrolling rebuilds to the newly-visible rows.
/// </summary>
public class VirtualListTests
{
    private sealed class Model { public List<string> Items { get; set; } = Enumerable.Range(0, 1000).Select(i => $"Row {i}").ToList(); }

    private const string Html =
        "<body><cupri-virtual height=\"200\" item-height=\"40\">" +
        "<div class=\"vrow\" data-repeat=\"Items\">{{.}}</div></cupri-virtual></body>";

    private static List<string> RowTexts(TestDoc t)
    {
        var outp = new List<string>();
        void W(RenderNode n) { if (n.Element?.ClassList.Contains("vrow") == true) outp.Add(n.Element.TextContent.Trim()); foreach (var c in n.Children) W(c); }
        W(t.Root);
        return outp;
    }

    [Fact]
    public void Only_the_visible_window_is_built_not_the_whole_list()
    {
        using var t = new TestDoc(Html, "", new Model(), width: 320, height: 300, components: true);
        var rows = RowTexts(t);
        Assert.InRange(rows.Count, 6, 30);          // a screenful (~13), not 1000
        Assert.Equal("Row 0", rows[0]);             // starts at the top

        // The scroll extent still spans all 1000 rows (spacer divs), so the scrollbar stays correct.
        Assert.True(t.FindClass("cupri-virtual").MaxScrollY > 39000, "full extent preserved");
    }

    [Fact]
    public void Scrolling_windows_to_the_newly_visible_rows()
    {
        using var t = new TestDoc(Html, "", new Model(), width: 320, height: 300, components: true);
        var (vx, vy) = TestDoc.Center(t.FindClass("cupri-virtual"));
        t.Doc.DispatchWheel(vx, vy, 400f);          // scroll down ~10 rows
        t.Layout();

        var rows = RowTexts(t);
        Assert.DoesNotContain("Row 0", rows);        // scrolled out of the window
        Assert.Contains("Row 10", rows);             // now built
    }
}
