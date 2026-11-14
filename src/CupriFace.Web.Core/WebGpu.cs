using SkiaSharp;

namespace CupriFace.Web;

/// <summary>
/// Where a web host paints when it paints on the GPU: the page's canvas, as an
/// <see cref="SKSurface"/>. With one of these, <see cref="WebHostCore"/> draws straight into it and
/// presents nothing — the browser composites the canvas itself — instead of rasterising into a
/// bitmap and copying it out with <c>putImageData</c>.
///
/// <para>An interface rather than <see cref="WebGlTarget"/> alone because creating a context is the
/// one part that differs by runtime, and because a test can stand a CPU surface in for the GPU and
/// check everything the host does around it.</para>
/// </summary>
public interface IWebGpuTarget
{
    /// <summary>The surface for this frame, at the canvas's backing-store size in device pixels, or
    /// null when there is nothing to draw on (the context was lost and has not come back).</summary>
    /// <param name="fresh">True when the surface does not hold the last frame — new, resized or
    /// restored — so the engine must repaint everything rather than only what changed.</param>
    SKSurface? Begin(int width, int height, out bool fresh);

    /// <summary>The frame is drawn: send it to the GPU.</summary>
    void End();
}

/// <summary>
/// Skia's GL backend on the page's WebGL2 canvas — framebuffer 0, bottom-left origin — and the
/// <see cref="IWebGpuTarget"/> the NativeAOT host paints into when the page asks for WebGL.
///
/// <para><b>The context must be current before anything here runs</b>, and the caller passes in how
/// to make it so. That is not ceremony: a page may hold other WebGL contexts (the 3D viewport makes
/// its own current every frame it draws), and Skia's GL calls go to whichever is current. Without the
/// call in <see cref="Begin"/> the UI would be drawn into the 3D canvas.</para>
///
/// <para>The context needs a stencil buffer (Skia clips with it) and <c>preserveDrawingBuffer</c>:
/// the engine repaints only what changed, which is only correct on a canvas that still holds the
/// last frame. Antialias off — Skia antialiases its own geometry, and a multisampled default
/// framebuffer would make every frame resolve the whole canvas.</para>
/// </summary>
public sealed class WebGlTarget : IWebGpuTarget, IDisposable
{
    private const uint GlRgba8 = 0x8058;

    private readonly Func<bool> _makeCurrent;
    private GRContext? _gr;
    private GRBackendRenderTarget? _target;
    private SKSurface? _surface;
    private bool _fresh = true;

    private WebGlTarget(Func<bool> makeCurrent, GRContext gr) { _makeCurrent = makeCurrent; _gr = gr; }

    /// <summary>Skia's GPU backend on the context <paramref name="makeCurrent"/> selects, or null
    /// with the reason it could not be had.</summary>
    public static WebGlTarget? TryCreate(Func<bool> makeCurrent, out string? why)
    {
        var gr = CreateContext(makeCurrent, out why);
        return gr is null ? null : new WebGlTarget(makeCurrent, gr);
    }

    private static GRContext? CreateContext(Func<bool> makeCurrent, out string? why)
    {
        why = null;
        if (!makeCurrent()) { why = "the WebGL context would not become current"; return null; }
        using var gl = GRGlInterface.Create();
        if (gl is null) { why = "Skia found no WebGL interface"; return null; }
        var gr = GRContext.CreateGl(gl);
        if (gr is null) why = "Skia's GL backend refused the context";
        return gr;
    }

    /// <summary>Whether there is a context to draw with — false between a loss and a restore.</summary>
    public bool Available => _gr is not null;

    public SKSurface? Begin(int width, int height, out bool fresh)
    {
        fresh = false;
        if (_gr is null || !_makeCurrent()) return null;
        if (_surface is null || _target!.Width != width || _target.Height != height)
        {
            // A resize clears the canvas's drawing buffer, so a new surface is always a fresh one.
            DisposeSurface();
            _target = new GRBackendRenderTarget(width, height, 0, 8, new GRGlFramebufferInfo(0, GlRgba8));
            _surface = SKSurface.Create(_gr, _target, GRSurfaceOrigin.BottomLeft, SKColorType.Rgba8888);
            if (_surface is null) { DisposeSurface(); return null; }
            _fresh = true;
        }
        fresh = _fresh;
        _fresh = false;
        return _surface;
    }

    public void End() => _gr?.Flush();

    /// <summary>The browser took the context away (a GPU reset, a driver update, too many contexts in
    /// the tab). Everything Skia held on it is gone, so it is abandoned rather than released — a
    /// release would issue GL calls against a context that no longer exists.</summary>
    public void ContextLost()
    {
        DisposeSurface();
        if (_gr is null) return;
        _gr.AbandonContext(true);
        _gr.Dispose();
        _gr = null;
    }

    /// <summary>The context is back, empty: a new Skia context on it, and the next frame repaints in
    /// full. False, with the reason, when it cannot be had.</summary>
    public bool ContextRestored(out string? why)
    {
        ContextLost();
        _gr = CreateContext(_makeCurrent, out why);
        _fresh = true;
        return _gr is not null;
    }

    private void DisposeSurface()
    {
        _surface?.Dispose(); _surface = null;
        _target?.Dispose(); _target = null;
    }

    public void Dispose() { DisposeSurface(); _gr?.Dispose(); _gr = null; }
}
