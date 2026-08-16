using CupriFace.Interaction;
using Xunit;

namespace CupriFace.Tests;

/// <summary>
/// The author seam for gestures the engine deliberately doesn't implement. The engine routes
/// pointers and guarantees capture; what two fingers MEAN is the author's decision.
/// </summary>
public class MultiPointerTests
{
    private const string Html = "<body><div class='page'><div class='tile' data-gesture='photo'>x</div></div></body>";
    private const string Css = """
        body { margin:0 }
        .page { width:200px; height:400px; overflow:scroll; }
        .tile { width:200px; height:200px; }
        .filler { height:600px; }
        """;

    [Fact]
    public void An_element_that_opted_in_receives_raw_pointers_and_captures_them()
    {
        using var doc = CupriDocument.Load(Html, Css);
        var seen = new List<(int Id, PointerPhase Phase, int Count)>();
        doc.OnPointer("data-gesture", e => { seen.Add((e.Id, e.Phase, e.Pointers.Count)); return true; });
        doc.BuildFrame(300, 400);

        Assert.True(doc.DispatchPointer(1, PointerPhase.Down, 50, 50));
        Assert.True(doc.IsPointerCaptured(1));

        Assert.True(doc.DispatchPointer(2, PointerPhase.Down, 150, 150));   // a second finger, same tile
        Assert.True(doc.DispatchPointer(1, PointerPhase.Move, 60, 60));
        Assert.True(doc.DispatchPointer(2, PointerPhase.Up, 150, 150));
        Assert.False(doc.IsPointerCaptured(2));
        Assert.True(doc.IsPointerCaptured(1));                              // the other finger stays

        Assert.Equal(PointerPhase.Down, seen[0].Phase);
        Assert.Equal(1, seen[0].Count);                                     // one finger on the tile
        Assert.Equal(2, seen[1].Count);                                     // then two
        Assert.Equal(2, seen[2].Count);                                     // the move still sees both
    }

    [Fact]
    public void Two_fingers_give_an_author_everything_a_pinch_needs()
    {
        // The engine computes no gesture. It hands over the pointer set, and this is what an author
        // does with it — the arithmetic for scale is theirs, and so is deciding it means "zoom".
        using var doc = CupriDocument.Load(Html, Css);
        float? scale = null;
        var startSpan = 0f;
        doc.OnPointer("data-gesture", e =>
        {
            if (e.Pointers.Count < 2) return true;
            var span = MathF.Sqrt(MathF.Pow(e.Pointers[1].X - e.Pointers[0].X, 2)
                                + MathF.Pow(e.Pointers[1].Y - e.Pointers[0].Y, 2));
            if (e.Phase == PointerPhase.Down) startSpan = span;
            else scale = span / startSpan;
            return true;
        });
        doc.BuildFrame(300, 400);

        doc.DispatchPointer(1, PointerPhase.Down, 90, 100);
        doc.DispatchPointer(2, PointerPhase.Down, 110, 100);    // 20px apart
        doc.DispatchPointer(2, PointerPhase.Move, 130, 100);    // now 40px apart

        Assert.NotNull(scale);
        Assert.Equal(2f, scale!.Value, 2);                      // pinched to double
    }

    [Fact]
    public void A_captured_finger_never_scrolls_the_page_underneath()
    {
        // The reason capture exists. Without it, dragging a photo inside a scroller would scroll the
        // scroller too, and the engine would be fighting the author's gesture.
        using var doc = CupriDocument.Load(
            "<body><div class='page'><div class='tile' data-gesture='photo'>x</div><div class='filler'>y</div></div></body>",
            Css);
        doc.OnPointer("data-gesture", _ => true);
        doc.BuildFrame(300, 400);

        var page = TestDoc.Find(doc.Root, n => n.Element?.ClassList.Contains("page") == true)!;
        Assert.True(page.IsScrollable, "the page must be scrollable for this test to mean anything");

        var touch = new TouchInput(doc);
        // The host's rule: a captured pointer never reaches the recognizer.
        Assert.True(doc.DispatchPointer(1, PointerPhase.Down, 100, 100));
        for (var i = 1; i <= 8; i++) doc.DispatchPointer(1, PointerPhase.Move, 100, 100 - i * 15);

        Assert.Equal(0f, page.ScrollY, 1);                      // the page stayed exactly where it was
        Assert.True(doc.HasCapturedPointers);
    }

    [Fact]
    public void A_pointer_nobody_claimed_falls_through_to_the_ordinary_gesture()
    {
        using var doc = CupriDocument.Load(
            "<body><div class='page'><div class='tile'>x</div><div class='filler'>y</div></div></body>", Css);
        doc.OnPointer("data-gesture", _ => true);               // registered, but no element opts in
        doc.BuildFrame(300, 400);

        Assert.False(doc.DispatchPointer(1, PointerPhase.Down, 100, 100));
        Assert.False(doc.HasCapturedPointers);

        var touch = new TouchInput(doc);                         // so the recognizer takes it
        touch.Down(100, 100, 0);
        for (var i = 1; i <= 8; i++) touch.Move(100, 100 - i * 15, i * 0.01);

        var page = TestDoc.Find(doc.Root, n => n.Element?.ClassList.Contains("page") == true)!;
        Assert.True(page.ScrollY > 50, $"the page should have scrolled, but sits at {page.ScrollY:F0}");
    }

    [Fact]
    public void Declining_a_pointer_on_down_leaves_it_to_the_engine()
    {
        using var doc = CupriDocument.Load(Html, Css);
        doc.OnPointer("data-gesture", e => e.Pointers.Count >= 2);   // only interested in two fingers
        doc.BuildFrame(300, 400);

        Assert.False(doc.DispatchPointer(1, PointerPhase.Down, 50, 50));
        Assert.False(doc.IsPointerCaptured(1));
    }

    [Fact]
    public void Cancelling_unwinds_every_captured_gesture()
    {
        using var doc = CupriDocument.Load(Html, Css);
        var cancelled = new List<int>();
        doc.OnPointer("data-gesture", e =>
        {
            if (e.Phase == PointerPhase.Cancel) cancelled.Add(e.Id);
            return true;
        });
        doc.BuildFrame(300, 400);

        doc.DispatchPointer(1, PointerPhase.Down, 50, 50);
        doc.DispatchPointer(2, PointerPhase.Down, 60, 60);
        doc.CancelPointers();

        Assert.Equal(new[] { 1, 2 }, cancelled.OrderBy(i => i));
        Assert.False(doc.HasCapturedPointers);
    }
}
