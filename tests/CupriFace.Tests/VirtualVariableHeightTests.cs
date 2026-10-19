using System.Collections.Generic;
using System.Linq;
using CupriFace.Dom;
using CupriFace.Interaction;
using Xunit;

namespace CupriFace.Tests;

/// <summary>
/// Variable-height virtualisation (issue #67): <c>item-height</c> is an estimate, each materialised
/// row's real pitch is measured back into a per-list cache, and the scroll offset is anchored so
/// measurement never makes the visible content jump. <c>anchor="bottom"</c> is the chat-log mode —
/// open at the bottom, follow appends while there, release on scroll-up — and
/// <c>VirtualListInserted</c> is the prepend hook ("load older history"). Rows here carry EXPLICIT
/// bound heights so every expectation is exact rather than font-dependent.
/// </summary>
public class VirtualVariableHeightTests
{
    private sealed class Msg { public string Text { get; set; } = ""; public int H { get; set; } }
    private sealed class ChatModel { public List<Msg> Messages { get; set; } = new(); }

    // Heights cycle 20/40/80 (average 46.67 against the 40px estimate — deliberately wrong, so the
    // estimate-vs-measured machinery is always exercised).
    private static ChatModel Cycle(int n) => new()
    {
        Messages = Enumerable.Range(0, n)
            .Select(i => new Msg { Text = $"m{i}", H = (i % 3) switch { 0 => 20, 1 => 40, _ => 80 } }).ToList(),
    };

    private static ChatModel Fixed(int n, int h) => new()
    {
        Messages = Enumerable.Range(0, n).Select(i => new Msg { Text = $"m{i}", H = h }).ToList(),
    };

    private static string Html(string extra = "") =>
        $"<body><cupri-virtual height='300' item-height='40'{extra}>" +
        "<div class='m' data-repeat='Messages' style='height:{{H}}px'>{{Text}}</div></cupri-virtual></body>";

    private static List<RenderNode> Rows(TestDoc t)
    {
        var outp = new List<RenderNode>();
        void W(RenderNode n) { if (n.Element?.ClassList.Contains("m") == true) outp.Add(n); foreach (var c in n.Children) W(c); }
        W(t.Root);
        return outp;
    }

    private static RenderNode? Row(TestDoc t, string text) =>
        Rows(t).FirstOrDefault(r => r.Element!.TextContent.Trim() == text);

    private static float ScreenY(RenderNode n) => HitTesting.ScreenBox(n).Y;

    [Fact]
    public void Mixed_height_rows_window_and_stack_contiguously()
    {
        using var t = new TestDoc(Html(), "", Cycle(300), width: 320, height: 400, components: true);

        var rows = Rows(t);
        Assert.InRange(rows.Count, 6, 40);                    // a windowful, not 300
        Assert.Equal("m0", rows[0].Element!.TextContent.Trim());
        for (var i = 0; i < rows.Count; i++)                  // each row at its OWN height…
            Assert.Equal((i % 3) switch { 0 => 20, 1 => 40, _ => 80 }, rows[i].Height, 1);
        for (var i = 1; i < rows.Count; i++)                  // …stacked with no gap and no overlap
            Assert.Equal(rows[i - 1].Y + rows[i - 1].Height, rows[i].Y, 1);

        // The extent spans all 300 rows: unmeasured ones at the 40px estimate, the window measured.
        Assert.InRange(t.FindClass("cupri-virtual").MaxScrollY, 10_000, 14_500);
    }

    [Fact]
    public void Measuring_rows_above_the_viewport_does_not_jump_the_content()
    {
        using var t = new TestDoc(Html(), "", Cycle(300), width: 320, height: 400, components: true);
        var (vx, vy) = TestDoc.Center(t.FindClass("cupri-virtual"));

        // Two big hops leave never-measured bands between the stops…
        t.Doc.DispatchWheel(vx, vy, 1500f); t.Layout();
        t.Doc.DispatchWheel(vx, vy, 1500f); t.Layout();

        // …then a modest scroll UP re-windows into one of those bands: its rows materialise above
        // the viewport with real pitches ≠ the estimate the spacer had assumed. Unanchored, the
        // reference row would shift by that error on top of the 200px scroll; anchored, it moves by
        // exactly the wheel distance.
        var reference = Rows(t).First(r => ScreenY(r) > vy - 60 && ScreenY(r) < vy + 60);
        var text = reference.Element!.TextContent.Trim();
        var y0 = ScreenY(reference);

        t.Doc.DispatchWheel(vx, vy, -200f); t.Layout();

        var after = Row(t, text);
        Assert.NotNull(after);
        Assert.Equal(y0 + 200f, ScreenY(after!), 1);          // moved by the wheel, nothing else
    }

    [Fact]
    public void Anchor_bottom_opens_at_the_bottom()
    {
        using var t = new TestDoc(Html(" anchor='bottom'"), "", Cycle(400), width: 320, height: 400, components: true);

        var list = t.FindClass("cupri-virtual");
        Assert.Equal(list.MaxScrollY, System.Math.Clamp(list.ScrollY, 0, list.MaxScrollY), 1);

        var lastRow = Row(t, "m399");
        Assert.NotNull(lastRow);                              // the tail is materialised…
        var box = HitTesting.ScreenBox(lastRow!);
        var listBox = HitTesting.ScreenBox(list);
        Assert.Equal(listBox.Y + listBox.H, box.Y + box.H, 2); // …and flush with the list's bottom
    }

    [Fact]
    public void Appends_follow_while_at_the_bottom_and_release_when_scrolled_up()
    {
        var model = Cycle(400);
        using var t = new TestDoc(Html(" anchor='bottom'"), "", model, width: 320, height: 400, components: true);

        // At the bottom: an appended message scrolls into view by itself.
        model.Messages.Add(new Msg { Text = "fresh", H = 60 });
        t.Doc.Refresh(); t.Layout();
        var fresh = Row(t, "fresh");
        Assert.NotNull(fresh);
        var list = t.FindClass("cupri-virtual");
        Assert.Equal(list.MaxScrollY, System.Math.Clamp(list.ScrollY, 0, list.MaxScrollY), 1);

        // Scroll up: the pin releases — the next append must NOT yank the view back down.
        var (vx, vy) = TestDoc.Center(list);
        t.Doc.DispatchWheel(vx, vy, -400f); t.Layout();
        var reference = Rows(t).First(r => ScreenY(r) > vy - 60 && ScreenY(r) < vy + 60);
        var text = reference.Element!.TextContent.Trim();
        var y0 = ScreenY(reference);

        model.Messages.Add(new Msg { Text = "later", H = 60 });
        t.Doc.Refresh(); t.Layout();

        var still = Row(t, text);
        Assert.NotNull(still);
        Assert.Equal(y0, ScreenY(still!), 1);                 // reading position undisturbed
        list = t.FindClass("cupri-virtual");
        Assert.True(list.ScrollY < list.MaxScrollY - 50, "the released pin must not re-engage on append");
    }

    /// <summary>
    /// Two rebuilds before a layout - what a host does whenever its own refresh cadence lands in the
    /// frame an app already rebuilt in, which on a render-on-demand phone is often. The second
    /// rebuild captured the first's tree before anything had laid it out, read its stale
    /// MaxScrollY of 0 as "at the bottom", and the follow-tail restore put a reader who had scrolled
    /// up straight back at the end of the log.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_second_rebuild_before_layout_leaves_a_scrolled_up_reader_where_they_were(bool prepend)
    {
        var model = Cycle(400);
        using var t = new TestDoc(Html(" anchor='bottom'"), "", model, width: 320, height: 400, components: true);
        var list = t.FindClass("cupri-virtual");
        var (vx, vy) = TestDoc.Center(list);
        t.Doc.DispatchWheel(vx, vy, -1200f); t.Layout();
        var reference = Rows(t).First(r => ScreenY(r) > vy - 60 && ScreenY(r) < vy + 60);
        var text = reference.Element!.TextContent.Trim();
        var y0 = ScreenY(reference);

        if (prepend)
        {
            model.Messages.InsertRange(0, Enumerable.Range(0, 50).Select(i => new Msg { Text = $"old{i}", H = 40 }));
            t.Doc.VirtualListInserted("Messages", 0, 50);
        }
        else
        {
            model.Messages.Add(new Msg { Text = "later", H = 60 });
        }

        t.Doc.Refresh();
        t.Doc.Refresh();                                       // no layout in between
        t.Layout();

        list = t.FindClass("cupri-virtual");
        Assert.True(list.ScrollY < list.MaxScrollY - 50, $"pulled back to the bottom: {list.ScrollY:F0} of {list.MaxScrollY:F0}");
        var still = Row(t, text);
        Assert.NotNull(still);
        Assert.Equal(y0, ScreenY(still!), 1);                  // reading position undisturbed
    }

    /// <summary>The other half: a reader who WAS at the bottom still follows the tail through the
    /// same double rebuild, so the fix cannot be "never trust an unlaid tree's bottom".</summary>
    [Fact]
    public void A_second_rebuild_before_layout_still_follows_a_reader_at_the_bottom()
    {
        var model = Cycle(400);
        using var t = new TestDoc(Html(" anchor='bottom'"), "", model, width: 320, height: 400, components: true);

        model.Messages.Add(new Msg { Text = "fresh", H = 60 });
        t.Doc.Refresh();
        t.Doc.Refresh();
        t.Layout();

        var list = t.FindClass("cupri-virtual");
        Assert.Equal(list.MaxScrollY, System.Math.Clamp(list.ScrollY, 0, list.MaxScrollY), 1);
        Assert.NotNull(Row(t, "fresh"));
    }

    [Fact]
    public void Prepended_history_keeps_the_visible_content_stationary()
    {
        // Fixed 30px rows with a 30px estimate: the prepend compensation is exact, so the assertion
        // can be too.
        var model = Fixed(100, 30);
        using var t = new TestDoc(
            "<body><cupri-virtual height='300' item-height='30'>" +
            "<div class='m' data-repeat='Messages' style='height:{{H}}px'>{{Text}}</div></cupri-virtual></body>",
            "", model, width: 320, height: 400, components: true);
        var (vx, vy) = TestDoc.Center(t.FindClass("cupri-virtual"));
        t.Doc.DispatchWheel(vx, vy, 600f); t.Layout();        // mid-list

        var reference = Rows(t).First(r => ScreenY(r) > vy - 40 && ScreenY(r) < vy + 40);
        var text = reference.Element!.TextContent.Trim();
        var y0 = ScreenY(reference);
        var extent0 = t.FindClass("cupri-virtual").MaxScrollY;

        model.Messages.InsertRange(0, Enumerable.Range(0, 10).Select(i => new Msg { Text = $"old{i}", H = 30 }));
        t.Doc.VirtualListInserted("Messages", 0, 10);
        t.Doc.Refresh(); t.Layout();

        var still = Row(t, text);
        Assert.NotNull(still);
        Assert.Equal(y0, ScreenY(still!), 1);                 // the screen did not move…
        Assert.Equal(extent0 + 300f, t.FindClass("cupri-virtual").MaxScrollY, 1); // …but 10×30px of history exists above
    }

    [Fact]
    public void Fixed_height_lists_behave_exactly_as_before()
    {
        // The legacy contract: estimate == measured, so every correction is zero and the window
        // math degenerates to the old division. Same shape as VirtualListTests, kept here as the
        // explicit regression pin for the variable-height rework.
        var model = Fixed(1000, 40);
        using var t = new TestDoc(Html(), "", model, width: 320, height: 400, components: true);

        var rows = Rows(t);
        Assert.Equal("m0", rows[0].Element!.TextContent.Trim());
        Assert.InRange(rows.Count, 6, 40);
        Assert.Equal(40f * 1000 - 300, t.FindClass("cupri-virtual").MaxScrollY, 1);

        var (vx, vy) = TestDoc.Center(t.FindClass("cupri-virtual"));
        t.Doc.DispatchWheel(vx, vy, 400f); t.Layout();
        var texts = Rows(t).Select(r => r.Element!.TextContent.Trim()).ToList();
        Assert.DoesNotContain("m0", texts);
        Assert.Contains("m10", texts);
    }

    [Fact]
    public void A_width_change_remeasures_without_breaking_the_window()
    {
        // Wrap-determined heights invalidate on resize. With explicit heights the pitches do not
        // actually change, so this is the smoke half: a resize mid-scroll must neither crash nor
        // tear the window (rows stay contiguous).
        using var t = new TestDoc(Html(), "", Cycle(200), width: 320, height: 400, components: true);
        var (vx, vy) = TestDoc.Center(t.FindClass("cupri-virtual"));
        t.Doc.DispatchWheel(vx, vy, 800f); t.Layout();

        using (t.Doc.RenderToImage(260, 400)) { }             // narrower viewport → width invalidation
        using (t.Doc.RenderToImage(260, 400)) { }

        var rows = Rows(t);
        Assert.True(rows.Count > 4);
        for (var i = 1; i < rows.Count; i++)
            Assert.Equal(rows[i - 1].Y + rows[i - 1].Height, rows[i].Y, 1);
    }
}
