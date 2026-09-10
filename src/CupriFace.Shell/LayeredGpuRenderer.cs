using Silk.NET.Maths;
using Silk.NET.Windowing;
using SkiaSharp;

namespace CupriFace.Shell;

/// <summary>Skia GPU drawing on an off-screen surface, with explicit readback for Windows
/// layered presentation. The hidden context never presents a WGL swap chain to the desktop.</summary>
internal sealed class LayeredGpuRenderer : IDisposable
{
    private IWindow? _contextWindow;
    private GRGlInterface? _interface;
    private bool _contextReady;
    public GRContext Context { get; private set; } = null!;
    public SKSurface Surface { get; private set; } = null!;

    public LayeredGpuRenderer()
    {
        try
        {
            _contextWindow = Window.Create(WindowOptions.Default with
            {
                Title = "CupriFace off-screen GPU context",
                Size = new Vector2D<int>(1, 1),
                IsVisible = false,
                API = GraphicsAPI.Default,
                ShouldSwapAutomatically = false,
                VSync = false,
            });
            _contextWindow.Initialize();
            var loader = _contextWindow.GLContext
                ?? throw new InvalidOperationException("Off-screen GL context unavailable.");
            loader.MakeCurrent();
            _contextReady = true;
            using var gl = Silk.NET.OpenGL.GL.GetApi(loader);
            var renderer = gl.GetStringS(Silk.NET.OpenGL.StringName.Renderer);
            if (string.IsNullOrEmpty(renderer)) throw new InvalidOperationException("Off-screen OpenGL is unusable.");
            _interface = GRGlInterface.Create(name =>
                name.StartsWith("gl", StringComparison.Ordinal) && loader.TryGetProcAddress(name, out var address)
                    ? address : 0) ?? throw new InvalidOperationException("Skia GL interface unavailable.");
            Context = GRContext.CreateGl(_interface)
                ?? throw new InvalidOperationException("Skia GPU context unavailable.");
            Console.WriteLine($"[CupriFace] Layered GPU renderer: {renderer}; GPU drawing with CPU readback for alpha presentation.");
        }
        catch { Dispose(); throw; }
    }

    public void MakeCurrent() => _contextWindow!.GLContext!.MakeCurrent();

    public SKCanvas Resize(int width, int height)
    {
        MakeCurrent();
        Surface?.Dispose();
        Surface = SKSurface.Create(Context, false,
            new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul))
            ?? throw new InvalidOperationException("Could not allocate the off-screen GPU surface.");
        return Surface.Canvas;
    }

    /// <summary>Frames read back, and the total time spent doing it. This is the cost the mode
    /// trades for working alpha, and it is the number someone needs to decide whether to adopt it —
    /// so it is measured here rather than described in a caveat.</summary>
    public int Readbacks { get; private set; }
    public double ReadbackMs { get; private set; }

    /// <summary>Average milliseconds per readback so far, or 0 before the first one.</summary>
    public double AverageReadbackMs => Readbacks == 0 ? 0 : ReadbackMs / Readbacks;

    public void ReadBack(SKBitmap bitmap)
    {
        MakeCurrent();
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        Context.Flush();
        if (!Surface.ReadPixels(bitmap.Info, bitmap.GetPixels(), bitmap.RowBytes, 0, 0))
            throw new InvalidOperationException("GPU alpha readback failed.");
        ReadbackMs += System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        Readbacks++;
    }

    public void Dispose()
    {
        if (_contextReady) MakeCurrent();
        Surface?.Dispose();
        Context?.Dispose();
        _interface?.Dispose();
        _contextWindow?.Dispose();
        Surface = null!; Context = null!; _interface = null; _contextWindow = null;
        _contextReady = false;
    }
}
