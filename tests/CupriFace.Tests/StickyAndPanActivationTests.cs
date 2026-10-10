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
