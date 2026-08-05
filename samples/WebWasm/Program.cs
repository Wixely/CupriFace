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
        Paint(width, height, animating);
        return true;
    }

    private static void Paint(int width, int height, bool animating)
    {
        var p = _app.Present(width, height);
        _scale = p.Scale <= 0 ? 1f : p.Scale;

        if (_bitmap is null || _bitmap.Width != width || _bitmap.Height != height)
        {
            _bitmap?.Dispose();
            _bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        }

        using (var canvas = new SKCanvas(_bitmap))
        {
            // Overlay apps clear transparent so the HTML page shows through the canvas.
            canvas.Clear(_transparent ? SKColors.Transparent : _bg);
            if (animating) _doc.Animate(_clock.Elapsed.TotalSeconds); // @keyframes (spinner)
            canvas.Save();
            if (_scale != 1f) canvas.Scale(_scale);
            _doc.Render(canvas, p.LogicalWidth, p.LogicalHeight);
            canvas.Restore();
            canvas.Flush();
        }

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
        // allocation or managed→JS copy — .Bytes would allocate + copy 2.7 MB each frame).
        unsafe
        {
            var span = new Span<byte>((void*)present.GetPixels(), present.ByteCount);
            Present(span, width, height);
        }

        // Mirror the semantics tree into the off-screen ARIA DOM so screen readers can read the
        // canvas UI. Only on input-driven repaints (not every animation frame) — the tree only
        // changes on interaction, and re-parsing HTML 30×/s during a spinner would be wasteful.
        if (!animating) A11y(_doc.BuildAriaHtml(p.LogicalWidth, p.LogicalHeight));
    }

    // Pointer + wheel + keyboard route through the SAME dispatch the desktop hosts use. Each
    // Dispatch* returns whether anything actually changed; only THEN mark dirty for a repaint.
    // (Marking dirty unconditionally repainted the whole 940x720 canvas on every mouse-move —
    // even over empty space where hover didn't change — saturating the CPU while moving.)
    [JSExport] internal static void PointerDown(double x, double y, int clicks) { if (_doc?.DispatchClick((float)(x / _scale), (float)(y / _scale), clicks) == true) _dirty = true; }
    [JSExport] internal static void ContextMenu(double x, double y) { if (_doc?.DispatchContextMenu((float)(x / _scale), (float)(y / _scale)) == true) _dirty = true; }
    [JSExport] internal static void PointerMove(double x, double y) { if (_doc?.DispatchPointerMove((float)(x / _scale), (float)(y / _scale)) == true) _dirty = true; }
    [JSExport] internal static void PointerUp(double x, double y) { if (_doc?.DispatchPointerUp((float)(x / _scale), (float)(y / _scale)) == true) _dirty = true; }
    [JSExport] internal static void Wheel(double x, double y, double dy) { if (_doc?.DispatchWheel((float)(x / _scale), (float)(y / _scale), (float)-dy) == true) _dirty = true; }
    [JSExport] internal static void KeyChar(string text) { if (_doc?.DispatchKey(text, EditKey.None) == true) _dirty = true; }
    [JSExport] internal static void EditKeyPress(int code, int mods) { if (_doc?.DispatchKey(null, (EditKey)code, (KeyMods)mods) == true) _dirty = true; }

    // Clipboard bridge — the engine has no clipboard access (host concern); JS does the actual
    // navigator.clipboard I/O. Copy/Cut return the selected text; Paste inserts via KeyChar.
    [JSExport] internal static string? CopySelection() => _doc?.CopySelection();
    [JSExport] internal static string? CutSelection() { var t = _doc?.CutSelection(); _dirty = true; return t; }
    [JSExport] internal static void Undo() { if (_doc?.Undo() == true) _dirty = true; }
    [JSExport] internal static void Redo() { if (_doc?.Redo() == true) _dirty = true; }

    /// <summary>True when the app renders as a transparent overlay — JS then makes the canvas
    /// transparent and passes pointer events through wherever nothing is drawn.</summary>
    [JSExport] internal static bool IsTransparent() => _transparent;

    // JS side (module "cupri") copies the pixels into the 2D canvas via putImageData.
    [JSImport("present", "cupri")]
    internal static partial void Present([JSMarshalAs<JSType.MemoryView>] Span<byte> rgba, int width, int height);

    // Clipboard bridge for the context menu (the browser clipboard is async, so it lives in JS).
    [JSImport("clipboardWrite", "cupri")] internal static partial void ClipboardWrite(string text);
    [JSImport("clipboardPaste", "cupri")] internal static partial void ClipboardPaste();

    // Push the ARIA mirror HTML into the off-screen accessibility DOM (JS sets innerHTML).
    [JSImport("a11y", "cupri")] internal static partial void A11y(string html);
}
