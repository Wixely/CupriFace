using System.Diagnostics;
using System.Runtime.InteropServices;
using CupriFace;
using CupriFace.Samples.Shaders;
using SkiaSharp;

/// <summary>
/// The GpuWeb probe, compiled by NativeAOT-LLVM. Same two lanes and the same three frame modes as
/// ../GpuWeb/Program.cs; what differs is how the page reaches it (plain wasm exports) and that the
/// WebGL context can be made from here, because Emscripten's html5 API is a link-time symbol on
/// this toolchain rather than something only the page can touch.
/// </summary>
internal static unsafe partial class Probe
{
    private static CupriDocument _doc = null!;
    private static SKColor _bg;
    private static int _w, _h;

    private static SKBitmap? _bitmap;                 // CPU lane
    private static GRContext? _gr;                    // GPU lane
    private static GRBackendRenderTarget? _target;
    private static SKSurface? _surface;

    private static int Main() { Console.WriteLine("[gpuweb-llvm] runtime ready"); return 0; }

    [StructLayout(LayoutKind.Sequential)]
    private struct ContextAttributes
    {
        public int Alpha, Depth, Stencil, Antialias, PremultipliedAlpha, PreserveDrawingBuffer;
        public int PowerPreference, FailIfMajorPerformanceCaveat;
        public int MajorVersion, MinorVersion;
        public int EnableExtensionsByDefault, ExplicitSwapControl;
        public int ProxyContextToMainThread, RenderViaOffscreenBackBuffer;
    }

    [DllImport("emscripten", EntryPoint = "emscripten_webgl_init_context_attributes")]
    private static extern void InitAttributes(ContextAttributes* attrs);
    [DllImport("emscripten", EntryPoint = "emscripten_webgl_create_context")]
    private static extern nint CreateContext(byte* target, ContextAttributes* attrs);
    [DllImport("emscripten", EntryPoint = "emscripten_webgl_make_context_current")]
    private static extern int MakeCurrent(nint context);

    [DllImport("js", EntryPoint = "probe_present")]
    private static extern void Present(nint rgba, int width, int height, int dx, int dy, int dw, int dh);

    /// <param name="app">0 shaders, 1 shaders with the page CRT, 2 the Showcase.</param>
    /// <returns>0 CPU lane ready, 1 GPU lane ready, negative for the step that failed.</returns>
    [UnmanagedCallersOnly(EntryPoint = "ProbeInit")]
    private static int Init(int app, int gpu, int width, int height)
    {
        _w = width; _h = height;
        CupriApp a = app switch
        {
            2 => new CupriFace.Demo.ShowcaseApp(),
            1 => new ShadersApp(crt: true),
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

        if (gpu == 0)
        {
            _bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
            return 0;
        }

        // preserveDrawingBuffer: the engine repaints only what changed, which is only valid on a
        // target that still holds last frame's pixels. stencil: Skia clips with it.
        ContextAttributes attrs;
        InitAttributes(&attrs);
        attrs.Alpha = 1; attrs.Depth = 0; attrs.Stencil = 1; attrs.Antialias = 0;
        attrs.PremultipliedAlpha = 1; attrs.PreserveDrawingBuffer = 1;
        attrs.MajorVersion = 2; attrs.MinorVersion = 0; attrs.EnableExtensionsByDefault = 1;
        nint ctx;
        fixed (byte* target = "#c\0"u8) ctx = CreateContext(target, &attrs);
        if (ctx <= 0) return -1;
        if (MakeCurrent(ctx) != 0) return -2;

        using var gl = GRGlInterface.Create();
        if (gl is null) return -3;
        _gr = GRContext.CreateGl(gl);
        if (_gr is null) return -4;
        // Framebuffer 0 is the canvas; 0x8058 is GL_RGBA8.
        _target = new GRBackendRenderTarget(width, height, 0, 8, new GRGlFramebufferInfo(0, 0x8058));
        _surface = SKSurface.Create(_gr, _target, GRSurfaceOrigin.BottomLeft, SKColorType.Rgba8888);
        return _surface is null ? -5 : 1;
    }

    /// <summary>One frame; returns the milliseconds it took.</summary>
    /// <param name="mode">0: animate, repaint the damage, present. 1: a full stateless repaint.
    /// 2: the same repaint onto a canvas that draws nothing.</param>
    /// <param name="sync">GPU lane: read one pixel back through Skia so the GPU has finished
    /// before the clock stops.</param>
    [UnmanagedCallersOnly(EntryPoint = "ProbeFrame")]
    private static double Frame(double seconds, int mode, int sync)
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
            if (sync != 0) _surface.ReadPixels(new SKImageInfo(1, 1, SKColorType.Rgba8888, SKAlphaType.Premul), (nint)(&px), 4, 0, 0);
        }
        else if (damage is { } d)
            Present(_bitmap!.GetPixels(), _w, _h, d.Left, d.Top, d.Width, d.Height);
        return Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
    }

    /// <summary>Animated frames back to back, with no browser frame between them: the soak that
    /// found the undisposed-SKData crash (see ../GpuWeb/README.md). Returns the frames drawn;
    /// collections since start are in <see cref="Timing"/> part 4.</summary>
    [UnmanagedCallersOnly(EntryPoint = "ProbeSoak")]
    private static int Soak(int frames, double startSeconds)
    {
        var canvas = _surface?.Canvas;
        using var owned = canvas is null ? new SKCanvas(_bitmap) : null;
        canvas ??= owned!;
        for (var i = 0; i < frames; i++)
        {
            _doc.Animate(startSeconds + i / 60.0);
            _doc.RenderIncremental(canvas, _w, _h, _bg);
            canvas.Flush();
            _gr?.Flush();
        }
        return frames;
    }

    /// <summary>The engine's own split of the last mode-0 frame: 0 layout, 1 display list,
    /// 2 damage diff, 3 raster — milliseconds. 4: garbage collections so far.</summary>
    [UnmanagedCallersOnly(EntryPoint = "ProbeTiming")]
    private static double Timing(int part)
    {
        if (part == 4) return GC.CollectionCount(0);
        var f = _doc.LastFrame;
        return part switch { 0 => f.LayoutMs, 1 => f.PaintListMs, 2 => f.DiffMs, _ => f.RasterMs };
    }
}
