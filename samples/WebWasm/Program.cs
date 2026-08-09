using System.Diagnostics;
using System.Runtime.InteropServices.JavaScript;
using CupriFace;
using CupriFace.Demo;
using CupriFace.Interaction;
using SkiaSharp;

// Raw .NET WebAssembly host — no Blazor. The engine renders the SAME ShowcaseApp the desktop
// Viewer runs to a CPU Skia surface; the thin JS glue (main.js) blits the pixels to a <canvas>
// and forwards pointer/wheel/keyboard input. This is the "thin JS glue over a canvas" model
// from DESIGN.md §9.1 — no browser engine and no JS in the UI itself.
Console.WriteLine("[CupriFace] WASM runtime started.");

public partial class Interop
{
    private static CupriApp _app = null!;
    private static CupriDocument _doc = null!;
    private static SKColor _bg;
    private static bool _transparent;           // overlay mode: transparent clear + straight-alpha present
    private static float _scale = 1f;           // Present scale, for un-scaling pointer coords
    private static SKBitmap? _bitmap;
    private static SKBitmap? _straight;         // staging buffer for the premul→straight-alpha conversion
    private static readonly Stopwatch _clock = Stopwatch.StartNew();
    private static double _lastRefresh, _lastAnimMs;
    private static int _lastW, _lastH;          // last canvas size, to repaint on resize
    private static bool _dirty = true;          // render-on-demand: paint only when something changed

    /// <summary>Create the shared app's document once.</summary>
    [JSExport]
    internal static void Init()
    {
        _app = new ShowcaseApp();
        _doc = _app.CreateDocument();
        _bg = _app.Background;
        _transparent = _app.Transparent;

        // External links open in a new browser tab (window.open). Internal routing + #anchors are handled
        // by the app / engine — same split as the desktop host.
        _doc.Navigated += e => { if (e.External) OpenUrl(e.Href); };

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
        var present = _bitmap;
        if (_transparent)
        {
            if (_straight is null || _straight.Width != width || _straight.Height != height)
            {
                _straight?.Dispose();
                _straight = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
            }
            _bitmap.PeekPixels().ReadPixels(_straight.PeekPixels()); // premul → straight
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
    private static void UpdateCursor(double x, double y)
    {
        if (_doc is null) return;
        var css = CupriDocument.CursorCss(_doc.CursorAt((float)(x / _scale), (float)(y / _scale)));
        if (css != _cursor) { _cursor = css; SetCursor(css); }
    }
    [JSExport] internal static void Wheel(double x, double y, double dy) { if (_doc?.DispatchWheel((float)(x / _scale), (float)(y / _scale), (float)-dy) == true) _dirty = true; }
    [JSExport] internal static void KeyChar(string text) { if (_doc?.DispatchKey(text, EditKey.None) == true) _dirty = true; }
    [JSExport] internal static void EditKeyPress(int code, int mods) { if (_doc?.DispatchKey(null, (EditKey)code, (KeyMods)mods) == true) _dirty = true; }
    // A Ctrl/Cmd + letter chord (e.g. Ctrl+K) → an app keyboard shortcut. Returns whether the engine handled
    // it, so the page can preventDefault only then and otherwise let the browser keep its own shortcuts.
    [JSExport] internal static bool KeyChord(string text, int mods) { var h = _doc?.DispatchKey(text, EditKey.None, (KeyMods)mods) == true; if (h) _dirty = true; return h; }

    // Clipboard bridge — the engine has no clipboard access (host concern); JS does the actual
    // navigator.clipboard I/O. Copy/Cut return the selected text; Paste inserts via KeyChar.
    [JSExport] internal static string? CopySelection() => _doc?.CopySelection();
    [JSExport] internal static string? CutSelection() { var t = _doc?.CutSelection(); _dirty = true; return t; }
    [JSExport] internal static void Undo() { if (_doc?.Undo() == true) _dirty = true; }
    [JSExport] internal static void Redo() { if (_doc?.Redo() == true) _dirty = true; }

    /// <summary>True when the app renders as a transparent overlay — JS then makes the canvas
    /// transparent and passes pointer events through wherever nothing is drawn.</summary>
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

    // Clipboard bridge for the context menu (the browser clipboard is async, so it lives in JS).
    [JSImport("clipboardWrite", "cupri")] internal static partial void ClipboardWrite(string text);
    [JSImport("clipboardPaste", "cupri")] internal static partial void ClipboardPaste();

    // Push the ARIA mirror HTML into the off-screen accessibility DOM (JS sets innerHTML).
    [JSImport("a11y", "cupri")] internal static partial void A11y(string html);
}
