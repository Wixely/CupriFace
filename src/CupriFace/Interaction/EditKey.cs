namespace CupriFace.Interaction;

/// <summary>Non-printable keys delivered to the document: text editing plus keyboard focus.</summary>
public enum EditKey
{
    None,
    Backspace,
    Delete,
    Left,
    Right,
    Home,
    End,
    Enter,
    Up,         // arrow up (group / slider nav)
    Down,       // arrow down (group / slider nav)
    Tab,        // move keyboard focus to the next control
    ShiftTab,   // move keyboard focus to the previous control
    Space,      // activate the focused (non-text) control
    Escape,     // close the top-most open overlay (or blur a field)
    SelectAll,  // Ctrl+A — select all text in the focused field

    // The function keys. The engine gives none of them a meaning, so they reach an app's own key
    // handling untouched — with one exception in a DEBUG desktop build, where the dev tools take
    // the key CupriApp.DevToolsShortcut names (F12 unless the app says otherwise). Appended rather
    // than inserted so no existing value is renumbered.
    F1, F2, F3, F4, F5, F6, F7, F8, F9, F10, F11, F12,
}

/// <summary>A key plus the modifiers held with it — what a shortcut is bound to, for example
/// <see cref="CupriApp.DevToolsShortcut"/>.</summary>
public readonly record struct KeyChord(EditKey Key, KeyMods Mods = KeyMods.None)
{
    /// <summary>Whether a key press is this chord. Modifiers must match exactly, so a chord of
    /// plain F12 is not triggered by Ctrl+F12 — an app can bind that one to something else.</summary>
    public bool Matches(EditKey key, KeyMods mods) => key == Key && mods == Mods;

    public override string ToString() =>
        (Mods.HasFlag(KeyMods.Ctrl) ? "Ctrl+" : "") + (Mods.HasFlag(KeyMods.Shift) ? "Shift+" : "") + Key;
}

/// <summary>Modifier keys held during a key/pointer event (for text selection + shortcuts).</summary>
[Flags]
public enum KeyMods
{
    None = 0,
    Shift = 1,  // extend the selection instead of collapsing it
    Ctrl = 2,   // word-wise movement/delete (Cmd on macOS maps here too)
}
