namespace CupriFace.Interaction;

/// <summary>
/// Where an input event came from, as far as the engine can honestly tell.
///
/// <para>The distinction that matters is <see cref="HostGamepad"/> versus <see cref="Gamepad"/>: the
/// first is the host's own controller wiring having found a pad and fed it in, the second is an
/// application's input source of its own calling
/// <see cref="GamepadDriver.PostStick"/>. They are indistinguishable from inside the document —
/// both end in the same shared driver — which is exactly why an integration debugging "nothing
/// moves" could not tell "the host never found the pad" from "the host found it and the engine
/// ignored it". The host paths say which they are.</para>
/// </summary>
public enum InputSource
{
    /// <summary>A direct engine call that no device is attributable to — application code calling
    /// <see cref="CupriDocument.MoveFocus(NavigationDirection)"/> or
    /// <see cref="CupriDocument.Activate"/> itself.</summary>
    Application,

    /// <summary>A keystroke, via <see cref="CupriDocument.DispatchKey"/>.</summary>
    Keyboard,

    /// <summary>A mouse or stylus, via click, wheel or context menu.</summary>
    Pointer,

    /// <summary>A controller, through the APPLICATION's own input source — its own reader calling
    /// <see cref="GamepadDriver.PostStick"/> and friends.</summary>
    Gamepad,

    /// <summary>A controller, through the HOST's own wiring — GLFW or SDL on desktop, Android's
    /// motion events, the browser's Gamepad API. Silenced by
    /// <see cref="CupriDocument.HostGamepadNavigation"/>.</summary>
    HostGamepad,
}

/// <summary>What the engine did with an input event. Reported rather than inferred: a caller cannot
/// work this out from the returned bool, which says only that SOMETHING changed.</summary>
public enum InputAction
{
    /// <summary>Nothing. For a key, this plus <c>Handled = false</c> is the signature of an event
    /// that reached the engine and meant nothing to it — which is a different bug from one that
    /// never arrived at all, and previously looked identical.</summary>
    None,

    /// <summary>The selection moved. <see cref="InputObservation.Target"/> is where it landed.</summary>
    Navigate,

    /// <summary>A control was activated — Enter, Space, a pad's confirm, a click.</summary>
    Activate,

    /// <summary>Text went into a focused field.</summary>
    Text,

    /// <summary>A registered <see cref="CupriDocument.OnShortcut(KeyMods, string, Action)"/> handler ran.</summary>
    Shortcut,

    /// <summary>A view scrolled.</summary>
    Scroll,

    /// <summary>Swallowed deliberately — by <see cref="CupriDocument.KeyboardNavigation"/>,
    /// <see cref="CupriDocument.ArrowKeyNavigation"/>, or <see cref="CupriDocument.HostGamepadInput"/>
    /// turning away a capability of the host's pad. The engine was told not to act on this.
    /// <see cref="InputObservation.Route"/> distinguishes them: <see cref="InputRoute.Ignore"/> is a
    /// host pad capability that is switched off, and the event is left for the host to use.</summary>
    Swallowed,
}

/// <summary>
/// One input event, and what the engine made of it. Delivered to
/// <see cref="CupriDocument.InputObserved"/>.
///
/// <para><b>Why this exists.</b> An integration owns one half of its input path and the engine owns
/// the other, and the seam between them was invisible: every dispatch returns a single bool, so
/// "the engine never saw it", "the engine saw it and had no use for it" and "the engine deliberately
/// ignored it because I told it to" all look exactly the same from outside. Two separate
/// integrations have now lost time to that — one of them misattributing a bug in its own evdev code
/// to CupriFace — which is a diagnostic gap rather than a bug, and this closes it.</para>
///
/// <para>Treat it as a diagnostic, not a hook: it reports, it cannot veto, and the engine has
/// already acted by the time it is raised.</para>
/// </summary>
/// <param name="Source">Who delivered it.</param>
/// <param name="Input">The event itself, human-readable — a key name, a direction, a stick reading,
/// a click position. For reading, not for parsing.</param>
/// <param name="Action">What the engine did.</param>
/// <param name="Route">The routing policy in force for this kind of input. Anything but
/// <see cref="InputRoute.Navigate"/> is a deliberate silencing, and is the first thing to check when
/// input arrives and nothing happens.</param>
/// <param name="Handled">What the dispatch returned: whether anything changed that a host should
/// repaint for. Authoritative, where <paramref name="Action"/> is descriptive.</param>
/// <param name="Target">The accessible name of the control involved — where focus landed, or what
/// was activated. Named the same way the accessibility tree names it, so a report and a test agree.</param>
public readonly record struct InputObservation(
    InputSource Source,
    string Input,
    InputAction Action,
    InputRoute Route,
    bool Handled,
    string? Target)
{
    /// <summary>One line, for a log. This is the format <c>CUPRIFACE_KEY_DEBUG</c> writes.</summary>
    public override string ToString()
    {
        var target = Target is { Length: > 0 } ? $" \"{Target}\"" : "";
        var route = Route == InputRoute.Navigate ? "" : $" route={Route}";
        return $"{Source} {Input} -> {Action}{target}{route} {(Handled ? "handled" : "unhandled")}";
    }
}
