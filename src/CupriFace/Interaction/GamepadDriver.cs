namespace CupriFace.Interaction;

/// <summary>
/// A controller, scripted. One call per press, on no clock at all — nothing sleeps, nothing needs
/// hardware, and the same script produces the same moves on any machine.
///
/// <para>The counterpart to <see cref="TouchDriver"/>, and it exists for the same reason: a stick is
/// not a keyboard, and testing the thing by pretending it is one tests the wrong code. A thumbstick
/// delivers a continuous axis, so turning it into "one move left" needs a deadzone, an edge, and a
/// rule about what counts as returning to centre — and those are where the bugs are. This drives the
/// same path a real stick does rather than a parallel pretend one.</para>
///
/// <code>
/// var pad = new GamepadDriver(doc, onFrame: () => Render());
/// pad.Press(NavigationDirection.Down);   // a D-pad press: one move
/// pad.Stick(0.9f, 0f);                   // a stick pushed right: one move, then nothing
/// pad.Stick(0f, 0f);                     // …until it returns to centre
/// pad.Stick(0.9f, 0f);                   // and is pushed again
/// pad.Confirm();                         // A / OK — activates the focused control
/// </code>
///
/// <para><paramref name="onFrame"/> is a repaint hook, not a correctness one: a focus move is only
/// visible once something renders. Navigation itself does not need it —
/// <see cref="CupriDocument.MoveFocus(NavigationDirection)"/> lays the tree out on entry, because a
/// held D-pad autorepeats faster than a frame and a swallowed press is not an acceptable failure.</para>
///
/// <para><b>Call these on the thread that renders.</b> A press ends in a dispatch that rebuilds the
/// document's tree, so a reader thread calling <see cref="Stick"/> directly races layout and paint.
/// An input source of your own should use <see cref="PostStick"/> and friends, which marshal.</para>
///
/// <para>Three things that bite if you drive
/// <see cref="CupriDocument.MoveFocus(NavigationDirection)"/> by hand instead. <b>A held stick moves
/// once</b>, not once per frame — holding right should not race across the panel, and
/// <see cref="Stick"/> enforces that by edge. <b>A stick inside the deadzone is centred</b>, so the
/// resting drift every real controller has does not creep the selection. And <b>a diagonal push picks
/// one axis</b>, the dominant one, because a selection that moves two steps for one flick feels
/// broken.</para>
/// </summary>
/// <param name="doc">The document to move focus in.</param>
/// <param name="deadzone">How far the stick travels before it counts as pushed, 0..1. Left null it
/// follows <see cref="CupriDocument.GamepadDeadzone"/>, which is where an integrating app sets it —
/// a host builds the driver, so a value fixed here would be one the app could not reach. Pass a
/// value only to pin it, as a test does.</param>
/// <param name="onFrame">Called after every press that changed something, for a caller that wants
/// the repaint a host would do anyway. Optional — see the remarks.</param>
/// <param name="diagonals">Let the stick resolve to a corner as well as to an axis — eight sectors
/// rather than four. Left null it FOLLOWS <see cref="CupriDocument.DiagonalNavigation"/>, which is
/// what a host wants: the app has already said whether this UI has corners, and a host that had to
/// answer again could disagree with it. Pass a value only to pin the behaviour regardless, as a
/// test does.
/// <b>A stick needs no waiting period to do this</b>, unlike the keyboard: it reports a VECTOR, so
/// "down and right" arrives as one reading and the corner is simply what it says. The latency that
/// flag costs on a keyboard is the price of not having a vector, and it is not paid here.</param>
public sealed class GamepadDriver(CupriDocument doc, float? deadzone = null, Action? onFrame = null,
                                 bool? diagonals = null)
{
    private NavigationDirection? _held;

    /// <summary>A D-pad press: exactly one move. Digital, so no deadzone and no edge to track —
    /// a press is an event in a way a stick position is not.</summary>
    public bool Press(NavigationDirection direction) => Frame(doc.MoveFocus(direction));

    /// <summary>
    /// A thumbstick at (<paramref name="x"/>, <paramref name="y"/>), each −1..1 with y DOWN
    /// positive, matching the engine's coordinate space rather than a controller's.
    ///
    /// <para>Moves once when the stick crosses the deadzone into a new direction, and not again until
    /// it returns inside it or changes direction. Returns true when a move happened.</para>
    /// </summary>
    public bool Stick(float x, float y)
    {
        var direction = Resolve(x, y);
        if (direction is null) { _held = null; return false; }   // back to centre: re-arm
        if (direction == _held) return false;                     // still held: already moved
        _held = direction;
        return Frame(doc.MoveFocus(direction.Value));
    }

    /// <summary>Let go of the stick. The same as <c>Stick(0, 0)</c>, named for the times a test wants
    /// to say what it means.</summary>
    public void Release() => _held = null;

    /// <summary>
    /// A / OK — activate the focused control. The same thing Enter does, reached without going
    /// through the keyboard.
    ///
    /// <para>It used to be dispatched AS an Enter key, which was tidy until
    /// <see cref="CupriDocument.KeyboardNavigation"/> existed: an application that had said "I
    /// navigate, not you" then found its own confirm swallowed by its own setting, because nothing
    /// distinguished the pad from the keyboard it was borrowing. <see cref="Press"/> never broke,
    /// calling MoveFocus directly — and that asymmetry is what identified it.</para>
    /// </summary>
    public bool Confirm() => Frame(doc.Activate());

    // ---- from another thread -------------------------------------------------------------------

    /// <summary>
    /// <b>None of the members above are thread-safe</b>, and the reason is worth knowing rather than
    /// guessing at: each ends in a dispatch that REBUILDS the document's render tree. Called from a
    /// reader thread, that swaps the tree out from under layout and paint. There is no exception at
    /// the call site — just sporadic corruption somewhere else.
    ///
    /// <para>These three take the same input from any thread and run it on the one that renders, at
    /// the start of the next frame. They are what an input source of your own — a Linux evdev reader,
    /// a HID device, a pad over the network — should call.</para>
    ///
    /// <code>
    /// pad.PostStick(x, y);   // on the evdev thread
    /// </code>
    ///
    /// <para>They return nothing, necessarily: whether the selection moved is not known until that
    /// frame runs, and inventing an answer now would be a lie.</para>
    /// </summary>
    public void PostStick(float x, float y) => doc.Post(() => Stick(x, y));

    /// <inheritdoc cref="PostStick"/>
    public void PostPress(NavigationDirection direction) => doc.Post(() => Press(direction));

    /// <inheritdoc cref="PostStick"/>
    public void PostConfirm() => doc.Post(() => Confirm());

    /// <summary>
    /// Which way the stick is pointing, or null when it is at rest.
    ///
    /// <para>With <c>diagonals</c> off this is the DOMINANT axis — one flick is one move, and a stick
    /// is never perfectly on an axis, so a push 10° off vertical still means "up". With it on the
    /// circle is cut into eight equal 45° sectors instead of four 90° ones, which is what a D-pad
    /// does and what a player expects: a corner is claimed only when the push is genuinely nearer
    /// the corner than either axis, not whenever both axes happen to be off centre.</para>
    /// </summary>
    private NavigationDirection? Resolve(float x, float y)
    {
        var (ax, ay) = (MathF.Abs(x), MathF.Abs(y));
        var rest = deadzone ?? doc.GamepadDeadzone;   // read per call: the app may retune it live
        if (ax < rest && ay < rest) return null;

        // The 45° sector boundary sits where the smaller axis is tan(22.5°) of the larger.
        const float CornerRatio = 0.4142f;
        if ((diagonals ?? doc.DiagonalNavigation) && MathF.Min(ax, ay) >= MathF.Max(ax, ay) * CornerRatio)
            return (x < 0, y < 0) switch
            {
                (true, true) => NavigationDirection.UpLeft,
                (false, true) => NavigationDirection.UpRight,
                (true, false) => NavigationDirection.DownLeft,
                _ => NavigationDirection.DownRight,
            };

        return ax >= ay
            ? x < 0 ? NavigationDirection.Left : NavigationDirection.Right
            : y < 0 ? NavigationDirection.Up : NavigationDirection.Down;
    }

    // Let the caller repaint after anything that actually moved — and only then, so a press at an
    // edge that goes nowhere does not cost a frame.
    private bool Frame(bool changed)
    {
        if (changed) onFrame?.Invoke();
        return changed;
    }
}
