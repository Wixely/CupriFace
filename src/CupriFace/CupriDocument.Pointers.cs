using AngleSharp.Dom;
using CupriFace.Interaction;

namespace CupriFace;

/// <summary>
/// Multi-pointer input. The engine's own gestures — tap, scroll, fling, long-press, drag surfaces —
/// are single-pointer by design, and the built-in components stay that way so they keep their
/// keyboard and screen-reader behaviour. This is the seam for everything else: an author who wants
/// pinch-to-zoom, two-finger rotate, a collage tile that scales under two fingers, or simply two
/// sliders moved at once gets the raw pointers and decides what they mean.
///
/// The model is the web's, not a bespoke one: <b>a pointer is captured by a target on down and
/// stays with it</b> until it lifts. Capture is what keeps a gesture from fighting the scroller
/// underneath — once an element owns a finger, the recognizer never sees it — which is why there is
/// no <c>touch-action</c> arbitration here yet. It would be solving a problem capture already
/// solved.
///
/// Accessibility is explicitly the author's business here. A pinch has no keyboard equivalent and
/// no screen-reader affordance unless someone writes one; the engine does not pretend otherwise,
/// and TOOLBOX says so plainly.
/// </summary>
public sealed partial class CupriDocument
{
    private readonly List<(string Attribute, Func<MultiPointerEvent, bool> Handler)> _pointerHandlers = new();

    // Captured pointers, keyed by pointer id → the STRUCTURAL PATH of the element that owns it.
    // A path, not a node: the tree is rebuilt constantly (every keystroke), and a node reference
    // captured on finger-down would dangle before the finger lifts.
    private readonly Dictionary<int, (string Path, string Attribute)> _captured = new();
    private readonly Dictionary<int, (float X, float Y)> _active = new();

    /// <summary>Register a handler for elements carrying <paramref name="dataAttribute"/>. Shaped
    /// like <see cref="OnAction"/>: the attribute is the opt-in, so nothing becomes multi-touch by
    /// accident. Returning true consumes the pointer for that element (it is captured); returning
    /// false on the DOWN phase declines, and the pointer falls through to the ordinary gesture
    /// recognizer as if the handler were not there.</summary>
    public CupriDocument OnPointer(string dataAttribute, Func<MultiPointerEvent, bool> handler)
    {
        _pointerHandlers.Add((dataAttribute, handler));
        return this;
    }

    /// <summary>True while any pointer is captured — the host's cue that its single-pointer gesture
    /// recognizer should stay out of the way.</summary>
    public bool HasCapturedPointers => _captured.Count > 0;

    /// <summary>Whether this particular pointer belongs to an element rather than to the engine.</summary>
    public bool IsPointerCaptured(int pointerId) => _captured.ContainsKey(pointerId);

    /// <summary>Feed one pointer. Returns true when an element owns it — the caller must then NOT
    /// give that pointer to the ordinary touch recognizer.</summary>
    public bool DispatchPointer(int pointerId, PointerPhase phase, float x, float y)
    {
        EnsureLaidOut();

        if (phase is PointerPhase.Down)
        {
            _active[pointerId] = (x, y);
            var (node, attribute, handler) = FindPointerTarget(x, y);
            if (node is null) { _active.Remove(pointerId); return false; }   // nobody opted in here

            // The handler may decline on down (a tile that only reacts to a SECOND finger, say),
            // in which case the pointer is never captured and the recognizer takes it.
            var path = PathOf(node);
            // Capture BEFORE invoking, so the handler's view of "pointers on this element" already
            // includes the one that just arrived.
            _captured[pointerId] = (path, attribute);
            if (Invoke(handler, node.Element!, attribute, path, pointerId, phase, x, y)) return true;
            _captured.Remove(pointerId);                 // declined: the recognizer may have it
            _active.Remove(pointerId);
            return false;
        }

        if (!_captured.TryGetValue(pointerId, out var capture)) return false;

        _active[pointerId] = (x, y);
        if (NodeAtPath(capture.Path)?.Element is { } el && FindHandler(capture.Attribute) is { } h)
            Invoke(h, el, capture.Attribute, capture.Path, pointerId, phase, x, y);

        if (phase is PointerPhase.Up or PointerPhase.Cancel)
        {
            _captured.Remove(pointerId);
            _active.Remove(pointerId);
        }
        // Consumed whatever the handler answered: a captured pointer belongs to that element until
        // it lifts, or one indecisive frame would hand a half-finished gesture to the scroller.
        return true;
    }

    /// <summary>Drop every captured pointer — the window lost focus, the app was backgrounded, the
    /// surface went away. Handlers hear Cancel so a half-finished gesture can unwind itself.</summary>
    public void CancelPointers()
    {
        foreach (var (id, capture) in _captured.ToList())
        {
            var pos = _active.TryGetValue(id, out var p) ? p : (X: 0f, Y: 0f);
            if (NodeAtPath(capture.Path)?.Element is { } el && FindHandler(capture.Attribute) is { } h)
                Invoke(h, el, capture.Attribute, capture.Path, id, PointerPhase.Cancel, pos.X, pos.Y);
        }
        _captured.Clear();
        _active.Clear();
    }

    private Func<MultiPointerEvent, bool>? FindHandler(string attribute)
    {
        foreach (var (attr, handler) in _pointerHandlers)
            if (attr == attribute) return handler;
        return null;
    }

    private (Dom.RenderNode? Node, string Attribute, Func<MultiPointerEvent, bool> Handler) FindPointerTarget(float x, float y)
    {
        for (var n = HitTesting.HitTest(_root, x, y); n is not null; n = n.Parent)
        {
            if (n.Element is not { } el) continue;
            foreach (var (attr, handler) in _pointerHandlers)
                if (el.HasAttribute(attr)) return (n, attr, handler);
        }
        return (null, "", _ => false);
    }

    private bool Invoke(Func<MultiPointerEvent, bool> handler, IElement element, string attribute,
                        string path, int pointerId, PointerPhase phase, float x, float y)
    {
        // Every pointer currently held BY THIS ELEMENT — what a pinch or a rotate is computed from.
        var mine = new List<CupriPointer>();
        foreach (var (id, pos) in _active)
            if (_captured.TryGetValue(id, out var c) && c.Path == path)
                mine.Add(new CupriPointer(id, pos.X, pos.Y));
        mine.Sort((a, b) => a.Id.CompareTo(b.Id));

        return Bump(handler(new MultiPointerEvent(
            pointerId, phase, x, y, mine, element, element.GetAttribute(attribute) ?? "", _model)));
    }
}
