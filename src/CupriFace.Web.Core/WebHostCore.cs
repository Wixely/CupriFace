using System.Diagnostics;
using CupriFace.Interaction;
using SkiaSharp;

namespace CupriFace.Web;

/// <summary>
/// The web host, once. Everything a browser host does that is not interop lives here: the
/// render-on-demand lifecycle, damage-rect painting, the premultiplied→straight alpha conversion,
/// input dispatch and scaling, the touch recognizer, the ARIA mirror and IME cadence, and the
/// clipboard/undo plumbing.
///
/// <para>Each host keeps only what is genuinely its own — the declarations that reach JS, in
/// whichever way its runtime reaches JS — and hands this an <see cref="IWebBridge"/>. That split is
/// not tidiness: the two hosts drifted for as long as they both existed, and the difference nobody
/// noticed (the NativeAOT host could not position an IME, #77) was in exactly this shared half. A
/// call added here now reaches both hosts, or fails the parity gate trying (#79).</para>
///
/// <para>Static state, deliberately: one page hosts one app, and the JS half calls in on a single
/// thread. The hosts were written this way and this preserves it rather than inventing an instance
/// model no caller wants.</para>
/// </summary>
public static class WebHostCore
{
    private static IWebBridge _js = null!;
    private static CupriApp _app = null!;
    private static CupriDocument _doc = null!;
    private static TouchInput _touch = null!;
    private static WebVideoBackend? _video;
    private static WebUnderlays? _underlays;

    private static int _primaryPointer = -1;     // the recognizer follows one finger; apps may hold others
    private static SKColor _bg;
    private static bool _transparent;            // overlay mode: transparent clear + straight-alpha present
    private static float _scale = 1f;            // Present scale, for un-scaling pointer coords
    private static SKBitmap? _bitmap;
    private static SKBitmap? _straight;          // staging buffer for the premul→straight conversion
    private static readonly Stopwatch _clock = Stopwatch.StartNew();
    private static double _lastRefresh, _lastAnimMs;
    private static int _lastW, _lastH;
    private static bool _dirty = true;
    private static string _cursor = "";
    private static (bool, bool, bool, float, float) _lastTextInput;

    /// <summary>The live document — for a host's own queued work.</summary>
    public static CupriDocument Document => _doc;

    /// <summary>Ask for a repaint. Video events and other out-of-band nudges come through here.</summary>
    public static void MarkDirty() => _dirty = true;

    /// <summary>Rebind and repaint (a video's metadata arriving changes what the controls show).</summary>
    public static void Refresh() { _doc?.Refresh(); _dirty = true; }

    public static void NotifyHostFullscreen(bool active) { _doc?.NotifyHostFullscreen(active); _dirty = true; }

    internal static WebVideoBackend? Video => _video;

    /// <summary>Build the app's document and wire it to the page. Called once, after the runtime is
    /// resident and <c>Main</c> has registered an app.</summary>
    public static void Init(CupriApp app, Action<CupriDocument>? configure, IWebBridge bridge)
    {
        _js = bridge;
        _app = app;
        _doc = app.CreateDocument();
        configure?.Invoke(_doc);
        _touch = new TouchInput(_doc);

        // The wasm Skia build has ONE embedded face (Noto Mono) — without these, sans-serif silently
        // renders monospaced. Registered faces win over platform lookup; the first family becomes
        // the generic sans target (see FontService).
        foreach (var res in new[] { "fonts.NotoSans-Regular.ttf", "fonts.NotoSans-Bold.ttf" })
        {
            using var fs = typeof(WebHostCore).Assembly.GetManifestResourceStream(res)
                           ?? throw new InvalidOperationException(
                               $"The host is missing its embedded font '{res}'. Without a real sans " +
                               "face every app renders monospaced (see src/WebFonts.props).");
            var buf = new byte[fs.Length];
            fs.ReadExactly(buf);
            _doc.LoadFont(buf);
        }

        _bg = _app.Background;
        _transparent = _app.Transparent;

        // The tab icon, from the app's own bytes — the same CupriApp.Icon the desktop host puts on
        // the window. The page ships without a hard-coded copy: a second, hand-pasted base64 of the
        // logo is a duplicate that drifts the moment the real asset changes.
        if (_app.IconDataUri is { } favicon) _js.SetFavicon(favicon);

        // External links open in a new browser tab. Internal routing and #anchors are the app's and
        // the engine's concern — the same split every host makes.
        _doc.Navigated += e => { if (e.External) _js.Navigate(e.Href); };

        // Video: the browser decodes (no codecs in the wasm binary). Each <cupri-video> gets an
        // underlaid element; the engine punches a transparent hole where it shows and paints its own
        // controls on top. Rect/clip sync happens after each painted frame.
        _video = new WebVideoBackend(_js);
        _underlays = new WebUnderlays(_js);
        _doc.UseVideo(_video);

        // Fullscreen requests (the ⛶ control) go to the browser's Fullscreen API. Escape exits
        // natively; the resize that follows reflows the app like any window resize.
        _doc.WindowCommandRequested += cmd => _js.WindowCommand((int)cmd);

        // Right-click menu → clipboard. The engine raises the chosen command; the browser owns the
        // clipboard (asynchronously), so Copy/Cut/Paste route through the page.
        _doc.ContextRequested += cmd =>
        {
            switch (cmd)
            {
                case ContextCommand.Copy: if (_doc.CopySelection() is { } cp) _js.ClipboardWrite(cp); break;
                case ContextCommand.Cut: if (_doc.CutSelection() is { } ct) { _js.ClipboardWrite(ct); _dirty = true; } break;
                case ContextCommand.Paste: _js.ClipboardPaste(); break;   // comes back through KeyChar
                case ContextCommand.SelectAll: if (_doc.DispatchKey(null, EditKey.SelectAll)) _dirty = true; break;
            }
        };

        // A freshly initialised host owes its first frame, and owes the page everything it caches as
        // "already sent". These are all per-PAGE state, and Init means a new page: _cursor and
        // _lastTextInput exist to suppress repeat calls, so carrying them over a re-init silently
        // withholds the first cursor and the first text-input state from a bridge that has never
        // been told either. In a browser this is academic — the page loads once and the statics
        // start empty — and it stops being academic the moment Init runs twice in one process.
        _dirty = true;
        _cursor = "";
        _lastTextInput = default;
        _lastW = _lastH = 0;
    }

    /// <summary>One animation frame. Renders ONLY when something changed — after input, on the app's
    /// periodic re-bind, or throttled while something animates — so an idle page costs nothing.
    /// Returns whether it painted.</summary>
    public static bool Tick(int width, int height, double nowMs)
    {
        if (_doc is null || width <= 0 || height <= 0) return false;

        // Canvas resized → repaint so scaling reflows to the new viewport.
        if (width != _lastW || height != _lastH) { _lastW = width; _lastH = height; _dirty = true; }

        // A background (remote) image finished loading → repaint so it appears.
        if (_doc.ConsumeImageArrived()) _dirty = true;

        // Live re-bind (e.g. a diagnostics readout) on the app's own cadence.
        if (_app.RefreshIntervalSeconds > 0 && _clock.Elapsed.TotalSeconds - _lastRefresh >= _app.RefreshIntervalSeconds)
        {
            _lastRefresh = _clock.Elapsed.TotalSeconds;
            _doc.Refresh();
            _dirty = true;
        }

        // A press held still becomes a context menu. The recognizer says when it next wants asking;
        // the frame loop is already running, so it asks — no second timer, and the same clock the
        // page stamps its events with.
        if (_touch.NextDeadline is { } deadline && nowMs / 1000.0 >= deadline && _touch.Tick(nowMs / 1000.0))
            _dirty = true;

        // Continuous repaint only while something is actually animating, capped at ~30 fps.
        var animating = _doc.HasActiveAnimations;
        if (animating && nowMs - _lastAnimMs >= 33) { _lastAnimMs = nowMs; _dirty = true; }

        if (!_dirty) return false;
        _dirty = false;
        return Paint(width, height, animating);
    }

    private static unsafe bool Paint(int width, int height, bool animating)
    {
        var p = _app.Present(width, height);
        _scale = p.Scale <= 0 ? 1f : p.Scale;

        if (_bitmap is null || _bitmap.Width != width || _bitmap.Height != height)
        {
            _bitmap?.Dispose();
            _bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        }

        // The staging bitmap RETAINS last frame's pixels, so the engine repaints only the damaged
        // rect — and tells us when the frame is identical, in which case nothing is converted or
        // presented at all.
        SKRectI? damage;
        using (var canvas = new SKCanvas(_bitmap))
        {
            if (animating) _doc.Animate(_clock.Elapsed.TotalSeconds);  // @keyframes
            var bg = _transparent ? SKColors.Transparent : _bg;        // overlays: page shows through
            // Scale the canvas, then let the engine damage-clip inside it: the clip it applies is
            // interpreted in the scaled space, which IS logical space, so the region it repaints is
            // right and only the rectangle it hands back needs converting to device pixels.
            //
            // This used to repaint the whole surface whenever the scale was not exactly 1, on the
            // grounds that damage coordinates would not map 1:1. They do not — but the mapping is a
            // multiply, because the scale is uniform. Scale 1 is the rare case, not the common one:
            // a HiDPI ratio of 2, fractional desktop scaling of 1.25, or any fit-to-viewport factor
            // all landed here, so most machines re-uploaded every pixel on every hover (#99).
            canvas.Save();
            if (_scale != 1f) canvas.Scale(_scale);
            damage = _doc.RenderIncremental(canvas, p.LogicalWidth, p.LogicalHeight, bg);
            canvas.Restore();
            if (damage is { } logical)
                damage = CupriDocument.ScaleDamageToDevice(logical, _scale, width, height);
            canvas.Flush();
        }
        if (damage is not { } d) return false;   // identical frame

        // What the page receives. Opaque apps present the premultiplied render directly; transparent
        // ones must present STRAIGHT alpha, which is what putImageData expects, so they convert into
        // a staging buffer (Skia unpremultiplies for us). Video holes need the same: their alpha-0
        // pixels only reach the page through the straight-alpha path.
        var present = _bitmap;
        if (_transparent || (_video?.AnyReady ?? false) || (_underlays?.Any ?? false))
        {
            var fresh = _straight is null || _straight.Width != width || _straight.Height != height;
            if (fresh)
            {
                _straight?.Dispose();
                _straight = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
            }
            using var src = _bitmap.PeekPixels();
            using var dst = _straight!.PeekPixels();
            if (fresh)
            {
                src.ReadPixels(dst);            // new staging buffer: everything converts once
            }
            else
            {
                // Only the damage rect changed. Converting the WHOLE bitmap per present cost a
                // full-frame pass for a 10 px repaint whenever any video was open; the rest of the
                // staging buffer already holds this frame's pixels from earlier presents.
                var rectInfo = new SKImageInfo(d.Width, d.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
                src.ReadPixels(rectInfo,
                    (nint)((byte*)dst.GetPixels() + d.Top * dst.RowBytes + d.Left * 4),
                    dst.RowBytes, d.Left, d.Top);
            }
            present = _straight;
        }

        // Zero-copy on both hosts: an address into wasm memory, never a managed copy. `.Bytes` would
        // allocate and copy ~2.7 MB every frame. The damage rect narrows the blit to what changed.
        _js.Present(present.GetPixels(), present.ByteCount, width, height, d.Left, d.Top, d.Width, d.Height);

        // Mirror the semantics tree so screen readers can read a canvas. Input-driven repaints only:
        // the tree changes on interaction, and re-parsing HTML 30x/s under a spinner is waste.
        if (!animating) _js.PublishAria(_doc.BuildAriaHtml(p.LogicalWidth, p.LogicalHeight));

        // IME placement, on the same cadence and only when it moved. The caret's BOTTOM is where a
        // candidate window belongs.
        if (!animating)
        {
            var ti = _doc.GetTextInputState();
            var r = ti.CaretRect ?? default;
            var cur = (ti.Focused, ti.Numeric, ti.Multiline, r.X, r.Y);
            if (cur != _lastTextInput)
            {
                _lastTextInput = cur;
                _js.SetTextInput(ti.Focused, ti.Numeric, ti.Multiline, r.X * _scale, (r.Y + r.H) * _scale);
            }
        }

        // Keep each underlaid element glued to its box: same painted frame, same page task as the
        // blit above, so the hole and the element cannot shear apart.
        // One syncer for every underlaid element. Video resolves to the player id it already
        // owns; a surface asking for a canvas gets one created here. Both then move identically
        // through the clip and transform chains.
        _underlays?.Sync(_doc, _scale, key => _video?.IdForSurface(key));
        return true;
    }

    // ---- pointer -------------------------------------------------------------------------------
    // Each Dispatch* returns whether anything actually changed; only then is a repaint marked.
    // (Marking dirty unconditionally repainted the whole canvas on every mouse-move, even over
    // empty space where hover did not change, saturating the CPU while moving.)

    private static float L(double v) => (float)(v / _scale);

    public static void PointerDown(double x, double y, int clicks)
    { if (_doc?.DispatchClick(L(x), L(y), clicks) == true) _dirty = true; UpdateCursor(x, y); }

    public static void PointerMove(double x, double y)
    { if (_doc?.DispatchPointerMove(L(x), L(y)) == true) _dirty = true; UpdateCursor(x, y); }

    public static void PointerUp(double x, double y)
    { if (_doc?.DispatchPointerUp(L(x), L(y)) == true) _dirty = true; UpdateCursor(x, y); }

    public static void ContextMenu(double x, double y)
    { if (_doc?.DispatchContextMenu(L(x), L(y)) == true) _dirty = true; }

    public static void Wheel(double x, double y, double dy)
    { if (_doc?.DispatchWheel(L(x), L(y), (float)dy) == true) _dirty = true; }

    /// <summary>Push the cursor for the current position — only when it changes, because assigning
    /// it on every mouse-move is needless DOM churn.</summary>
    private static void UpdateCursor(double x, double y)
    {
        if (_doc is null) return;
        var css = CupriDocument.CursorCss(_doc.CursorAt(L(x), L(y)));
        if (css != _cursor) { _cursor = css; _js.SetCursor(css); }
    }

    // ---- touch ---------------------------------------------------------------------------------
    // Routed exactly as the Android host routes it: an element that opted into raw pointers
    // (doc.OnPointer / doc.OnManipulate) CAPTURES the finger that lands on it and the single-pointer
    // recognizer never sees it — which is what stops a pinch from also scrolling the page beneath.
    // Everything uncaptured goes to the recognizer, one finger at a time.

    public static void TouchDown(int id, double x, double y, double tMs)
    {
        if (_doc is null) return;
        float lx = L(x), ly = L(y);
        if (_doc.DispatchPointer(id, PointerPhase.Down, lx, ly)) _dirty = true;
        CancelTouchForPageZoom(tMs);
        if (_doc.IsPointerCaptured(id) || _doc.PageZoomActive) return;
        if (_primaryPointer >= 0) return;                  // a second finger the app did not want
        _primaryPointer = id;
        if (_touch.Down(lx, ly, tMs / 1000.0)) _dirty = true;
    }

    public static void TouchMove(int id, double x, double y, double tMs)
    {
        if (_doc is null) return;
        float lx = L(x), ly = L(y);
        // Offered to the document first: captured pointers belong to their element, and an
        // uncaptured one may be half of a page-zoom pinch. Only the declined reach the recognizer.
        if (_doc.DispatchPointer(id, PointerPhase.Move, lx, ly)) { CancelTouchForPageZoom(tMs); _dirty = true; return; }
        if (id == _primaryPointer && _touch.Move(lx, ly, tMs / 1000.0)) _dirty = true;
    }

    public static void TouchUp(int id, double x, double y, double tMs)
    {
        if (_doc is null) return;
        float lx = L(x), ly = L(y);
        if (_doc.DispatchPointer(id, PointerPhase.Up, lx, ly)) { _dirty = true; return; }
        if (id != _primaryPointer) return;
        _primaryPointer = -1;
        if (_touch.Up(lx, ly, tMs / 1000.0)) _dirty = true;
    }

    /// <summary>The browser took the gesture away (scroll takeover, a system gesture, the tab
    /// hiding). A cancel must never become a click.</summary>
    public static void TouchCancel(int id, double tMs)
    {
        if (_doc is null) return;
        if (_doc.IsPointerCaptured(id)) { if (_doc.DispatchPointer(id, PointerPhase.Cancel, 0, 0)) _dirty = true; return; }
        if (id != _primaryPointer) return;
        _primaryPointer = -1;
        if (_touch.Cancel(tMs / 1000.0)) _dirty = true;
    }

    /// <summary>A page-zoom pinch took over: end the single-pointer gesture so a half-finished
    /// scroll cannot run alongside it, and so the finger never becomes a tap when it lifts.</summary>
    private static void CancelTouchForPageZoom(double tMs)
    {
        if (_doc is null || !_doc.PageZoomActive || _primaryPointer < 0) return;
        _primaryPointer = -1;
        if (_touch.Cancel(tMs / 1000.0)) _dirty = true;
    }

    /// <summary>What is driving the app right now. The engine puts it on the body as
    /// cupri-coarse/cupri-fine/cupri-nohover, so adapting is ordinary CSS. Reported from the POINTER
    /// actually in use rather than from the device: a laptop with a touchscreen is honestly both,
    /// and whichever the user just touched is the truthful answer.</summary>
    public static void SetCoarsePointer(bool coarse)
    {
        if (_doc is null) return;
        var next = coarse ? InputProfile.Touch : InputProfile.Desktop;
        if (_doc.InputProfile == next) return;
        _doc.InputProfile = next;
        _dirty = true;
    }

    // ---- keyboard, clipboard, IME --------------------------------------------------------------

    public static void KeyChar(string text) { if (_doc?.DispatchKey(text, EditKey.None) == true) _dirty = true; }
    public static void EditKeyPress(int code, int mods)
    { if (_doc?.DispatchKey(null, (EditKey)code, (KeyMods)mods) == true) _dirty = true; }

    /// <summary>A Ctrl/Cmd + letter chord. Returns whether the engine took it, so the page can
    /// preventDefault only then and otherwise leave the browser its own shortcuts.</summary>
    public static bool KeyChord(string text, int mods)
    {
        var handled = _doc?.DispatchKey(text, EditKey.None, (KeyMods)mods) == true;
        if (handled) _dirty = true;
        return handled;
    }

    public static void SetComposition(string text) { if (_doc?.SetComposition(text) == true) _dirty = true; }
    public static void CommitComposition(string text) { if (_doc?.CommitComposition(text) == true) _dirty = true; }
    public static void CancelComposition() { if (_doc?.ClearComposition() == true) _dirty = true; }

    public static string? CopySelection() => _doc?.CopySelection();
    public static string? CutSelection() { var t = _doc?.CutSelection(); _dirty = true; return t; }
    public static void Undo() { if (_doc?.Undo() == true) _dirty = true; }
    public static void Redo() { if (_doc?.Redo() == true) _dirty = true; }

    // ---- video events pushed in from the page --------------------------------------------------
    // The browser owns playback; these are its truth arriving. Shared, because what they do to a
    // player is the same wherever the call came from — only the declaration that receives them is
    // host-specific.

    public static void VideoMeta(int id, double duration, int width, int height)
    {
        if (_video?.Get(id) is not { } p) return;
        p.DurationSeconds = duration;
        p.Natural = width > 0 && height > 0 ? (width, height) : null;
        _dirty = true;   // intrinsic size may reflow the element
    }

    public static void VideoReady(int id)
    {
        if (_video?.Get(id) is not { } p) return;
        p.Ready = true;  // HostComposited flips on → the next paint punches the hole
        _dirty = true;
    }

    /// <summary>The browser's own play/pause truth, autoplay-policy rejections included: the
    /// engine's controls follow it, so they can never claim a playback the browser refused.</summary>
    public static void VideoPlayState(int id, bool playing)
    {
        if (_video?.Get(id) is not { } p) return;
        p.PlayingNow = playing;
        _doc?.Refresh();  // relabel the play/pause control
        _dirty = true;
    }

    public static void VideoTime(int id, double seconds)
    { if (_video?.Get(id) is { } p) p.PositionSeconds = seconds; }

    public static void VideoEnded(int id) => _video?.Get(id)?.RaiseEnded();

    public static bool IsCoarsePointer() => _doc?.InputProfile.CoarsePointer == true;

    /// <summary>True when the app renders as a transparent overlay — the page then makes the canvas
    /// transparent and passes pointer events through wherever nothing is drawn.</summary>
    public static bool IsTransparent() => _transparent;

    /// <summary>The EditKey wire codes, published ONCE. Both pages used to hand-copy these ordinals,
    /// which is the kind of duplicated contract that breaks silently when an enum member moves.</summary>
    public static string EditKeyMap() =>
        $"{{\"Backspace\":{(int)EditKey.Backspace},\"Delete\":{(int)EditKey.Delete}," +
        $"\"ArrowLeft\":{(int)EditKey.Left},\"ArrowRight\":{(int)EditKey.Right}," +
        $"\"Home\":{(int)EditKey.Home},\"End\":{(int)EditKey.End},\"Enter\":{(int)EditKey.Enter}," +
        $"\"ArrowUp\":{(int)EditKey.Up},\"ArrowDown\":{(int)EditKey.Down},\"Escape\":{(int)EditKey.Escape}," +
        $"\"Tab\":{(int)EditKey.Tab},\"ShiftTab\":{(int)EditKey.ShiftTab},\"SelectAll\":{(int)EditKey.SelectAll}}}";
}
