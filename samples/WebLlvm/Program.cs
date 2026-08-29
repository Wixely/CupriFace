using System.Diagnostics;
using System.Runtime.InteropServices;
using CupriFace;
using CupriFace.Demo;
using CupriFace.Interaction;
using SkiaSharp;

// EXPERIMENTAL NativeAOT-LLVM web host — the same ShowcaseApp as WebWasm, but compiled with the
// runtimelab LLVM backend instead of running interpreted on Mono (see WebLlvm.csproj for why).
// There is no Mono runtime here, so no [JSImport]/[JSExport]: exports are UnmanagedCallersOnly
// (plain C ABI — ints, doubles, UTF-16 pointers) and imports are DllImports resolved at link time
// against the Emscripten JS library in wwwroot/imports.js. Semantics mirror WebWasm/Program.cs.
Console.WriteLine("[CupriFace] NativeAOT-LLVM host started.");

public static unsafe partial class Interop
{
    private static CupriApp _app = null!;
    private static CupriDocument _doc = null!;
    // The same portable recognizer the Android host and the WASM host use.
    private static TouchInput _touch = null!;
    private static int _primaryPointer = -1;

    // Hooks for the partial-class halves (BrowserVideo.cs): the video events arrive over the C ABI
    // and need to nudge the same render-on-demand state the input exports use.
    internal static void MarkDirty() => _dirty = true;
    internal static void RefreshDoc() { _doc?.Refresh(); _dirty = true; }
    internal static void NotifyHostFullscreen(bool active) { _doc?.NotifyHostFullscreen(active); _dirty = true; }
    private static SKColor _bg;
    private static bool _transparent;
    private static float _scale = 1f;
    private static (bool, bool, bool, float, float) _lastTextInput;
    private static SKBitmap? _bitmap;
    private static SKBitmap? _straight;
    private static readonly Stopwatch _clock = Stopwatch.StartNew();
    private static double _lastRefresh, _lastAnimMs;
    private static int _lastW, _lastH;
    private static bool _dirty = true;
    private static string _cursor = "";
    private static bool _crashed;

    // ---- imports (wwwroot/imports.js; DirectPInvoke("js") binds these at link time) --------------

    [DllImport("js", EntryPoint = "js_present")]
    private static extern void JsPresent(byte* rgba, int width, int height, int dx, int dy, int dw, int dh);

    [DllImport("js", EntryPoint = "js_cursor")]
    private static extern void JsCursor(char* utf16, int len);

    [DllImport("js", EntryPoint = "js_navigate")]
    private static extern void JsNavigate(char* utf16, int len);

    [DllImport("js", EntryPoint = "js_favicon")]
    private static extern void JsFavicon(char* utf16, int len);

    [DllImport("js", EntryPoint = "js_clip_write")]
    private static extern void JsClipWrite(char* utf16, int len);

    [DllImport("js", EntryPoint = "js_clip_paste")]
    private static extern void JsClipPaste(); // async on the JS side; feeds back via PasteText

    [DllImport("js", EntryPoint = "js_a11y")]
    private static extern void JsA11y(char* utf16, int len);

    [DllImport("js", EntryPoint = "js_window_command")]
    private static extern void JsWindowCommand(int command); // 0 toggle / 1 enter / 2 exit fullscreen

    // Where the caret is, and what kind of field it is in. The JS half moves the hidden textarea
    // there so an IME's candidate window opens AT the field rather than at the page origin, and
    // sets inputmode so a touch keyboard offers digits for a numeric field. ints, not bools: the
    // C ABI this host talks over has no bool.
    [DllImport("js", EntryPoint = "js_text_input")]
    private static extern void JsTextInput(int focused, int numeric, int multiline, double x, double y);

    // Synchronous JS calls inside the fixed scope — the pointer is only valid for the call.
    private static void SendCursor(string s) { fixed (char* p = s) JsCursor(p, s.Length); }
    private static void SendNavigate(string s) { fixed (char* p = s) JsNavigate(p, s.Length); }
    private static void SendFavicon(string s) { fixed (char* p = s) JsFavicon(p, s.Length); }
    private static void SendClipWrite(string s) { fixed (char* p = s) JsClipWrite(p, s.Length); }
    private static void SendA11y(string s) { fixed (char* p = s) JsA11y(p, s.Length); }

    // ---- shared string buffers (no malloc/free exports needed) -----------------------------------

    // JS → C#: JS asks for a buffer of N chars, writes UTF-16 into it, then calls the consuming
    // export with the length. One live buffer is enough — all input is dispatched synchronously.
    private static char* _inBuf;
    private static int _inBufChars;

    [UnmanagedCallersOnly(EntryPoint = "TextBuffer")]
    public static char* TextBuffer(int chars)
    {
        if (chars > _inBufChars)
        {
            if (_inBuf is not null) NativeMemory.Free(_inBuf);
            _inBufChars = Math.Max(chars, 256);
            _inBuf = (char*)NativeMemory.Alloc((nuint)(_inBufChars * sizeof(char)));
        }
        return _inBuf;
    }

    // C# → JS: returned strings live in a reused null-terminated buffer the JS side consumes
    // immediately (UTF16ToString) before any other call can overwrite it.
    private static char* _outBuf;
    private static int _outBufChars;

    private static char* OutString(string? s)
    {
        if (s is null) return null;
        if (s.Length + 1 > _outBufChars)
        {
            if (_outBuf is not null) NativeMemory.Free(_outBuf);
            _outBufChars = Math.Max(s.Length + 1, 256);
            _outBuf = (char*)NativeMemory.Alloc((nuint)(_outBufChars * sizeof(char)));
        }
        s.CopyTo(new Span<char>(_outBuf, s.Length));
        _outBuf[s.Length] = '\0';
        return _outBuf;
    }

    // ---- lifecycle -------------------------------------------------------------------------------

    [UnmanagedCallersOnly(EntryPoint = "Init")]
    public static void Init()
    {
        try
        {
            _app = new ShowcaseApp();
            _doc = _app.CreateDocument();
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

            // Tab icon from CupriApp.Icon, exactly as the Mono host does it — index.html carries no
            // hard-coded copy, so the logo has one home (Assets/logo-512.png).
            if (_app.IconDataUri is { } favicon) SendFavicon(favicon);

            _doc.Navigated += e => { if (e.External) SendNavigate(e.Href); };
            // Video: the browser decodes into underlaid <video> elements (no codecs in the wasm
            // binary) — the same design as the Mono host, over the C ABI. Fullscreen requests go
            // to the browser's Fullscreen API.
            _doc.UseVideo(new LlvmBrowserVideoBackend());
            _doc.WindowCommandRequested += cmd => JsWindowCommand((int)cmd);
            _doc.ContextRequested += cmd =>
            {
                switch (cmd)
                {
                    case ContextCommand.Copy: if (_doc.CopySelection() is { } cp) SendClipWrite(cp); break;
                    case ContextCommand.Cut: if (_doc.CutSelection() is { } ct) { SendClipWrite(ct); _dirty = true; } break;
                    case ContextCommand.Paste: JsClipPaste(); break;
                    case ContextCommand.SelectAll: if (_doc.DispatchKey(null, EditKey.SelectAll)) _dirty = true; break;
                }
            };
            Console.WriteLine("[CupriFace] Init ok (LLVM).");
        }
        catch (Exception ex) { Crash("Init", ex); }
    }

    [UnmanagedCallersOnly(EntryPoint = "Tick")]
    public static int Tick(int width, int height, double nowMs)
    {
        if (_crashed || _doc is null || width <= 0 || height <= 0) return 0;
        try
        {
            if (width != _lastW || height != _lastH) { _lastW = width; _lastH = height; _dirty = true; }
            if (_doc.ConsumeImageArrived()) _dirty = true;
            if (_app.RefreshIntervalSeconds > 0 && _clock.Elapsed.TotalSeconds - _lastRefresh >= _app.RefreshIntervalSeconds)
            {
                _lastRefresh = _clock.Elapsed.TotalSeconds;
                _doc.Refresh();
                _dirty = true;
            }
            // Long-press: the recognizer names its own deadline and the frame loop asks.
            if (_touch is not null && _touch.NextDeadline is { } deadline
                && nowMs / 1000.0 >= deadline && _touch.Tick(nowMs / 1000.0)) _dirty = true;

            var animating = _doc.HasActiveAnimations;
            if (animating && nowMs - _lastAnimMs >= 33) { _lastAnimMs = nowMs; _dirty = true; }
            if (!_dirty) return 0;
            _dirty = false;
            return Paint(width, height, animating) ? 1 : 0;
        }
        catch (Exception ex) { Crash("Tick", ex); return 0; }
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

        SKRectI? damage;
        using (var canvas = new SKCanvas(_bitmap))
        {
            if (animating) _doc.Animate(_clock.Elapsed.TotalSeconds);
            var bg = _transparent ? SKColors.Transparent : _bg;
            if (_scale == 1f)
            {
                damage = _doc.RenderIncremental(canvas, p.LogicalWidth, p.LogicalHeight, bg);
            }
            else
            {
                canvas.Clear(bg);
                canvas.Save();
                canvas.Scale(_scale);
                _doc.Render(canvas, p.LogicalWidth, p.LogicalHeight);
                canvas.Restore();
                damage = new SKRectI(0, 0, width, height);
            }
            canvas.Flush();
        }
        if (damage is not { } d) return false;

        var present = _bitmap;
        // Straight alpha whenever a video underlay can show pixels, not only for transparent
        // apps: the engine punches alpha-0 holes over the underlays, and premultiplied bytes
        // through putImageData would never let that transparency reach the page. Only the damage
        // rect converts (the rest of the staging buffer already holds this frame's pixels) — a
        // full-frame pass per present made every interaction pay for an open video.
        if (_transparent || LlvmBrowserVideoBackend.AnyReady)
        {
            var fresh = _straight is null || _straight.Width != width || _straight.Height != height;
            if (fresh)
            {
                _straight?.Dispose();
                _straight = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
            }
            using var src = _bitmap.PeekPixels();
            using var dstPix = _straight!.PeekPixels();
            if (fresh)
                src.ReadPixels(dstPix);
            else
            {
                var rectInfo = new SKImageInfo(d.Width, d.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
                src.ReadPixels(rectInfo,
                    (nint)((byte*)dstPix.GetPixels() + d.Top * dstPix.RowBytes + d.Left * 4),
                    dstPix.RowBytes, d.Left, d.Top);
            }
            present = _straight;
        }

        // TRUE zero-copy: the SKBitmap's pixels live in wasm linear memory; JS wraps HEAPU8 at this
        // address in a Uint8ClampedArray view and putImageData blits the damage rect.
        JsPresent((byte*)present.GetPixels(), width, height, d.Left, d.Top, d.Width, d.Height);

        // Same JS task as the blit: the underlaid <video> rects/clips move WITH the hole, never
        // a frame apart (scroll, reflow, the size-transition demo, fullscreen).
        LlvmBrowserVideoBackend.SyncRects(_doc, _scale);

        if (!animating) SendA11y(_doc.BuildAriaHtml(p.LogicalWidth, p.LogicalHeight));

        // IME placement, on the same input-driven cadence as the mirror above and only when it
        // actually changed — the caret's BOTTOM is where a candidate window belongs.
        if (!animating)
        {
            var ti = _doc.GetTextInputState();
            var r = ti.CaretRect ?? default;
            var cur = (ti.Focused, ti.Numeric, ti.Multiline, r.X, r.Y);
            if (cur != _lastTextInput)
            {
                _lastTextInput = cur;
                JsTextInput(ti.Focused ? 1 : 0, ti.Numeric ? 1 : 0, ti.Multiline ? 1 : 0,
                            r.X * _scale, (r.Y + r.H) * _scale);
            }
        }
        return true;
    }

    // ---- input (mirrors WebWasm; cursor pushed only when it changes) -----------------------------

    private static void UpdateCursor(double x, double y)
    {
        var css = CupriDocument.CursorCss(_doc.CursorAt((float)(x / _scale), (float)(y / _scale)));
        if (css == _cursor) return;
        _cursor = css;
        SendCursor(css);
    }

    // ---- touch ---------------------------------------------------------------------------------
    // Mirrors the WASM host exactly: raw-pointer elements capture their finger, everything else
    // goes to the single-pointer recognizer (tap on RELEASE, slop, momentum, long-press).
    private static float L(double v) => (float)(v / _scale);

    [UnmanagedCallersOnly(EntryPoint = "TouchDown")]
    public static void TouchDown(int id, double x, double y, double tMs)
    {
        try
        {
            float lx = L(x), ly = L(y);
            if (_doc.DispatchPointer(id, PointerPhase.Down, lx, ly)) _dirty = true;
            CancelTouchForPageZoom(tMs);
            if (_doc.IsPointerCaptured(id) || _doc.PageZoomActive) return;
            if (_primaryPointer >= 0) return;
            _primaryPointer = id;
            if (_touch.Down(lx, ly, tMs / 1000.0)) _dirty = true;
        }
        catch (Exception ex) { Crash("TouchDown", ex); }
    }

    [UnmanagedCallersOnly(EntryPoint = "TouchMove")]
    public static void TouchMove(int id, double x, double y, double tMs)
    {
        try
        {
            float lx = L(x), ly = L(y);
            // Offered to the document first: captured pointers belong to their element, and an
            // uncaptured one may be half of a page-zoom pinch. Only the declined reach the recognizer.
            if (_doc.DispatchPointer(id, PointerPhase.Move, lx, ly)) { CancelTouchForPageZoom(tMs); _dirty = true; return; }
            if (id == _primaryPointer && _touch.Move(lx, ly, tMs / 1000.0)) _dirty = true;
        }
        catch (Exception ex) { Crash("TouchMove", ex); }
    }

    [UnmanagedCallersOnly(EntryPoint = "TouchUp")]
    public static void TouchUp(int id, double x, double y, double tMs)
    {
        try
        {
            float lx = L(x), ly = L(y);
            if (_doc.DispatchPointer(id, PointerPhase.Up, lx, ly)) { _dirty = true; return; }
            if (id != _primaryPointer) return;
            _primaryPointer = -1;
            if (_touch.Up(lx, ly, tMs / 1000.0)) _dirty = true;
        }
        catch (Exception ex) { Crash("TouchUp", ex); }
    }

    // A page-zoom pinch took over: end the single-pointer gesture so a half-finished scroll cannot
    // run alongside it, and so the finger never becomes a tap when it lifts.
    private static void CancelTouchForPageZoom(double tMs)
    {
        if (!_doc.PageZoomActive || _primaryPointer < 0) return;
        _primaryPointer = -1;
        if (_touch.Cancel(tMs / 1000.0)) _dirty = true;
    }

    [UnmanagedCallersOnly(EntryPoint = "TouchCancel")]
    public static void TouchCancel(int id, double tMs)
    {
        try
        {
            if (_doc.IsPointerCaptured(id))
            { if (_doc.DispatchPointer(id, PointerPhase.Cancel, 0, 0)) _dirty = true; return; }
            if (id != _primaryPointer) return;
            _primaryPointer = -1;
            if (_touch.Cancel(tMs / 1000.0)) _dirty = true;
        }
        catch (Exception ex) { Crash("TouchCancel", ex); }
    }

    /// <summary>What the host told the engine it is being driven by — the twin of the WASM host's
    /// export, so one browser gate can drive either host through the same contract.</summary>
    [UnmanagedCallersOnly(EntryPoint = "IsCoarsePointer")]
    public static int IsCoarsePointer()
    {
        try { return _doc.InputProfile.CoarsePointer ? 1 : 0; } catch { return 0; }
    }

    [UnmanagedCallersOnly(EntryPoint = "SetCoarsePointer")]
    public static void SetCoarsePointer(int coarse)
    {
        try
        {
            var next = coarse != 0 ? InputProfile.Touch : InputProfile.Desktop;
            if (_doc.InputProfile == next) return;
            _doc.InputProfile = next;
            _dirty = true;
        }
        catch (Exception ex) { Crash("SetCoarsePointer", ex); }
    }

    [UnmanagedCallersOnly(EntryPoint = "PointerDown")]
    public static void PointerDown(double x, double y, int clicks)
    { try { if (_doc.DispatchClick((float)(x / _scale), (float)(y / _scale), clicks)) _dirty = true; UpdateCursor(x, y); } catch (Exception ex) { Crash("PointerDown", ex); } }

    [UnmanagedCallersOnly(EntryPoint = "PointerMove")]
    public static void PointerMove(double x, double y)
    { try { if (_doc.DispatchPointerMove((float)(x / _scale), (float)(y / _scale))) _dirty = true; UpdateCursor(x, y); } catch (Exception ex) { Crash("PointerMove", ex); } }

    [UnmanagedCallersOnly(EntryPoint = "PointerUp")]
    public static void PointerUp(double x, double y)
    { try { if (_doc.DispatchPointerUp((float)(x / _scale), (float)(y / _scale))) _dirty = true; UpdateCursor(x, y); } catch (Exception ex) { Crash("PointerUp", ex); } }

    [UnmanagedCallersOnly(EntryPoint = "ContextMenu")]
    public static void ContextMenu(double x, double y)
    { try { if (_doc.DispatchContextMenu((float)(x / _scale), (float)(y / _scale))) _dirty = true; } catch (Exception ex) { Crash("ContextMenu", ex); } }

    [UnmanagedCallersOnly(EntryPoint = "Wheel")]
    public static void Wheel(double x, double y, double dy)
    // Browser deltaY is PIXELS, positive = scroll down — the same direction ScrollY grows, so it passes
    // straight through. (Desktop wheels report notches, positive = up, hence DesktopHost's negation —
    // copying that here inverted scrolling in the browser.)
    { try { if (_doc.DispatchWheel((float)(x / _scale), (float)(y / _scale), (float)dy)) _dirty = true; } catch (Exception ex) { Crash("Wheel", ex); } }

    [UnmanagedCallersOnly(EntryPoint = "KeyChar")]
    public static void KeyChar(int len)
    { try { if (_doc.DispatchKey(new string(_inBuf, 0, len), EditKey.None)) _dirty = true; } catch (Exception ex) { Crash("KeyChar", ex); } }

    [UnmanagedCallersOnly(EntryPoint = "PasteText")]
    public static void PasteText(int len)
    { try { if (_doc.DispatchKey(new string(_inBuf, 0, len), EditKey.None)) _dirty = true; } catch (Exception ex) { Crash("PasteText", ex); } }

    [UnmanagedCallersOnly(EntryPoint = "SetComposition")]
    public static void SetComposition(int len)
    { try { if (_doc.SetComposition(new string(_inBuf, 0, len))) _dirty = true; } catch (Exception ex) { Crash("SetComposition", ex); } }

    [UnmanagedCallersOnly(EntryPoint = "CommitComposition")]
    public static void CommitComposition(int len)
    { try { if (_doc.CommitComposition(new string(_inBuf, 0, len))) _dirty = true; } catch (Exception ex) { Crash("CommitComposition", ex); } }

    [UnmanagedCallersOnly(EntryPoint = "EditKeyMap")]
    public static char* EditKeyMap()
    {
        // The engine's own wire codes — the JS fallback table is replaced with this at boot.
        return OutString(
            $"{{\"Backspace\":{(int)EditKey.Backspace},\"Delete\":{(int)EditKey.Delete}," +
            $"\"ArrowLeft\":{(int)EditKey.Left},\"ArrowRight\":{(int)EditKey.Right}," +
            $"\"Home\":{(int)EditKey.Home},\"End\":{(int)EditKey.End},\"Enter\":{(int)EditKey.Enter}," +
            $"\"ArrowUp\":{(int)EditKey.Up},\"ArrowDown\":{(int)EditKey.Down},\"Escape\":{(int)EditKey.Escape}," +
            $"\"Tab\":{(int)EditKey.Tab},\"ShiftTab\":{(int)EditKey.ShiftTab},\"SelectAll\":{(int)EditKey.SelectAll}}}");
    }

    [UnmanagedCallersOnly(EntryPoint = "EditKeyPress")]
    public static void EditKeyPress(int code, int mods)
    { try { if (_doc.DispatchKey(null, (EditKey)code, (KeyMods)mods)) _dirty = true; } catch (Exception ex) { Crash("EditKeyPress", ex); } }

    [UnmanagedCallersOnly(EntryPoint = "KeyChord")]
    public static int KeyChord(int ch, int mods)
    {
        try
        {
            var handled = _doc.DispatchKey(((char)ch).ToString(), EditKey.None, (KeyMods)mods);
            if (handled) _dirty = true;
            return handled ? 1 : 0;
        }
        catch (Exception ex) { Crash("KeyChord", ex); return 0; }
    }

    [UnmanagedCallersOnly(EntryPoint = "CopySelection")]
    public static char* CopySelection()
    { try { return OutString(_doc.CopySelection()); } catch (Exception ex) { Crash("CopySelection", ex); return null; } }

    [UnmanagedCallersOnly(EntryPoint = "CutSelection")]
    public static char* CutSelection()
    { try { var t = _doc.CutSelection(); _dirty = true; return OutString(t); } catch (Exception ex) { Crash("CutSelection", ex); return null; } }

    [UnmanagedCallersOnly(EntryPoint = "Undo")]
    public static void Undo() { try { if (_doc.Undo()) _dirty = true; } catch (Exception ex) { Crash("Undo", ex); } }

    [UnmanagedCallersOnly(EntryPoint = "Redo")]
    public static void Redo() { try { if (_doc.Redo()) _dirty = true; } catch (Exception ex) { Crash("Redo", ex); } }

    [UnmanagedCallersOnly(EntryPoint = "IsTransparent")]
    public static int IsTransparent() => _transparent ? 1 : 0;

    // A throw across an UnmanagedCallersOnly boundary is undefined — catch, report once, stop.
    private static void Crash(string where, Exception ex)
    {
        if (_crashed) return;
        _crashed = true;
        Console.WriteLine($"[CupriFace] CRASH in {where}: {ex}");
    }
}

// (No explicit Program class: the top-level statement above IS Main — it runs, prints, and
// returns; the runtime stays resident and JS drives everything through the exports.)
