using CupriFace.Dom;
using CupriFace.Interaction;
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
