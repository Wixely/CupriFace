using System.Diagnostics;
using System.Runtime.InteropServices.JavaScript;
using CupriFace.Interaction;
using SkiaSharp;

namespace CupriFace.Web;

// The browser host. The engine renders a CupriApp to a CPU Skia surface; the JS half (main.js,
// shipped beside this file) blits the pixels to a <canvas> and forwards pointer, touch, wheel and
// keyboard input. This is the "thin JS glue over a canvas" model from DESIGN.md 9.1 — no browser
// engine and no JS in the UI itself.
//
// Everything here used to live in samples/WebWasm, which meant a second web app had to copy it,
// and copies arrived without the ARIA mirror, the IME and the touch recognizer because they are
// the parts you can omit and still see a first frame (#73). They are not optional now.

/// <summary>The JS-facing surface: every name here is bound by main.js, which ships in this same
/// package precisely so the two halves cannot drift apart. Internal — an app talks to
/// <see cref="WebHost"/>, never to this.</summary>
internal partial class Interop
{
    private static CupriApp _app = null!;
    private static CupriDocument _doc = null!;
    // The SAME recognizer the Android host uses — tap-on-release, slop, momentum fling,
    // long-press, axis lock, rubber band. It was portable from the day it was written; the web
    // host simply never called it, so a phone in a browser got desktop semantics: buttons that
    // fired on touch-down and lists that stopped dead instead of coasting.
    private static TouchInput _touch = null!;
    private static int _primaryPointer = -1;     // the recognizer follows one finger; apps may hold others
    private static SKColor _bg;
    private static bool _transparent;           // overlay mode: transparent clear + straight-alpha present
    private static float _scale = 1f;           // Present scale, for un-scaling pointer coords
    private static SKBitmap? _bitmap;
    private static SKBitmap? _straight;         // staging buffer for the premul→straight-alpha conversion
    private static readonly Stopwatch _clock = Stopwatch.StartNew();
    private static double _lastRefresh, _lastAnimMs;
    private static int _lastW, _lastH;          // last canvas size, to repaint on resize
    private static bool _dirty = true;          // render-on-demand: paint only when something changed

    /// <summary>What WebHost.Run was handed, before Init consumes it.</summary>
    internal static CupriApp? Pending;
    internal static Action<CupriDocument>? Configure;
    internal static bool Started;

    /// <summary>Create the app's document once. Called by main.js after the runtime is resident,
    /// which is after Main has run — so the app WebHost.Run registered is waiting here.</summary>
    [JSExport]
    internal static void Init()
    {
        _app = Pending ?? throw new InvalidOperationException(
            "No app was registered. A CupriFace.Web app's Main must call WebHost.Run(new MyApp()) — " +
            "the host is handed its app there, and the page boots into whatever Main registered.");
        Pending = null;
        Started = true;
        _doc = _app.CreateDocument();
        Configure?.Invoke(_doc);
        _touch = new TouchInput(_doc);

        // The wasm Skia build has ONE embedded face (Noto Mono) — without these, sans-serif silently
        // renders monospaced. Registered faces win over platform lookup; first family becomes the
        // generic sans target (see FontService).
        foreach (var res in new[] { "fonts.NotoSans-Regular.ttf", "fonts.NotoSans-Bold.ttf" })
        {
            using var fs = typeof(Interop).Assembly.GetManifestResourceStream(res)!;
            var buf = new byte[fs.Length];
            fs.ReadExactly(buf);
            _doc.LoadFont(buf);
        }
        _bg = _app.Background;
        _transparent = _app.Transparent;

        // The tab icon, from the app's own bytes — the same CupriApp.Icon the desktop host puts on
        // the window. index.html deliberately ships without a hard-coded copy: a second, hand-pasted
        // base64 of the logo is a duplicate that drifts the moment the real asset changes.
        if (_app.IconDataUri is { } favicon) SetFavicon(favicon);

        // External links open in a new browser tab (window.open). Internal routing + #anchors are handled
        // by the app / engine — same split as the desktop host.
        _doc.Navigated += e => { if (e.External) OpenUrl(e.Href); };

        // Video: the browser decodes (no codecs in the wasm binary). Each <cupri-video> gets an
        // underlaid <video> element; the engine punches a transparent hole where it shows and
        // paints its own controls on top. Rect/clip sync happens after each painted frame.
        _doc.UseVideo(new BrowserVideoBackend());

        // Fullscreen requests (the ⛶ control) go to the browser's Fullscreen API. Escape exits
        // natively; the resize that follows reflows the app like any window resize.
        _doc.WindowCommandRequested += cmd => WindowCommand((int)cmd);

        // Right-click menu → clipboard. The engine raises the chosen command; the browser owns the
        // clipboard (async), so route Copy/Cut/Paste through JS (same as the Ctrl+C/X/V handlers).
        _doc.ContextRequested += cmd =>
        {
            switch (cmd)
            {
                case ContextCommand.Copy: if (_doc.CopySelection() is { } cp) ClipboardWrite(cp); break;
                case ContextCommand.Cut: if (_doc.CutSelection() is { } ct) { ClipboardWrite(ct); _dirty = true; } break;
                case ContextCommand.Paste: ClipboardPaste(); break; // JS reads the clipboard, then calls KeyChar
                case ContextCommand.SelectAll: if (_doc.DispatchKey(null, EditKey.SelectAll)) _dirty = true; break;
            }
        };
    }

    /// <summary>Called every animation frame by JS. Renders ONLY when needed — after input, on
    /// the app's periodic re-bind, or (throttled) while a visible element is animating — so an
    /// idle page costs nothing. Returns true if it painted this frame.</summary>
    [JSExport]
    internal static bool Tick(int width, int height, double nowMs)
    {
        if (_doc is null || width <= 0 || height <= 0) return false;

        // Canvas resized (window resize) → repaint so scaling reflows to the new viewport.
        if (width != _lastW || height != _lastH) { _lastW = width; _lastH = height; _dirty = true; }

        // A background (remote) image finished loading → repaint so it appears.
        if (_doc.ConsumeImageArrived()) _dirty = true;

        // Live re-bind (e.g. the Diagnostics readout) on the app's cadence.
        if (_app.RefreshIntervalSeconds > 0 && _clock.Elapsed.TotalSeconds - _lastRefresh >= _app.RefreshIntervalSeconds)
        {
            _lastRefresh = _clock.Elapsed.TotalSeconds;
            _doc.Refresh();
            _dirty = true;
        }

        // A press held still becomes a context menu. The recognizer says WHEN it next wants
        // asking; the frame loop is already running, so it asks — no second timer, and the same
        // clock JS stamps its events with.
        if (_touch.NextDeadline is { } deadline && nowMs / 1000.0 >= deadline && _touch.Tick(nowMs / 1000.0))
            _dirty = true;

        // Continuous repaint only while something is actually animating, capped at ~30 fps.
        var animating = _doc.HasActiveAnimations;
        if (animating && nowMs - _lastAnimMs >= 33) { _lastAnimMs = nowMs; _dirty = true; }

        if (!_dirty) return false;
        _dirty = false;
        return Paint(width, height, animating);
    }

    private static bool Paint(int width, int height, bool animating)
    {
        var p = _app.Present(width, height);
        _scale = p.Scale <= 0 ? 1f : p.Scale;

        if (_bitmap is null || _bitmap.Width != width || _bitmap.Height != height)
        {
            _bitmap?.Dispose();
            _bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        }

        // The staging bitmap RETAINS last frame's pixels, so the engine repaints only the damaged
        // rect (and tells us when the frame is identical — then nothing is converted or presented).
        SKRectI? damage;
        using (var canvas = new SKCanvas(_bitmap))
        {
            if (animating) _doc.Animate(_clock.Elapsed.TotalSeconds); // @keyframes (spinner)
            var bg = _transparent ? SKColors.Transparent : _bg;       // overlays: page shows through
            if (_scale == 1f)
            {
                damage = _doc.RenderIncremental(canvas, p.LogicalWidth, p.LogicalHeight, bg);
            }
            else
            {
                // Scaled present (hybrid zoom): damage coords wouldn't map 1:1 — full frame.
                canvas.Clear(bg);
                canvas.Save();
                canvas.Scale(_scale);
                _doc.Render(canvas, p.LogicalWidth, p.LogicalHeight);
                canvas.Restore();
                damage = new SKRectI(0, 0, width, height);
            }
            canvas.Flush();
        }
        if (damage is not { } d) return false; // identical frame — skip conversion, present, and ARIA

        // The buffer we hand JS. Opaque apps present the premultiplied render directly. Transparent
        // apps must present STRAIGHT (non-premultiplied) alpha — that's what the browser's ImageData
        // / putImageData expects — so convert into a staging buffer (Skia unpremultiplies for us).
        // Video holes need the same: their alpha-0 pixels only work through the straight-alpha path.
        var present = _bitmap;
        if (_transparent || BrowserVideoBackend.AnyReady)
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
                src.ReadPixels(dst);                     // new staging buffer: everything converts once
            else
                unsafe
                {
                    // Only the damage rect changed — converting the WHOLE bitmap per present cost a
                    // full-frame pass for a 10 px repaint whenever any video was open. The rest of
                    // the staging buffer already holds this frame's pixels from earlier presents.
                    var rectInfo = new SKImageInfo(d.Width, d.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
                    src.ReadPixels(rectInfo,
                        (nint)((byte*)dst.GetPixels() + d.Top * dst.RowBytes + d.Left * 4),
                        dst.RowBytes, d.Left, d.Top);
                }
            present = _straight;
        }

        // Zero-copy: hand JS a view over the buffer's pixels in WASM memory (no per-frame
        // allocation or managed→JS copy — .Bytes would allocate + copy 2.7 MB each frame). The
        // damage rect narrows the putImageData blit to the changed region.
        unsafe
        {
            var span = new Span<byte>((void*)present.GetPixels(), present.ByteCount);
            Present(span, width, height, d.Left, d.Top, d.Width, d.Height);
        }

        // Mirror the semantics tree into the off-screen ARIA DOM so screen readers can read the
        // canvas UI. Only on input-driven repaints (not every animation frame) — the tree only
        // changes on interaction, and re-parsing HTML 30×/s during a spinner would be wasteful.
        if (!animating) A11y(_doc.BuildAriaHtml(p.LogicalWidth, p.LogicalHeight));

        // IME positioning: push the focus/kind/caret only when it moved (input-driven frames only,
        // like the mirror above). The caret's BOTTOM is where a candidate window belongs.
        if (!animating)
        {
            var ti = _doc.GetTextInputState();
            var r = ti.CaretRect ?? default;
            var cur = (ti.Focused, ti.Numeric, ti.Multiline, r.X, r.Y);
            if (cur != _lastTextInput)
            {
                _lastTextInput = cur;
                TextInputJs(ti.Focused, ti.Numeric, ti.Multiline, r.X * _scale, (r.Y + r.H) * _scale);
            }
        }

        // Keep each underlaid <video> glued to its element: same painted frame, same JS task as
        // the blit above, so the hole and the element can't shear apart.
        BrowserVideoBackend.SyncRects(_doc, _scale);
        return true;
    }

    // Pointer + wheel + keyboard route through the SAME dispatch the desktop hosts use. Each
    // Dispatch* returns whether anything actually changed; only THEN mark dirty for a repaint.
    // (Marking dirty unconditionally repainted the whole 940x720 canvas on every mouse-move —
    // even over empty space where hover didn't change — saturating the CPU while moving.)
    [JSExport] internal static void PointerDown(double x, double y, int clicks) { if (_doc?.DispatchClick((float)(x / _scale), (float)(y / _scale), clicks) == true) _dirty = true; UpdateCursor(x, y); }
    [JSExport] internal static void ContextMenu(double x, double y) { if (_doc?.DispatchContextMenu((float)(x / _scale), (float)(y / _scale)) == true) _dirty = true; }
    [JSExport] internal static void PointerMove(double x, double y) { if (_doc?.DispatchPointerMove((float)(x / _scale), (float)(y / _scale)) == true) _dirty = true; UpdateCursor(x, y); }
    [JSExport] internal static void PointerUp(double x, double y) { if (_doc?.DispatchPointerUp((float)(x / _scale), (float)(y / _scale)) == true) _dirty = true; UpdateCursor(x, y); }

    // Push the cursor for the current pointer position to the canvas (only when it changes — setting
    // canvas.style.cursor every mouse-move is needless DOM churn).
    private static string _cursor = "";
    private static (bool, bool, bool, float, float) _lastTextInput;
    private static void UpdateCursor(double x, double y)
    {
        if (_doc is null) return;
        var css = CupriDocument.CursorCss(_doc.CursorAt((float)(x / _scale), (float)(y / _scale)));
        if (css != _cursor) { _cursor = css; SetCursor(css); }
    }
    // Browser deltaY is PIXELS, positive = scroll down — the same direction ScrollY grows, so no
    // negation (desktop wheels report notches, positive = up; copying DesktopHost's -dy inverted us).
    // ---- touch ---------------------------------------------------------------------------------
    // Routed exactly as the Android host routes it: an element that opted into raw pointers
    // (doc.OnPointer / doc.OnManipulate) CAPTURES the finger that lands on it and the single-pointer
    // recognizer never sees that finger — which is what stops a pinch from also scrolling the page
    // underneath. Everything uncaptured goes to the recognizer, one finger at a time.
    private static float L(double v) => (float)(v / _scale);

    [JSExport]
    internal static void TouchDown(int id, double x, double y, double tMs)
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

    [JSExport]
    internal static void TouchMove(int id, double x, double y, double tMs)
    {
        if (_doc is null) return;
        float lx = L(x), ly = L(y);
        // Offered to the document first: captured pointers belong to their element, and an
        // uncaptured one may be half of a page-zoom pinch. Only the declined reach the recognizer.
        if (_doc.DispatchPointer(id, PointerPhase.Move, lx, ly)) { CancelTouchForPageZoom(tMs); _dirty = true; return; }
        if (id == _primaryPointer && _touch.Move(lx, ly, tMs / 1000.0)) _dirty = true;
    }

    [JSExport]
    internal static void TouchUp(int id, double x, double y, double tMs)
    {
        if (_doc is null) return;
        float lx = L(x), ly = L(y);
        if (_doc.DispatchPointer(id, PointerPhase.Up, lx, ly)) { _dirty = true; return; }
        if (id != _primaryPointer) return;
        _primaryPointer = -1;
        if (_touch.Up(lx, ly, tMs / 1000.0)) _dirty = true;
    }

    // A page-zoom pinch took over: end the single-pointer gesture so a half-finished scroll cannot
    // run alongside it, and so the finger never becomes a tap when it lifts.
    private static void CancelTouchForPageZoom(double tMs)
    {
        if (_doc is null || !_doc.PageZoomActive || _primaryPointer < 0) return;
        _primaryPointer = -1;
        if (_touch.Cancel(tMs / 1000.0)) _dirty = true;
    }

    /// <summary>The browser took the gesture away (scroll takeover, a system gesture, the tab
    /// hiding). A cancel must never become a click.</summary>
    [JSExport]
    internal static void TouchCancel(int id, double tMs)
    {
        if (_doc is null) return;
        if (_doc.IsPointerCaptured(id)) { if (_doc.DispatchPointer(id, PointerPhase.Cancel, 0, 0)) _dirty = true; return; }
        if (id != _primaryPointer) return;
        _primaryPointer = -1;
        if (_touch.Cancel(tMs / 1000.0)) _dirty = true;
    }

    /// <summary>What is driving the app right now. The engine puts it on the body as
    /// cupri-coarse/cupri-fine/cupri-nohover, so adapting is ordinary CSS. Reported from the
    /// POINTER that is actually being used rather than from the device: a laptop with a
    /// touchscreen is both, and whichever the user just touched is the truthful answer.</summary>
    [JSExport]
    internal static void SetCoarsePointer(bool coarse)
    {
        if (_doc is null) return;
        var next = coarse ? InputProfile.Touch : InputProfile.Desktop;
        if (_doc.InputProfile == next) return;
        _doc.InputProfile = next;
        _dirty = true;
    }

    [JSExport] internal static void Wheel(double x, double y, double dy) { if (_doc?.DispatchWheel((float)(x / _scale), (float)(y / _scale), (float)dy) == true) _dirty = true; }
    [JSExport] internal static void KeyChar(string text) { if (_doc?.DispatchKey(text, EditKey.None) == true) _dirty = true; }
    // The browser's own fullscreen transitions (its Esc key never reaches EditKeyPress).
    [JSExport] internal static void HostFullscreen(bool active) { _doc?.NotifyHostFullscreen(active); _dirty = true; }
    [JSExport] internal static void EditKeyPress(int code, int mods) { if (_doc?.DispatchKey(null, (EditKey)code, (KeyMods)mods) == true) _dirty = true; }
    // A Ctrl/Cmd + letter chord (e.g. Ctrl+K) → an app keyboard shortcut. Returns whether the engine handled
    // it, so the page can preventDefault only then and otherwise let the browser keep its own shortcuts.
    [JSExport] internal static bool KeyChord(string text, int mods) { var h = _doc?.DispatchKey(text, EditKey.None, (KeyMods)mods) == true; if (h) _dirty = true; return h; }

    // Clipboard bridge — the engine has no clipboard access (host concern); JS does the actual
    // navigator.clipboard I/O. Copy/Cut return the selected text; Paste inserts via KeyChar.
    // ---- IME composition (the engine seam Phase 3 built; the browser's composition events feed
    // ---- these — the web's CJK/dead-key path, previously impossible from keydown alone).
    [JSExport] internal static void SetComposition(string text) { if (_doc?.SetComposition(text) == true) _dirty = true; }
    [JSExport] internal static void CommitComposition(string text) { if (_doc?.CommitComposition(text) == true) _dirty = true; }
    [JSExport] internal static void CancelComposition() { if (_doc?.ClearComposition() == true) _dirty = true; }

    // The EditKey wire codes, exported ONCE — main.js used to hand-copy these ordinals, which is
    // the kind of duplicated contract that breaks silently when an enum member moves.
    [JSExport] internal static string EditKeyMap() =>
        $"{{\"Backspace\":{(int)EditKey.Backspace},\"Delete\":{(int)EditKey.Delete}," +
        $"\"ArrowLeft\":{(int)EditKey.Left},\"ArrowRight\":{(int)EditKey.Right}," +
        $"\"Home\":{(int)EditKey.Home},\"End\":{(int)EditKey.End},\"Enter\":{(int)EditKey.Enter}," +
        $"\"ArrowUp\":{(int)EditKey.Up},\"ArrowDown\":{(int)EditKey.Down},\"Escape\":{(int)EditKey.Escape}," +
        $"\"Tab\":{(int)EditKey.Tab},\"ShiftTab\":{(int)EditKey.ShiftTab},\"SelectAll\":{(int)EditKey.SelectAll}}}";

    [JSExport] internal static string? CopySelection() => _doc?.CopySelection();
    [JSExport] internal static string? CutSelection() { var t = _doc?.CutSelection(); _dirty = true; return t; }
    [JSExport] internal static void Undo() { if (_doc?.Undo() == true) _dirty = true; }
    [JSExport] internal static void Redo() { if (_doc?.Redo() == true) _dirty = true; }

    /// <summary>True when the app renders as a transparent overlay — JS then makes the canvas
    /// transparent and passes pointer events through wherever nothing is drawn.</summary>
    /// <summary>What the host told the engine it is being driven by. Exists so a browser test can
    /// check the CAPABILITY BOUNDARY — that a touch reached the document as a coarse pointer. What
    /// the engine then does with it (body classes, the cascade) is covered by InputProfileTests.</summary>
    [JSExport] internal static bool IsCoarsePointer() => _doc?.InputProfile.CoarsePointer == true;

    [JSExport] internal static bool IsTransparent() => _transparent;

    // JS side (module "cupri") copies the pixels into the 2D canvas via putImageData; the damage rect
    // (dx, dy, dw, dh) narrows the blit to the region this frame actually changed.
    [JSImport("present", "cupri")]
    internal static partial void Present([JSMarshalAs<JSType.MemoryView>] Span<byte> rgba, int width, int height,
        int dx, int dy, int dw, int dh);

    // Set the canvas cursor (JS assigns canvas.style.cursor). Called only when the cursor changes.
    [JSImport("cursor", "cupri")] internal static partial void SetCursor(string name);

    // Open an external link in a new tab (JS window.open). Wired from the app's Navigated handler.
    [JSImport("navigate", "cupri")] internal static partial void OpenUrl(string href);

    // Point the page's <link rel="icon"> at a data URI. The tab icon is this host's answer to the
    // window icon a desktop host sets from the same CupriApp.Icon bytes.
    [JSImport("favicon", "cupri")] internal static partial void SetFavicon(string dataUri);

    // Clipboard bridge for the context menu (the browser clipboard is async, so it lives in JS).
    [JSImport("clipboardWrite", "cupri")] internal static partial void ClipboardWrite(string text);
    [JSImport("clipboardPaste", "cupri")] internal static partial void ClipboardPaste();

    // Push the ARIA mirror HTML into the off-screen accessibility DOM (JS sets innerHTML).
    [JSImport("a11y", "cupri")] internal static partial void A11y(string html);

    // Text-input state for the IME: JS moves the hidden textarea to the caret (so the candidate
    // window appears AT the field, not at the page's top-left) and sets inputmode.
    [JSImport("textInput", "cupri")] internal static partial void TextInputJs(
        bool focused, bool numeric, bool multiline, double x, double y);
}
