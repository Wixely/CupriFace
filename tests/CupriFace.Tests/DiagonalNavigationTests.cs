using CupriFace.Interaction;
using Xunit;
using Xunit.Abstractions;

namespace CupriFace.Tests;

/// <summary>
/// <c>doc.DiagonalNavigation</c> — two arrows pressed together as one move to the corner.
///
/// <para>The bug it fixes is that without it, <b>where you end up depends on which key the hardware
/// reported first</b>. The layout here is the one it was reported on: cluster B of the SpatialNav
/// sample, two staggered columns. From B1, Right lands on B2 and then Down overshoots to B4; Down
/// lands on B3 and then Right goes to B4 as well. Neither is B2, which is the box actually sitting
/// down-and-right of B1, and a user pressing both keys cannot predict which race they will win.</para>
///
/// <para>These tests drive <c>Animate</c> because the feature is a deadline — the first press is held
/// in case its partner is coming, exactly as a masked field is held before it re-masks. That is the
/// cost of the flag and the reason it is off by default.</para>
/// </summary>
public class DiagonalNavigationTests(ITestOutputHelper output)
{
    // Cluster B of samples/SpatialNav, to scale: two columns, offset so that the nearest box in the
    // other column is nearer than the next box down this one.
    private const string Staggered = """
        <body>
          <div class='b' style='left:600px;top:124px' role='button'>B1</div>
          <div class='b' style='left:740px;top:234px' role='button'>B2</div>
          <div class='b' style='left:600px;top:360px' role='button'>B3</div>
          <div class='b' style='left:740px;top:424px' role='button'>B4</div>
          <div class='b' style='left:600px;top:500px' role='button'>B5</div>
          <div class='b' style='left:740px;top:584px' role='button'>B6</div>
        </body>
        """;
    private const string Css = """
        body { margin:0; position:relative; background:#14161C }
        .b { position:absolute; width:120px; height:66px; background:#242A36; color:#fff; font-size:14px }
        """;

    private static TestDoc Open(bool diagonal = true)
    {
        var t = new TestDoc(Staggered, Css, width: 1040, height: 720);
        t.Doc.ArrowNavigation = true;
        t.Doc.DiagonalNavigation = diagonal;
        return t;
    }

    /// <summary>Let the held press time out, the way a host's frame loop does.</summary>
    private static void Settle(TestDoc t, double from = 0)
    {
        t.Doc.Animate(from);                 // stamps the press
        t.Doc.Animate(from + 1.0);           // well past the window
        t.Layout();
    }

    private static void Focus(TestDoc t, string label)
    {
        t.Key(EditKey.Tab);                  // B1 is first in the document
        Assert.Equal(label, t.FocusedName());
    }

    /// <summary>
    /// THE BUG, both ways round. Right-then-Down and Down-then-Right must land on the same box, and
    /// that box must be B2 — the one actually diagonally adjacent. Before this flag the first gave
    /// B4 and the second gave B4 by a different route, and neither was reachable deliberately.
    /// </summary>
    [Theory]
    [InlineData(EditKey.Right, EditKey.Down)]
    [InlineData(EditKey.Down, EditKey.Right)]
    public void Two_arrows_together_make_one_move_to_the_corner(EditKey first, EditKey second)
    {
        using var t = Open();
        Focus(t, "B1");

        t.Key(first);
        Assert.Equal("B1", t.FocusedName());   // nothing has moved yet: the window is open
        t.Key(second);

        output.WriteLine($"{first} + {second} → {t.FocusedName()}");
        Assert.Equal("B2", t.FocusedName());   // one move, to the corner, whichever key won the race
    }

    /// <summary>The order-independence is the point, stated on its own: the two orders agree.</summary>
    [Fact]
    public void The_result_does_not_depend_on_which_key_arrived_first()
    {
        using var a = Open();
        Focus(a, "B1");
        a.Key(EditKey.Right); a.Key(EditKey.Down);

        using var b = Open();
        Focus(b, "B1");
        b.Key(EditKey.Down); b.Key(EditKey.Right);

        Assert.Equal(a.FocusedName(), b.FocusedName());
    }

    /// <summary>Without the flag, the same two keypresses are two moves and overshoot the corner —
    /// the behaviour being fixed. If this ever stops failing to reach B2, the test above has stopped
    /// proving anything.</summary>
    [Fact]
    public void Without_the_flag_the_same_presses_overshoot()
    {
        using var t = Open(diagonal: false);
        Focus(t, "B1");
        t.Key(EditKey.Right);
        Assert.Equal("B2", t.FocusedName());   // moved immediately — no window
        t.Key(EditKey.Down);
        Assert.Equal("B4", t.FocusedName());   // …and straight past the corner
    }

    /// <summary>A LONE arrow still moves. It is held for the window and then released, so the flag
    /// costs latency rather than function — if this were wrong, turning diagonals on would break
    /// ordinary navigation entirely.</summary>
    [Fact]
    public void A_single_arrow_still_moves_once_its_window_passes()
    {
        using var t = Open();
        Focus(t, "B1");

        t.Key(EditKey.Down);
        Assert.Equal("B1", t.FocusedName());   // still waiting
        Settle(t);
        Assert.Equal("B3", t.FocusedName());   // released, and ordinary spatial navigation applies
    }

    /// <summary>
    /// A WAITING PRESS MUST WAKE THE HOST, and specifically through
    /// <c>HasActiveAnimations</c> — the signal every host polls to decide whether to produce a frame
    /// at all. <c>HasActiveTransitions</c> only decides whether to call <c>Animate</c> WITHIN a
    /// frame, so a flag on that alone is invisible: no frame happens, <c>Animate</c> is never
    /// reached, and the held press is never released.
    ///
    /// <para>That is not hypothetical — it shipped. The symptom is maddening rather than obviously
    /// broken: the first press appears to do nothing, and then merges with the next press whenever
    /// it comes, so two deliberately separate moves become one diagonal seconds apart.</para>
    /// </summary>
    [Fact]
    public void A_waiting_press_wakes_a_render_on_demand_host()
    {
        using var t = Open();
        Focus(t, "B1");
        Assert.False(t.Doc.HasActiveAnimations, "nothing is pending yet");
        Assert.False(t.Doc.HasActiveTransitions);

        t.Key(EditKey.Down);
        Assert.True(t.Doc.HasActiveAnimations,
            "the host polls THIS to decide whether to draw a frame; without it nothing ticks");
        Assert.True(t.Doc.HasActiveTransitions,
            "and THIS to decide whether to call Animate inside that frame");

        Settle(t);
        Assert.False(t.Doc.HasActiveAnimations, "and both go quiet once it has been released");
        Assert.False(t.Doc.HasActiveTransitions);
    }

    /// <summary>
    /// The bug as a user would describe it: two presses meant as separate moves, far enough apart to
    /// be obviously separate, must not combine. They only did because the first was never released —
    /// so this is the regression test for the wake signal above, written in terms of what was seen
    /// rather than which flag was missing.
    ///
    /// <para>It is NOT the guard, though, and that is worth knowing: this harness calls
    /// <c>Animate</c> itself, so it has no render-on-demand gate to get wrong and it passed happily
    /// while the bug was live. The assertion that actually catches it is the one above, on the flag
    /// the hosts poll.</para>
    /// </summary>
    [Fact]
    public void Two_presses_seconds_apart_are_two_moves_not_a_diagonal()
    {
        using var t = Open();
        Focus(t, "B1");

        t.Key(EditKey.Right);
        Settle(t, from: 0);                    // the host ticks; the window expires
        Assert.Equal("B2", t.FocusedName());

        t.Key(EditKey.Down);                   // a separate move, much later
        Settle(t, from: 100);
        Assert.Equal("B4", t.FocusedName());   // down from B2 — NOT a corner move from B1
    }

    /// <summary>Two presses on the SAME axis are two presses, not a corner. Pressing Down twice
    /// quickly must still travel two rows — the second must not be swallowed into the first.</summary>
    [Fact]
    public void Two_presses_on_the_same_axis_are_two_moves()
    {
        using var t = Open();
        Focus(t, "B1");

        t.Key(EditKey.Down);
        t.Key(EditKey.Down);                   // releases the first, holds the second
        Assert.Equal("B3", t.FocusedName());
        Settle(t);
        Assert.Equal("B5", t.FocusedName());   // and then the second lands
    }

    /// <summary>The window is configurable, and it is the latency the flag adds. A press must not be
    /// released before it elapses, or the partner never gets its chance and the feature does nothing.
    /// </summary>
    [Fact]
    public void A_press_is_not_released_before_its_window_elapses()
    {
        using var t = Open();
        t.Doc.DiagonalWindowSeconds = 0.2;
        Focus(t, "B1");

        t.Key(EditKey.Down);
        t.Doc.Animate(10.0);                   // stamp
        t.Doc.Animate(10.1);                   // half way: too early
        t.Layout();
        Assert.Equal("B1", t.FocusedName());

        t.Doc.Animate(10.25);                  // past it
        t.Layout();
        Assert.Equal("B3", t.FocusedName());
    }

    /// <summary>Off by default, because the latency is real and a UI that never wants diagonals
    /// should not pay for it.</summary>
    [Fact]
    public void Diagonals_are_off_by_default()
    {
        using var t = new TestDoc(Staggered, Css, width: 1040, height: 720);
        Assert.False(t.Doc.DiagonalNavigation);
    }

    // ---- the stick, which needs no window at all -------------------------------------------------

    /// <summary>A THUMBSTICK NEEDS NO WAITING. It reports a vector, so "down and right" arrives as a
    /// single reading and the corner is simply what it says — no deadline, no latency, no Animate.
    /// This is why the keyboard's window is a keyboard problem rather than a navigation problem.</summary>
    [Fact]
    public void A_stick_resolves_a_corner_from_one_reading()
    {
        using var t = new TestDoc(Staggered, Css, width: 1040, height: 720);
        var pad = new GamepadDriver(t.Doc, onFrame: t.Layout, diagonals: true);

        pad.Press(NavigationDirection.Down);
        Assert.Equal("B1", t.FocusedName());   // entry point, no window involved

        Assert.True(pad.Stick(0.8f, 0.8f));    // one push, one move
        Assert.Equal("B2", t.FocusedName());
    }

    /// <summary>Eight sectors, not "both axes are off centre". A push that is mostly down is still
    /// down — otherwise every slightly-off flick would travel diagonally.</summary>
    [Fact]
    public void A_mostly_vertical_push_is_still_vertical()
    {
        using var t = new TestDoc(Staggered, Css, width: 1040, height: 720);
        var pad = new GamepadDriver(t.Doc, onFrame: t.Layout, diagonals: true);
        pad.Press(NavigationDirection.Down);

        pad.Stick(0.25f, 0.95f);               // ~15° off vertical: down, not down-right
        Assert.Equal("B3", t.FocusedName());
    }

    /// <summary>The driver's default is unchanged: diagonals are opt-in there too, so nothing that
    /// already uses a stick starts moving differently.
    ///
    /// <para>Note what this does and does not show. On this layout a 45° push resolves to B2 whether
    /// it is read as Right or as DownRight, so the destination proves nothing — the assertion that
    /// carries weight is that NO window opened, which is the stick's whole advantage over the
    /// keyboard and is false the moment the stick is routed through the held-press path.</para>
    /// </summary>
    [Fact]
    public void The_stick_path_never_opens_a_window()
    {
        using var t = new TestDoc(Staggered, Css, width: 1040, height: 720);
        var pad = new GamepadDriver(t.Doc, onFrame: t.Layout);   // diagonals default off
        t.Doc.ArrowNavigation = true;
        t.Doc.DiagonalNavigation = true;       // on for the DOCUMENT: the stick must still not wait
        pad.Press(NavigationDirection.Down);

        Assert.True(pad.Stick(0.8f, 0.8f), "the stick moves on the reading, not on a deadline");
        Assert.False(t.Doc.HasActiveTransitions, "and leaves nothing pending behind it");
    }
}
