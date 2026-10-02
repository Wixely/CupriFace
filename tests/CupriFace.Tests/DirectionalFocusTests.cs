using CupriFace.Interaction;
using Xunit;
using Xunit.Abstractions;

namespace CupriFace.Tests;

/// <summary>
/// Directional focus — a D-pad or a thumbstick, rather than Tab order.
///
/// <para>Tab is one-dimensional and follows the document, so on a panel laid out in two columns
/// "down" and "next" are different controls and only one of them is what the user pointed the stick
/// at. Every controller-driven CupriFace app so far wrote this itself, reading geometry back out of
/// the accessibility tree because the engine offered nothing else.</para>
///
/// <para>Focus is asserted by accessible NAME rather than by index, because an index says nothing
/// about where the thing is on screen — and where it is on screen is the entire subject.</para>
/// </summary>
public class DirectionalFocusTests(ITestOutputHelper output)
{
    // A 2x3 grid whose labels say which square each button is, so a failure names where it landed.
    private const string Grid = """
        <body>
          <div class='row'><div class='b' role='button'>a1</div><div class='b' role='button'>b1</div></div>
          <div class='row'><div class='b' role='button'>a2</div><div class='b' role='button'>b2</div></div>
          <div class='row'><div class='b' role='button'>a3</div><div class='b' role='button'>b3</div></div>
        </body>
        """;
    private const string GridCss = """
        body { margin:0; background:#111; }
        .row { display:flex; gap:20px; padding:10px; }
        .b { width:160px; height:48px; background:#345; color:#fff; font-size:15px; }
        """;

    private static TestDoc Open() => new(Grid, GridCss, width: 600, height: 400);

    /// <summary>Down moves down a column, right moves across a row. The baseline: without the cone
    /// test every direction walks the row, because the nearest control to almost anything is its
    /// immediate neighbour.</summary>
    [Fact]
    public void It_moves_in_the_direction_asked_for()
    {
        using var t = Open();

        t.Pad.Press(NavigationDirection.Down);
        Assert.Equal("a1", t.FocusedName());     // first press: the topmost, not markup order

        t.Pad.Press(NavigationDirection.Down);
        Assert.Equal("a2", t.FocusedName());
        t.Pad.Press(NavigationDirection.Right);
        Assert.Equal("b2", t.FocusedName());
        t.Pad.Press(NavigationDirection.Up);
        Assert.Equal("b1", t.FocusedName());
        t.Pad.Press(NavigationDirection.Left);
        Assert.Equal("a1", t.FocusedName());
    }

    /// <summary>Holding Down walks a column rather than wandering across the grid. On an even grid the
    /// cone alone is enough for this — see
    /// <see cref="A_nearer_candidate_in_another_column_loses_to_one_in_this_column"/> for the layout
    /// where it is not.</summary>
    [Fact]
    public void Holding_down_walks_a_column_without_drifting_sideways()
    {
        using var t = Open();
        t.Pad.Press(NavigationDirection.Down);
        t.Pad.Press(NavigationDirection.Right);
        Assert.Equal("b1", t.FocusedName());

        t.Pad.Press(NavigationDirection.Down);
        Assert.Equal("b2", t.FocusedName());
        t.Pad.Press(NavigationDirection.Down);
        Assert.Equal("b3", t.FocusedName());     // still column b, never a2/a3
    }

    /// <summary>
    /// A COLUMN STAYS A COLUMN EVEN WHEN THAT COSTS DISTANCE. This is the case that separates a
    /// working implementation from a plausible one, and an even grid will not show it: there, the 45°
    /// cone rejects the diagonal on its own, so a build with no beam rule at all still passes every
    /// test above.
    ///
    /// <para>The layout that exposes it is a STAGGERED one — two columns where this column's next
    /// control is far down and the other column has something level with the gap. Here
    /// <c>right-middle</c> is 130px below the focused control and <b>inside</b> the cone (dx 110 ≤ dy
    /// 130), while <c>left-bottom</c>, directly below in the same column, is 280px away. Scored on
    /// distance the selection hops to the other column; scored on the beam first it descends the
    /// column the user is actually in. Masonry panels and a two-column form with one tall field both
    /// produce exactly this.</para>
    /// </summary>
    [Fact]
    public void A_nearer_candidate_in_another_column_loses_to_one_in_this_column()
    {
        const string staggered = """
            <body>
              <div class='b' style='left:0px;top:0px' role='button'>left-top</div>
              <div class='b' style='left:0px;top:280px' role='button'>left-bottom</div>
              <div class='b' style='left:110px;top:130px' role='button'>right-middle</div>
            </body>
            """;
        using var t = new TestDoc(staggered,
            "body{margin:0;position:relative} .b{position:absolute;width:100px;height:40px;background:#345;color:#fff}",
            width: 400, height: 400);
        output.WriteLine(t.Doc.DumpTree());   // the geometry the assertion depends on

        t.Pad.Press(NavigationDirection.Down);
        Assert.Equal("left-top", t.FocusedName());

        t.Pad.Press(NavigationDirection.Down);
        Assert.Equal("left-bottom", t.FocusedName());   // not right-middle, though that is 150px nearer
    }

    /// <summary>It does NOT wrap. A stick held right should stop at the edge, not reappear on the
    /// left — Tab wraps because a form is a loop, and a grid is not. Returning false is what lets a
    /// host decide to do something else at the boundary (page across, or nudge the panel).</summary>
    [Fact]
    public void It_stops_at_the_edge_instead_of_wrapping()
    {
        using var t = Open();
        t.Pad.Press(NavigationDirection.Down);
        t.Pad.Press(NavigationDirection.Right);
        Assert.Equal("b1", t.FocusedName());

        Assert.False(t.Pad.Press(NavigationDirection.Right), "nothing lies right of the last column");
        Assert.Equal("b1", t.FocusedName());     // and focus did not move
        Assert.False(t.Pad.Press(NavigationDirection.Up), "nothing lies above the first row");
        Assert.Equal("b1", t.FocusedName());
    }

    /// <summary>The first press enters from the edge the user is travelling FROM: Down lands on the
    /// top row, Up on the bottom, Right on the left column. Starting at markup order would put the
    /// selection wherever the document happens to begin, which on a grid is rarely where anyone is
    /// looking.</summary>
    [Theory]
    [InlineData(NavigationDirection.Down, "a1")]
    [InlineData(NavigationDirection.Right, "a1")]
    [InlineData(NavigationDirection.Up, "a3")]
    [InlineData(NavigationDirection.Left, "b1")]
    public void The_first_press_enters_from_the_edge_it_travels_from(NavigationDirection first, string expected)
    {
        using var t = Open();
        t.Pad.Press(first);
        output.WriteLine($"{first} → {t.FocusedName()}");
        Assert.Equal(expected, t.FocusedName());
    }

    /// <summary>
    /// PRESSES ARRIVING FASTER THAN FRAMES STILL NAVIGATE. Moving focus rebuilds the document, which
    /// throws away every box's position, and navigation reads exactly those positions — so without a
    /// guard the second press in a row scores against a tree of zeros, finds nothing "in direction",
    /// and is silently swallowed. Three Downs would leave focus on the first control.
    ///
    /// <para>A host does not get to avoid this: Android delivers a key event whenever it likes, and
    /// a D-pad held down autorepeats faster than a frame. So the engine lays itself out on entry
    /// rather than trusting the caller to have rendered — hence NO <c>onFrame</c> here, deliberately.
    /// </para>
    /// </summary>
    [Fact]
    public void Presses_between_frames_still_navigate()
    {
        using var t = Open();
        var pad = new GamepadDriver(t.Doc);  // deliberately NO onFrame: the engine must cope alone

        // Nothing reads focus until the end: FocusedName() lays out, which would paper over exactly
        // the staleness this is about. (Checked: without the guard this lands on a1, not a3.)
        pad.Press(NavigationDirection.Down);
        pad.Press(NavigationDirection.Down);
        pad.Press(NavigationDirection.Down);
        output.WriteLine($"after three blind Downs: {t.FocusedName()}");
        Assert.Equal("a3", t.FocusedName());
    }

    // ---- the stick, which is where the bugs are ------------------------------------------------

    /// <summary>A HELD STICK MOVES ONCE. A thumbstick reports a position every frame, so a naive
    /// reading races the selection across the panel for one flick. The move happens on the edge into
    /// a direction, and not again until the stick comes back through the deadzone.</summary>
    [Fact]
    public void A_held_stick_moves_once_and_then_waits_for_centre()
    {
        using var t = Open();
        t.Pad.Press(NavigationDirection.Down);
        Assert.Equal("a1", t.FocusedName());

        Assert.True(t.Pad.Stick(0f, 0.9f));                 // pushed down: one move
        Assert.Equal("a2", t.FocusedName());
        for (var frame = 0; frame < 10; frame++)
            Assert.False(t.Pad.Stick(0f, 0.9f), "a held stick must not keep moving");
        Assert.Equal("a2", t.FocusedName());

        t.Pad.Stick(0f, 0f);                                // back to centre: re-armed
        Assert.True(t.Pad.Stick(0f, 0.9f));
        Assert.Equal("a3", t.FocusedName());
    }

    /// <summary>Inside the deadzone the stick is at rest. Every real controller drifts, and without
    /// this the selection creeps on its own while nobody is touching it.</summary>
    [Fact]
    public void Drift_inside_the_deadzone_does_not_move_anything()
    {
        using var t = Open();
        t.Pad.Press(NavigationDirection.Down);

        foreach (var drift in new[] { 0.05f, -0.2f, 0.4f, -0.49f })
            Assert.False(t.Pad.Stick(drift, drift), $"drift of {drift} is at rest");
        Assert.Equal("a1", t.FocusedName());
    }

    /// <summary>A diagonal push picks ONE axis, the dominant one. A flick that moves the selection two
    /// squares feels broken, and a stick is never perfectly on an axis.</summary>
    [Fact]
    public void A_diagonal_push_moves_on_one_axis_only()
    {
        using var t = Open();
        t.Pad.Press(NavigationDirection.Down);
        Assert.Equal("a1", t.FocusedName());

        t.Pad.Stick(0.9f, 0.6f);                            // right-and-down: right dominates
        Assert.Equal("b1", t.FocusedName());
    }

    /// <summary>Changing direction without passing through centre still moves — a stick rolled from
    /// right to down is a new direction, and waiting for a true centre that a rolling thumb never
    /// reaches would drop the input.</summary>
    [Fact]
    public void Rolling_the_stick_to_a_new_direction_moves_again()
    {
        using var t = Open();
        t.Pad.Press(NavigationDirection.Down);

        Assert.True(t.Pad.Stick(0.9f, 0f));                 // right → b1
        Assert.Equal("b1", t.FocusedName());
        Assert.True(t.Pad.Stick(0.1f, 0.9f), "rolled from right to down, never centred");
        Assert.Equal("b2", t.FocusedName());
    }

    /// <summary>Confirm activates the focused control, through the same path Enter takes — so a
    /// controller and a keyboard cannot come to disagree about what "activate" means.</summary>
    [Fact]
    public void Confirm_activates_the_focused_control()
    {
        using var t = Open();
        var clicked = new List<string>();
        t.Doc.OnClick(".b", e => clicked.Add(t.FocusedName()));

        t.Pad.Press(NavigationDirection.Down);
        t.Pad.Press(NavigationDirection.Down);
        Assert.Equal("a2", t.FocusedName());

        t.Pad.Confirm();
        Assert.Equal(new[] { "a2" }, clicked);
    }

    /// <summary>Tab order is untouched. Directional navigation is an addition, and the regression to
    /// fear is that it quietly becomes the thing Tab does too — Tab follows the DOCUMENT, so on this
    /// grid it crosses the first row rather than descending the first column.</summary>
    [Fact]
    public void Tab_order_is_unchanged()
    {
        using var t = Open();
        t.Key(EditKey.Tab);
        Assert.Equal("a1", t.FocusedName());
        t.Key(EditKey.Tab);
        Assert.Equal("b1", t.FocusedName());     // document order, not spatial
    }

    /// <summary>Nothing focusable, nothing to do — and specifically no crash. A panel of plain text is
    /// a real state while a view is loading, and it is where an unguarded "focus the first control"
    /// throws.</summary>
    [Fact]
    public void A_document_with_no_controls_reports_no_move()
    {
        using var t = new TestDoc("<body><div>just text</div></body>", "body{margin:0}");
        Assert.False(t.Pad.Press(NavigationDirection.Down));
        Assert.Equal("", t.FocusedName());
    }
}
