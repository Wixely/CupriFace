using System;
using System.Linq;
using CupriFace.Dom;
using CupriFace.Interaction;
using CupriFace.Paint;
using Xunit;

namespace CupriFace.Tests;

/// <summary>
/// <c>&lt;cupri-board&gt;</c> kanban: dragging a card's grip into a different column moves it there. On drop
/// OnReorder carries the source list (<c>List</c>/<c>From</c>) and the target list (<c>ToList</c>/<c>To</c>),
/// which differ across a column move and match for a within-column reorder.
/// </summary>
public class KanbanTests
{
    private const string Html =
        "<body><cupri-board>" +
          "<cupri-reorder data-col='todo' style='width:150px'>" +
            "<cupri-reorder-item>A</cupri-reorder-item>" +
            "<cupri-reorder-item>B</cupri-reorder-item>" +
          "</cupri-reorder>" +
          "<cupri-reorder data-col='done' style='width:150px'>" +
            "<cupri-reorder-item>X</cupri-reorder-item>" +
          "</cupri-reorder>" +
        "</cupri-board></body>";

    private static (string From, int F, string To, int T)? Drag(TestDoc t, int handleIndex, System.Func<TestDoc, (float, float)> target)
    {
        (string, int, string, int)? drop = null;
        t.Doc.OnReorder(e => drop = (e.List.GetAttribute("data-col")!, e.From, e.ToList.GetAttribute("data-col")!, e.To));

        var handles = new System.Collections.Generic.List<RenderNode>();
        void W(RenderNode n) { if (n.Element?.ClassList.Contains("cupri-reorder-handle") == true) handles.Add(n); foreach (var c in n.Children) W(c); }
        W(t.Root);
        var (hx, hy) = TestDoc.Center(handles[handleIndex]);
        t.Doc.DispatchClick(hx, hy, 1);                        // grab

        var (tx, ty) = target(t);
        t.Doc.DispatchPointerMove(tx, ty);
        for (var tm = 0.0; tm <= 0.3; tm += 0.03) t.Doc.Animate(tm); // let the gap ease in
        t.Doc.DispatchPointerUp(tx, ty);                       // drop
        return drop;
    }

    private static RenderNode Col(TestDoc t, string col) => t.Find(n => n.Element?.GetAttribute("data-col") == col)!;

    [Fact]
    public void Dragging_a_card_into_another_column_moves_it_there()
    {
        using var t = new TestDoc(Html, "", null, width: 500, height: 300, components: true);
        var drop = Drag(t, handleIndex: 0, target: tt =>          // grab card A (todo[0]) → drop at the top of "done"
        {
            var b = HitTesting.AbsoluteBox(Col(tt, "done"));
            return (b.X + b.W / 2f, b.Y + 8f);
        });

        Assert.NotNull(drop);
        Assert.Equal("todo", drop!.Value.From);
        Assert.Equal(0, drop.Value.F);
        Assert.Equal("done", drop.Value.To);                     // landed in a different column
        Assert.Equal(0, drop.Value.T);                           // at the top slot
    }

    // Index of the FillRect that paints a card's background (at its dragged/eased position).
    private static int CardFillIndex(System.Collections.Generic.List<PaintCommand> cmds, RenderNode card)
    {
        var b = HitTesting.AbsoluteBox(card);
        float px = b.X + card.DragOffsetX, py = b.Y + card.DragOffsetY;
        for (var i = 0; i < cmds.Count; i++)
            if (cmds[i] is FillRect f && Math.Abs(f.X - px) < 1.5f && Math.Abs(f.Y - py) < 1.5f && Math.Abs(f.W - card.Width) < 1.5f)
                return i;
        return -1;
    }

    [Fact]
    public void The_lifted_card_paints_on_top_of_a_later_column()
    {
        using var t = new TestDoc(Html, "", null, width: 500, height: 300, components: true);
        var handle = t.Find(n => n.Element?.ClassList.Contains("cupri-reorder-handle") == true)!; // "todo" card A's grip
        var (hx, hy) = TestDoc.Center(handle);
        t.Doc.DispatchClick(hx, hy, 1);                       // grab A
        var done = HitTesting.AbsoluteBox(Col(t, "done"));
        t.Doc.DispatchPointerMove(done.X + done.W / 2f, done.Y + 8f); // drag it over the "done" column
        t.Layout();

        var cmds = t.Doc.BuildFrame(500, 300).Commands.ToList();
        var dragged = t.Find(n => n.Dragging)!;
        var doneCard = t.Find(n => n.Element?.ClassList.Contains("cupri-reorder-item") == true
                                   && n.Element.TextContent.Contains("X"))!; // the card already in "done"

        var di = CardFillIndex(cmds, dragged);
        var oi = CardFillIndex(cmds, doneCard);
        Assert.True(di >= 0 && oi >= 0, $"found both cards' fills (dragged={di}, done={oi})");
        Assert.True(di > oi, $"the lifted card (paint #{di}) must paint after the done column's card (#{oi}) — on top");
    }

    [Fact]
    public void Dragging_within_a_column_keeps_the_same_source_and_target_list()
    {
        using var t = new TestDoc(Html, "", null, width: 500, height: 300, components: true);
        var drop = Drag(t, handleIndex: 0, target: tt =>          // grab A → drag below B, still in "todo"
        {
            var b = HitTesting.AbsoluteBox(Col(tt, "todo"));
            return (b.X + b.W / 2f, b.Y + b.H - 6f);
        });

        Assert.NotNull(drop);
        Assert.Equal("todo", drop!.Value.From);
        Assert.Equal("todo", drop.Value.To);                     // same column
        Assert.Equal(1, drop.Value.T);                           // moved past B
    }
}
