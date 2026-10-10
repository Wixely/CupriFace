using CupriFace;
using CupriFace.Interaction;
using Xunit;

namespace CupriFace.Tests;

/// <summary>
/// Issue #302. Two halves of the same complaint: a stuck element has to be CLICKABLE where it is
/// painted, and a press on a drag-to-pan scroller must be able to turn into a pan instead of a click.
/// </summary>
public class StickyAndPanActivationTests
{
    private const string StickyCss = """
        .page   { width:380px; height:240px; overflow:scroll; }
        .spacer { height:120px; }
        .strip  { position:sticky; top:0; height:80px; }
        .below  { height:700px; }
        """;

    private const string StickyHtml = """
        <body><div class='page'>
          <div class='spacer'></div>
          <div class='strip' role='button' aria-label='Strip'></div>
          <div class='below' role='button' aria-label='Below'></div>
        </div></body>
        """;

    [Fact]
    public void A_stuck_element_is_hit_where_it_is_painted()
    {
        using var t = new TestDoc(StickyHtml, StickyCss, components: true);
        t.Layout();
        t.Doc.DispatchWheel(50, 50, 340);   // well past the point where it sticks
        t.Layout();

        // Pinned to the scrollport top, so the top 80px of the port is the strip.
        Assert.Equal("Strip", HitTesting.HitTest(t.Doc.Root, 60, 20)?.Element?.GetAttribute("aria-label"));
        Assert.Equal("Strip", HitTesting.HitTest(t.Doc.Root, 60, 70)?.Element?.GetAttribute("aria-label"));
        Assert.Equal("Below", HitTesting.HitTest(t.Doc.Root, 60, 100)?.Element?.GetAttribute("aria-label"));
    }

    [Fact]
    public void A_stuck_element_reports_the_box_and_activation_point_it_is_painted_at()
    {
        using var t = new TestDoc(StickyHtml, StickyCss, components: true);
        t.Layout();
        t.Doc.DispatchWheel(50, 50, 340);
        t.Layout();
        var strip = t.Find(n => n.Element?.GetAttribute("aria-label") == "Strip")!;

        // ScreenBox and ActivationPoint drive synthesised clicks and the accessibility tree, so
        // they have to agree with paint too — not just HitTest.
        var box = HitTesting.ScreenBox(strip);
        Assert.Equal(0f, box.Y, 1);
        var (ax, ay) = HitTesting.ActivationPoint(strip);
        Assert.Equal("Strip", HitTesting.HitTest(t.Doc.Root, ax, ay)?.Element?.GetAttribute("aria-label"));
    }

    [Fact]
    public void Sticky_clamps_against_the_scrolled_content_not_the_visible_box()
    {
        // The containing block here IS the scroll container. Clamping against its border-box height
        // dragged the node off the top of the scrollport once the scroll passed one port's worth
        // of content — it stopped being painted at all.
        using var t = new TestDoc(StickyHtml, StickyCss, components: true);
        t.Layout();
        var strip = t.Find(n => n.Element?.GetAttribute("aria-label") == "Strip")!;

        foreach (var scroll in new[] { 100f, 200f, 340f, 500f })
        {
            using var d = new TestDoc(StickyHtml, StickyCss, components: true);
            d.Layout();
            d.Doc.DispatchWheel(50, 50, scroll);
            d.Layout();
            var s = d.Find(n => n.Element?.GetAttribute("aria-label") == "Strip")!;
            var y = HitTesting.ScreenBox(s).Y;
            // Never above the port, never below its natural place.
            Assert.True(y >= -0.5f, $"scroll {scroll}: stuck above the scrollport at y={y}");
            Assert.True(y <= 120f + 0.5f, $"scroll {scroll}: below its natural place at y={y}");
        }
        Assert.NotNull(strip);
    }

    private const string BottomCss = """
        .page   { width:380px; height:240px; overflow:scroll; }
        .above  { height:400px; }
        .foot   { position:sticky; bottom:0; height:60px; }
        .after  { height:400px; }
        """;

    private const string BottomHtml = """
        <body><div class='page'>
          <div class='above' role='button' aria-label='Above'></div>
          <div class='foot' role='button' aria-label='Foot'></div>
          <div class='after' role='button' aria-label='After'></div>
        </div></body>
        """;

    [Fact]
    public void A_bottom_sticky_element_pins_to_the_scrollport_bottom()
    {
        using var t = new TestDoc(BottomHtml, BottomCss, components: true);
        t.Layout();                      // unscrolled: the footer's natural place is far below
        var foot = t.Find(n => n.Element?.GetAttribute("aria-label") == "Foot")!;

        // Scrollport is 240 tall, so a bottom:0 footer of 60 holds at y=180..240.
        var box = HitTesting.ScreenBox(foot);
        Assert.Equal(180f, box.Y, 1);
        Assert.Equal("Foot", HitTesting.HitTest(t.Doc.Root, 60, 200)?.Element?.GetAttribute("aria-label"));
        Assert.Equal("Above", HitTesting.HitTest(t.Doc.Root, 60, 100)?.Element?.GetAttribute("aria-label"));
    }

    [Fact]
    public void A_bottom_sticky_element_stops_pinning_once_its_natural_place_arrives()
    {
        using var t = new TestDoc(BottomHtml, BottomCss, components: true);
        t.Layout();
        t.Doc.DispatchWheel(50, 50, 400);   // scroll until the footer's own place reaches the port
        t.Layout();
        var foot = t.Find(n => n.Element?.GetAttribute("aria-label") == "Foot")!;

        // Natural y is 400; scrolled by 400 it sits at 0, which is above the pin line — so it is
        // no longer stuck and must not be dragged back down to the bottom edge.
        Assert.Equal(0f, HitTesting.ScreenBox(foot).Y, 1);
        Assert.Equal("Foot", HitTesting.HitTest(t.Doc.Root, 60, 30)?.Element?.GetAttribute("aria-label"));
    }

    [Fact]
    public void A_bottom_sticky_element_never_rides_above_its_containing_block()
    {
        using var t = new TestDoc(BottomHtml, BottomCss, components: true);
        t.Layout();
        var foot = t.Find(n => n.Element?.GetAttribute("aria-label") == "Foot")!;

        foreach (var requested in new[] { 0f, 100f, 400f, 700f, 900f })
        {
            using var d = new TestDoc(BottomHtml, BottomCss, components: true);
            d.Layout();
            if (requested > 0) d.Doc.DispatchWheel(50, 50, requested);
            d.Layout();
            var page = d.FindClass("page");
            var f = d.Find(n => n.Element?.GetAttribute("aria-label") == "Foot")!;

            // It is pinned 60px above the port's bottom edge (240 - 60 = 180) for exactly as long
            // as its own place is still below that line, and scrolls away normally afterwards. It
            // only ever moves UP from where it would otherwise be — never down.
            var natural = 400f - page.ScrollY;
            Assert.Equal(MathF.Min(natural, 180f), HitTesting.ScreenBox(f).Y, 1);
        }
        Assert.NotNull(foot);
    }

    [Fact]
    public void Sticky_with_no_inset_on_an_axis_behaves_as_relative_on_that_axis()
    {
        // CSS: an axis whose two insets are both auto is not sticky at all. This engine used to
        // invent top:0 there, so a box nobody asked to pin pinned anyway.
        using var t = new TestDoc(
            StickyHtml.Replace("class='strip'", "class='strip noinset'"),
            StickyCss + " .noinset { top:auto; }", components: true);
        t.Layout();
        t.Doc.DispatchWheel(50, 50, 340);
        t.Layout();
        var strip = t.Find(n => n.Element?.GetAttribute("aria-label") == "Strip")!;

        // Natural y is 120; scrolled 340 it is simply gone, like any other block.
        Assert.Equal(120f - 340f, HitTesting.ScreenBox(strip).Y, 1);
    }

    [Fact]
    public void A_vertically_sticky_element_is_not_pinned_sideways()
    {
        // Each axis is decided on its own: `top` alone must leave the horizontal axis free.
        const string css = """
            .row  { width:200px; height:100px; overflow:scroll; display:flex; }
            .head { position:sticky; top:0; width:120px; height:40px; flex:none; }
            .wide { width:600px; height:300px; flex:none; }
            """;
        using var t = new TestDoc(
            "<body><div class='row'><div class='head' role='button' aria-label='Head'></div>" +
            "<div class='wide'></div></div></body>", css, components: true);
        t.Layout();
        t.Doc.DispatchWheel(50, 50, 0f, 150f);   // scroll sideways
        t.Layout();
        var head = t.Find(n => n.Element?.GetAttribute("aria-label") == "Head")!;

        Assert.Equal(-150f, HitTesting.ScreenBox(head).X, 1);   // travelled with the content
    }

    private const string HorizCss = """
        .row   { width:300px; height:100px; overflow:scroll; display:flex; }
        .first { position:sticky; left:0; width:80px; height:60px; flex:none; }
        .rest  { width:600px; height:60px; flex:none; }
        """;

    private const string HorizHtml = """
        <body><div class='row'>
          <div class='first' role='button' aria-label='First'></div>
          <div class='rest' role='button' aria-label='Rest'></div>
        </div></body>
        """;

    [Fact]
    public void A_left_sticky_element_pins_to_the_scrollport_left_edge()
    {
        using var t = new TestDoc(HorizHtml, HorizCss, components: true);
        t.Layout();
        t.Doc.DispatchWheel(50, 50, 0f, 200f);    // scroll right
        t.Layout();
        var first = t.Find(n => n.Element?.GetAttribute("aria-label") == "First")!;

        Assert.Equal(0f, HitTesting.ScreenBox(first).X, 1);     // pinned, not scrolled away
        Assert.Equal("First", HitTesting.HitTest(t.Doc.Root, 40, 30)?.Element?.GetAttribute("aria-label"));
        Assert.Equal("Rest", HitTesting.HitTest(t.Doc.Root, 200, 30)?.Element?.GetAttribute("aria-label"));
    }

    [Fact]
    public void A_left_sticky_element_rides_out_with_its_containing_block()
    {
        using var t = new TestDoc(HorizHtml, HorizCss, components: true);
        t.Layout();
        var first = t.Find(n => n.Element?.GetAttribute("aria-label") == "First")!;

        foreach (var requested in new[] { 0f, 50f, 200f, 400f })
        {
            using var d = new TestDoc(HorizHtml, HorizCss, components: true);
            d.Layout();
            if (requested > 0) d.Doc.DispatchWheel(50, 50, 0f, requested);
            d.Layout();
            var row = d.FindClass("row");
            var f = d.Find(n => n.Element?.GetAttribute("aria-label") == "First")!;
            // Pinned at the left edge for as long as its own place is off to the left of it.
            Assert.Equal(MathF.Max(0f - row.ScrollX, 0f), HitTesting.ScreenBox(f).X, 1);
        }
        Assert.NotNull(first);
    }

    [Fact]
    public void A_right_sticky_element_pins_to_the_scrollport_right_edge()
    {
        const string css = """
            .row  { width:300px; height:100px; overflow:scroll; display:flex; }
            .wide { width:600px; height:60px; flex:none; }
            .last { position:sticky; right:0; width:80px; height:60px; flex:none; }
            """;
        using var t = new TestDoc(
            "<body><div class='row'><div class='wide'></div>" +
            "<div class='last' role='button' aria-label='Last'></div></div></body>", css, components: true);
        t.Layout();
        var last = t.Find(n => n.Element?.GetAttribute("aria-label") == "Last")!;

        // Unscrolled, its own place (x=600) is far beyond the port, so it holds at 300-80=220.
        Assert.Equal(220f, HitTesting.ScreenBox(last).X, 1);
        Assert.Equal("Last", HitTesting.HitTest(t.Doc.Root, 260, 30)?.Element?.GetAttribute("aria-label"));
    }

    [Fact]
    public void CF0053_reports_sticky_that_pins_to_nothing()
    {
        var report = CupriFace.Diagnostics.CupriDoctor.Check(
            "<body><div class='page'><div class='ghost'>x</div><div class='tall'>y</div></div></body>",
            ".page{width:200px;height:100px;overflow:scroll} .ghost{position:sticky;height:20px} .tall{height:400px}");

        var f = Assert.Single(report.Findings, x => x.Code == "CF0053");
        Assert.Contains("ghost", f.Message);
        Assert.Contains("pins to nothing", f.Message);
    }

    [Theory]
    [InlineData("top:0")]
    [InlineData("bottom:0")]
    [InlineData("left:0")]
    [InlineData("right:0")]
    public void CF0053_stays_quiet_once_an_inset_is_given(string inset)
    {
        var report = CupriFace.Diagnostics.CupriDoctor.Check(
            "<body><div class='page'><div class='ghost'>x</div><div class='tall'>y</div></div></body>",
            ".page{width:200px;height:100px;overflow:scroll} " +
            $".ghost{{position:sticky;height:20px;{inset}}} .tall{{height:400px}}");

        Assert.DoesNotContain(report.Findings, x => x.Code == "CF0053");
    }

    private const string PanHtml = """
        <body><div class='strip' data-drag-scroll>
          <cupri-checkbox checked="{{A}}"></cupri-checkbox>
        </div></body>
        """;
    private const string PanCss = """
        .strip { display:flex; width:120px; height:90px; overflow:scroll; }
        cupri-checkbox { flex:none; margin-right:300px; }
        """;

    private sealed class Flag { public bool A { get; set; } }

    [Fact]
    public void By_default_a_press_inside_a_pannable_scroller_still_activates_on_the_press()
    {
        var m = new Flag();
        using var t = new TestDoc(PanHtml, PanCss, m, components: true);
        var (x, y) = TestDoc.Center(t.FindRole("checkbox"));

        t.Click(x, y);
        Assert.True(m.A);   // unchanged behaviour: the click has already happened
    }

    [Fact]
    public void OnRelease_turns_a_press_that_becomes_a_pan_into_a_pan_and_not_a_click()
    {
        var m = new Flag();
        using var t = new TestDoc(PanHtml, PanCss, m, components: true);
        t.Doc.MouseActivation = PointerActivation.OnRelease;
        var (x, y) = TestDoc.Center(t.FindRole("checkbox"));

        t.Click(x, y);
        Assert.False(m.A);                 // nothing fires yet — this is only the press
        t.Move(x - 120, y);                // travel past the pan slop
        t.Up(x - 120, y);
        Assert.False(m.A);                 // it was a pan, so it was never a click

        var strip = t.Find(n => n.Element?.HasAttribute("data-drag-scroll") == true)!;
        Assert.True(strip.ScrollX > 1f, "the strip should have panned");
    }

    [Fact]
    public void OnRelease_still_activates_a_press_and_release_that_did_not_drag()
    {
        var m = new Flag();
        using var t = new TestDoc(PanHtml, PanCss, m, components: true);
        t.Doc.MouseActivation = PointerActivation.OnRelease;
        var (x, y) = TestDoc.Center(t.FindRole("checkbox"));

        t.Click(x, y);
        Assert.False(m.A);
        t.Up(x, y);
        Assert.True(m.A);   // down + up over the same control is the confirmed click
    }
}
