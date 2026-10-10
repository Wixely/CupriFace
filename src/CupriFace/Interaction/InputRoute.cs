namespace CupriFace.Interaction;

/// <summary>
/// How the ARROW KEYS move focus.
///
/// <para>This replaces a boolean that had three meaningful states and could only express two — and
/// the one it could not express is the one an integration needed. <c>ArrowNavigation = false</c> read
/// as "arrows off" and meant "arrows move in document order", so an application that drove focus
/// itself found CupriFace still moving the selection underneath it, and worked around it by
/// registering handlers that did nothing.</para>
/// </summary>
public enum NavigationMode
{
    /// <summary>Arrows move focus to the next or previous control in DOCUMENT order — Tab by another
    /// name. The default, and what an ordinary application wants: in a form, "down" means the next
    /// field.</summary>
    Sequential,

    /// <summary>Arrows move to the nearest control in that DIRECTION — a D-pad. What a game wants,
    /// and what makes a controller and a keyboard agree about a panel.</summary>
    Spatial,

    /// <summary>Arrows do not move focus at all. For an application that decides what is selected
    /// itself — one driving focus through its own model and <c>AccessibilityFocus</c>, where any
    /// movement from the engine is a second opinion it did not ask for.
    ///
    /// <para>Control-level arrow behaviour is unaffected: a focused slider still nudges and a radio
    /// group still follows the ARIA pattern, because those belong to the control rather than to
    /// navigation.</para>
    /// </summary>
    Disabled,
}

/// <summary>
/// What CupriFace does with an input it could act on.
///
/// <para>The distinction between the two "no" answers is not pedantry: <c>DispatchKey</c> returns
/// whether the engine handled the key, and a host decides what to do next from that. The desktop host
/// exits fullscreen on an Escape the document did not take, so "ignore" and "consume" are visibly
/// different keys to press.</para>
/// </summary>
public enum InputRoute
{
    /// <summary>CupriFace acts on it, as it always has.</summary>
    Navigate,

    /// <summary>CupriFace does not act on it, but reports it as HANDLED — so the host treats it as
    /// spoken for and does not apply its own fallback. This is what an application means when it
    /// drives navigation itself and does not want the key leaking onward.</summary>
    Consume,

    /// <summary>CupriFace does not act on it and reports it as unhandled, leaving the host free to do
    /// whatever it would have done with a key nobody wanted.</summary>
    Ignore,
}

/// <summary>
/// When a MOUSE press turns into activation. Touch is unaffected: a finger has always activated on
/// release, because a press that becomes a scroll must not press what it began on.
/// </summary>
public enum PointerActivation
{
    /// <summary>On the press (the default, and what every release so far has done). The click has
    /// already happened by the time a drag could cancel it, so an application that needs a drag to
    /// take precedence intercepts the press itself.</summary>
    OnPress,

    /// <summary>On the release, and only if the press did not become a pan or a drag — a mouse
    /// down + up over the same control is the confirmed click. This is the model touch already
    /// uses, so a carousel behaves the same under a finger and under a mouse.</summary>
    OnRelease,
}
