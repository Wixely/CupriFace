using CupriFace.Interaction;
using Xunit;
using Xunit.Abstractions;

namespace CupriFace.Tests;

/// <summary>
/// <see cref="TouchDriver"/> — touch as one call per gesture, the finger equivalent of
/// <c>DispatchClick</c>.
///
/// <para>The recogniser underneath was always testable; driving it was not. Every gesture needed an
/// invented monotonic clock, the slop radius, the knowledge that a long press only fires when the
/// host calls <c>Tick</c> at the deadline, and the knowledge that momentum comes from the velocity
/// of the last 100 ms before the finger lifts. Each of those is easy to get wrong, and a gesture
/// built wrong does nothing — which reads as a bug in whatever was being tested rather than in the
/// test.</para>
///
/// <para>So these assert the VERBS, not the recogniser: that a tap really activates, that a swipe
/// really scrolls and stops, that a fling really leaves momentum behind, that a long press really
/// opens a menu and does not also tap. They double as the worked examples the docs point at.</para>
/// </summary>
public class TouchDriverTests(ITestOutputHelper output)
{
    private sealed class Model { public bool On { get; set; } public int Volume { get; set; } = 50; }
    private sealed class Text { public string Value { get; set; } = "alpha bravo charlie delta"; }

    private const string ScrollerCss = ".box{height:120px;overflow:auto}.pad{height:900px}";
    private const string ScrollerHtml = """
        <body><div class="box">
          <cupri-switch checked="{{On}}">X</cupri-switch>
          <div class="pad"></div>
        </div></body>
        """;

    private static (float X, float Y) Center(TestDoc t, string role)
    {
        var n = FindRole(t.Doc.BuildAccessibilityTree(t.Width, t.Height), role)
                ?? throw new Xunit.Sdk.XunitException($"no {role} in the tree");
        return (n.Bounds.X + n.Bounds.W / 2, n.Bounds.Y + n.Bounds.H / 2);
    }

    private static CupriFace.Accessibility.AccessibilityNode? FindRole(
        CupriFace.Accessibility.AccessibilityNode n, string role)
    {
        if (n.Role == role) return n;
        foreach (var c in n.Children) if (FindRole(c, role) is { } f) return f;
        return null;
    }

    // ---- taps -------------------------------------------------------------------------------

    /// <summary>One line, and it activates — on finger-UP, which is the whole reason touch needs a
    /// recogniser at all.</summary>
    [Fact]
    public void Tap_activates_a_control()
    {
        var m = new Model();
        using var t = new TestDoc("<body><cupri-switch checked='{{On}}'>X</cupri-switch></body>",
                                  "", m, components: true);
        var touch = new TouchDriver(t.Doc);

        var (x, y) = Center(t, "switch");
        touch.Tap(x, y);

        Assert.True(m.On);
    }

    /// <summary>A tap is press and release with no travel, so the down must not activate. Driving it
    /// by hand is the only way to see the half-gesture.</summary>
    [Fact]
    public void The_press_alone_does_not_activate()
    {
        var m = new Model();
        using var t = new TestDoc("<body><cupri-switch checked='{{On}}'>X</cupri-switch></body>",
                                  "", m, components: true);
        var touch = new TouchDriver(t.Doc);

        var (x, y) = Center(t, "switch");
        touch.Down(x, y);
        Assert.False(m.On);

        touch.Up(x, y);
        Assert.True(m.On);
    }

    /// <summary>Taps in a row escalate the click count, which is what selects a word — and that
    /// needs each tap inside the previous one's window AND radius. Getting the spacing wrong
    /// silently produces two separate single taps, and a caret instead of a selection.</summary>
    [Fact]
    public void Double_tap_selects_a_word()
    {
        var m = new Text();
        using var t = new TestDoc("<body><cupri-textfield value='{{Value}}'></cupri-textfield></body>",
                                  "", m, components: true);
        var touch = new TouchDriver(t.Doc);
        var (x, y) = Center(t, "textbox");

        touch.DoubleTap(x, y);

        var selected = t.Doc.CopySelection();
        output.WriteLine($"selected '{selected}'");
        Assert.False(string.IsNullOrEmpty(selected));
        Assert.Contains(selected!, m.Value);
    }

    /// <summary>…and a deliberate pause between the taps does not select anything. This is what
    /// <c>Advance</c> is for, and it is the control that proves the escalation above was real
    /// rather than a selection that happens on any tap.</summary>
    [Fact]
    public void A_pause_between_taps_starts_the_count_again()
    {
        var m = new Text();
        using var t = new TestDoc("<body><cupri-textfield value='{{Value}}'></cupri-textfield></body>",
                                  "", m, components: true);
        var touch = new TouchDriver(t.Doc);
        var (x, y) = Center(t, "textbox");

        touch.Tap(x, y);
        touch.Advance(1.0);                 // well past the double-tap window
        touch.Tap(x, y);

        Assert.True(string.IsNullOrEmpty(t.Doc.CopySelection()), "two separate taps select nothing");
    }

    // ---- travel -----------------------------------------------------------------------------

    /// <summary>A swipe scrolls the container under the finger, and the control the finger started
    /// on is never pressed — a press that becomes a scroll must not activate what it began on.</summary>
    [Fact]
    public void Swipe_scrolls_and_never_activates_what_it_started_on()
    {
        var m = new Model();
        using var t = new TestDoc(ScrollerHtml, ScrollerCss, m, width: 300, height: 200, components: true);
        var touch = new TouchDriver(t.Doc);
        var (x, y) = Center(t, "switch");

        touch.Swipe(x, y, dy: -90);
        t.Layout();

        var scroller = t.Find(n => n.IsScrollable)!;
        output.WriteLine($"scrolled to {scroller.ScrollY:0.0}");
        Assert.True(scroller.ScrollY > 20, $"the swipe should have scrolled, got {scroller.ScrollY:0}");
        Assert.False(m.On, "the switch under the finger must not have been pressed");
    }

    /// <summary>A swipe ENDS STILL: the content stops where the finger left it. Asserted by letting
    /// the clock run afterwards and finding nothing moved — which is the difference between this
    /// verb and the next one, and is invisible without the second render.</summary>
    [Fact]
    public void Swipe_leaves_no_momentum()
    {
        using var t = new TestDoc(ScrollerHtml, ScrollerCss, new Model(), width: 300, height: 200, components: true);
        var touch = new TouchDriver(t.Doc);

        touch.Swipe(150, 60, dy: -90);
        t.Layout();
        var atRelease = t.Find(n => n.IsScrollable)!.ScrollY;

        for (var i = 1; i <= 30; i++) t.Doc.Animate(i * 0.016);
        t.Layout();
        var later = t.Find(n => n.IsScrollable)!.ScrollY;

        output.WriteLine($"release {atRelease:0.0} → later {later:0.0}");
        Assert.Equal(atRelease, later, 0.5);
    }

    /// <summary>A fling ends MOVING: the content keeps going once the finger is gone. The movement
    /// is the frame loop's, so this only shows if the clock is run afterwards — a fling with no
    /// animation looks exactly like a fling that did nothing.</summary>
    [Fact]
    public void Fling_leaves_momentum_for_the_frame_loop()
    {
        using var t = new TestDoc(ScrollerHtml, ScrollerCss, new Model(), width: 300, height: 200, components: true);
        var touch = new TouchDriver(t.Doc);

        touch.Fling(150, 60, dy: -90);
        t.Layout();
        var atRelease = t.Find(n => n.IsScrollable)!.ScrollY;

        for (var i = 1; i <= 30; i++) t.Doc.Animate(i * 0.016);
        t.Layout();
        var later = t.Find(n => n.IsScrollable)!.ScrollY;

        output.WriteLine($"release {atRelease:0.0} → coasted to {later:0.0}");
        Assert.True(later > atRelease + 5, $"the fling should have coasted on: {atRelease:0} → {later:0}");
    }

    /// <summary>A swipe on a dedicated grip is a drag from the first contact — no deferral, because
    /// nobody grabs a slider thumb meaning to tap it. Same verb; the engine decides.</summary>
    [Fact]
    public void Swipe_on_a_slider_drags_it()
    {
        var m = new Model { Volume = 50 };
        using var t = new TestDoc(
            "<body><cupri-slider min='0' max='100' value='{{Volume}}' style='width:200px'></cupri-slider></body>",
            "body{margin:0}", m, width: 300, height: 120, components: true);
        var touch = new TouchDriver(t.Doc);
        var (x, y) = Center(t, "slider");

        touch.Swipe(x, y, dx: 60);

        output.WriteLine($"volume {m.Volume}");
        Assert.True(m.Volume > 60, $"dragging the thumb right should have raised the value, got {m.Volume}");
    }

    // ---- hold -------------------------------------------------------------------------------

    /// <summary>A held press opens the context menu. It fires from the recogniser's <c>Tick</c> at
    /// the deadline, not from the release — a caller who sends only down and up waits for ever, and
    /// gets a tap instead. Driving that by hand is the single easiest thing to get wrong about
    /// touch, which is why it is one verb here.</summary>
    [Fact]
    public void Long_press_opens_the_context_menu()
    {
        var m = new Text();
        using var t = new TestDoc("<body><cupri-textfield value='{{Value}}'></cupri-textfield></body>",
                                  "", m, components: true);
        var touch = new TouchDriver(t.Doc);
        var (x, y) = Center(t, "textbox");

        touch.Tap(x, y);                                   // focus it, as a finger would
        touch.Advance(1.0);
        Assert.Null(ContextMenu(t));

        touch.LongPress(x, y);
        t.Layout();

        Assert.NotNull(ContextMenu(t));
    }

    private static CupriFace.Dom.RenderNode? ContextMenu(TestDoc t)
        => t.Find(n => n.Element?.HasAttribute("data-ctx-menu") == true);

    /// <summary>…and the release that follows it is swallowed. A host that forgets gets a menu AND a
    /// tap, which on a real control means the menu opens over something that just changed.</summary>
    [Fact]
    public void The_release_after_a_long_press_does_not_also_tap()
    {
        var m = new Model();
        using var t = new TestDoc("<body><cupri-switch checked='{{On}}'>X</cupri-switch></body>",
                                  "", m, components: true);
        var touch = new TouchDriver(t.Doc);

        var (x, y) = Center(t, "switch");
        touch.LongPress(x, y);

        Assert.False(m.On);
    }

    /// <summary>A press the platform takes away activates nothing. Worth its own verb: any control
    /// that acts on release has to survive never getting one.</summary>
    [Fact]
    public void A_cancelled_press_activates_nothing()
    {
        var m = new Model();
        using var t = new TestDoc("<body><cupri-switch checked='{{On}}'>X</cupri-switch></body>",
                                  "", m, components: true);
        var touch = new TouchDriver(t.Doc);

        var (x, y) = Center(t, "switch");
        touch.Down(x, y);
        touch.Advance(0.05);
        touch.Cancel();

        Assert.False(m.On);
    }

    // ---- two fingers -------------------------------------------------------------------------

    /// <summary>A pinch reaches the author's own pointer handler with both fingers, because what a
    /// second finger MEANS is the author's to decide — the engine delivers, it does not interpret.</summary>
    [Fact]
    public void Pinch_delivers_both_fingers_to_the_author()
    {
        var widest = 0f;
        using var t = new TestDoc("<body><div class='surface' data-pan>map</div></body>",
                                  "body{margin:0}.surface{width:300px;height:200px}");
        // The attribute is the opt-in — nothing becomes multi-touch by accident — and returning
        // true captures the pointer for this element.
        t.Doc.OnPointer("data-pan", e =>
        {
            if (e.Pointers.Count >= 2)
                widest = MathF.Max(widest, MathF.Abs(e.Pointers[0].X - e.Pointers[1].X));
            return true;
        });
        var touch = new TouchDriver(t.Doc);

        touch.Pinch(150, 100, gapFrom: 40, gapTo: 160);

        output.WriteLine($"widest gap seen: {widest:0.0}");
        Assert.True(widest > 140, $"the handler should have seen the fingers spread, got {widest:0}");
    }

    // ---- the clock ---------------------------------------------------------------------------

    /// <summary>Nothing sleeps and nothing reads the wall clock: the same script produces the same
    /// times on any machine. That is what makes a gesture test reproducible rather than flaky.</summary>
    [Fact]
    public void The_clock_is_scripted_not_real()
    {
        using var t = new TestDoc("<body><div class='hit'>x</div></body>", ".hit{width:100px;height:40px}");
        var touch = new TouchDriver(t.Doc);

        Assert.Equal(0, touch.Now);
        touch.Tap(20, 20);
        var afterTap = touch.Now;
        touch.Advance(2.5);

        Assert.Equal(afterTap + 2.5, touch.Now, 6);
        Assert.True(afterTap is > 0 and < 1, $"a tap should cost a fraction of a second, got {afterTap}");
    }

    /// <summary>The tunables are reachable, so a gesture can be tested AT its boundary rather than
    /// safely inside it — the slop radius here, and the same for the long-press duration.</summary>
    [Fact]
    public void The_thresholds_can_be_moved_for_a_boundary_test()
    {
        var m = new Model();
        using var t = new TestDoc(ScrollerHtml, ScrollerCss, m, width: 300, height: 200, components: true);
        // A huge slop: travel that would normally be a scroll stays a tap.
        var touch = new TouchDriver(t.Doc, new TouchOptions { SlopPx = 500f });
        var (x, y) = Center(t, "switch");

        touch.Down(x, y);
        touch.Advance(0.02);
        touch.Move(x, y - 60);
        touch.Advance(0.02);
        touch.Up(x, y - 60);

        Assert.True(m.On, "inside the slop the gesture is still a tap, however far it travelled");
    }
}
