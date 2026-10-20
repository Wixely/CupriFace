using Xunit;

namespace CupriFace.Tests;

/// <summary>
/// A follow-tail textarea (a log) across rebuilds that land before any layout.
///
/// <para>The scroll memory asks "was this scroller at the bottom?" on every rebuild. Asked of a box
/// no layout has reached, the live answer is meaningless: its MaxScrollY is still 0. The fix for a
/// scrolled-up reader being jumped to the end reads the REMEMBERED answer instead — but a box seen
/// for the first time has nothing remembered, and answering "no" there made every follow-tail log
/// open at its first line and then never follow. Bind itself rebuilds before the first layout, so
/// that was every app, not only one that refreshed twice.</para>
///
/// <para>So: remembered answer when there is one, the old live answer when there is not. These
/// tests hold both halves; the bottom-anchored virtual list has its own in
/// <see cref="VirtualVariableHeightTests"/>.</para>
/// </summary>
public class FollowTailColdStartTests
{
    private sealed class Log { public string Text { get; set; } = ""; }

    private static Log Lines(int n) => new() { Text = string.Join("\n", Enumerable.Range(0, n).Select(i => $"line {i}")) };

    private const string Html = "<body><cupri-textarea style='height:200px' follow-tail value='{{Text}}'></cupri-textarea></body>";

    private static Dom.RenderNode Scroller(TestDoc t) => t.FindClass("cupri-textarea");

    private static bool AtBottom(Dom.RenderNode n) =>
        n.MaxScrollY > 0 && Math.Clamp(n.ScrollY, 0, n.MaxScrollY) >= n.MaxScrollY - 1f;

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void A_follow_tail_log_opens_at_its_last_line(int refreshesBeforeFirstLayout)
    {
        using var t = new TestDoc(Html, "", Lines(200), width: 320, height: 400, components: true,
            beforeFirstLayout: d => { for (var i = 0; i < refreshesBeforeFirstLayout; i++) d.Refresh(); });

        var log = Scroller(t);
        Assert.True(AtBottom(log), $"opened at {log.ScrollY:F0} of {log.MaxScrollY:F0}");
    }

    [Fact]
    public void A_log_at_its_last_line_follows_new_lines_through_a_double_rebuild()
    {
        var model = Lines(200);
        using var t = new TestDoc(Html, "", model, width: 320, height: 400, components: true);
        t.Layout();

        model.Text += "\nfresh line";
        t.Doc.Refresh();
        t.Doc.Refresh();                                     // no layout in between
        t.Layout();

        var log = Scroller(t);
        Assert.True(AtBottom(log), $"left behind at {log.ScrollY:F0} of {log.MaxScrollY:F0}");
    }

    [Fact]
    public void A_reader_scrolled_up_a_log_stays_put_through_a_double_rebuild()
    {
        var model = Lines(200);
        using var t = new TestDoc(Html, "", model, width: 320, height: 400, components: true);
        t.Layout();
        var (x, y) = TestDoc.Center(Scroller(t));
        t.Doc.DispatchWheel(x, y, -600f);
        t.Layout();
        var before = Scroller(t).ScrollY;
        Assert.False(AtBottom(Scroller(t)), "the wheel did not scroll the log up");

        model.Text += "\nfresh line";
        t.Doc.Refresh();
        t.Doc.Refresh();                                     // no layout in between
        t.Layout();

        var log = Scroller(t);
        Assert.False(AtBottom(log), $"pulled back to the bottom: {log.ScrollY:F0} of {log.MaxScrollY:F0}");
        Assert.Equal(before, log.ScrollY, 1);
    }
}
