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

    /// <summary>
    /// THE REAL HOST SEQUENCE. Every test above presses both keys back to back with nothing in
    /// between, which is not what a window does: it polls input, draws a frame, calls Animate, and
    /// only then sees the second key. If the held press were released by that intervening frame the
    /// feature would work in a test and fail in front of a user — which is exactly the shape of bug
    /// this feature has already had once.
    ///
    /// <para>Also pins the boundary: a pair inside the window combines, a pair outside it does not.
    /// "Simultaneous" by hand is not simultaneous, and <c>DiagonalWindowSeconds</c> is how much
    /// human skew is forgiven.</para>
    /// </summary>
    [Theory]
    [InlineData(0.008, "B2")]   // 8 ms  — one frame at 120 fps
    [InlineData(0.016, "B2")]   // 16 ms — one frame at 60 fps
    [InlineData(0.040, "B2")]   // 40 ms — a slow but deliberate "together"
    [InlineData(0.120, "B4")]   // 120 ms — past the window: two separate moves, and rightly so
    public void A_pair_split_across_frames_still_combines(double skew, string expected)
    {
        using var t = Open();
        t.Doc.DiagonalWindowSeconds = 0.05;   // stated, not inherited: this pins the RULE, and a
                                              // change to the shipped default must not silently
                                              // redefine what these cases are asserting.
        Focus(t, "B1");

        var now = 0.0;
        t.Doc.DispatchKey(null, EditKey.Down);      // frame N: the first key
        Frame(t, ref now, 0.008);                   // …the host draws and ticks
        while (now < skew) Frame(t, ref now, 0.008);

        t.Doc.DispatchKey(null, EditKey.Right);     // frame N+k: the second key
        Frame(t, ref now, 0.008);
        for (var i = 0; i < 30; i++) Frame(t, ref now, 0.008);   // let anything held drain

        output.WriteLine($"skew {skew * 1000:F0} ms → {t.FocusedName()}");
        Assert.Equal(expected, t.FocusedName());
    }

    /// <summary>One frame of a host loop: render, then tick the clock, in that order.</summary>
    private static void Frame(TestDoc t, ref double now, double dt)
    {
        t.Layout();
        now += dt;
        t.Doc.Animate(now);
        t.Layout();
    }

    // ---- key-up: "together" as a fact rather than a guess -----------------------------------------

    /// <summary>Press a key, telling the document it is now physically down.</summary>
    private static void Down(TestDoc t, EditKey k) => t.Key(k);

    /// <summary>Release it.</summary>
    private static void Up(TestDoc t, EditKey k) { t.Doc.DispatchKeyUp(k); t.Layout(); }

    /// <summary>
    /// WITH KEY-UP THERE IS NO WINDOW AND NO WAITING. The first press moves immediately — ordinary
    /// navigation costs nothing — and the corner is recognised because the first key is STILL DOWN
    /// when the second arrives, which is a fact about the keyboard rather than a guess about timing.
    ///
    /// <para>The window is set absurdly short here precisely to prove it is not involved: under the
    /// timing fallback these presses would be two separate moves.</para>
    /// </summary>
    [Theory]
    [InlineData(EditKey.Right, EditKey.Down)]
    [InlineData(EditKey.Down, EditKey.Right)]
    public void Two_keys_held_together_make_a_corner_with_no_window(EditKey first, EditKey second)
    {
        using var t = Open();
        t.Doc.DiagonalWindowSeconds = 0.001;   // irrelevant on this path, and proves it
        t.Doc.ReportsKeyUp = true;             // a host that forwards releases
        Focus(t, "B1");

        Down(t, first);                        // moves at once — no latency
        Assert.NotEqual("B1", t.FocusedName());
        Down(t, second);                       // …and the first is still down, so this is a corner
        Assert.Equal("B2", t.FocusedName());

        Up(t, first); Up(t, second);
    }

    /// <summary>ANY GAP WORKS, because nothing is being timed. A pair held across a third of a second
    /// still combines — under the timing fallback this is far outside any sane window.</summary>
    [Fact]
    public void A_slow_pair_still_combines_while_the_first_key_is_held()
    {
        using var t = Open();
        t.Doc.ReportsKeyUp = true;
        Focus(t, "B1");

        Down(t, EditKey.Right);
        for (var i = 0; i < 20; i++) { t.Doc.Animate(i * 0.02); t.Layout(); }   // ~400 ms of frames
        Down(t, EditKey.Down);                 // still holding Right
        Assert.Equal("B2", t.FocusedName());
    }

    /// <summary>And the converse, which is what makes it correct rather than merely permissive: two
    /// presses where the first was RELEASED are two separate moves, however fast they came.</summary>
    [Fact]
    public void Released_then_pressed_is_two_moves_however_fast()
    {
        using var t = Open();
        t.Doc.ReportsKeyUp = true;
        Focus(t, "B1");

        Down(t, EditKey.Right);
        Up(t, EditKey.Right);                  // let go — whatever comes next is a separate move
        Assert.Equal("B2", t.FocusedName());
        Down(t, EditKey.Down);
        Assert.Equal("B4", t.FocusedName());   // down from B2, not a corner from B1
    }

    /// <summary>A key held when the window loses focus must not be remembered as held. Its key-up is
    /// delivered to whoever has focus next and never arrives here, so without this the next arrow
    /// press would be read as half of a corner for the rest of the session.</summary>
    [Fact]
    public void Focus_loss_forgets_everything_held()
    {
        using var t = Open();
        t.Doc.ReportsKeyUp = true;
        Focus(t, "B1");

        Down(t, EditKey.Right);                // → B2, and Right is "held"
        t.Doc.ReleaseAllKeys();                // the host saw the window lose focus
        t.Layout();

        Down(t, EditKey.Down);                 // a fresh press, not a corner
        Assert.Equal("B4", t.FocusedName());
    }

    /// <summary>A third press after a corner starts afresh rather than compounding on the pair.</summary>
    [Fact]
    public void A_press_after_a_corner_is_an_ordinary_move()
    {
        using var t = Open();
        t.Doc.ReportsKeyUp = true;
        Focus(t, "B1");

        Down(t, EditKey.Right); Down(t, EditKey.Down);
        Assert.Equal("B2", t.FocusedName());
        Up(t, EditKey.Right); Up(t, EditKey.Down);

        Down(t, EditKey.Down);
        Assert.Equal("B4", t.FocusedName());   // straight down from B2
    }

    /// <summary>A host that never reports key-up keeps the timing fallback, so nothing regresses for
    /// one that has not been taught to forward releases yet.</summary>
    [Fact]
    public void A_host_that_never_reports_key_up_still_uses_the_window()
    {
        using var t = Open();                  // no DispatchKeyUp call anywhere
        Focus(t, "B1");

        t.Key(EditKey.Right);
        Assert.Equal("B1", t.FocusedName());   // held back: the window path
        Assert.True(t.Doc.HasActiveAnimations);
        t.Key(EditKey.Down);
        Assert.Equal("B2", t.FocusedName());
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

    /// <summary>
    /// A HOST'S DRIVER FOLLOWS THE DOCUMENT. Left unset, <c>diagonals</c> takes its answer from
    /// <see cref="CupriDocument.DiagonalNavigation"/> — the app has already said whether this UI has
    /// corners, and a host that had to answer again could disagree with it. That disagreement is the
    /// bug this prevents: a stick producing corners in a UI whose keyboard refuses to.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_driver_with_no_opinion_follows_the_document(bool documentWantsCorners)
    {
        using var t = new TestDoc(Staggered, Css, width: 1040, height: 720);
        t.Doc.ArrowNavigation = true;
        t.Doc.DiagonalNavigation = documentWantsCorners;
        var pad = new GamepadDriver(t.Doc, onFrame: t.Layout);   // no opinion of its own

        pad.Press(NavigationDirection.Down);                      // B1
        pad.Stick(0.8f, 0.8f);                                    // to B2 on either reading
        pad.Stick(0f, 0f);
        Assert.Equal("B2", t.FocusedName());

        // A down-and-LEFT push from B2 is where the two readings genuinely disagree. As a CORNER it
        // is DownLeft, and the nearest box in that quadrant is B3. As a DOMINANT AXIS it is a tie,
        // which goes to the horizontal, so it is Left — and leftwards the cone holds both B1 and B3,
        // neither sharing B2's row, so the tie breaks on cross-axis distance: B1 at 110 beats B3
        // at 126. Two different boxes, so this actually discriminates.
        pad.Stick(-0.8f, 0.8f);
        Assert.Equal(documentWantsCorners ? "B3" : "B1", t.FocusedName());
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
