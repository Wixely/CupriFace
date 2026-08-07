using System.Collections.Generic;
using CupriFace.Dom;
using Xunit;

namespace CupriFace.Tests;

/// <summary>A &lt;cupri-split&gt; lays panels side by side with a draggable divider between them; dragging
/// the divider grows one panel and shrinks its neighbour (via flex-grow), and the split survives rebuilds.</summary>
public class SplitPaneTests
{
    private const string Html =
        "<body><cupri-split style=\"width:400px;height:200px\">" +
          "<cupri-split-panel>A</cupri-split-panel>" +
          "<cupri-split-panel>B</cupri-split-panel>" +
        "</cupri-split></body>";

    private static List<RenderNode> Panels(TestDoc t)
    {
        var outp = new List<RenderNode>();
        void W(RenderNode n) { if (n.Element?.ClassList.Contains("cupri-split-panel") == true) outp.Add(n); foreach (var c in n.Children) W(c); }
        W(t.Root);
        return outp;
    }

    [Fact]
    public void Dragging_the_divider_grows_one_panel_and_shrinks_the_other()
    {
        using var t = new TestDoc(Html, "", null, width: 500, height: 300, components: true);
        var p = Panels(t);
        Assert.Equal(2, p.Count);
        Assert.Equal(p[0].Width, p[1].Width, 3);          // equal split to start (both flex:1)
        var (a0, b0) = (p[0].Width, p[1].Width);

        var (dx, dy) = TestDoc.Center(t.Find(n => n.Element?.ClassList.Contains("cupri-split-divider") == true)!);
        t.Doc.DispatchClick(dx, dy, 1);                   // grab the divider
        t.Doc.DispatchPointerMove(dx + 60, dy);           // drag it right
        t.Layout();

        Assert.True(p[0].Width > a0 + 45, $"A grew: {a0} -> {p[0].Width}");
        Assert.True(p[1].Width < b0 - 45, $"B shrank: {b0} -> {p[1].Width}");
        Assert.Equal(a0 + b0, p[0].Width + p[1].Width, 2); // total unchanged (only the boundary moved)

        t.Doc.DispatchPointerUp(dx + 60, dy);
    }

    [Fact]
    public void The_split_ratio_survives_a_rebuild()
    {
        using var t = new TestDoc(Html, "", null, width: 500, height: 300, components: true);
        var (dx, dy) = TestDoc.Center(t.Find(n => n.Element?.ClassList.Contains("cupri-split-divider") == true)!);
        t.Doc.DispatchClick(dx, dy, 1);
        t.Doc.DispatchPointerMove(dx + 60, dy);
        t.Doc.DispatchPointerUp(dx + 60, dy);
        t.Layout();
        var dragged = Panels(t)[0].Width;

        t.Doc.Refresh();   // a rebuild (as any other interaction would trigger)
        t.Layout();
        Assert.Equal(dragged, Panels(t)[0].Width, 2); // preserved, not reset to the equal split
    }

    [Fact]
    public void Neither_panel_collapses_past_the_floor()
    {
        using var t = new TestDoc(Html, "", null, width: 500, height: 300, components: true);
        var (dx, dy) = TestDoc.Center(t.Find(n => n.Element?.ClassList.Contains("cupri-split-divider") == true)!);
        t.Doc.DispatchClick(dx, dy, 1);
        t.Doc.DispatchPointerMove(dx + 10000, dy);        // yank far past the edge
        t.Layout();

        var p = Panels(t);
        Assert.True(p[1].Width >= 35f, $"B kept a minimum: {p[1].Width}");
    }
}
