using CupriFace.Dom;
using CupriFace.Interaction;
using Xunit;
using Xunit.Abstractions;

namespace CupriFace.Tests;

/// <summary>
/// Moving a carousel with the things people actually have.
///
/// <para>It scrolled sideways and only sideways, and both ways of reaching that axis needed hardware
/// or a hand: a horizontal wheel, or a finger. On a desktop with an ordinary mouse there was no way
/// to move it at all — a plain wheel has no horizontal component, so the one axis the strip has was
/// unreachable, and pressing and dragging did nothing because a scroll box is not a drag surface.
/// The component read as broken while every part of it worked.</para>
///
/// <para>Two fixes. A wheel over a scroller that can ONLY move sideways moves it sideways, which is
/// what browsers do. And a scroller can opt into being pushed by hand with
/// <c>data-drag-scroll</c>, which the carousel sets on its viewport.</para>
/// </summary>
public class CarouselDragTests(ITestOutputHelper output)
{
    private const string Html = """
        <body><cupri-carousel peek="30" gap="12">
          <cupri-slide><div class='card'>one</div></cupri-slide>
          <cupri-slide>two</cupri-slide><cupri-slide>three</cupri-slide>
          <cupri-slide>four</cupri-slide><cupri-slide>five</cupri-slide>
        </cupri-carousel></body>
        """;
    private const string Css = "body{margin:0;font-family:sans-serif}cupri-carousel{width:300px}"
                             + ".cupri-carousel-slide{height:90px}.card{height:60px}";

    private static TestDoc Doc() => new(Html, Css, width: 360, height: 200, components: true);

    private static RenderNode Strip(TestDoc t)
        => t.Find(n => n.Element?.ClassList.Contains("cupri-carousel-viewport") == true)!;

    private static (float X, float Y) Middle(TestDoc t)
    {
        var b = HitTesting.AbsoluteBox(Strip(t));
        return (b.X + b.W / 2, b.Y + b.H / 2);
    }

    [Fact]
    public void The_strip_overflows_sideways_and_not_down()
    {
        using var t = Doc();
        var s = Strip(t);
        output.WriteLine($"{s.Width:0}x{s.Height:0} content={s.ScrollContentWidth:0} max={s.MaxScrollX:0}");
        Assert.True(s.IsScrollableX);
        Assert.False(s.IsScrollable);
    }

    // ---- the wheel ------------------------------------------------------------------------------

    /// <summary>The report, in one assertion: a plain mouse wheel moves it. It has no horizontal
    /// component, so before this the only axis the strip has could not be reached at all.</summary>
    [Fact]
    public void A_plain_vertical_wheel_moves_it_sideways()
    {
        using var t = Doc();
        var (x, y) = Middle(t);

        t.Doc.DispatchWheel(x, y, 120f);
        t.Layout();

        output.WriteLine($"scrollX={Strip(t).ScrollX:0.0}");
        Assert.True(Strip(t).ScrollX > 0);
    }

    [Fact]
    public void A_horizontal_wheel_still_moves_it()
    {
        using var t = Doc();
        var (x, y) = Middle(t);

        t.Doc.DispatchWheel(x, y, 0f, 120f);
        t.Layout();

        Assert.True(Strip(t).ScrollX > 0);
    }

    /// <summary>At its end the wheel chains outward, so a carousel at the last slide does not trap
    /// the page. Browsers do this and the vertical axis already did.</summary>
    [Fact]
    public void At_the_end_the_wheel_chains_to_the_page()
    {
        using var t = new TestDoc(
            "<body><div class='page'>" + Html[6..^7] + "<div class='tall'></div></div></body>",
            Css + ".page{height:150px;overflow:scroll}.tall{height:800px}",
            width: 360, height: 200, components: true);

        var strip = Strip(t);
        strip.ScrollX = strip.MaxScrollX;               // already at the far end
        t.Layout();

        var page = t.Find(n => n.Element?.ClassList.Contains("page") == true)!;
        var b = HitTesting.AbsoluteBox(Strip(t));
        t.Doc.DispatchWheel(b.X + b.W / 2, b.Y + b.H / 2, 120f);
        t.Layout();

        var after = t.Find(n => n.Element?.ClassList.Contains("page") == true)!.ScrollY;
        output.WriteLine($"page scrolled to {after:0.0}");
        Assert.True(after > 0, "the wheel should have chained out to the page");
    }

    // ---- the hand -------------------------------------------------------------------------------

    /// <summary>Press and drag pushes the strip, and it follows the hand: dragging left brings the
    /// later slides in. The other direction feels like pushing a rope.</summary>
    [Fact]
    public void Dragging_left_pushes_the_strip_along()
    {
        using var t = Doc();
        var (x, y) = Middle(t);

        t.Click(x, y);
        t.Move(x - 90, y);
        var during = Strip(t).ScrollX;
        t.Up(x - 90, y);

        output.WriteLine($"scrollX={during:0.0}");
        Assert.True(during > 50, $"dragging 90px left should move the strip, got {during:0}");
    }

    [Fact]
    public void Dragging_back_returns_it()
    {
        using var t = Doc();
        var (x, y) = Middle(t);

        t.Click(x, y);
        t.Move(x - 90, y);
        t.Move(x, y);                                   // back where it started
        t.Up(x, y);

        Assert.Equal(0f, Strip(t).ScrollX, 1);
    }

    /// <summary>A press that does NOT travel is still a click. Most presses on a carousel are
    /// someone clicking a card, so panning waits for real movement — otherwise every click on a
    /// slide would be swallowed by a gesture nobody made.</summary>
    [Fact]
    public void A_press_without_movement_still_clicks_the_card()
    {
        var clicks = 0;
        using var t = Doc();
        t.Doc.OnClick(".card", _ => clicks++);

        var card = t.Find(n => n.Element?.ClassList.Contains("card") == true)!;
        t.ClickNode(card);

        Assert.Equal(1, clicks);
        Assert.Equal(0f, Strip(t).ScrollX, 0.5);
    }

    /// <summary>…and a wobble inside the slop is not a pan either.</summary>
    [Fact]
    public void A_wobble_does_not_pan()
    {
        using var t = Doc();
        var (x, y) = Middle(t);

        t.Click(x, y);
        t.Move(x - 2, y + 1);
        t.Up(x - 2, y + 1);

        Assert.Equal(0f, Strip(t).ScrollX, 0.5);
    }

    /// <summary>An ordinary scroll box is NOT pannable — dragging across a page of text selects it,
    /// and turning every scroller into something a hand pushes would take that away everywhere.</summary>
    [Fact]
    public void A_plain_scroll_box_is_not_dragged_by_the_mouse()
    {
        using var t = new TestDoc(
            "<body><div class='box'><div class='pad'>text</div></div></body>",
            "body{margin:0}.box{width:200px;height:100px;overflow:scroll}.pad{width:900px;height:80px}",
            width: 300, height: 200);

        var box = t.Find(n => n.IsScrollableX)!;
        var b = HitTesting.AbsoluteBox(box);
        t.Click(b.X + 40, b.Y + 40);
        t.Move(b.X - 60, b.Y + 40);
        t.Up(b.X - 60, b.Y + 40);

        Assert.Equal(0f, t.Find(n => n.IsScrollableX)!.ScrollX, 0.5);
    }

    /// <summary>The finger path is untouched — it went through the gesture recogniser and always
    /// worked, and this must not have quietly replaced it.</summary>
    [Fact]
    public void A_finger_still_swipes_it()
    {
        using var t = Doc();
        var (x, y) = Middle(t);

        var touch = new TouchInput(t.Doc);
        touch.Down(x, y, 0.00);
        for (var i = 1; i <= 8; i++) touch.Move(x - 10 * i, y, i * 0.016);
        touch.Up(x - 80, y, 0.30);
        t.Layout();

        Assert.True(Strip(t).ScrollX > 0);
    }
}
