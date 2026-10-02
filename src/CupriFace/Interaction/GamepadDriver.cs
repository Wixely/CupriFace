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
/// <para>Three things that bite if you drive
/// <see cref="CupriDocument.MoveFocus(NavigationDirection)"/> by hand instead. <b>A held stick moves
/// once</b>, not once per frame — holding right should not race across the panel, and
/// <see cref="Stick"/> enforces that by edge. <b>A stick inside the deadzone is centred</b>, so the
/// resting drift every real controller has does not creep the selection. And <b>a diagonal push picks
/// one axis</b>, the dominant one, because a selection that moves two steps for one flick feels
/// broken.</para>
/// </summary>
/// <param name="doc">The document to move focus in.</param>
/// <param name="deadzone">How far the stick travels before it counts as pushed, 0..1.</param>
/// <param name="onFrame">Called after every press that changed something, for a caller that wants
/// the repaint a host would do anyway. Optional — see the remarks.</param>
public sealed class GamepadDriver(CupriDocument doc, float deadzone = 0.5f, Action? onFrame = null)
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

    /// <summary>A / OK / Enter — activate the focused control, exactly as the keyboard does, so a
    /// controller and a keyboard cannot come to disagree about what "activate" means.</summary>
    public bool Confirm() => Frame(doc.DispatchKey("", EditKey.Enter, KeyMods.None));

    /// <summary>The dominant axis past the deadzone, or null when the stick is at rest. Dominant
    /// rather than both: one flick is one move, and a stick is never perfectly on an axis.</summary>
    private NavigationDirection? Resolve(float x, float y)
    {
        if (MathF.Abs(x) < deadzone && MathF.Abs(y) < deadzone) return null;
        return MathF.Abs(x) >= MathF.Abs(y)
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
