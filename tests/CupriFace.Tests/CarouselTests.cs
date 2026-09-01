using CupriFace.Dom;
using CupriFace.Interaction;
using Xunit;
using Xunit.Abstractions;

namespace CupriFace.Tests;

/// <summary>
/// <c>cupri-carousel</c>: a horizontal strip that scrolls sideways.
///
/// It is a scroll container rather than a widget with its own gesture code, so the finger drag, the
/// horizontal wheel, the fling and the overscroll rubber-band come from the engine's second scrolling
/// axis. The tests therefore check that it really IS one — that the track overflows its viewport, and
/// that a real drag moves it — rather than checking that the markup came out the expected shape.
/// </summary>
public class CarouselTests(ITestOutputHelper output)
{
    private const string Html = """
        <body style="margin:0">
          <cupri-carousel label="Featured" slide-width="200" gap="10" height="120" style="width:420px">
            <cupri-slide>One</cupri-slide>
            <cupri-slide>Two</cupri-slide>
            <cupri-slide>Three</cupri-slide>
            <cupri-slide>Four</cupri-slide>
          </cupri-carousel>
        </body>
        """;

    private static RenderNode Viewport(TestDoc t) =>
        TestDoc.Find(t.Doc.Root, n => n.Element?.ClassList.Contains("cupri-carousel-viewport") == true)!;

    private static List<RenderNode> Slides(TestDoc t)
    {
        var list = new List<RenderNode>();
        void Walk(RenderNode n)
        {
            if (n.Element?.ClassList.Contains("cupri-carousel-slide") == true) list.Add(n);
            foreach (var c in n.Children) Walk(c);
        }
        Walk(t.Doc.Root);
        return list;
    }

    [Fact]
    public void The_track_overflows_its_viewport_so_there_is_something_to_scroll()
    {
        using var t = new TestDoc(Html, "", width: 500, height: 300, components: true);
        var vp = Viewport(t);

        output.WriteLine($"viewport w={vp.Width} maxScrollX={vp.MaxScrollX}");
        Assert.True(vp.IsScrollableX, "four 200px slides in a 420px viewport must overflow sideways");
        Assert.False(vp.IsScrollable, "…and must not overflow vertically");
    }

    /// <summary>The claim that matters: a finger really drags it. This is the case the Showcase's CSS
    /// once said was impossible, on the strength of a comment that outlived the limitation.</summary>
    [Fact]
    public void A_finger_drag_scrolls_it_sideways()
    {
        using var t = new TestDoc(Html, "", width: 500, height: 300, components: true);
        var vp = Viewport(t);
        var touch = new TouchInput(t.Doc);

        var y = vp.Y + vp.Height / 2;
        touch.Down(vp.X + 300, y, 0.0);
        touch.Move(vp.X + 120, y, 0.05);      // pull left: the strip should follow
        touch.Move(vp.X + 60, y, 0.10);
        touch.Up(vp.X + 60, y, 0.12);
        t.Layout();

        var moved = Viewport(t).ScrollX;
        output.WriteLine($"after a 240px leftward drag: scrollX={moved}");
        Assert.True(moved > 50, $"the strip should have scrolled with the finger, got {moved}");
    }

    [Fact]
    public void A_horizontal_wheel_scrolls_it_and_stops_at_the_end()
    {
        using var t = new TestDoc(Html, "", width: 500, height: 300, components: true);
        var vp = Viewport(t);
        var (cx, cy) = (vp.X + vp.Width / 2, vp.Y + vp.Height / 2);

        // A POSITIVE horizontal delta scrolls right — the convention HorizontalScrollTests pins.
        t.Doc.DispatchWheel(cx, cy, 0, 120);
        t.Layout();
        Assert.Equal(120f, Viewport(t).ScrollX, 1);

        t.Doc.DispatchWheel(cx, cy, 0, 10_000);
        t.Layout();
        var end = Viewport(t);
        Assert.Equal(end.MaxScrollX, end.ScrollX, 1);      // clamped at the far end, not past it
    }

    /// <summary>Each slide announces its place, so a screen reader is not handed four unlabelled
    /// groups.</summary>
    [Fact]
    public void Every_slide_says_where_it_is_in_the_run()
    {
        using var t = new TestDoc(Html, "", width: 500, height: 300, components: true);
        var slides = Slides(t);

        Assert.Equal(4, slides.Count);
        Assert.Equal("1 of 4", slides[0].Element!.GetAttribute("aria-label"));
        Assert.Equal("4 of 4", slides[3].Element!.GetAttribute("aria-label"));

        var root = TestDoc.Find(t.Doc.Root, n => n.Element?.LocalName == "cupri-carousel")!;
        Assert.Equal("carousel", root.Element!.GetAttribute("aria-roledescription"));
        Assert.Equal("Featured", root.Element!.GetAttribute("aria-label"));
    }

    /// <summary><c>peek</c> sizes slides against the CONTAINER rather than a fixed number, so a sliver
    /// of the next one shows at any width — which is what tells a reader there is more to the side.</summary>
    [Fact]
    public void Peek_leaves_the_next_slide_showing()
    {
        using var t = new TestDoc(
            "<body style='margin:0'><cupri-carousel peek='40' gap='10' height='100' style='width:400px'>" +
            "<cupri-slide>A</cupri-slide><cupri-slide>B</cupri-slide><cupri-slide>C</cupri-slide>" +
            "</cupri-carousel></body>", "", width: 500, height: 300, components: true);

        var vp = Viewport(t);
        var first = Slides(t)[0];
        output.WriteLine($"viewport w={vp.Width}, slide w={first.Width}");

        Assert.True(first.Width < vp.ContentBoxWidth - 30,
            $"a peeking slide must be narrower than the viewport; slide {first.Width} vs {vp.ContentBoxWidth}");
        Assert.True(vp.IsScrollableX);
    }
}
