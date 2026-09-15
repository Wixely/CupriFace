namespace CupriFace.Interaction;

/// <summary>
/// Touch, scripted. The finger equivalent of <c>DispatchClick</c> — one call per gesture, on a
/// clock this class owns, so a tap, a scroll, a fling, a long press or a pinch can be written in a
/// headless test without a window, a device or a real second passing.
///
/// <code>
/// using var doc = CupriDocument.Load(html, css);
/// var touch = new TouchDriver(doc);
/// touch.Tap(box.X + box.W / 2f, box.Y + box.H / 2f);
/// touch.Swipe(200, 400, dy: -180);        // drag the content up: a scroll
/// touch.Fling(200, 400, dy: -180);        // …and let go moving: momentum
/// touch.LongPress(200, 300);              // the context menu
/// </code>
///
/// <para><b>Why this exists.</b> <see cref="TouchInput"/> already recognises every gesture the
/// engine has, deterministically, from raw finger events — but driving it means inventing a
/// monotonic clock, knowing the slop radius, knowing that a long press only fires when the host
/// calls <see cref="TouchInput.Tick"/> at the deadline, and knowing that momentum comes from the
/// velocity of the last 100 ms before the finger lifts. Every one of those is easy to get subtly
/// wrong, and getting one wrong produces a gesture that does nothing and looks like a bug in the
/// thing under test. The verbs below get them right.</para>
///
/// <para><b>The clock is scripted, never the wall.</b> <see cref="Now"/> advances only because a
/// gesture advanced it (or <see cref="Advance"/> was called), so the same script produces the same
/// events on a fast machine and a slow one. Nothing sleeps.</para>
///
/// <para><b>Momentum needs the frame loop.</b> A <see cref="Fling"/> hands the document a velocity;
/// the content then moves as <c>doc.Animate(t)</c> is called, exactly as it does under a host. A
/// fling followed by no animation looks like a fling that did nothing.</para>
///
/// <para>One finger. A second at the same time is a different question — what a pinch or a rotate
/// MEANS is the author's, not the engine's — so <see cref="Pinch"/> drives the raw pointer layer
/// (<c>CupriDocument.DispatchPointer</c>) that an author's own <c>OnPointer</c> handler sees.</para>
/// </summary>
/// <param name="document">The document to drive.</param>
/// <param name="options">Gesture tunables — slop, long-press duration, fling threshold. The
/// defaults are the ones a host uses; override them to test a gesture at its boundary.</param>
/// <param name="onFrame">Called after every finger event, for a caller that wants the layout a host
/// would have done between frames. Left out, this sequences events and nothing else, which is what
/// the engine's own <c>Dispatch*</c> calls do.</param>
public sealed class TouchDriver(CupriDocument document, TouchOptions? options = null, Action? onFrame = null)
{
    /// <summary>The recogniser being driven, for a gesture these verbs do not cover.</summary>
    public TouchInput Input { get; } = new(document) { Options = options ?? new TouchOptions() };

    /// <summary>The scripted clock, in seconds. Moves only when a gesture moves it.</summary>
    public double Now { get; private set; }

    private TouchOptions Opt => Input.Options;

    /// <summary>Let scripted time pass — between two taps that must NOT be a double tap, or while a
    /// press matures into a long one.</summary>
    public void Advance(double seconds) => Now += seconds;

    // ---- the raw three, on the scripted clock ---------------------------------------------------

    public bool Down(float x, float y) => Frame(Input.Down(x, y, Now));
    public bool Move(float x, float y) => Frame(Input.Move(x, y, Now));
    public bool Up(float x, float y) => Frame(Input.Up(x, y, Now));

    /// <summary>The platform took the gesture away (an ancestor stole the pointer, the window went
    /// away). Nothing is ever activated by a cancel — worth a test of its own for any control that
    /// acts on release.</summary>
    public bool Cancel() => Frame(Input.Cancel(Now));

    // ---- taps -----------------------------------------------------------------------------------

    /// <summary>A tap: press and release without moving. This is how touch ACTIVATES — the click
    /// lands at finger-up, not finger-down, so a press that turns into a scroll never presses the
    /// button it began on.</summary>
    public bool Tap(float x, float y)
    {
        var down = Down(x, y);
        Advance(0.05);
        return Up(x, y) || down;
    }

    /// <summary>Two taps inside the double-tap window and radius — the word-select gesture, and the
    /// one that escalates a field's click count to 2.</summary>
    public bool DoubleTap(float x, float y) => MultiTap(x, y, 2);

    /// <summary>Three taps — line select.</summary>
    public bool TripleTap(float x, float y) => MultiTap(x, y, 3);

    /// <summary><paramref name="count"/> taps in a row, each inside the previous one's window so the
    /// click count escalates rather than restarting.</summary>
    public bool MultiTap(float x, float y, int count)
    {
        var acted = false;
        for (var i = 0; i < count; i++)
        {
            if (i > 0) Advance(Opt.DoubleTapSeconds / 2);   // comfortably inside the window
            acted |= Tap(x, y);
        }
        return acted;
    }

    /// <summary>Press and hold until the context menu fires, then lift. The lift is deliberately
    /// included and deliberately does nothing: the long press already acted, and a host that
    /// forgets to swallow the release gets a menu AND a tap.</summary>
    public bool LongPress(float x, float y)
    {
        var acted = Down(x, y);
        Advance(Opt.LongPressSeconds + 0.01);
        acted |= Frame(Input.Tick(Now));     // what a host does when NextDeadline falls due
        Advance(0.02);
        Up(x, y);                            // swallowed by design
        return acted;
    }

    // ---- travel ---------------------------------------------------------------------------------

    /// <summary>
    /// Drag and let go while still: the content moves with the finger and STOPS when it lifts.
    /// This is the verb for scrolling to a known position, and for dragging a slider, a scrollbar
    /// thumb, a reorder handle or a split divider — on one of those the engine forwards the touch
    /// as a drag from the first contact rather than waiting to see if it is a tap.
    /// </summary>
    /// <param name="steps">Intermediate moves. More than one on purpose: a single jump would be one
    /// velocity sample, and several is what a finger actually produces.</param>
    public bool Swipe(float x, float y, float dx = 0, float dy = 0, int steps = 10)
    {
        var acted = Travel(x, y, dx, dy, steps, secondsPerStep: 0.016);
        // Long enough that the velocity ring (the last 100 ms) is empty at release, so lifting
        // ends the gesture instead of flinging it. "Let go while still" has to mean still.
        Advance(0.2);
        return Up(x + dx, y + dy) || acted;
    }

    /// <summary>
    /// Drag and let go while MOVING: the content keeps going. The velocity comes from the last
    /// 100 ms of travel, so this lifts the finger immediately rather than pausing.
    ///
    /// <para>The movement afterwards is the frame loop's: call <c>doc.Animate(t)</c> with a rising
    /// t, or <c>doc.Settle(...)</c>, or the content will sit exactly where the finger left it.</para>
    /// </summary>
    public bool Fling(float x, float y, float dx = 0, float dy = 0, int steps = 6)
    {
        var acted = Travel(x, y, dx, dy, steps, secondsPerStep: 0.008);   // fast: ~125 px per 8 ms
        return Up(x + dx, y + dy) || acted;
    }

    private bool Travel(float x, float y, float dx, float dy, int steps, double secondsPerStep)
    {
        if (steps < 1) steps = 1;
        var acted = Down(x, y);
        for (var i = 1; i <= steps; i++)
        {
            Advance(secondsPerStep);
            acted |= Move(x + dx * i / steps, y + dy * i / steps);
        }
        return acted;
    }

    // ---- two fingers ----------------------------------------------------------------------------

    /// <summary>
    /// Two fingers moving apart or together, centred on a point. Drives the RAW pointer layer
    /// (<c>CupriDocument.DispatchPointer</c>) rather than the gesture recogniser, because the engine
    /// deliberately does not decide what a pinch means — an author's <c>OnPointer</c> handler gets
    /// every pointer the element holds and computes zoom, or rotation, or neither.
    /// </summary>
    /// <param name="gapFrom">Distance between the fingers at the start, in logical px.</param>
    /// <param name="gapTo">Distance at the end. Larger opens the pinch, smaller closes it.</param>
    public bool Pinch(float centerX, float centerY, float gapFrom, float gapTo, int steps = 6, bool vertical = false)
    {
        if (steps < 1) steps = 1;
        bool At(int id, PointerPhase phase, float gap, int sign)
        {
            var half = gap / 2f * sign;
            return document.DispatchPointer(id, phase,
                vertical ? centerX : centerX + half,
                vertical ? centerY + half : centerY);
        }

        var acted = At(1, PointerPhase.Down, gapFrom, -1);
        acted |= At(2, PointerPhase.Down, gapFrom, +1);
        Frame(true);

        for (var i = 1; i <= steps; i++)
        {
            var gap = gapFrom + (gapTo - gapFrom) * i / steps;
            Advance(0.016);
            acted |= At(1, PointerPhase.Move, gap, -1);
            acted |= At(2, PointerPhase.Move, gap, +1);
            Frame(true);
        }

        acted |= At(1, PointerPhase.Up, gapTo, -1);
        acted |= At(2, PointerPhase.Up, gapTo, +1);
        Frame(true);
        return acted;
    }

    private bool Frame(bool acted)
    {
        onFrame?.Invoke();
        return acted;
    }
}
