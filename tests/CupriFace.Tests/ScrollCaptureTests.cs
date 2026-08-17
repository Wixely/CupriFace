using CupriFace.Interaction;
using Xunit;

namespace CupriFace.Tests;

/// <summary>
/// Which scroller a drag belongs to is decided ONCE, when the finger lands.
///
/// Reported from a device: "it seems like I can initiate scrolling in them when I do not tap inside
/// them (tapping far above them to drag the main page)". The cause was that every move re-dispatched
/// a wheel at the fixed down point. That is right for a mouse, whose pointer really is over what it
/// re-resolves to — but a finger holds ONE point while the content slides underneath it. Drag the
/// page from above an inner list and the list eventually arrives under the finger, at which point it
/// silently stole the rest of the gesture.
/// </summary>
public class ScrollCaptureTests
{
    // A long page with an inner scroller partway down it. The finger starts ABOVE the inner box,
    // and the page scrolls far enough that the inner box passes under the finger mid-drag.
    private const string Html =
        "<body><div class='page'>" +
        "<div class='spacer'>s</div>" +
        "<div class='inner'><div class='tall'>i</div></div>" +
        "<div class='spacer'>s</div>" +
        "</div></body>";
    private const string Css =
        "body{margin:0} .page{width:200px;height:200px;overflow:scroll} " +
        ".spacer{height:400px} .inner{height:150px;overflow:scroll} .tall{height:900px}";

    private static CupriDocument Nested()
    {
        var doc = CupriDocument.Load(Html, Css);
        doc.BuildFrame(300, 300);
        return doc;
    }

    // Looked up on demand, never cached across a gesture: the finger-down restyles for :active,
    // which rebuilds the tree and leaves any node reference held from before it dangling.
    private static Dom.RenderNode N(CupriDocument doc, string cls) =>
        TestDoc.Find(doc.Root, n => n.Element?.ClassList.Contains(cls) == true)!;

    [Fact]
    public void A_drag_begun_on_the_page_never_transfers_to_an_inner_scroller()
    {
        using var doc = Nested();
        var touch = new TouchInput(doc);

        // Start near the top, well above the inner box (which begins 400px down the content).
        touch.Down(100, 30, 0);
        for (var i = 1; i <= 40; i++) touch.Move(100, 30 - i * 12, i * 0.01);   // drag up: page scrolls down

        Assert.True(N(doc, "page").ScrollY > 300, $"the page barely moved ({N(doc, "page").ScrollY:F0}px)");
        Assert.Equal(0f, N(doc, "inner").ScrollY, 1);
    }

    [Fact]
    public void A_drag_begun_inside_the_inner_scroller_still_moves_it()
    {
        // The converse: capturing the target must not break the ordinary case.
        using var doc = Nested();

        // Put the inner box under the finger first, by scrolling the page there with a wheel.
        doc.DispatchWheel(100, 100, 420);
        var pageAt = N(doc, "page").ScrollY;

        var touch = new TouchInput(doc);
        touch.Down(100, 60, 1.0);
        for (var i = 1; i <= 20; i++) touch.Move(100, 60 - i * 10, 1.0 + i * 0.01);

        Assert.True(N(doc, "inner").ScrollY > 50,
            $"the inner list did not scroll ({N(doc, "inner").ScrollY:F0}px)");
        Assert.Equal(pageAt, N(doc, "page").ScrollY, 1);   // …and it did not drag the page with it
    }

    [Fact]
    public void A_mouse_wheel_still_resolves_under_the_pointer_every_time()
    {
        // The capture is a TOUCH fix. A wheel has a live pointer, so re-resolving per event is
        // correct for it — moving the mouse to a different scroller must scroll that one.
        using var doc = Nested();

        doc.DispatchWheel(100, 100, 420);           // bring the inner box under the pointer
        var pageAt = N(doc, "page").ScrollY;
        doc.DispatchWheel(100, 60, 60);             // now wheel over the inner box

        Assert.True(N(doc, "inner").ScrollY > 5, "the wheel should act on whatever is under the pointer");
        Assert.Equal(pageAt, N(doc, "page").ScrollY, 1);
    }

    [Fact]
    public void A_finger_that_catches_a_fling_judges_its_axis_from_where_it_landed()
    {
        // A fling-catch enters scrolling without passing through Down's origin assignment. When the
        // origin was left over from the previous gesture, the axis lock read the stale offset,
        // cleared the slop instantly and committed to the wrong axis — a sideways drag after a
        // fling moved nothing at all.
        using var doc = CupriDocument.Load(
            "<body><div class='strip'><div class='wide'>w</div></div></body>",
            "body{margin:0} .strip{width:200px;height:80px;overflow:scroll} .wide{width:900px;height:40px}");
        doc.BuildFrame(300, 300);
        var touch = new TouchInput(doc);

        // A sideways fling, well away from where the next gesture will land.
        touch.Down(180, 20, 0);
        for (var i = 1; i <= 6; i++) touch.Move(180 - i * 20, 20, i * 0.01);
        touch.Up(60, 20, 0.07);
        Assert.True(doc.FlingActive, "no fling to catch — the test proves nothing");

        // Catch it far below, then drag sideways.
        touch.Down(100, 70, 0.2);
        for (var i = 1; i <= 10; i++) touch.Move(100 - i * 10, 70, 0.2 + i * 0.01);

        Assert.True(N(doc, "strip").ScrollX > 20,
            $"the sideways drag after a catch moved {N(doc, "strip").ScrollX:F0}px");
    }
}
