using CupriFace.Interaction;
using Xunit;

namespace CupriFace.Tests;

/// <summary>
/// The rubber band. Reported from a device: dragging past the end of a list gave no feedback at
/// all, so there was no way to tell "you have arrived" from "the app has stopped responding".
/// </summary>
public class OverscrollTests
{
    private const string Html = "<body><div class='page'><div class='tall'>t</div></div></body>";
    private const string Css = "body{margin:0} .page{width:200px;height:200px;overflow:scroll} .tall{height:800px}";

    private static CupriDocument Scrollable()
    {
        var doc = CupriDocument.Load(Html, Css);
        doc.BuildFrame(300, 300);
        return doc;
    }

    private static Dom.RenderNode Page(CupriDocument doc) =>
        TestDoc.Find(doc.Root, n => n.Element?.ClassList.Contains("page") == true)!;

    [Fact]
    public void Dragging_past_the_top_stretches_instead_of_doing_nothing()
    {
        using var doc = Scrollable();
        var touch = new TouchInput(doc);

        touch.Down(100, 40, 0);
        for (var i = 1; i <= 10; i++) touch.Move(100, 40 + i * 12, i * 0.01);   // pull DOWN at the top

        var page = Page(doc);
        Assert.Equal(0f, page.ScrollY, 1);                       // still at the top…
        Assert.True(page.OverscrollY < -5, $"nothing stretched ({page.OverscrollY:F1})");
        Assert.True(doc.OverscrollActive);
    }

    [Fact]
    public void The_band_resists_and_is_bounded()
    {
        using var doc = Scrollable();
        var touch = new TouchInput(doc);

        touch.Down(100, 40, 0);
        for (var i = 1; i <= 60; i++) touch.Move(100, 40 + i * 30, i * 0.01);   // pull very hard

        var page = Page(doc);
        Assert.True(Math.Abs(page.OverscrollY) <= 90.5f,
            $"the band stretched to {page.OverscrollY:F0}px — it should be bounded");
        Assert.True(Math.Abs(page.OverscrollY) > 30, "…but it should still visibly give");
    }

    [Fact]
    public void Letting_go_springs_it_back()
    {
        using var doc = Scrollable();
        var touch = new TouchInput(doc);

        touch.Down(100, 40, 0);
        for (var i = 1; i <= 10; i++) touch.Move(100, 40 + i * 12, i * 0.01);
        Assert.True(Math.Abs(Page(doc).OverscrollY) > 5);

        touch.Up(100, 160, 0.11);
        for (var f = 1; f <= 60 && doc.OverscrollActive; f++) doc.Animate(0.11 + f * 0.016);

        Assert.Equal(0f, Page(doc).OverscrollY, 1);
        Assert.False(doc.OverscrollActive);
    }

    [Fact]
    public void A_scroller_with_room_left_just_scrolls()
    {
        // The band is for edges only: mid-list, a drag must move the content and stretch nothing.
        using var doc = Scrollable();
        var touch = new TouchInput(doc);

        touch.Down(100, 150, 0);
        for (var i = 1; i <= 6; i++) touch.Move(100, 150 - i * 12, i * 0.01);

        var page = Page(doc);
        Assert.True(page.ScrollY > 30);
        Assert.Equal(0f, page.OverscrollY, 1);
    }

    [Fact]
    public void What_is_stretched_is_still_where_it_can_be_tapped()
    {
        // Paint and hit-testing read the same effective offset, so a row pushed down by the band
        // can be tapped where it now appears — not where it would have been.
        using var doc = Scrollable();
        var touch = new TouchInput(doc);
        touch.Down(100, 40, 0);
        for (var i = 1; i <= 10; i++) touch.Move(100, 40 + i * 12, i * 0.01);

        var page = Page(doc);
        var shift = page.OverscrollY;                       // negative: content pushed down
        Assert.True(shift < -5);

        var tall = TestDoc.Find(doc.Root, n => n.Element?.ClassList.Contains("tall") == true)!;
        var (_, y, _, _) = HitTesting.ScreenBox(tall);
        Assert.Equal(-shift, y, 1);                         // it really did move on screen
    }
}
