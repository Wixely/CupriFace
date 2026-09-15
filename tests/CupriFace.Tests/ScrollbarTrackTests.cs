using CupriFace.Diagnostics;
using CupriFace.Dom;
using CupriFace.Interaction;
using SkiaSharp;
using Xunit;
using Xunit.Abstractions;

namespace CupriFace.Tests;

/// <summary>
/// The scrollbar as something you can actually hit: a track column rather than a five-pixel bar.
///
/// <para>It was five pixels wide, drawn by the painter and hit-tested by the document from two
/// separate copies of the same arithmetic — which had already drifted, the hit-test padding its
/// thumb by six pixels on one side and eight on the other to make the bar catchable at all. So the
/// region you could grab was nowhere near the region you could see, and the empty space above and
/// below the thumb did nothing at all.</para>
///
/// <para>Now there is a track. It is the hit surface, it shows itself when the pointer is in it, and
/// a press anywhere along it does something — drag on the thumb, page above or below it.</para>
/// </summary>
public class ScrollbarTrackTests(ITestOutputHelper output)
{
    private const string Css = """
        body { margin:0; background:#fff; font-family:sans-serif }
        .box { width:240px; height:160px; overflow:scroll; background:#fff }
        .pad { height:1200px; background:#eef }
        """;
    private const string Html = "<body><div class='box'><div class='pad'>content</div></div></body>";

    private static TestDoc Doc() => new(Html, Css, width: 300, height: 220);

    private static RenderNode Box(TestDoc t) => t.Find(n => n.IsScrollable)!;

    /// <summary>A point inside the track, at a given fraction of its height.</summary>
    private static (float X, float Y) TrackPoint(TestDoc t, float fraction)
    {
        var box = Box(t);
        var b = HitTesting.AbsoluteBox(box);
        // The track hugs the right-hand edge of the padding box; aim at its middle.
        var x = b.X + box.Width - box.BorderRightW - 6f;
        return (x, b.Y + box.ContentTopInset + box.ContentBoxHeight * fraction);
    }

    // ---- the column shows itself -----------------------------------------------------------------

    /// <summary>Nothing changes until the pointer is in the column. An always-visible track would be
    /// chrome on every scrollable box on the page, which is exactly what the thin bar avoided.</summary>
    [Fact]
    public void The_track_is_invisible_until_the_pointer_is_in_it()
    {
        using var a = Doc();
        using var before = a.Render(SKColors.White);

        a.Move(10, 10);                              // in the content, nowhere near the bar
        using var elsewhere = a.Render(SKColors.White);
        Assert.True(ImageDiff.Compare(before, elsewhere).IsIdentical);

        var (x, y) = TrackPoint(a, 0.5f);
        a.Move(x, y);
        using var hovered = a.Render(SKColors.White);

        var diff = ImageDiff.Compare(before, hovered);
        output.WriteLine(diff.ToString());
        Assert.False(diff.IsIdentical, "the track should appear under the pointer");
    }

    /// <summary>…and the thumb grows to fill the column while it is there. That growth IS the
    /// feedback: it says the column will take a press, before one is made.</summary>
    [Fact]
    public void The_thumb_grows_to_fill_the_track_on_hover()
    {
        using var t = Doc();
        var box = Box(t);
        var b = HitTesting.AbsoluteBox(box);

        var resting = Scrollbar.Thumb(box, b.X, b.Y, hot: false).W;

        var (x, y) = TrackPoint(t, 0.5f);
        t.Move(x, y);
        var hot = Scrollbar.Thumb(box, b.X, b.Y, hot: true).W;

        output.WriteLine($"thumb {resting} → {hot} in a {Scrollbar.TrackWidth} track");
        Assert.True(hot > resting, "the thumb should be fatter while the track is hovered");
        Assert.True(hot <= Scrollbar.TrackWidth, "…but never wider than the column it lives in");
        Assert.True(Box(t).ScrollbarHot, "the box should know its bar is hot");
    }

    /// <summary>Leaving the column puts it away again.</summary>
    [Fact]
    public void Leaving_the_track_hides_it_again()
    {
        using var t = Doc();
        var (x, y) = TrackPoint(t, 0.5f);
        t.Move(x, y);
        Assert.True(Box(t).ScrollbarHot);

        t.Move(10, 10);
        Assert.False(Box(t).ScrollbarHot);
    }

    // ---- a press anywhere in the column does something --------------------------------------------

    [Fact]
    public void Clicking_below_the_thumb_pages_down()
    {
        using var t = Doc();
        Assert.Equal(0f, Box(t).ScrollY);

        var (x, y) = TrackPoint(t, 0.9f);            // well below a thumb parked at the top
        t.Click(x, y);

        var after = Box(t).ScrollY;
        output.WriteLine($"paged to {after:0.0} of {Box(t).MaxScrollY:0.0}");
        Assert.True(after > 0, "a press below the thumb should page down");
        Assert.True(after <= Box(t).ContentBoxHeight, "…by about a page, not to the end");
    }

    [Fact]
    public void Clicking_above_the_thumb_pages_up()
    {
        using var t = Doc();
        var box = Box(t);
        box.ScrollY = box.MaxScrollY;                // start at the bottom, thumb parked low
        t.Layout();

        var (x, y) = TrackPoint(t, 0.05f);
        t.Click(x, y);

        var after = Box(t).ScrollY;
        output.WriteLine($"paged up to {after:0.0}");
        Assert.True(after < box.MaxScrollY, "a press above the thumb should page up");
    }

    /// <summary>A page keeps a little of what you were looking at, rather than replacing the view
    /// outright — there has to be something left to reattach the eye to.</summary>
    [Fact]
    public void A_page_is_a_little_less_than_the_visible_height()
    {
        using var t = Doc();
        var (x, y) = TrackPoint(t, 0.9f);
        t.Click(x, y);

        var box = Box(t);
        output.WriteLine($"page {box.ScrollY:0.0} of a {box.ContentBoxHeight:0.0} viewport");
        Assert.True(box.ScrollY < box.ContentBoxHeight);
        Assert.True(box.ScrollY > box.ContentBoxHeight * 0.5f);
    }

    /// <summary>A press in the track never reaches what is behind it. The bar sits over the content,
    /// and a button that happens to be under the column is not what someone aimed at.</summary>
    [Fact]
    public void A_press_in_the_track_does_not_reach_the_content_behind_it()
    {
        var clicks = 0;
        using var t = new TestDoc(
            "<body><div class='box'><div class='hit'>x</div><div class='pad'></div></div></body>",
            Css + ".hit{height:1200px;background:#dfd}", width: 300, height: 220);
        t.Doc.OnClick(".hit", _ => clicks++);

        var (x, y) = TrackPoint(t, 0.9f);
        t.Click(x, y);

        Assert.Equal(0, clicks);
        Assert.True(Box(t).ScrollY > 0, "…and it still paged");
    }

    // ---- and the thumb still drags ----------------------------------------------------------------

    /// <summary>The thing the whole column exists to make reachable.</summary>
    [Fact]
    public void Dragging_the_thumb_scrolls()
    {
        using var t = Doc();
        var (x, y) = TrackPoint(t, 0.02f);           // on the thumb, parked at the top

        t.Click(x, y);
        Assert.Equal(0f, Box(t).ScrollY, 0.5);       // a press on the thumb pages nothing

        t.Move(x, y + 60);
        var dragged = Box(t).ScrollY;
        t.Up(x, y + 60);

        output.WriteLine($"dragged to {dragged:0.0}");
        Assert.True(dragged > 0, "dragging the thumb down should scroll down");
    }

    /// <summary>Dragging keeps working when the pointer wanders out of the column, which is what
    /// hands do — the gesture is still about the bar it started on.</summary>
    [Fact]
    public void A_drag_survives_the_pointer_leaving_the_column()
    {
        using var t = Doc();
        var (x, y) = TrackPoint(t, 0.02f);
        t.Click(x, y);

        t.Move(20, y + 80);                          // far to the left, still dragging
        Assert.True(Box(t).ScrollY > 0);
        Assert.True(Box(t).ScrollbarHot, "the bar stays hot for the length of the gesture");

        t.Up(20, y + 80);
    }

    // ---- one source of truth ----------------------------------------------------------------------

    /// <summary>The painter and the hit-test ask <see cref="Scrollbar"/> the same question. Asserted
    /// because they used to answer it separately and had already drifted: a five-pixel bar with a
    /// nineteen-pixel grab region offset to one side of it.</summary>
    [Fact]
    public void The_thumb_is_where_it_is_drawn()
    {
        using var t = Doc();
        var box = Box(t);
        var b = HitTesting.AbsoluteBox(box);
        var th = Scrollbar.Thumb(box, b.X, b.Y, hot: false);

        // Its own middle is on it; a point a thumb's height below it is not.
        Assert.True(Scrollbar.OnThumb(box, b.X, b.Y, th.X + th.W / 2, th.Y + th.H / 2, hot: false));
        Assert.False(Scrollbar.OnThumb(box, b.X, b.Y, th.X + th.W / 2, th.Y + th.H + 20, hot: false));

        // And the track contains the thumb.
        var tr = Scrollbar.Track(box, b.X, b.Y);
        Assert.True(th.X >= tr.X && th.X + th.W <= tr.X + tr.W);
        Assert.True(th.Y >= tr.Y && th.Y + th.H <= tr.Y + tr.H + 0.5f);
    }
}
