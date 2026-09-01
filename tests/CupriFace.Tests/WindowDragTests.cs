using CupriFace.Interaction;
using Xunit;
using Xunit.Abstractions;

namespace CupriFace.Tests;

/// <summary>
/// <c>data-window-drag</c>: the title bar a frameless window does not have.
///
/// The engine owns pixels, not the OS window, so it reports how far a drag has travelled and the host
/// adds that to the window's position — the same split as <c>WindowCommandRequested</c> and
/// <c>Navigated</c>. The deltas are what these tests check, since the window itself is the host's.
/// </summary>
public class WindowDragTests(ITestOutputHelper output)
{
    private const string Html = """
        <body style="margin:0">
          <div class="bar" data-window-drag style="height:40px">title</div>
          <div class="content" style="height:100px">body</div>
        </body>
        """;

    private static (TestDoc Doc, List<WindowMove> Moves) Hud()
    {
        var t = new TestDoc(Html, "", width: 300, height: 200);
        var moves = new List<WindowMove>();
        t.Doc.WindowMoveRequested += m => moves.Add(m);
        return (t, moves);
    }

    [Fact]
    public void Dragging_the_bar_reports_how_far_the_pointer_moved()
    {
        var (t, moves) = Hud();
        using var _ = t;

        t.Click(60, 20);                 // grab the bar
        t.Move(90, 34);                  // …and pull down-right

        Assert.Single(moves);
        Assert.Equal(new WindowMove(30, 14), moves[0]);
    }

    /// <summary>The press point does NOT advance with the pointer. Once the host has moved the window,
    /// the pointer is back over the point it grabbed, so every delta is measured from the same origin.
    /// Advancing it would halve every movement — the window would lag the cursor by half the distance
    /// and drift further behind on every event.</summary>
    [Fact]
    public void Each_delta_is_measured_from_the_grab_point_not_the_previous_event()
    {
        var (t, moves) = Hud();
        using var _ = t;

        t.Click(50, 20);
        t.Move(60, 20);
        t.Move(70, 20);
        t.Move(80, 20);

        output.WriteLine(string.Join(", ", moves));
        // A host that moves the window sees the pointer return to the grab point each time, so these
        // are each "10 further than where I grabbed" and not a running total.
        Assert.Equal([new WindowMove(10, 0), new WindowMove(20, 0), new WindowMove(30, 0)], moves);
    }

    [Fact]
    public void Releasing_ends_the_drag()
    {
        var (t, moves) = Hud();
        using var _ = t;

        t.Click(60, 20);
        t.Move(80, 20);
        t.Up(80, 20);
        moves.Clear();

        t.Move(200, 20);                 // moving with nothing held must report nothing
        Assert.Empty(moves);
    }

    [Fact]
    public void A_drag_that_starts_off_the_bar_moves_nothing()
    {
        var (t, moves) = Hud();
        using var _ = t;

        t.Click(60, 90);                 // in the body, below the bar
        t.Move(120, 140);

        Assert.Empty(moves);
    }

    /// <summary>A host with no window to move — a browser page, an Android activity — does not
    /// subscribe, and the press is then left to whatever else wanted it rather than being swallowed by
    /// a drag that can never do anything.</summary>
    [Fact]
    public void With_no_host_listening_the_press_is_not_claimed()
    {
        using var t = new TestDoc(
            "<body style='margin:0'><div class='bar' data-window-drag style='height:40px'>" +
            "<span class='btn'>x</span></div></body>", "", width: 300, height: 200);
        var clicks = 0;
        t.Doc.OnClick(".btn", _ => clicks++);      // no WindowMoveRequested subscriber

        t.ClickMatch(n => n.Element?.ClassList.Contains("btn") == true);
        Assert.Equal(1, clicks);
    }

    /// <summary>Both desktop windows must map a grab to something that is NOT the link hand.
    ///
    /// <para>The engine reporting <c>Grab</c> is only half of it: the hosts used to fold Grab and
    /// Grabbing in with Pointer, so a drag handle arrived at the OS as the same pointing hand a
    /// hyperlink gets and read as clickable rather than draggable. Neither GLFW nor SDL has an
    /// open/closed hand, so the move arrow is the closest either can do — the point is that it is
    /// DIFFERENT from a link.</para>
    ///
    /// <para>Source analysis, because a cursor mapping needs a real window to exercise, and checked
    /// for BOTH windows because the GL and SDL paths have drifted before.</para></summary>
    [Fact]
    public void Both_desktop_windows_map_a_grab_away_from_the_link_hand()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "CupriFace.slnx"))) root = root.Parent;
        Assert.NotNull(root);

        foreach (var (file, hand) in new[]
                 {
                     ("SkiaWindow.cs", "StandardCursor.Hand"),
                     ("SdlSoftwareWindow.cs", "SystemCursor.SystemCursorHand"),
                 })
        {
            var src = File.ReadAllText(Path.Combine(root!.FullName, "src", "CupriFace.Shell", file));
            var line = src.Split('\n').FirstOrDefault(l => l.Contains(hand) && l.Contains("=>"));
            Assert.NotNull(line);
            output.WriteLine($"{file}: {line!.Trim()}");

            // The hand belongs to Pointer alone now.
            Assert.DoesNotContain("Grab", line);
            Assert.Contains("Pointer", line);
        }
    }

    /// <summary>The bar shows a grab cursor, so it looks like what it is — but only once a host is
    /// listening, since offering a grab that cannot move anything is a lie.</summary>
    [Fact]
    public void The_bar_offers_a_grab_cursor_only_when_a_host_can_act_on_it()
    {
        using var quiet = new TestDoc(Html, "", width: 300, height: 200);
        Assert.NotEqual(CupriFace.Style.CursorType.Grab, quiet.Doc.CursorAt(60, 20));

        var (t, _) = Hud();
        using var _d = t;
        Assert.Equal(CupriFace.Style.CursorType.Grab, t.Doc.CursorAt(60, 20));

        t.Click(60, 20);                 // …and while dragging it is the closed hand
        Assert.Equal(CupriFace.Style.CursorType.Grabbing, t.Doc.CursorAt(60, 20));
    }
}
