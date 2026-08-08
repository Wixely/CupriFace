using System.Collections.Generic;
using CupriFace.Dom;
using Xunit;

namespace CupriFace.Tests;

/// <summary>Drag-to-reorder: grabbing a row's handle and dragging it past others previews the move
/// (the lifted row follows the pointer, the rest slide to open a gap) and, on drop, raises OnReorder
/// with the old/new index.</summary>
public class ReorderTests
{
    private const string Html =
        "<body><cupri-reorder>" +
          "<cupri-reorder-item>Alpha</cupri-reorder-item>" +
          "<cupri-reorder-item>Beta</cupri-reorder-item>" +
          "<cupri-reorder-item>Gamma</cupri-reorder-item>" +
          "<cupri-reorder-item>Delta</cupri-reorder-item>" +
        "</cupri-reorder></body>";

    private static List<RenderNode> Items(TestDoc t)
    {
        var outp = new List<RenderNode>();
        void W(RenderNode n) { if (n.Element?.ClassList.Contains("cupri-reorder-item") == true) outp.Add(n); foreach (var c in n.Children) W(c); }
        W(t.Root);
        return outp;
    }

    [Fact]
    public void Dragging_a_row_down_past_two_others_reorders_on_drop()
    {
        (int From, int To)? drop = null;
        using var t = new TestDoc(Html, "", null, width: 300, height: 500, components: true);
        t.Doc.OnReorder(e => drop = (e.From, e.To));

        var items = Items(t);
        var handle = t.Find(n => n.Element?.ClassList.Contains("cupri-reorder-handle") == true)!; // first item's grip
        var (hx, hy) = TestDoc.Center(handle);
        var targetY = TestDoc.Center(items[2]).Y + 8f;   // drag just past the third row's midpoint

        t.Doc.DispatchClick(hx, hy, 1);             // grab row 0
        Assert.True(items[0].Dragging, "grabbed row 0");

        t.Doc.DispatchPointerMove(hx, targetY);     // drag down over row 2
        Assert.True(items[0].DragOffsetY > 1f, $"lifted row follows the pointer: off={items[0].DragOffsetY}");
        Assert.True(items[1].DragTargetY < -1f, $"row 1 targets the gap: {items[1].DragTargetY}"); // slides up
        Assert.True(items[2].DragTargetY < -1f, $"row 2 targets the gap: {items[2].DragTargetY}");
        Assert.True(t.Doc.HasActiveTransitions, "the slide keeps the frame loop ticking");
        Assert.True(items[1].DragOffsetY > items[1].DragTargetY + 1f, "hasn't snapped — it eases");

        for (var tm = 0.0; tm <= 0.4; tm += 0.03) t.Doc.Animate(tm);   // let the slide ease in
        Assert.Equal(items[1].DragTargetY, items[1].DragOffsetY, 1.0);  // arrived
        Assert.False(t.Doc.HasActiveTransitions, "settled");

        t.Doc.DispatchPointerUp(hx, targetY);       // drop
        Assert.Equal((0, 2), drop);
    }

    [Fact]
    public void A_rebuild_mid_drag_abandons_the_drag_so_the_pointer_is_not_stuck()
    {
        // A drag holds references to render nodes; a rebuild (a background refresh, or a pointer-up
        // missed because the button was released off-window) replaces them. If the stale drag isn't
        // dropped, every later move keeps routing to the dead drag — the row looks stuck/stacked and
        // :hover stops updating — until some unrelated event clears it.
        using var t = new TestDoc(Html, ".cupri-reorder-item:hover { color:#ff0000; }", null,
            width: 300, height: 500, components: true);

        var handle = t.Find(n => n.Element?.ClassList.Contains("cupri-reorder-handle") == true)!;
        var (hx, hy) = TestDoc.Center(handle);
        t.Doc.DispatchClick(hx, hy, 1);                 // grab row 0
        t.Doc.DispatchPointerMove(hx, hy + 30f);        // …and start dragging it
        Assert.True(Items(t)[0].Dragging, "drag is active");

        t.Doc.Refresh();                                // a rebuild lands mid-drag
        t.Layout();

        // Moving the pointer now must update :hover, proving the drag was abandoned (not still eating moves).
        var second = Items(t)[1];
        var (bx, by) = TestDoc.Center(second);
        t.Doc.DispatchPointerMove(bx, by);
        t.Layout();

        var hovered = t.Find(n => n.Element?.ClassList.Contains("cupri-reorder-item") == true
                                  && n.Element.HasAttribute("data-hover"));
        Assert.NotNull(hovered);                        // hover works ⇒ not stuck in a phantom drag
        foreach (var it in Items(t)) Assert.Equal(0f, it.DragOffsetY);  // nothing left lifted/stacked
    }

    [Fact]
    public void A_grab_and_release_without_moving_is_not_a_reorder()
    {
        var fired = false;
        using var t = new TestDoc(Html, "", null, width: 300, height: 500, components: true);
        t.Doc.OnReorder(_ => fired = true);

        var handle = t.Find(n => n.Element?.ClassList.Contains("cupri-reorder-handle") == true)!;
        var (hx, hy) = TestDoc.Center(handle);
        t.Doc.DispatchClick(hx, hy, 1);
        t.Doc.DispatchPointerMove(hx, hy + 2);      // a tiny jitter — stays on the same slot
        t.Doc.DispatchPointerUp(hx, hy + 2);

        Assert.False(fired);
        Assert.False(Items(t)[0].Dragging);         // drag state cleared
    }
}
