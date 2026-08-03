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
    Tab,        // move keyboard focus to the next control
    ShiftTab,   // move keyboard focus to the previous control
    Space,      // activate the focused (non-text) control
}
