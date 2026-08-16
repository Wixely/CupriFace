using CupriFace.Dom;
using CupriFace.Interaction;
using Xunit;

namespace CupriFace.Tests;

/// <summary>
/// Scrolling sideways. The engine had exactly one axis: a box whose content was too wide simply
/// overflowed, and a phone had no way to reach the rest of it — which is why the Showcase's table
/// and kanban had to be shrunk to fit rather than scrolled. <c>ScrollX</c> existed only as a
/// single-line text field's caret-follow shift, and that behaviour must survive untouched.
/// </summary>
public class HorizontalScrollTests
{
    // A 300px-wide box holding 900px of cards: 600px of horizontal overflow.
    // A 300px window onto a 900px track. Absolute placement, deliberately: flex is a separate
    // question (and has its own bug here — min-width stops the shrink but the pen still advances by
    // the shrunk width), and this test is about the scrolling axis, not about how the row was built.
    private const string Css = """
        body { margin:0 }
        .strip { width:300px; height:80px; overflow:scroll; }
        .track { width:900px; height:60px; position:relative; }
        .card  { position:absolute; top:0; width:150px; height:60px; }
        """;
    private const string Html = """
        <body><div class='strip'><div class='track'>
          <div class='card' id='c0' style='left:0'>0</div>
          <div class='card' id='c3' style='left:450px'>3</div>
          <div class='card' id='c5' style='left:750px'>5</div>
        </div></div></body>
        """;

    private static RenderNode Strip(CupriDocument doc) =>
        TestDoc.Find(doc.Root, n => n.Element?.ClassList.Contains("strip") == true)!;

    [Fact]
    public void A_box_wider_than_its_content_box_reports_horizontal_overflow()
    {
        using var doc = CupriDocument.Load(Html, Css);
        doc.BuildFrame(400, 200);

        var strip = Strip(doc);
        Assert.True(strip.IsScrollableX, "a 900px track in a 300px box is horizontal overflow");
        Assert.Equal(600f, strip.MaxScrollX, 1);
        Assert.False(strip.IsScrollable, "…and it does NOT overflow vertically");
    }

    [Fact]
    public void A_horizontal_wheel_moves_it_and_stops_at_the_edges()
    {
        using var doc = CupriDocument.Load(Html, Css);
        doc.BuildFrame(400, 200);

        Assert.True(doc.DispatchWheel(150, 40, 0, 200));
        Assert.Equal(200f, Strip(doc).ScrollX, 1);

        Assert.True(doc.DispatchWheel(150, 40, 0, 10_000));      // clamps at the far edge
        Assert.Equal(600f, Strip(doc).ScrollX, 1);
        Assert.False(doc.DispatchWheel(150, 40, 0, 200));        // nothing left to give
    }

    [Fact]
    public void A_finger_drag_scrolls_sideways_and_a_flick_coasts()
    {
        using var doc = CupriDocument.Load(Html, Css);
        doc.BuildFrame(400, 200);
        var touch = new TouchInput(doc);

        // Drag left across the strip: content follows the finger.
        touch.Down(250, 40, 0);
        for (var i = 1; i <= 10; i++) touch.Move(250 - i * 15, 40, i * 0.01);
        Assert.True(Strip(doc).ScrollX > 100, $"drag moved it only {Strip(doc).ScrollX:F0}px");

        var atRelease = Strip(doc).ScrollX;
        touch.Up(100, 40, 0.10);                                  // ~1500 px/s
        Assert.True(doc.FlingActive, "a fast sideways flick should coast");

        for (var f = 1; f <= 60 && doc.FlingActive; f++) doc.Animate(0.10 + f * 0.016);
        Assert.True(Strip(doc).ScrollX > atRelease, "momentum carried it no further");
    }

    [Fact]
    public void What_is_scrolled_into_view_is_where_it_can_be_tapped()
    {
        // The whole point: hit-testing and the accessibility rectangle have to follow the offset,
        // or a card dragged into view is visible and untouchable.
        using var doc = CupriDocument.Load(Html, Css);
        doc.BuildFrame(400, 200);
        doc.DispatchWheel(150, 40, 0, 450);                        // card 3 now sits at the left edge

        var hit = HitTesting.HitTest(doc.Root, 20, 40);
        Assert.Equal("c3", hit?.Element?.Id);

        var card = TestDoc.Find(doc.Root, n => n.Element?.Id == "c5")!;
        var (x, _, _, _) = HitTesting.ScreenBox(card);
        Assert.Equal(750 - 450, x, 1);                            // laid out at 750, scrolled by 450
    }

    // (A single-line field's caret-follow ScrollX is covered by the existing text-field tests —
    // they pass unchanged, which is the regression check that matters here.)
}
