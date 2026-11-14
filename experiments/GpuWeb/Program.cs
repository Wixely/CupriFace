using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.JavaScript;
using CupriFace;
using CupriFace.Samples.Shaders;
using SkiaSharp;

// Nothing to do here: the page drives the probe through the exports below, and runMain only has to
// leave the runtime resident.
Console.WriteLine("[gpuweb] runtime ready");

/// <summary>
/// One document, drawn one of two ways: into a CPU bitmap that the page blits with putImageData
/// (what both web hosts do today), or straight onto the canvas's WebGL framebuffer through Skia's
/// GPU backend. Everything above the canvas — layout, the display list, the rasteriser's walk — is
/// the same code in both, so the difference between the two numbers is the pixels.
/// </summary>
internal static partial class Probe
{
    private static CupriDocument _doc = null!;
    private static SKColor _bg;
    private static int _w, _h;

    private static SKBitmap? _bitmap;                 // CPU lane
    private static GRContext? _gr;                    // GPU lane
    private static GRBackendRenderTarget? _target;
    private static SKSurface? _surface;

    [DllImport("libSkiaSharp", EntryPoint = "cupri_probe_expose_gl")]
    private static extern void ExposeGlNative();

    /// <summary>Publish Emscripten's GL object as <c>globalThis.CupriGL</c>.</summary>
    [JSExport] internal static void ExposeGl() => ExposeGlNative();

    /// <param name="app"><c>shaders</c>, <c>shaders-crt</c> or <c>showcase</c>.</param>
    /// <param name="gpu">Draw through the WebGL context the page has ALREADY made current.</param>
    /// <returns>A line describing what was set up, or the reason it could not be.</returns>
    [JSExport]
    internal static string Init(string app, bool gpu, int width, int height)
    {
        _w = width; _h = height;
        CupriApp a = app switch
        {
            "showcase" => new CupriFace.Demo.ShowcaseApp(),
            "shaders-crt" => new ShadersApp(crt: true),
            _ => new ShadersApp(),
        };
        _bg = a.Background;
        _doc = a.CreateDocument();
        foreach (var res in new[] { "fonts.NotoSans-Regular.ttf", "fonts.NotoSans-Bold.ttf" })
        {
            using var fs = typeof(Probe).Assembly.GetManifestResourceStream(res)!;
            var buf = new byte[fs.Length];
            fs.ReadExactly(buf);
            _doc.LoadFont(buf);
        }
        _doc.Refresh();

        if (!gpu)
        {
            _bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
            return "cpu: SKBitmap + putImageData";
        }

        using var gl = GRGlInterface.Create();
        if (gl is null) return "FAILED: GRGlInterface.Create() returned null (no current WebGL context?)";
        _gr = GRContext.CreateGl(gl);
        if (_gr is null) return "FAILED: GRContext.CreateGl returned null";
        // Framebuffer 0 is the canvas. 0x8058 is GL_RGBA8; the stencil depth must match what the
        // page asked the context for, because Skia uses it for clips it cannot do with scissor.
        _target = new GRBackendRenderTarget(width, height, 0, 8, new GRGlFramebufferInfo(0, 0x8058));
        _surface = SKSurface.Create(_gr, _target, GRSurfaceOrigin.BottomLeft, SKColorType.Rgba8888);
        return _surface is null ? "FAILED: SKSurface.Create returned null" : "gpu: Skia Ganesh on WebGL";
    }

    /// <summary>One frame. Returns the managed milliseconds it took.</summary>
    /// <param name="sync">GPU lane only: read one pixel back through Skia before returning. GL is
    /// asynchronous — submitting a frame is not drawing it — and a readback is what makes the GPU
    /// finish, so a stopwatch around this call measures the frame rather than the queueing of it.
    /// Through Skia and not through the page's own readPixels: Skia leaves its own framebuffers
    /// bound (a multisampled one, on a page with rounded corners), and reading from that fails
    /// with INVALID_OPERATION and waits for nothing.</param>
    /// <param name="mode">0: what a host does — animate, repaint only the damage, present.
    /// 1: a full stateless repaint of the page. 2: the same repaint onto a canvas that draws
    /// nothing, which is the cost of everything that is NOT pixels.</param>
    [JSExport]
    internal static unsafe double Frame(double seconds, int mode, bool sync)
    {
        var t0 = Stopwatch.GetTimestamp();
        if (mode == 2)
        {
            using var none = new SKNoDrawCanvas(_w, _h);
            _doc.Render(none, _w, _h);
            return Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        }

        using var owned = _surface is null ? new SKCanvas(_bitmap) : null;
        var canvas = owned ?? _surface!.Canvas;
        SKRectI? damage;
        if (mode == 0)
        {
            _doc.Animate(seconds);
            damage = _doc.RenderIncremental(canvas, _w, _h, _bg);
        }
        else
        {
            canvas.Clear(_bg);
            _doc.Render(canvas, _w, _h);
            damage = new SKRectI(0, 0, _w, _h);
        }
        canvas.Flush();

        if (_surface is not null)
        {
            _gr!.Flush();
            uint px = 0;
            if (sync) _surface.ReadPixels(new SKImageInfo(1, 1, SKColorType.Rgba8888, SKAlphaType.Premul), (nint)(&px), 4, 0, 0);
        }
        else if (damage is { } d)
            Present(new Span<byte>((void*)_bitmap!.GetPixels(), _bitmap.ByteCount), _w, _h, d.Left, d.Top, d.Width, d.Height);
        return Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
    }

    /// <summary>The engine's own split of the last mode-0 frame.</summary>
    [JSExport]
    internal static string Timings()
    {
        var f = _doc.LastFrame;
        return FormattableString.Invariant(
            $"{f.LayoutMs:0.00},{f.PaintListMs:0.00},{f.DiffMs:0.00},{f.RasterMs:0.00},{f.DamagePixels}");
    }

    [JSImport("present", "probe")]
    internal static partial void Present([JSMarshalAs<JSType.MemoryView>] Span<byte> rgba, int width, int height,
        int dx, int dy, int dw, int dh);
}
