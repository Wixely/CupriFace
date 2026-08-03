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
    private static double _lastRefresh;

    /// <summary>Create the shared app's document once.</summary>
    [JSExport]
    internal static void Init()
    {
        _app = new ShowcaseApp();
        _doc = _app.CreateDocument();
        _bg = _app.Background;
    }

    /// <summary>Render one frame (Present scale + animations + periodic re-bind) and hand the
    /// RGBA pixels to JS for <c>putImageData</c>. Mirrors the desktop host's draw loop.</summary>
    [JSExport]
    internal static void RenderFrame(int width, int height)
    {
        if (_doc is null || width <= 0 || height <= 0) return;

        var p = _app.Present(width, height);
        _scale = p.Scale <= 0 ? 1f : p.Scale;

        // Live values (e.g. the Diagnostics RAM readout) re-read on the app's cadence.
        if (_app.RefreshIntervalSeconds > 0 && _clock.Elapsed.TotalSeconds - _lastRefresh >= _app.RefreshIntervalSeconds)
        {
            _lastRefresh = _clock.Elapsed.TotalSeconds;
            _doc.Refresh();
        }

        if (_bitmap is null || _bitmap.Width != width || _bitmap.Height != height)
        {
            _bitmap?.Dispose();
            _bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        }

        using var canvas = new SKCanvas(_bitmap);
        canvas.Clear(_bg);
        if (_doc.HasAnimations) _doc.Animate(_clock.Elapsed.TotalSeconds); // @keyframes (spinner)
        canvas.Save();
        if (_scale != 1f) canvas.Scale(_scale);
        _doc.Render(canvas, p.LogicalWidth, p.LogicalHeight);
        canvas.Restore();
        canvas.Flush();

        Present(_bitmap.Bytes, width, height);
    }

    // Pointer + wheel + keyboard route through the SAME dispatch the desktop hosts use; the
    // rAF loop repaints the next frame, so these don't render directly.
    [JSExport] internal static void PointerDown(double x, double y) => _doc?.DispatchClick((float)(x / _scale), (float)(y / _scale));
    [JSExport] internal static void PointerMove(double x, double y) => _doc?.DispatchPointerMove((float)(x / _scale), (float)(y / _scale));
    [JSExport] internal static void PointerUp(double x, double y) => _doc?.DispatchPointerUp((float)(x / _scale), (float)(y / _scale));
    [JSExport] internal static void Wheel(double x, double y, double dy) => _doc?.DispatchWheel((float)(x / _scale), (float)(y / _scale), (float)-dy);
    [JSExport] internal static void KeyChar(string text) => _doc?.DispatchKey(text, EditKey.None);
    [JSExport] internal static void EditKeyPress(int code) => _doc?.DispatchKey(null, (EditKey)code);

    // JS side (module "cupri") copies the pixels into the 2D canvas.
    [JSImport("present", "cupri")]
    internal static partial void Present(byte[] rgba, int width, int height);
}
