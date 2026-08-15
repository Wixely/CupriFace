using CupriFace.Interaction;
using Xunit;

namespace CupriFace.Tests;

/// <summary>
/// Momentum scrolling, stepped with a scripted Animate clock — deterministic physics, no timers.
/// The fling lives in the DOCUMENT (path-keyed, integrated in Animate) so every host's existing
/// wake and drive gates see it with zero host changes; these tests prove that contract too.
/// </summary>
public class FlingTests
{
    private const string Css = ".box{height:100px;overflow:auto;} .pad{height:2000px;}";
    private const string Html = """
        <body><div class="box"><div class="pad">content</div></div></body>
        """;

    [Fact]
    public void A_fast_release_keeps_scrolling_and_decays_to_a_stop()
    {
        using var t = new TestDoc(Html, Css);
        var touch = new TouchInput(t.Doc);

        // Drag 60px over 60ms (1000 px/s) and release.
        touch.Down(200, 80, 0.00);
        touch.Move(200, 60, 0.02);
        touch.Move(200, 40, 0.04);
        touch.Up(200, 20, 0.06);

        Assert.True(t.Doc.FlingActive, "release above the threshold starts a fling");
        Assert.True(t.Doc.HasActiveAnimations, "the wake gate sees the fling");
        Assert.True(t.Doc.HasActiveTransitions, "the drive gate sees the fling");

        var scroller = t.Find(n => n.IsScrollable)!;
        var atRelease = scroller.ScrollY;
        Assert.True(atRelease > 0, "the drag itself scrolled");

        // Step the clock the way a host does. The fling must ADD distance, then die out.
        var last = atRelease;
        var grew = false;
        for (var i = 1; i <= 240 && t.Doc.FlingActive; i++)
        {
            t.Doc.Animate(0.06 + i * (1 / 60.0));
            var now = t.Find(n => n.IsScrollable)!.ScrollY;
            Assert.True(now >= last - 0.01f, "a downward fling never scrolls back up");
            if (now > last) grew = true;
            last = now;
        }

        Assert.True(grew, "momentum added travel beyond the finger's");
        Assert.False(t.Doc.FlingActive, "the fling decayed to a stop");
        Assert.True(last < 2000, "it stopped before the far end — decay, not teleport");
    }

    [Fact]
    public void A_fling_stops_dead_at_the_edge_without_chaining()
    {
        const string css = ".outer{height:150px;overflow:auto;} .box{height:100px;overflow:auto;} " +
                           ".pad{height:160px;} .opad{height:1500px;}";
        const string html = """
            <body><div class="outer">
              <div class="box"><div class="pad">inner</div></div>
              <div class="opad">outer content</div>
            </div></body>
            """;
        using var t = new TestDoc(html, css);
        var touch = new TouchInput(t.Doc);

        // Fling the INNER scroller hard: its max travel is tiny (160-100=60px), the velocity huge.
        touch.Down(200, 80, 0.00);
        touch.Move(200, 50, 0.02);
        touch.Up(200, 20, 0.04);
        Assert.True(t.Doc.FlingActive);

        for (var i = 1; i <= 120 && t.Doc.FlingActive; i++)
            t.Doc.Animate(0.04 + i * (1 / 60.0));

        var inner = t.Find(n => n.IsScrollable && n.MaxScrollY < 100)!;
        var outer = t.Find(n => n.IsScrollable && n.MaxScrollY > 100)!;
        Assert.Equal(inner.MaxScrollY, inner.ScrollY, 1);   // pinned at its edge
        Assert.Equal(0, outer.ScrollY, 1);                  // momentum did NOT chain to the ancestor
        Assert.False(t.Doc.FlingActive);
    }

    [Fact]
    public void A_finger_landing_mid_fling_catches_the_list_without_clicking()
    {
        var m = new CatchModel();
        const string css = ".box{height:100px;overflow:auto;} .pad{height:2000px;}";
        const string html = """
            <body><div class="box">
              <div class="pad"><cupri-switch checked="{{On}}">X</cupri-switch></div>
            </div></body>
            """;
        using var t = new TestDoc(html, css, m, components: true);
        var touch = new TouchInput(t.Doc);

        touch.Down(200, 80, 0.00);
        touch.Move(200, 40, 0.03);
        touch.Up(200, 10, 0.06);
        Assert.True(t.Doc.FlingActive);
        t.Doc.Animate(0.08);                                // a couple of frames in flight
        t.Doc.Animate(0.10);

        touch.Down(20, 20, 0.12);                           // the catch — lands on the switch
        Assert.False(t.Doc.FlingActive, "the catch stopped the momentum");
        touch.Up(20, 20, 0.16);
        Assert.False(m.On, "a catch-tap never clicks what it landed on");
    }

    private sealed class CatchModel { public bool On { get; set; } }

    [Fact]
    public void A_slow_release_does_not_fling()
    {
        using var t = new TestDoc(Html, Css);
        var touch = new TouchInput(t.Doc);

        touch.Down(200, 80, 0.0);
        touch.Move(200, 60, 1.0);                            // 20px over a full second: a slow drag
        touch.Up(200, 60, 1.2);

        Assert.False(t.Doc.FlingActive);
    }

    [Fact]
    public void Fling_survives_a_virtual_list_rewindow()
    {
        // A <cupri-virtual> list re-windows (REBUILDS the tree) as it scrolls; the fling holds a
        // structural path, not a node, so momentum must carry straight through the rebuild.
        var m = new VirtualModel();
        const string html =
            "<body><cupri-virtual height=\"120\" item-height=\"30\">" +
            "<div class=\"row\" data-repeat=\"Rows\">{{.}}</div></cupri-virtual></body>";
        using var t = new TestDoc(html, "", m, components: true);
        var touch = new TouchInput(t.Doc);

        touch.Down(200, 100, 0.00);
        touch.Move(200, 60, 0.02);
        touch.Up(200, 20, 0.04);
        Assert.True(t.Doc.FlingActive, "fling started on the virtual list");

        for (var i = 1; i <= 240 && t.Doc.FlingActive; i++)
            t.Doc.Animate(0.04 + i * (1 / 60.0));

        t.Layout();
        var scroller = t.Find(n => n.IsScrollable)!;
        Assert.True(scroller.ScrollY > 60, $"momentum crossed at least one re-window (got {scroller.ScrollY})");
    }

    private sealed class VirtualModel
    {
        public List<string> Rows { get; set; } =
            Enumerable.Range(1, 200).Select(i => $"row {i}").ToList();
    }
}
