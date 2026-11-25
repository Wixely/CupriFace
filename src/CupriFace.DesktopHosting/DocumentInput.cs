using CupriFace.Interaction;

namespace CupriFace.Hosting;

/// <summary>
/// How a desktop window's input becomes a document's — shared by the main window and every window
/// an app opens beside it, so a click, a Ctrl+C or a wheel notch means the same thing in all of them.
///
/// <para>Compiled into the desktop host AND the tests as source (DesktopHosting.props), for the same
/// reason HostScale is: the routing is worth testing headless, and a test reference to the Shell
/// would load SDL into every test run.</para>
/// </summary>
internal static class DocumentInput
{
    // Raw-pointer elements get first refusal so a desktop mouse can drive the same captured hold /
    // drag interactions as touch. Everything else keeps the ordinary click/hover/drag path.
    //
    // The pointer id is a PARAMETER rather than the constant 0 it used to be, because a finger is
    // not the mouse and several can be down at once (#143). The mouse keeps 0 for ever; fingers get
    // 1 upwards from the window. Passing 0 for a finger would make two fingers one pointer, and
    // capture would then be handed back and forth between them.
    // EVERY phase goes through DispatchPointer first — including a pointer nothing owns, and
    // including the lift. That is not tidiness: an uncaptured pointer is exactly what the engine's
    // page-zoom tracker follows, and the ONLY thing that retires a finger from that set is seeing
    // its Up. Routing an uncaptured lift straight to the single-pointer path (which is what the
    // capture check used to do) leaves the finger on the page's books for ever. Two taps then look
    // like two fingers, the next press starts a "pinch" against a meaningless baseline, and the
    // page zooms away under the user — measured on a Steam Deck as "any kind of drag, even
    // accidental, massively zooms in". It bit the mouse too: a click left pointer 0 on the books,
    // so one later finger was enough to make a phantom pair.
    //
    // When the engine declines, the ordinary click/hover/drag path still runs, so nothing that
    // worked before changes.
    public static bool PointerDown(CupriDocument doc, float x, float y, int clickCount, int pointerId = 0) =>
        doc.DispatchPointer(pointerId, PointerPhase.Down, x, y) || doc.DispatchClick(x, y, clickCount);

    public static bool PointerMove(CupriDocument doc, float x, float y, int pointerId = 0) =>
        doc.DispatchPointer(pointerId, PointerPhase.Move, x, y) || doc.DispatchPointerMove(x, y);

    public static bool PointerUp(CupriDocument doc, float x, float y, int pointerId = 0) =>
        doc.DispatchPointer(pointerId, PointerPhase.Up, x, y) || doc.DispatchPointerUp(x, y);

    /// <summary>
    /// One finger from the SDL window (#143).
    ///
    /// <para>A tap goes through the same door a click does — raw pointer first, then the ordinary
    /// click path — so a control that works with a mouse works with a finger and nothing needs a
    /// touch-specific branch. What differs is the id, and that only the FIRST finger drives hover
    /// and the cursor: a second finger is part of a gesture, not a second mouse, and moving the
    /// cursor to it would fight the first.</para>
    /// </summary>
    public static bool Finger(CupriDocument doc, int pointerId, PointerPhase phase, float x, float y) =>
        phase switch
        {
            // A finger taps (touch-adjusted); only the mouse clicks exactly where it points.
            PointerPhase.Down => doc.DispatchPointer(pointerId, PointerPhase.Down, x, y) || doc.DispatchTap(x, y),
            PointerPhase.Move => PointerMove(doc, x, y, pointerId),
            _ => PointerUp(doc, x, y, pointerId),
        };

    /// <summary>A wheel notch: Ctrl+wheel is zoom — one ladder rung per notch, as every browser has
    /// it, anchored at the pointer because a wheel zoom HAS a pointer. A plain wheel scrolls (wheel up
    /// scrolls up). The split lives in the host so the engine never learns chord conventions.</summary>
    public static bool Wheel(CupriDocument doc, float x, float y, float notches, KeyMods mods)
    {
        if (!mods.HasFlag(KeyMods.Ctrl)) return doc.DispatchWheel(x, y, -notches * 50f);
        if (notches > 0) doc.ZoomIn(x, y);
        else if (notches < 0) doc.ZoomOut(x, y);
        return true;
    }

    // Text shortcuts common to both hosts. The engine owns selection/editing; the host owns the
    // OS clipboard (get/set passed in) — keeping the engine free of any platform clipboard code.
    public static void Shortcut(CupriDocument doc, char ch, KeyMods mods, Func<string?> getClip, Action<string> setClip)
    {
        switch (ch)
        {
            case 'a': doc.DispatchKey(null, EditKey.SelectAll); break;
            case 'c': if (doc.CopySelection() is { } cp) setClip(cp); break;
            case 'x': if (doc.CutSelection() is { } ct) setClip(ct); break;
            case 'v': if (getClip() is { Length: > 0 } pv) doc.DispatchKey(pv, EditKey.None); break;
            case 'z': if (mods.HasFlag(KeyMods.Shift)) doc.Redo(); else doc.Undo(); break; // Ctrl+Shift+Z = redo
            case 'y': doc.Redo(); break;
            // Page zoom, browser keys. The chrome owns these the way a browser does — they never
            // reach the app's OnShortcut. (The windows only send =/-/0 as chords for this purpose.)
            case '=': doc.ZoomIn(); break;
            case '-': doc.ZoomOut(); break;
            case '0': doc.ZoomReset(); break;
            // Anything else is the app's own shortcut (doc.OnShortcut) — e.g. Ctrl+K opening a command
            // palette. The web host has always forwarded these via KeyChord; the desktop hosts dropped
            // them, so a documented feature only worked in the browser.
            default: doc.DispatchKey(ch.ToString(), EditKey.None, mods); break;
        }
    }

    // A context-menu command routes through the SAME clipboard seam as the keyboard shortcuts.
    public static void ContextAction(CupriDocument doc, ContextCommand cmd, Func<string?> getClip, Action<string> setClip)
    {
        switch (cmd)
        {
            case ContextCommand.Cut: if (doc.CutSelection() is { } ct) setClip(ct); break;
            case ContextCommand.Copy: if (doc.CopySelection() is { } cp) setClip(cp); break;
            case ContextCommand.Paste: if (getClip() is { Length: > 0 } pv) doc.DispatchKey(pv, EditKey.None); break;
            case ContextCommand.SelectAll: doc.DispatchKey(null, EditKey.SelectAll); break;
        }
    }
}
