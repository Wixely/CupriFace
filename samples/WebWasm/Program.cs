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
    private static float _scale = 1f;           // Present scale, for un-scaling pointer coords
    private static SKBitmap? _bitmap;
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
            canvas.Clear(_bg);
            if (animating) _doc.Animate(_clock.Elapsed.TotalSeconds); // @keyframes (spinner)
            canvas.Save();
            if (_scale != 1f) canvas.Scale(_scale);
            _doc.Render(canvas, p.LogicalWidth, p.LogicalHeight);
            canvas.Restore();
            canvas.Flush();
        }

        // Zero-copy: hand JS a view over the bitmap's pixels in WASM memory (no per-frame
        // allocation or managed→JS copy — bitmap.Bytes would allocate + copy 2.7 MB each frame).
        unsafe
        {
            var span = new Span<byte>((void*)_bitmap.GetPixels(), _bitmap.ByteCount);
            Present(span, width, height);
        }
    }

    // Pointer + wheel + keyboard route through the SAME dispatch the desktop hosts use. Each
    // Dispatch* returns whether anything actually changed; only THEN mark dirty for a repaint.
    // (Marking dirty unconditionally repainted the whole 940x720 canvas on every mouse-move —
    // even over empty space where hover didn't change — saturating the CPU while moving.)
    [JSExport] internal static void PointerDown(double x, double y) { if (_doc?.DispatchClick((float)(x / _scale), (float)(y / _scale)) == true) _dirty = true; }
    [JSExport] internal static void PointerMove(double x, double y) { if (_doc?.DispatchPointerMove((float)(x / _scale), (float)(y / _scale)) == true) _dirty = true; }
    [JSExport] internal static void PointerUp(double x, double y) { _doc?.DispatchPointerUp((float)(x / _scale), (float)(y / _scale)); }
    [JSExport] internal static void Wheel(double x, double y, double dy) { if (_doc?.DispatchWheel((float)(x / _scale), (float)(y / _scale), (float)-dy) == true) _dirty = true; }
    [JSExport] internal static void KeyChar(string text) { if (_doc?.DispatchKey(text, EditKey.None) == true) _dirty = true; }
    [JSExport] internal static void EditKeyPress(int code) { if (_doc?.DispatchKey(null, (EditKey)code) == true) _dirty = true; }

    // JS side (module "cupri") copies the pixels into the 2D canvas via putImageData.
    [JSImport("present", "cupri")]
    internal static partial void Present([JSMarshalAs<JSType.MemoryView>] Span<byte> rgba, int width, int height);
}
