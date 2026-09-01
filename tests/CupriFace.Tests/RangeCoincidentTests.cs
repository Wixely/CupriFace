using CupriFace.Dom;
using Xunit;
using Xunit.Abstractions;

namespace CupriFace.Tests;

/// <summary>
/// A range whose thumbs have been pushed onto the same value must still be draggable apart.
///
/// Only the thumb painted last is hit-testable where they coincide, so every press there grabs that
/// same one and the other can never be moved again — a range dragged shut stays shut. Which thumb was
/// meant is genuinely unknowable at press time, so the choice waits for the first movement: pull left
/// and the low thumb follows, pull right and the high one does.
/// </summary>
public class RangeCoincidentTests(ITestOutputHelper output)
{
    private sealed class Span
    {
        public double From { get; set; }
        public double To { get; set; }
        public Span(double from, double to) { From = from; To = to; }
    }

    private const string Html =
        "<body style='margin:0'><cupri-range low=\"{{From}}\" high=\"{{To}}\" min='0' max='100' " +
        "style='width:400px'></cupri-range></body>";

    private static List<RenderNode> Thumbs(TestDoc t)
    {
        var list = new List<RenderNode>();
        void Walk(RenderNode n)
        {
            if (n.Element?.GetAttribute("role") == "slider") list.Add(n);
            foreach (var c in n.Children) Walk(c);
        }
        Walk(t.Doc.Root);
        return list;
    }

    private static RenderNode Track(TestDoc t) =>
        TestDoc.Find(t.Doc.Root, n => n.Element?.HasAttribute("data-slider-track") == true)!;

    /// <summary>Both thumbs sitting on 50. Dragging left must move the LOW thumb even though the high
    /// one is the only thing under the pointer, and vice versa.</summary>
    [Theory]
    [InlineData(0.20, 20.0, 50.0)]   // pull left  → the low thumb follows, the high one stays
    [InlineData(0.85, 50.0, 85.0)]   // pull right → the high thumb follows, the low one stays
    public void Coincident_thumbs_are_told_apart_by_the_direction_of_the_first_drag(
        double fraction, double expectedFrom, double expectedTo)
    {
        var m = new Span(50, 50);
        using var t = new TestDoc(Html, "", m, width: 500, height: 200, components: true);
        var track = Track(t);
        var thumbs = Thumbs(t);
        Assert.Equal(2, thumbs.Count);

        // Press exactly where they overlap — the hit necessarily lands on the high thumb.
        var (px, py) = TestDoc.Center(thumbs[1]);
        var target = track.X + (float)(track.Width * fraction);
        t.Click(px, py);
        t.Move(target, py);
        t.Up(target, py);

        output.WriteLine($"pressed at 50, dragged to {fraction:P0} -> From={m.From} To={m.To}");
        Assert.Equal(expectedFrom, m.From, 1);
        Assert.Equal(expectedTo, m.To, 1);
    }

    /// <summary>The press itself writes nothing. Deciding on press would move a thumb the moment you
    /// touched a shut range, before you had said which way you meant.</summary>
    [Fact]
    public void A_press_that_never_moves_changes_neither_thumb()
    {
        var m = new Span(50, 50);
        using var t = new TestDoc(Html, "", m, width: 500, height: 200, components: true);
        var (px, py) = TestDoc.Center(Thumbs(t)[1]);

        t.Click(px, py);
        t.Up(px, py);

        Assert.Equal(50, m.From, 1);
        Assert.Equal(50, m.To, 1);
    }

    /// <summary>A twitch is not a direction. Below the threshold nothing moves, so a shaky press does
    /// not pick a thumb at random and drag it.</summary>
    [Fact]
    public void A_movement_too_small_to_read_as_a_direction_moves_nothing()
    {
        var m = new Span(50, 50);
        using var t = new TestDoc(Html, "", m, width: 500, height: 200, components: true);
        var (px, py) = TestDoc.Center(Thumbs(t)[1]);

        t.Click(px, py);
        t.Move(px + 1, py);              // one pixel
        Assert.Equal(50, m.From, 1);
        Assert.Equal(50, m.To, 1);

        t.Move(px + 40, py);             // …and now a real direction
        Assert.Equal(50, m.From, 1);
        Assert.True(m.To > 55, $"the high thumb should have followed right, got {m.To}");
    }

    /// <summary>Once apart, they behave as before — the deferral is only for the ambiguous press, and
    /// must not add a dead pixel to every ordinary drag.</summary>
    [Fact]
    public void Thumbs_that_are_not_coincident_drag_immediately_as_before()
    {
        var m = new Span(20, 80);
        using var t = new TestDoc(Html, "", m, width: 500, height: 200, components: true);
        var track = Track(t);
        var thumbs = Thumbs(t);

        // A press alone (no move) on a NON-coincident thumb still applies, as it always has: pressing
        // the track at a point is how you jump a slider to it.
        var (hx, hy) = TestDoc.Center(thumbs[1]);
        t.Click(hx, hy);
        t.Move(track.X + track.Width * 0.6f, hy);
        t.Up(track.X + track.Width * 0.6f, hy);

        Assert.Equal(60, m.To, 1);
        Assert.Equal(20, m.From, 1);
    }

    /// <summary>Coincident at the very bottom of the scale: pulling right must still open the range
    /// rather than fight a low thumb that has nowhere left to go.</summary>
    [Fact]
    public void Thumbs_coincident_at_the_minimum_still_open_to_the_right()
    {
        var m = new Span(0, 0);
        using var t = new TestDoc(Html, "", m, width: 500, height: 200, components: true);
        var track = Track(t);
        var (px, py) = TestDoc.Center(Thumbs(t)[1]);

        var target = track.X + track.Width * 0.7f;
        t.Click(px, py);
        t.Move(target, py);
        t.Up(target, py);

        output.WriteLine($"from 0/0 dragged right -> From={m.From} To={m.To}");
        Assert.Equal(0, m.From, 1);
        Assert.Equal(70, m.To, 1);
    }

    /// <summary>…and at the very top, pulling left.</summary>
    [Fact]
    public void Thumbs_coincident_at_the_maximum_still_open_to_the_left()
    {
        var m = new Span(100, 100);
        using var t = new TestDoc(Html, "", m, width: 500, height: 200, components: true);
        var track = Track(t);
        var (px, py) = TestDoc.Center(Thumbs(t)[1]);

        var target = track.X + track.Width * 0.3f;
        t.Click(px, py);
        t.Move(target, py);
        t.Up(target, py);

        output.WriteLine($"from 100/100 dragged left -> From={m.From} To={m.To}");
        Assert.Equal(30, m.From, 1);
        Assert.Equal(100, m.To, 1);
    }
}
