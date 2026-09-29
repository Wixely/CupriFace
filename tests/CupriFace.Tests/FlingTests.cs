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

    /// <summary>A SLOW frame integrates ALL of the time that passed, not the first 0.1s of it.
    ///
    /// <para>The old clamp capped dt at 0.1s, so a 125ms frame advanced the animation as though
    /// only 100ms had elapsed and the fling fell progressively behind the clock (#229). Asserted as
    /// "a longer frame must advance it further", because that is the property the clamp destroys:
    /// under the clamp a 125ms frame and a 100ms frame are indistinguishable.</para>
    ///
    /// <para>Deliberately NOT asserted by comparing coarse stepping against fine: the integrator
    /// decays velocity before applying it, so a large dt undershoots the exact integral by ~20% at
    /// 8fps whatever the clamp does. That is a separate accuracy question and would make this test
    /// fail for a reason it is not about.</para>
    /// </summary>
    [Fact]
    public void A_longer_frame_advances_the_fling_further_than_a_shorter_one()
    {
        static float AfterOneFrame(double dt)
        {
            using var t = new TestDoc(Html, Css);
            var touch = new TouchInput(t.Doc);
            touch.Down(200, 80, 0.00);
            touch.Move(200, 60, 0.02);
            touch.Move(200, 40, 0.04);
            touch.Up(200, 20, 0.06);

            t.Doc.Animate(0.06);                      // stamps the animation clock, moves nothing
            var before = t.Find(n => n.IsScrollable)!.ScrollY;
            t.Doc.Animate(0.06 + dt);                 // exactly one frame of the given length
            return t.Find(n => n.IsScrollable)!.ScrollY - before;
        }

        var at100 = AfterOneFrame(0.100);             // the old clamp's ceiling
        var at125 = AfterOneFrame(0.125);             // past it

        Assert.True(at100 > 0, "a 100ms frame advances the fling");
        Assert.True(at125 > at100 * 1.05,
            $"a 125ms frame must advance further than a 100ms one — got {at125:F2} vs {at100:F2}");
    }

    /// <summary>THE SAME GESTURE TRAVELS THE SAME DISTANCE AT EVERY FRAME RATE. The one assertion
    /// that would have caught #231, and the one the suite did not have: every other fling test steps
    /// at a single rate, so a 32%-at-4fps distance error passed all 1543 of them.
    ///
    /// <para>The fling used to decay velocity and then move by <c>v * dt</c> — a rectangle drawn at
    /// the END of a falling curve, so it fitted underneath and the fling came up short. Always short,
    /// never long, because velocity falls monotonically and so the right-hand sample is below the
    /// interval's average on every single frame. Systematic bias, not noise, so it accumulated over
    /// the whole fling instead of averaging out: -2.3% at 60fps, -17% at 8fps, -32% at 4fps.</para>
    ///
    /// <para>Run to completion, not to a fixed instant: what a user sees is where the list ends up.
    /// The tolerance is 1.5%, which is comfortably inside the old error at every rate below 60fps and
    /// above the residual from <c>FlingStopSpeed</c> truncating the tail at a slightly different point
    /// per step size — that last part is a stop condition, not the integrator, and it is why this is
    /// not asserted to the float.</para>
    /// </summary>
    [Fact]
    public void The_same_fling_travels_the_same_distance_at_any_frame_rate()
    {
        static float Distance(double fps)
        {
            using var t = new TestDoc(Html, Css);
            var touch = new TouchInput(t.Doc);
            touch.Down(200, 80, 0.00);
            touch.Move(200, 60, 0.02);
            touch.Move(200, 40, 0.04);
            touch.Up(200, 20, 0.06);

            var start = t.Find(n => n.IsScrollable)!.ScrollY;
            var now = 0.06;
            for (var i = 0; i < 100_000 && t.Doc.FlingActive; i++) t.Doc.Animate(now += 1.0 / fps);
            return t.Find(n => n.IsScrollable)!.ScrollY - start;
        }

        var reference = Distance(240);                  // fine enough to stand in for the exact integral
        Assert.True(reference > 50, $"the reference fling must actually travel, got {reference:F1}px");

        foreach (var fps in new[] { 120.0, 60, 30, 16, 8, 4 })
        {
            var d = Distance(fps);
            Assert.InRange(d, reference * 0.985f, reference * 1.015f);
        }
    }

    /// <summary>A STALL contributes nothing. The thread was blocked; none of that gap was animation,
    /// so integrating it would teleport the scroll to wherever two seconds of momentum reaches.
    /// Measured: a blocked main thread hands the host the whole gap as a frame timestamp, and fires
    /// no visibilitychange — so this cannot be detected by page visibility.</summary>
    [Fact]
    public void A_stalled_clock_does_not_teleport_the_scroll()
    {
        using var t = new TestDoc(Html, Css);
        var touch = new TouchInput(t.Doc);
        touch.Down(200, 80, 0.00);
        touch.Move(200, 60, 0.02);
        touch.Move(200, 40, 0.04);
        touch.Up(200, 20, 0.06);

        t.Doc.Animate(0.06 + 1 / 60.0);                       // one ordinary frame
        var before = t.Find(n => n.IsScrollable)!.ScrollY;

        t.Doc.Animate(0.06 + 1 / 60.0 + 2.0);                 // …then the thread was gone for 2s
        var after = t.Find(n => n.IsScrollable)!.ScrollY;

        Assert.Equal(before, after);
    }

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
