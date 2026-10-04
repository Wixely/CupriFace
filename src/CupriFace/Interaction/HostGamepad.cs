namespace CupriFace.Interaction;

/// <summary>
/// Which parts of the HOST's own controller handling drive this document, as independent
/// capabilities rather than one switch.
///
/// <para>One switch could not say the thing applications actually needed. An app with an input
/// source of its own — a Linux evdev reader, a HID device, a pad over a network — usually owns
/// ONE capability rather than the whole controller: it reads the sticks itself because it needs
/// the raw axes for something else, and would still like the host's D-pad and buttons to work. The
/// boolean made that an all-or-nothing choice, so such an app switched the host off entirely and
/// then re-implemented the half it had not meant to take over.</para>
///
/// <code>
/// doc.HostGamepadInput = HostGamepad.Dpad | HostGamepad.Buttons;   // my reader owns the sticks
/// doc.HostGamepadInput = HostGamepad.None;                         // …and everything else too
/// </code>
///
/// <para>This is about the HOST's pad only. It never silences the keyboard — a person at a keyboard
/// is not the thing being arbitrated — and it never silences <see cref="CupriDocument.Gamepad"/>,
/// which is how the application's own source gets in.</para>
/// </summary>
[Flags]
public enum HostGamepad
{
    /// <summary>Nothing. The application's own input source is the only way in.</summary>
    None = 0,

    /// <summary>Thumbsticks.</summary>
    Stick = 1,

    /// <summary>The directional pad, and the hat that stands in for it.</summary>
    Dpad = 2,

    /// <summary>Face and shoulder buttons — confirm, back, and the rest.</summary>
    Buttons = 4,

    /// <summary>All of it. The default, and what the old boolean meant by true.</summary>
    All = Stick | Dpad | Buttons,
}
