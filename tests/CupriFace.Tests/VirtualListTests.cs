using System.Collections.Generic;
using System.Linq;
using CupriFace.Dom;
using CupriFace.Interaction;
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

    [Fact]
    public void Dragging_the_scrollbar_keeps_scrolling_past_the_first_rewindow()
    {
        // Regression: dragging the thumb re-windows the list, which rebuilds the tree and dropped the
        // scroll-drag reference — so the drag froze after the first frame. It must now track the pointer
        // across the rebuilds (the scroller is re-linked by its data-virtual-key).
        using var t = new TestDoc(Html, "", new Model(), width: 320, height: 300, components: true);
        var box = HitTesting.AbsoluteBox(t.FindClass("cupri-virtual"));
        float gx = box.X + box.W - 3, gy = box.Y + 8;    // the thumb sits top-right at ScrollY 0

        t.Click(gx, gy);                                 // grab the thumb
        t.Move(gx, gy + 12);                             // first drag → crosses the re-window threshold (rebuild)
        var afterFirst = t.FindClass("cupri-virtual").ScrollY;
        Assert.True(afterFirst > 100, "the first drag frame scrolled");

        t.Move(gx, gy + 44);                             // keep dragging — must not be frozen at afterFirst
        var afterMore = t.FindClass("cupri-virtual").ScrollY;
        t.Up(gx, gy + 44);

        Assert.True(afterMore > afterFirst * 2.5f, $"drag should keep tracking (was {afterFirst}, now {afterMore})");
        Assert.Contains("Row", string.Join(" ", RowTexts(t)));   // list still windowed/valid after the drag
    }

    // ---- height="auto": the list takes its height from LAYOUT (#152) ----------------------------

    /// <summary>A chat client's main surface IS the virtual list, so it has to fill what the chrome
    /// leaves. The height could only come from the attribute, which the component wrote as an INLINE
    /// style — beating every stylesheet, @media rule and flex rule there is.</summary>
    private const string FlexCss = """
        body { margin:0; font-family:sans-serif }
        .app { display:flex; flex-direction:column; height:100% }
        .head { height:60px }
        .list { flex:1; min-height:0 }
        .vrow { height:40px }
        """;

    private static string FlexHtml(string heightAttr) =>
        "<body><div class='app'><div class='head'></div>"
        + $"<cupri-virtual class='list' height='{heightAttr}' item-height='40'>"
        + "<div class='vrow' data-repeat='Items'>{{.}}</div>"
        + "</cupri-virtual></div></body>";

    /// <summary>
    /// The failure, stated as the thing a user sees. Layout gives the list 940px; with a numeric
    /// height the binder still materialises 300px of rows, so once it is scrolled the bottom of the
    /// viewport has NOTHING in it — the list paints tall and mostly empty, and looks perfectly fine
    /// until someone scrolls. With height="auto" the window is measured from the box layout gave it.
    /// </summary>
    [Fact]
    public void A_list_sized_by_layout_windows_off_the_box_it_was_given()
    {
        Assert.True(BlankStripWhenScrolled("300") > 100, "the numeric case should leave a hole — if it does not, this test proves nothing");
        Assert.Equal(0, BlankStripWhenScrolled("auto"));

        static float BlankStripWhenScrolled(string heightAttr)
        {
            using var t = new TestDoc(FlexHtml(heightAttr), FlexCss, new Model(), width: 400, height: 1000, components: true);
            var list = t.FindClass("cupri-virtual");
            var (vx, vy) = TestDoc.Center(list);
            for (var i = 0; i < 40; i++) { t.Doc.DispatchWheel(vx, vy, 500f); t.Layout(); }

            list = t.FindClass("cupri-virtual");
            var lastRowBottom = 0f;
            foreach (var r in list.Children)
                if (r.Element?.ClassList.Contains("vrow") == true)
                    lastRowBottom = MathF.Max(lastRowBottom, r.Y - list.ContentTopInset + r.Height);
            return MathF.Max(0, (float)list.ScrollY + list.ContentBoxHeight - lastRowBottom);
        }
    }

    /// <summary>The mechanism, separately from its effect: auto writes NO inline height, so a
    /// stylesheet can have the box. Anything else keeps the inline height it has always had — and an
    /// inline style beats every rule in every stylesheet, which is the whole problem.</summary>
    [Theory]
    [InlineData("auto", false)]
    [InlineData("", false)]         // omitted means auto too: layout decides unless a number says otherwise
    [InlineData("300", true)]
    public void Only_a_number_takes_the_height_away_from_the_cascade(string heightAttr, bool inlineHeight)
    {
        var attr = heightAttr.Length > 0 ? $" height='{heightAttr}'" : "";
        using var t = new TestDoc(
            $"<body><cupri-virtual{attr} item-height='40'><div class='vrow' data-repeat='Items'>{{{{.}}}}</div></cupri-virtual></body>",
            ".vrow{height:40px}", new Model(), width: 400, height: 1000, components: true);

        var style = t.FindClass("cupri-virtual").Element!.GetAttribute("style") ?? "";
        Assert.Equal(inlineHeight, style.Contains("height:", StringComparison.Ordinal));
        Assert.Contains("overflow:scroll", style);
    }

    /// <summary>In a flex parent the box comes out the same size either way — flex:1 stretches it
    /// past the inline height. That is precisely why the bug was invisible: the list PAINTS the
    /// right size, and only the windowing is wrong.</summary>
    [Fact]
    public void The_box_looks_right_either_way_which_is_why_this_was_missed()
    {
        using var fixedHeight = new TestDoc(FlexHtml("300"), FlexCss, new Model(), width: 400, height: 1000, components: true);
        using var auto = new TestDoc(FlexHtml("auto"), FlexCss, new Model(), width: 400, height: 1000, components: true);

        Assert.Equal(940f, fixedHeight.FindClass("cupri-virtual").Height, 1);   // 1000 less a 60px header
        Assert.Equal(940f, auto.FindClass("cupri-virtual").Height, 1);
    }

    /// <summary>Nothing has been measured before the first layout, so the first frame windows off the
    /// default and the second corrects it — the same way a never-measured row uses the item-height
    /// estimate. What must not happen is an empty list, or one that never recovers.</summary>
    [Fact]
    public void The_first_frame_has_no_measurement_yet_and_still_builds_rows()
    {
        using var t = new TestDoc(FlexHtml("auto"), FlexCss, new Model(), width: 400, height: 1000, components: true);
        Assert.NotEmpty(RowTexts(t));

        // Measure, bind, lay out: the correction lands on the frame after the measurement, the same
        // way a re-measured row pitch does.
        for (var i = 0; i < 3; i++) { t.Doc.Refresh(); t.Layout(); }
        var rows = RowTexts(t).Count;
        Assert.True(rows >= 940 / 40, $"once measured, the window should cover the 940px box; got {rows} rows");
    }

    /// <summary>
    /// The upgrade this must not break. A list with no height attribute keeps the 300px it has
    /// always had — but from the component's STYLESHEET now, not an inline style, so an app can
    /// override it. Leaving it genuinely `auto` was measured and rejected: a scroller with no
    /// constraint grows to its whole content, and a bare 2,000-row list came out 80,000px tall with
    /// nothing to scroll, which is worse than the arbitrary number it replaced.
    /// </summary>
    [Fact]
    public void A_list_with_no_height_still_gets_300px_and_still_scrolls()
    {
        using var t = new TestDoc(
            "<body><cupri-virtual item-height='40'><div class='vrow' data-repeat='Items'>{{.}}</div></cupri-virtual></body>",
            "body{margin:0} .vrow{height:40px}", new Model(), width: 400, height: 600, components: true);

        var list = t.FindClass("cupri-virtual");
        Assert.Equal(300f, list.Height, 1);
        Assert.True(list.MaxScrollY > 39000, "it must still be a scroller, not a box as tall as its content");
        Assert.InRange(RowTexts(t).Count, 8, 30);          // a screenful of 300px, not one row and not 1000
    }

    /// <summary>…and an app stylesheet now wins, which is the whole point: the same default used to
    /// be an inline style, which nothing could override.</summary>
    [Fact]
    public void An_app_stylesheet_overrides_the_default_height()
    {
        using var t = new TestDoc(
            "<body><cupri-virtual class='tall' item-height='40'><div class='vrow' data-repeat='Items'>{{.}}</div></cupri-virtual></body>",
            "body{margin:0} .vrow{height:40px} .tall{height:520px}", new Model(), width: 400, height: 600, components: true);

        Assert.Equal(520f, t.FindClass("cupri-virtual").Height, 1);
    }

    /// <summary>The first layout has measured nothing yet, and the capture pass reads the viewport
    /// height to bound what a list may window. Recording that height AFTER the capture left it at
    /// zero on the very first frame, so the list measured itself as 1px and the next frame built one
    /// row where it needed eight — a flash of a nearly empty list, once, at startup.</summary>
    [Fact]
    public void The_window_does_not_dip_on_the_frame_after_the_first()
    {
        using var t = new TestDoc(
            "<body><cupri-virtual item-height='40'><div class='vrow' data-repeat='Items'>{{.}}</div></cupri-virtual></body>",
            "body{margin:0} .vrow{height:40px}", new Model(), width: 400, height: 600, components: true);

        var counts = new List<int>();
        for (var i = 0; i < 4; i++) { counts.Add(RowTexts(t).Count); t.Doc.Refresh(); t.Layout(); }
        Assert.All(counts, c => Assert.True(c >= 300 / 40, $"a frame built {c} rows for a 300px box: {string.Join(",", counts)}"));
    }
}
