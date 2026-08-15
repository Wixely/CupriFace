namespace CupriFace.Interaction;

/// <summary>Tunables for <see cref="TouchInput"/>. Distances are LOGICAL pixels (the same space
/// every Dispatch* call uses), times are seconds on whatever clock the host feeds in.</summary>
public sealed record TouchOptions
{
    /// <summary>Finger travel beyond which a press stops being a tap and becomes a scroll.</summary>
    public float SlopPx { get; init; } = 8f;
    /// <summary>Hold duration that turns a still press into a context menu.</summary>
    public double LongPressSeconds { get; init; } = 0.5;
    /// <summary>Window after a tap's finger-up in which the next tap escalates the click count.</summary>
    public double DoubleTapSeconds { get; init; } = 0.3;
    /// <summary>Radius around the previous tap within which the next one counts as a repeat.</summary>
    public float DoubleTapRadiusPx { get; init; } = 24f;
    /// <summary>Release velocity (px/s) below which lifting the finger just stops the scroll.</summary>
    public float MinFlingPxPerSec { get; init; } = 50f;
}

/// <summary>
/// The engine's touch gesture recognizer: raw finger events in, correct <c>Dispatch*</c> calls
/// out. It exists because of one desktop fact a finger cannot live with — <b>activation fires on
/// pointer-down</b> (<c>DispatchClick</c> IS the down event), so a scroll gesture that began on a
/// button would press the button. This class holds the down back until it knows what the gesture
/// is:
///
///   - a still, short press → the TAP: <c>DispatchClick</c> at finger-up (with double/triple-tap
///     click-count escalation feeding the existing word/line selection),
///   - travel beyond the slop → a SCROLL: per-move <c>DispatchWheel</c> (reusing the engine's
///     chaining and virtual-list re-windowing), with a velocity-tracked fling handed to
///     <see cref="CupriDocument.StartFling"/> at release,
///   - a still, long press → the CONTEXT MENU (<c>DispatchContextMenu</c>), as platforms do,
///   - a dedicated drag surface (slider, scrollbar thumb, reorder handle, split divider, resize
///     grip, table column edge) → forwarded IMMEDIATELY as a mouse-style drag: explicit grips
///     drag from the first touch on every platform.
///
/// Hover never happens: no code path here routes an idle move to the engine's hover update, which
/// is the whole of touch hover-suppression — structural, not a flag.
///
/// Deliberately deterministic: the host supplies every timestamp, and long-press firing is the
/// host calling <see cref="Tick"/> when <see cref="NextDeadline"/> falls due — so every behaviour
/// is testable headlessly with a scripted clock. NOT thread-safe; call it on the document's
/// thread, like every Dispatch*.
/// </summary>
public sealed class TouchInput(CupriDocument document)
{
    private enum Mode { Idle, Pending, Scrolling, Dragging, SwallowUp }

    public TouchOptions Options { get; init; } = new();

    private Mode _mode = Mode.Idle;
    private float _downX, _downY;
    private double _downT;
    private float _prevY;
    private string? _scrollPath;

    // Velocity ring: the last ~100 ms of (time, finger-y) — enough to measure release speed
    // without letting the start of a long drag pollute it.
    private readonly List<(double T, float Y)> _ring = new();

    // Double-tap escalation state (the previous TAP's release).
    private double _lastTapT = double.NegativeInfinity;
    private float _lastTapX, _lastTapY;
    private int _clicks = 1;

    /// <summary>When <see cref="Tick"/> next wants calling (long-press), or null. The host arms a
    /// timer; tests simply call <c>Tick</c> with a scripted time.</summary>
    public double? NextDeadline => _mode == Mode.Pending ? _downT + Options.LongPressSeconds : null;

    /// <summary>Finger down. Returns whether the document changed (repaint).</summary>
    public bool Down(float x, float y, double t)
    {
        // A finger landing mid-fling CATCHES the list: kill the momentum and treat the whole
        // gesture as scrolling — a catch-tap must never click what it happened to land on.
        if (document.FlingActive)
        {
            document.StopFling();
            EnterScroll(x, y, t);
            return false;
        }

        _downX = x; _downY = y; _downT = t;

        if (document.ClassifyPress(x, y) == CupriDocument.PressKind.DragSurface)
        {
            // Dedicated drag affordances drag from the FIRST touch — this is the one case where
            // the mouse semantics (activation on down) are also the touch semantics.
            _mode = Mode.Dragging;
            return document.DispatchClick(x, y);
        }

        // Everything else defers: press feedback now, the decision later.
        _mode = Mode.Pending;
        _clicks = t - _lastTapT <= Options.DoubleTapSeconds
                  && MathF.Abs(x - _lastTapX) <= Options.DoubleTapRadiusPx
                  && MathF.Abs(y - _lastTapY) <= Options.DoubleTapRadiusPx
            ? Math.Min(_clicks + 1, 3) : 1;
        return document.SetPressed(x, y);
    }

    /// <summary>Finger moved. Returns whether the document changed.</summary>
    public bool Move(float x, float y, double t)
    {
        switch (_mode)
        {
            case Mode.Dragging:
                return document.DispatchPointerMove(x, y);

            case Mode.Pending:
                if (MathF.Abs(x - _downX) <= Options.SlopPx && MathF.Abs(y - _downY) <= Options.SlopPx)
                    return false;                       // still a candidate tap — hold
                var cleared = document.ClearPressed();  // it's a scroll: drop :active, never click
                EnterScroll(_downX, _downY, _downT);
                return Scroll(y, t) || cleared;         // first delta includes the pre-slop travel

            case Mode.Scrolling:
                return Scroll(y, t);

            default:
                return false;
        }
    }

    /// <summary>Finger up. Returns whether the document changed.</summary>
    public bool Up(float x, float y, double t)
    {
        var mode = _mode;
        _mode = Mode.Idle;
        switch (mode)
        {
            case Mode.Dragging:
                return document.DispatchPointerUp(x, y);

            case Mode.Pending:
            {
                // THE TAP — this is where touch activation actually happens. Down coordinates,
                // not up: the finger may have wobbled inside the slop, and the user pressed what
                // they first touched.
                _lastTapT = t; _lastTapX = _downX; _lastTapY = _downY;
                var clicked = document.DispatchClick(_downX, _downY, _clicks);
                var upped = document.DispatchPointerUp(_downX, _downY);
                return clicked || upped;
            }

            case Mode.Scrolling:
            {
                Prune(t);
                if (_scrollPath is not null && _ring.Count >= 2)
                {
                    // Release velocity over the last ≤100 ms of travel, in ScrollY terms: the
                    // finger moving UP the screen (y shrinking) scrolls content DOWN (positive),
                    // matching DispatchWheel's (prev − cur) convention.
                    var (t0, y0) = _ring[0];
                    var (t1, y1) = _ring[^1];
                    var dt = t1 - t0;
                    if (dt > 0.001)
                    {
                        var v = (y0 - y1) / (float)dt;   // px/s
                        if (MathF.Abs(v) >= Options.MinFlingPxPerSec)
                            return document.StartFling(_scrollPath, v);
                    }
                }
                return false;
            }

            default:
                return false;                            // SwallowUp: the long-press already acted
        }
    }

    /// <summary>Gesture cancelled by the platform (an ancestor stole the pointer, the app lost the
    /// window). Nothing is ever activated by a cancel.</summary>
    public bool Cancel(double t)
    {
        var mode = _mode;
        _mode = Mode.Idle;
        return mode switch
        {
            Mode.Dragging => document.DispatchPointerUp(_downX, _downY),   // end any engine drag
            Mode.Pending => document.ClearPressed(),
            _ => false,
        };
    }

    /// <summary>Fire due deadlines: a still press held past <see cref="TouchOptions.LongPressSeconds"/>
    /// becomes the context menu, and the eventual finger-up is swallowed.</summary>
    public bool Tick(double t)
    {
        if (_mode != Mode.Pending || t < _downT + Options.LongPressSeconds) return false;
        _mode = Mode.SwallowUp;
        var cleared = document.ClearPressed();
        return document.DispatchContextMenu(_downX, _downY) || cleared;
    }

    // ---- internals ----------------------------------------------------------------------------

    private void EnterScroll(float x, float y, double t)
    {
        _mode = Mode.Scrolling;
        _scrollPath = document.ScrollTargetAt(x, y);
        _prevY = _downY = y; _downX = x; _downT = t;
        _ring.Clear();
        _ring.Add((t, y));
    }

    private bool Scroll(float y, double t)
    {
        var delta = _prevY - y;                          // finger up (y shrinks) → scroll down (+)
        _prevY = y;
        _ring.Add((t, y));
        Prune(t);
        // Wheel at the DOWN point: the target chain stays stable however far the finger travels,
        // and the engine's own edge-chaining and virtual re-windowing do the rest.
        return delta != 0 && document.DispatchWheel(_downX, _downY, delta);
    }

    private void Prune(double t)
    {
        while (_ring.Count > 0 && t - _ring[0].T > 0.1) _ring.RemoveAt(0);
    }
}
