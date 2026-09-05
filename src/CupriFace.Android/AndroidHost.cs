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
    // Last painted size in dp, to notice the keyboard taking (or giving back) its half.
    private float _lastDpW, _lastDpH;
    private double _lastRefresh;
    private bool _firstFrameLogged;
    private volatile Snapshot? _snapshot;

    /// <summary>The last published frame snapshot; null before the first frame.</summary>
    public Snapshot? Current => _snapshot;

    /// <summary>The live document — for the bridge's GL-thread-queued actions only.</summary>
    internal CupriDocument Document => _doc;

    /// <summary>What the IME must be answered from: the text-input state as of the last focus
    /// change or frame, whichever is newer — never the painted snapshot alone, which lags a focus
    /// change by one frame and would describe the field the user just left.</summary>
    // Written on the document thread, read on the UI thread when the IME asks. A struct cannot be
    // volatile, and a torn read here would describe a field that never existed — so it is guarded.
    internal TextInputState ImeState { get { lock (_imeLock) return _imeStateField; } }
    private void SetImeState(TextInputState state) { lock (_imeLock) _imeStateField = state; }
    private readonly object _imeLock = new();
    private TextInputState _imeStateField;
    private TextInputState _lastImeKind;      // what the view was last told about (GL thread only)
    private (int Start, int End, bool Composing) _lastSel = (-1, -1, false);
    private (float X, float Y, float W, float H)? _lastCaretRect;

    /// <summary>Raised on the UI thread when the caret or selection moves, so the view can report
    /// it to the InputMethodManager. Distinct from <see cref="TextInputChanged"/>, which is about
    /// the KIND of field (and so about showing/hiding/restarting the keyboard): the caret moves
    /// constantly within one field and must not restart anything.</summary>
    public event Action<TextInputState>? SelectionChanged;

    /// <summary>Raised on the UI thread when the caret's on-screen RECTANGLE moves, so the view can
    /// report it as <c>CursorAnchorInfo</c> and a keyboard can place its candidate window over the
    /// right word.
    ///
    /// <para>Deliberately separate from <see cref="SelectionChanged"/>, which fires on the selection
    /// INDICES. Those are different questions: the caret's box moves for reasons the indices never
    /// see — the field scrolls, a reflow shifts the line, the window resizes, the app zooms — and an
    /// IME drawing over the text needs every one of them. Firing on indices alone would leave the
    /// candidate window behind exactly when the text moved under it.</para></summary>
    public event Action<TextInputState>? CaretMoved;

    /// <summary>The part of the state the keyboard itself depends on — what must trigger a
    /// show/hide/restart. Caret and value churn on every keystroke and must NOT.</summary>
    private static (bool, string?, bool, bool, bool) ImeKind(TextInputState s) =>
        (s.Focused, s.Role, s.Numeric, s.Multiline, s.Masked);

    /// <summary>The app's clear colour — the activity paints the edge-to-edge inset strips with
    /// it so the band behind the transparent status bar belongs to the app.</summary>
    internal SkiaSharp.SKColor AppBackground => _app.Transparent ? SkiaSharp.SKColors.Black : _app.Background;

    /// <summary>The running app's identity — what the OS shows for the TASK rather than for the
    /// window: the recents card's label and thumbnail badge. Changes when an app is pushed or
    /// popped, which is why it is read through the host and not captured once.</summary>
    internal (string Title, byte[]? Icon) AppIdentity => (_app.Title, _app.Icon);

    /// <summary>Raised on the UI thread once a pushed/popped app is running, so the activity can
    /// re-read <see cref="AppIdentity"/>. The background has its own event because it changes for
    /// reasons other than a swap (dark mode); this one fires only on a swap.</summary>
    public event Action? AppChanged;

    /// <summary>Raised on the UI thread when the PAGE's own background colour changes — the app
    /// switching to dark mode, or a different app being pushed. The activity paints the window and
    /// the inset strips with it, so the surfaces the document does not draw stop being white.</summary>
    public event Action<SkiaSharp.SKColor>? PageBackgroundChanged;
    private SkiaSharp.SKColor _lastPageBg = SkiaSharp.SKColors.Transparent;

    /// <summary>Diagnostics for the CI gate and for humans: CUPRIFACE_ANDROID_DEBUG has no
    /// environment on Android, so markers are always on — they are cheap, and they are the only
    /// window CI has into the app.</summary>
    private static void Log(string message) => global::Android.Util.Log.Info(Tag, message);

    internal AndroidHost(Context context, CupriApp app, Action<CupriDocument>? configure)
    {
        _context = context;
        _app = app;
        _configure = configure;

        // Portable apps print with Console (the MobileApp's gate markers, a user's own
        // diagnostics); on CoreCLR-Android that goes NOWHERE — the Mono-era stdout→logcat
        // redirector does not exist. Bridge it, once, so Console output lands under our tag and
        // is greppable by the CI gate and by humans alike.
        if (!_consoleBridged)
        {
            _consoleBridged = true;
            Console.SetOut(TextWriter.Synchronized(new LogcatWriter()));
        }
        var t0 = _clock.Elapsed.TotalMilliseconds;
        _doc = app.CreateDocument();                     // the SAME call desktop and web make
        _doc.InputProfile = InputProfile.Touch;          // a finger: coarse, and nothing hovers
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
            if (!e.External || !ExternalLinkPolicy.IsAllowed(e.Href)) return;
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

        // A submitted form is what makes a password manager OFFER TO SAVE. Filling is passive —
        // the service reads the structure whenever it likes — but saving needs the app to say the
        // entry is finished, which is exactly what Commit() means.
        doc.FormSubmitted += () => RunOnUi(() => FormSubmitted?.Invoke());

        // Focus edges are the soft keyboard's cue. Raised on the GL thread mid-dispatch; the VIEW
        // owns the InputMethodManager work, so hop to the UI thread and hand it the state.
        //
        // The state is ALSO stored synchronously here, because the IME asks its questions before
        // the next frame exists. Answering from the last painted snapshot meant answering about
        // the PREVIOUSLY focused field: tapping the name box produced a number pad (the amount
        // field's keyboard), and tapping the amount box produced a text keyboard. One frame of
        // staleness, and every field wore its predecessor's keyboard.
        doc.TextInputStateChanged += state =>
        {
            SetImeState(state);
            RunOnUi(() => TextInputChanged?.Invoke(state));
        };
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
        _doc.InputProfile = InputProfile.Touch;
        _configure?.Invoke(_doc);
        WireDocument(_doc);
        _touch = new TouchInput(_doc);
        old.Dispose();
        _doc.InvalidateRetainedFrame();
        Log($"app switched to '{next.Title}'");
        RunOnUi(() => AppChanged?.Invoke());   // the recents card names the app you are IN, not the one you launched
        MarkDirty();
    }

    /// <summary>Raised on the UI thread when text-input focus changes — the view shows/hides the
    /// soft keyboard and restarts the input connection off this.</summary>
    public event Action<TextInputState>? TextInputChanged;

    /// <summary>Raised on the UI thread when the document asks for fullscreen; the activity maps
    /// it to immersive mode (it owns the window).</summary>
    public event Action<WindowCommand>? FullscreenRequested;

    /// <summary>Raised on the UI thread when the app declares a form submitted; the view answers it
    /// with AutofillManager.Commit().</summary>
    public event Action? FormSubmitted;

    // ---- wiring -------------------------------------------------------------------------------

    internal void Attach(CupriHostView view) => _view = view;

    /// <summary>Run an action on the document's thread. Safe from any thread.</summary>
    public void OnGlThread(Action action) => _view?.QueueEvent(action);

    // ---- video -----------------------------------------------------------------------------
    // The platform decodes; the engine paints a hole. Attached by CupriActivity once it has a
    // container to put underlays in — an app's markup is identical on every host, only the
    // decoder differs (desktop ships codecs, the browser and Android use the platform's).
    private AndroidVideoBackend? _video;

    internal void UseVideo(global::Android.Content.Context context, global::Android.Widget.FrameLayout underlays)
    {
        _video = new AndroidVideoBackend(context, underlays, MarkDirty, RunOnUi);
        _doc.UseVideo(_video);
    }

    /// <summary>True while a video underlay can show pixels: the frame must then clear to
    /// TRANSPARENT so the engine's punched hole actually reveals what is beneath it. With no
    /// video ready this stays false and the app paints its opaque background exactly as before.</summary>
    private bool VideoUnderlayReady => _video?.AnyReady == true;

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
    internal void PaintFrame(SKCanvas canvas, int physicalWidth, int physicalHeight, float density,
                             GRContext? gpu = null)
    {
        // GPU surface producers go FIRST, before anything is recorded for this frame - they issue
        // raw GL on the same context Skia is about to use, and SurfaceRegistry calls ResetContext
        // afterwards. Android reaches this the same way the desktop GL window does; the web host,
        // which rasterises on the CPU, never does.
        if (gpu is not null) _doc.Surfaces.RenderGpuFrames(gpu);

        // The app thinks in density-independent pixels; Android gave us physical ones.
        var dpW = physicalWidth / density;
        var dpH = physicalHeight / density;
        var p = _app.Present(dpW, dpH);
        var scale = p.Scale <= 0 ? 1f : p.Scale;
        var inputScale = density * scale;                // ONE factor for canvas AND touch — the
                                                         // probe divided touch by density alone,
                                                         // which mis-hits whenever Present scales.

        // The usable area changed — almost always the soft keyboard arriving or leaving, since its
        // inset is applied as padding and shrinks this surface. The caret has NOT moved, so the
        // ordinary caret-follow will not fire, and without this the field you just tapped stays
        // behind the keyboard: reported as "when clicking on an input the keyboard hides the input
        // box". Compared in dp so a density change is not mistaken for a resize.
        if (MathF.Abs(dpW - _lastDpW) > 0.5f || MathF.Abs(dpH - _lastDpH) > 0.5f)
        {
            var shrank = dpH < _lastDpH - 0.5f;
            _lastDpW = dpW; _lastDpH = dpH;
            if (shrank) _doc.EnsureCaretVisible();
        }

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

        canvas.Clear(_app.Transparent || VideoUnderlayReady ? SKColors.Transparent : _app.Background);
        canvas.Save();
        canvas.Scale(inputScale);
        _doc.Render(canvas, p.LogicalWidth, p.LogicalHeight);
        canvas.Restore();

        // The page's own colour, watched for changes: a dark-mode swap is a CSS variable change
        // with no other signal, and the window behind us would stay white without this.
        var pageBg = _doc.PageBackground;
        if (pageBg.Alpha > 0 && pageBg != _lastPageBg)
        {
            _lastPageBg = pageBg;
            RunOnUi(() => PageBackgroundChanged?.Invoke(pageBg));
        }

        var textInput = _doc.GetTextInputState();
        SetImeState(textInput);              // caret/selection move without a focus edge
        _snapshot = new Snapshot(_doc.ContentVersion, p.LogicalWidth, p.LogicalHeight, inputScale, textInput);
        _video?.SyncRects(_doc, inputScale);

        // Not every focus change arrives as a focus EVENT: an overlay that opens with a focused
        // field (the command palette autofocuses its query box) lands the caret without one, and
        // the keyboard never appeared — you could see the caret and had nothing to type with.
        // Diffing the post-frame state catches focus however it moved. The view's handler is
        // idempotent, so the ordinary evented path costing an extra call is harmless.
        if (ImeKind(textInput) != ImeKind(_lastImeKind))
        {
            _lastImeKind = textInput;
            var state = textInput;
            RunOnUi(() => TextInputChanged?.Invoke(state));
        }

        // Tell the IME where the caret IS. An editor is expected to report every selection change,
        // and we never did — so a keyboard's model of this field was permanently empty. That is
        // what a spacebar-swipe needs in order to compute where to move TO, and what a
        // tap-a-word-to-correct needs in order to name the range. Reported whenever the selection
        // or the composition moves, which is exactly when the contract asks for it.
        if (textInput.SelStart != _lastSel.Start || textInput.SelEnd != _lastSel.End
            || textInput.Composing != _lastSel.Composing)
        {
            _lastSel = (textInput.SelStart, textInput.SelEnd, textInput.Composing);
            var s = textInput;
            RunOnUi(() => SelectionChanged?.Invoke(s));
        }

        // …and WHERE it is, which is a different question from what it points at. Reported off the
        // rect rather than the indices, so a caret that moved because the line reflowed under it is
        // still reported; deduped on the rect, so a still caret costs one comparison per frame and
        // no marshal. A null rect means layout is dirty and the answer would be a guess.
        if (textInput.CaretRect != _lastCaretRect)
        {
            _lastCaretRect = textInput.CaretRect;
            if (textInput.CaretRect is not null)
            {
                var c = textInput;
                RunOnUi(() => CaretMoved?.Invoke(c));
            }
        }

        // TalkBack: republish the semantics tree when content moved (Animate bumps the version
        // when a fling steps, so scrolled bounds follow the viewport). The bridge only exists
        // once an accessibility client asked the view for it — until then this line costs nothing.
        // A trailing-edge force also lands here: after a publish burst (a fling) ends, the tree
        // is rebuilt from the CURRENT document and only then announced — the accessibility
        // pipeline snapshots around announcements, and announcing a superseded tree left every
        // reader half a second behind the settle.
        if (_talkBack is { } tb && (_a11yForce || _doc.ContentVersion != _a11yVersion
                                    || p.LogicalWidth != _a11yWidth || p.LogicalHeight != _a11yHeight))
        {
            var forced = _a11yForce;
            _a11yForce = false;
            _a11yVersion = _doc.ContentVersion;
            _a11yWidth = p.LogicalWidth;
            _a11yHeight = p.LogicalHeight;
            tb.Publish(_doc.BuildAccessibilityTree(p.LogicalWidth, p.LogicalHeight), inputScale, forced);
            if (forced) tb.AnnounceContentChanged();
        }

        if (!_firstFrameLogged)
        {
            _firstFrameLogged = true;
            Log($"FIRST FRAME at {_clock.Elapsed.TotalMilliseconds:F0} ms since host start " +
                $"({physicalWidth}x{physicalHeight} px, {p.LogicalWidth:F0}x{p.LogicalHeight:F0} logical, " +
                $"density {density}, scale {scale:F3})");
        }

        // The CI gate's momentum observable: the frame after a fling dies, log where the scroller
        // came to rest — settled travel beyond the finger's is the proof the integrator ran.
        if (_wasFlinging && !_doc.FlingActive)
        {
            var y = MaxScrollOffset(_doc.Root);
            Log($"fling settled y={y:F0}");
            // The a11y freshness ledger: if the published version equals the document's here,
            // the settle-frame TREE went out and any staleness a reader sees is on the client
            // side of the accessibility protocol; if they differ, the publish gate skipped the
            // frame that mattered. The first-visible listitem of the published tree pins WHICH
            // tree went out. One line, and the gate's stale-tree diagnosis stops guessing.
            if (_talkBack is { } bridge)
                Log($"a11y at settle v={_a11yVersion} docv={_doc.ContentVersion} first='{bridge.FirstVisibleListitem()}'");
        }
        _wasFlinging = _doc.FlingActive;

        // Render-on-demand's other half: WHEN_DIRTY parks the GL thread after this frame, so an
        // active animation must chain the next one itself. Image arrivals ride the same check.
        if (_doc.HasActiveAnimations || _doc.ConsumeImageArrived()) MarkDirty();
    }

    private bool _wasFlinging;
    private TalkBackBridge? _talkBack;
    private int _a11yVersion = -1;
    private float _a11yWidth, _a11yHeight;

    /// <summary>An accessibility client asked the view for its provider: start publishing, and
    /// force a frame so the first query doesn't read an empty tree.</summary>
    internal void AttachTalkBack(TalkBackBridge bridge)
    {
        _talkBack = bridge;
        bridge.TrailingRepublish = () => OnGlThread(() =>
        {
            _a11yForce = true;      // next frame republishes even at an unchanged version
            MarkDirty();
        });
        Log("talkback bridge attached");
        MarkDirty();
    }

    private bool _a11yForce;

    private static float MaxScrollOffset(CupriFace.Dom.RenderNode n)
    {
        var best = n.IsScrollable ? n.ScrollY : 0f;
        foreach (var c in n.Children) best = Math.Max(best, MaxScrollOffset(c));
        return best;
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
    // Pointer routing. An element that opted into raw pointers (doc.OnPointer) CAPTURES the finger
    // that landed on it; the single-pointer recognizer below never sees that finger, which is what
    // keeps a two-finger pinch from also scrolling the page under it. Everything not captured
    // behaves exactly as before, and the recognizer only ever tracks the primary pointer.
    private int _primaryPointer = -1;

    internal void PointerDown(int id, float x, float y, double t)
    {
        var (lx, ly) = (x / InputScale(), y / InputScale());
        var changed = _doc.DispatchPointer(id, PointerPhase.Down, lx, ly);
        // Which fingers arrive, and who takes them — the line that turns any phone with adb into
        // this bug's test rig (`adb logcat -s cupri:I` while pinching). A two-finger grab that
        // logs only one id is the input path dropping a finger before the engine ever saw it;
        // two ids, both captured, puts the fault past the host.
        Log($"pointer down id={id} captured={_doc.IsPointerCaptured(id)}");
        if (changed) { CancelTouchForPageZoom(t); MarkDirty(); return; }
        if (_primaryPointer >= 0) return;                       // a second finger the app didn't want
        _primaryPointer = id;
        if (_touch.Down(lx, ly, t)) MarkDirty();
    }

    internal void PointerMove(int id, float x, float y, double t)
    {
        var (lx, ly) = (x / InputScale(), y / InputScale());
        // Every pointer is offered to the document first: a captured one belongs to its element,
        // and an uncaptured one may be half of a page-zoom pinch. Only what the document declines
        // reaches the single-pointer recognizer.
        if (_doc.DispatchPointer(id, PointerPhase.Move, lx, ly)) { CancelTouchForPageZoom(t); MarkDirty(); return; }
        if (id == _primaryPointer && _touch.Move(lx, ly, t)) MarkDirty();
    }

    internal void PointerUp(int id, float x, float y, double t)
    {
        var (lx, ly) = (x / InputScale(), y / InputScale());
        if (_doc.DispatchPointer(id, PointerPhase.Up, lx, ly)) { MarkDirty(); return; }
        if (id != _primaryPointer) return;
        _primaryPointer = -1;
        TouchUp(x, y, t);
    }

    // A page-zoom pinch has taken over: end whatever the single-pointer recognizer had begun so a
    // half-finished scroll cannot run alongside it, and never let that finger become a tap.
    private void CancelTouchForPageZoom(double t)
    {
        if (!_doc.PageZoomActive || _primaryPointer < 0) return;
        _primaryPointer = -1;
        _touch.Cancel(t);
    }

    internal void TouchDown(float x, float y, double t) { if (_touch.Down(x / InputScale(), y / InputScale(), t)) MarkDirty(); }
    internal void TouchMove(float x, float y, double t) { if (_touch.Move(x / InputScale(), y / InputScale(), t)) MarkDirty(); }
    internal void TouchUp(float x, float y, double t)
    {
        if (_touch.Up(x / InputScale(), y / InputScale(), t)) MarkDirty();
        // The diagnostic pair of "fling settled" — WITH the position, so settle minus start is
        // the coast: the CI gate's momentum assert, independent of how many drag-only gestures
        // a flaky injector delivered before one carried a fling.
        if (_doc.FlingActive) Log($"fling started y={MaxScrollOffset(_doc.Root):F0}");
    }
    internal void TouchCancel(double t)
    {
        _primaryPointer = -1;
        _doc.CancelPointers();                                  // half-finished gestures unwind
        if (_touch.Cancel(t)) MarkDirty();
    }
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

    /// <summary>A semantics tree for the autofill structure, built on demand. Autofill asks on the
    /// UI thread and cannot wait for a frame, so this reads the document directly — safe because it
    /// only READS, and a torn read costs at worst one stale rectangle in a fill dialog.</summary>
    internal Accessibility.AccessibilityNode BuildSemanticsForAutofill()
    {
        var s = _snapshot;
        return _doc.BuildAccessibilityTree(s?.LogicalWidth ?? 400, s?.LogicalHeight ?? 800);
    }

    /// <summary>Route an IME's own edit-menu action through the SAME clipboard seam the engine's
    /// context menu and the Ctrl chords use, so the three cannot disagree about this platform.</summary>
    internal void ContextCommandFromIme(ContextCommand command) => _doc.RequestContextCommand(command);

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

    private static bool _consoleBridged;

    /// <summary>Console → logcat, line-buffered. CoreCLR on Android has no stdout destination;
    /// without this, a portable app's Console output silently vanishes.</summary>
    private sealed class LogcatWriter : TextWriter
    {
        private readonly System.Text.StringBuilder _line = new();
        public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;

        public override void Write(char value)
        {
            if (value == '\n') Flush();
            else if (value != '\r') _line.Append(value);
        }

        public override void Write(string? value)
        {
            if (value is null) return;
            foreach (var c in value) Write(c);
        }

        public override void WriteLine(string? value)
        {
            Write(value);
            Flush();
        }

        public override void Flush()
        {
            if (_line.Length == 0) return;
            Log(_line.ToString());
            _line.Clear();
        }
    }
}
