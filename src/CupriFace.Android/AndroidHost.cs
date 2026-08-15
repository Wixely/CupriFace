using Android.Content;
using Android.Util;
using CupriFace.Interaction;
using SkiaSharp;

namespace CupriFace.Android;

/// <summary>
/// The Android analogue of <c>DesktopHost</c>: owns the document and wires the full host
/// contract — render-on-demand, the animation clock, clipboard, context menu, navigation,
/// fullscreen, and surface-loss recovery. <see cref="CupriActivity"/> composes it with a
/// <see cref="CupriHostView"/>; app authors never construct it directly.
///
/// THREADING — the one rule everything here follows: <b>the GL thread is the document thread.</b>
/// <c>SKGLSurfaceView</c> paints on its GL thread while Android delivers touch and key events on
/// the UI thread, and <see cref="CupriDocument"/> is single-threaded by design — so every event
/// crosses via <c>view.QueueEvent(...)</c> (GLSurfaceView's own event queue, processed on the GL
/// thread even between frames). Anything the UI thread must READ — sizes, scale, later the
/// text-input state — comes from an immutable <see cref="Snapshot"/> published after each frame.
/// The probe got this wrong (mutated the document from both threads); this class is the fix.
/// </summary>
public sealed class AndroidHost : IDisposable
{
    /// <summary>What the UI thread may read: an immutable view of the last painted frame —
    /// including the text-input state the IME's synchronous questions are answered from.</summary>
    public sealed record Snapshot(int ContentVersion, float LogicalWidth, float LogicalHeight,
        float InputScale, TextInputState TextInput);

    internal const string Tag = "cupri";                 // the logcat channel the CI gate asserts on

    private CupriApp _app;
    private CupriDocument _doc;
    private TouchInput _touch;
    private readonly Action<CupriDocument>? _configure;
    private readonly List<CupriApp> _stack = new();      // Back pops; About's launch pushes
    private readonly Context _context;
    private CupriHostView? _view;

    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private double _lastRefresh;
    private bool _firstFrameLogged;
    private volatile Snapshot? _snapshot;

    /// <summary>The last published frame snapshot; null before the first frame.</summary>
    public Snapshot? Current => _snapshot;

    /// <summary>Diagnostics for the CI gate and for humans: CUPRIFACE_ANDROID_DEBUG has no
    /// environment on Android, so markers are always on — they are cheap, and they are the only
    /// window CI has into the app.</summary>
    private static void Log(string message) => global::Android.Util.Log.Info(Tag, message);

    internal AndroidHost(Context context, CupriApp app, Action<CupriDocument>? configure)
    {
        _context = context;
        _app = app;
        _configure = configure;
        var t0 = _clock.Elapsed.TotalMilliseconds;
        _doc = app.CreateDocument();                     // the SAME call desktop and web make
        configure?.Invoke(_doc);                         // host-composition hook (DesktopHost parity)
        Log($"document built in {_clock.Elapsed.TotalMilliseconds - t0:F0} ms");
        _touch = new TouchInput(_doc);
        WireDocument(_doc);
    }

    /// <summary>Everything the host listens to ON a document — a method because a pushed/popped
    /// app gets a fresh document, and each one needs the identical wiring.</summary>
    private void WireDocument(CupriDocument doc)
    {
        // External links go to the OS; internal ones are the app's routing concern. Fires on the
        // GL thread (inside a dispatch), so hop to the UI thread for the Intent.
        doc.Navigated += e =>
        {
            if (!e.External) return;
            RunOnUi(() =>
            {
                try
                {
                    var intent = new Intent(Intent.ActionView, global::Android.Net.Uri.Parse(e.Href));
                    intent.AddFlags(ActivityFlags.NewTask);
                    _context.StartActivity(intent);
                }
                catch { /* no handler for the scheme — the click just does nothing, like desktop */ }
            });
        };

        // The engine's context menu asks the HOST to do clipboard work (the engine never touches
        // an OS clipboard). Same seam as DesktopHost.ContextAction, Android-shaped.
        doc.ContextRequested += cmd =>
        {
            switch (cmd)
            {
                case ContextCommand.Copy: PutClipboard(_doc.CopySelection()); break;
                case ContextCommand.Cut: PutClipboard(_doc.CutSelection()); MarkDirty(); break;
                case ContextCommand.Paste:
                    // Clipboard reads want the UI thread; the insert then crosses back.
                    RunOnUi(() =>
                    {
                        var text = ReadClipboard();
                        if (text is { Length: > 0 }) OnGlThread(() => { if (_doc.DispatchKey(text, EditKey.None)) MarkDirty(); });
                    });
                    break;
                case ContextCommand.SelectAll: if (_doc.DispatchKey(null, EditKey.SelectAll)) MarkDirty(); break;
            }
        };

        // The engine's fullscreen request (the video ⛶ button) maps to immersive mode here.
        doc.WindowCommandRequested += cmd => RunOnUi(() => FullscreenRequested?.Invoke(cmd));

        // Focus edges are the soft keyboard's cue. Raised on the GL thread mid-dispatch; the VIEW
        // owns the InputMethodManager work, so hop to the UI thread and hand it the state.
        doc.TextInputStateChanged += state => RunOnUi(() => TextInputChanged?.Invoke(state));
    }

    // ---- the app stack ------------------------------------------------------------------------

    /// <summary>Replace the running app with <paramref name="next"/>, keeping the current one on a
    /// stack that Back pops. App instances survive on the stack (their MODELS keep their state);
    /// documents are rebuilt on return — the same relationship an Activity has to its saved state.</summary>
    public void Push(CupriApp next) => OnGlThread(() => SwapApp(next, pushCurrent: true));

    /// <summary>Pop back to the previous app, if any. Called on the UI thread by Back handling;
    /// returns whether there was anything to pop (the swap itself runs on the document thread).</summary>
    public bool TryPop()
    {
        if (_stack.Count == 0) return false;
        OnGlThread(() =>
        {
            if (_stack.Count == 0) return;
            var previous = _stack[^1];
            _stack.RemoveAt(_stack.Count - 1);
            SwapApp(previous, pushCurrent: false);
        });
        return true;
    }

    private void SwapApp(CupriApp next, bool pushCurrent)
    {
        if (pushCurrent) _stack.Add(_app);
        var old = _doc;
        _app = next;
        _doc = next.CreateDocument();
        _configure?.Invoke(_doc);
        WireDocument(_doc);
        _touch = new TouchInput(_doc);
        old.Dispose();
        _doc.InvalidateRetainedFrame();
        Log($"app switched to '{next.Title}'");
        MarkDirty();
    }

    /// <summary>Raised on the UI thread when text-input focus changes — the view shows/hides the
    /// soft keyboard and restarts the input connection off this.</summary>
    public event Action<TextInputState>? TextInputChanged;

    /// <summary>Raised on the UI thread when the document asks for fullscreen; the activity maps
    /// it to immersive mode (it owns the window).</summary>
    public event Action<WindowCommand>? FullscreenRequested;

    // ---- wiring -------------------------------------------------------------------------------

    internal void Attach(CupriHostView view) => _view = view;

    /// <summary>Run an action on the document's thread. Safe from any thread.</summary>
    public void OnGlThread(Action action) => _view?.QueueEvent(action);

    private void RunOnUi(Action action)
    {
        if (_view?.Handler is { } h) h.Post(action);
        else action();
    }

    /// <summary>Request a repaint. Thread-safe (GLSurfaceView contract).</summary>
    internal void MarkDirty() => _view?.RequestRender();

    // ---- the frame (GL thread) ----------------------------------------------------------------

    /// <summary>One frame: refresh cadence, animation clock, present-scaled render, snapshot
    /// publish, and the self-chain that keeps animations advancing. Called from the view's
    /// PaintSurface on the GL thread.</summary>
    internal void PaintFrame(SKCanvas canvas, int physicalWidth, int physicalHeight, float density)
    {
        // The app thinks in density-independent pixels; Android gave us physical ones.
        var dpW = physicalWidth / density;
        var dpH = physicalHeight / density;
        var p = _app.Present(dpW, dpH);
        var scale = p.Scale <= 0 ? 1f : p.Scale;
        var inputScale = density * scale;                // ONE factor for canvas AND touch — the
                                                         // probe divided touch by density alone,
                                                         // which mis-hits whenever Present scales.

        // Host-driven refresh cadence (diagnostics pages etc.), same shape as DesktopHost.
        if (_app.RefreshIntervalSeconds > 0 &&
            _clock.Elapsed.TotalSeconds - _lastRefresh >= _app.RefreshIntervalSeconds)
        {
            _lastRefresh = _clock.Elapsed.TotalSeconds;
            _doc.Refresh();
        }

        // Drive the animation clock exactly as DesktopHost does: rules existing is not the same
        // as animation happening, hence the two-sided gate.
        if (_doc.HasAnimations || _doc.HasActiveTransitions)
            _doc.Animate(_clock.Elapsed.TotalSeconds);

        canvas.Clear(_app.Transparent ? SKColors.Transparent : _app.Background);
        canvas.Save();
        canvas.Scale(inputScale);
        _doc.Render(canvas, p.LogicalWidth, p.LogicalHeight);
        canvas.Restore();

        _snapshot = new Snapshot(_doc.ContentVersion, p.LogicalWidth, p.LogicalHeight, inputScale,
            _doc.GetTextInputState());

        if (!_firstFrameLogged)
        {
            _firstFrameLogged = true;
            Log($"FIRST FRAME at {_clock.Elapsed.TotalMilliseconds:F0} ms since host start " +
                $"({physicalWidth}x{physicalHeight} px, {p.LogicalWidth:F0}x{p.LogicalHeight:F0} logical, " +
                $"density {density}, scale {scale:F3})");
        }

        // Render-on-demand's other half: WHEN_DIRTY parks the GL thread after this frame, so an
        // active animation must chain the next one itself. Image arrivals ride the same check.
        if (_doc.HasActiveAnimations || _doc.ConsumeImageArrived()) MarkDirty();
    }

    /// <summary>The EGL surface was created (first show, or LOSS on background/foreground — routine
    /// on Android). Any retained-frame assumption is now false.</summary>
    internal void OnSurfaceRecreated()
    {
        OnGlThread(() =>
        {
            _doc.InvalidateRetainedFrame();
            MarkDirty();
        });
    }

    /// <summary>Periodic UI-thread pump (armed by the activity): work that has no event —
    /// refresh cadence while idle, image decodes finishing. Queues to the GL thread.</summary>
    internal void Pump()
    {
        OnGlThread(() =>
        {
            var due = _app.RefreshIntervalSeconds > 0 &&
                      _clock.Elapsed.TotalSeconds - _lastRefresh >= _app.RefreshIntervalSeconds;
            if (due || _doc.ConsumeImageArrived()) MarkDirty();
        });
    }

    // ---- input (called on the GL thread via the view's queue) ---------------------------------

    // The engine's gesture recognizer (the _touch field above): tap-vs-scroll slop, fling,
    // long-press context menu, double-tap escalation — and, structurally, no hover. Timestamps
    // come from MotionEvent's uptime clock; long-press fires via a UI-thread timer queueing Tick.
    internal void TouchDown(float x, float y, double t) { if (_touch.Down(x / InputScale(), y / InputScale(), t)) MarkDirty(); }
    internal void TouchMove(float x, float y, double t) { if (_touch.Move(x / InputScale(), y / InputScale(), t)) MarkDirty(); }
    internal void TouchUp(float x, float y, double t) { if (_touch.Up(x / InputScale(), y / InputScale(), t)) MarkDirty(); }
    internal void TouchCancel(double t) { if (_touch.Cancel(t)) MarkDirty(); }
    internal void TouchTick(double t) { if (_touch.Tick(t)) MarkDirty(); }

    internal void Key(EditKey key, KeyMods mods = KeyMods.None)
    { if (_doc.DispatchKey(null, key, mods)) MarkDirty(); }

    internal void KeyText(string text)
    { if (_doc.DispatchKey(text, EditKey.None)) MarkDirty(); }

    /// <summary>One IME mutation on the document thread: run it, mark dirty if it changed, and
    /// log the commit marker the CI gate asserts on.</summary>
    internal void Ime(Func<CupriDocument, bool> action)
    {
        if (action(_doc)) MarkDirty();
    }

    /// <summary>A committed text insert, with the gate's observable marker.</summary>
    internal void ImeCommitted(string text)
    {
        if (_doc.CommitComposition(text)) { Log($"commit '{text}'"); MarkDirty(); }
    }

    /// <summary>Hardware-keyboard Ctrl chords — the same table as DesktopHost.Shortcut, with the
    /// clipboard seam Android-shaped. Runs on the document thread.</summary>
    internal void HwShortcut(char ch, bool shift)
    {
        switch (char.ToLowerInvariant(ch))
        {
            case 'a': if (_doc.DispatchKey(null, EditKey.SelectAll)) MarkDirty(); break;
            case 'c': PutClipboard(_doc.CopySelection()); break;
            case 'x': PutClipboard(_doc.CutSelection()); MarkDirty(); break;
            case 'v':
                RunOnUi(() =>
                {
                    var text = ReadClipboard();
                    if (text is { Length: > 0 }) OnGlThread(() => KeyText(text));
                });
                break;
            case 'z': if (shift ? _doc.Redo() : _doc.Undo()) MarkDirty(); break;
            case 'y': if (_doc.Redo()) MarkDirty(); break;
            default:
                // Unclaimed chords reach the app's own OnShortcut bindings, exactly like desktop.
                if (_doc.DispatchKey(ch.ToString(), EditKey.None, KeyMods.Ctrl | (shift ? KeyMods.Shift : KeyMods.None)))
                    MarkDirty();
                break;
        }
    }

    /// <summary>Escape with a report: the activity uses this for Back — an overlay that consumed
    /// it stays; otherwise the platform's back behaviour proceeds.</summary>
    internal void EscapeThen(Action onUnhandled)
    {
        OnGlThread(() =>
        {
            if (_doc.DispatchKey(null, EditKey.Escape)) MarkDirty();
            else RunOnUi(onUnhandled);
        });
    }

    private float InputScale() => _snapshot?.InputScale ?? 1f;

    // ---- clipboard (UI thread) ----------------------------------------------------------------

    private void PutClipboard(string? text)
    {
        if (text is not { Length: > 0 }) return;
        RunOnUi(() =>
        {
            if (_context.GetSystemService(Context.ClipboardService) is ClipboardManager cm)
                cm.PrimaryClip = ClipData.NewPlainText("text", text);
        });
    }

    private string? ReadClipboard() =>
        _context.GetSystemService(Context.ClipboardService) is ClipboardManager cm
            ? cm.PrimaryClip?.GetItemAt(0)?.Text
            : null;

    public void Dispose() => _doc.Dispose();
}
