using Silk.NET.SDL;
using SkiaSharp;

namespace CupriFace.Shell;

/// <summary>
/// A real OpenGL context on the SDL window, drawing straight into its default framebuffer.
///
/// <para><b>Why a third presentation mode.</b> The GLFW window has the GPU but no touch API; the
/// SDL window has touch but rasterised on the CPU. A touchscreen Linux machine — the Steam Deck
/// (#143) — therefore had to choose between being tappable and being accelerated. SDL can own a GL
/// context perfectly well (<c>SDL_GL_CreateContext</c>), and Silk.NET already binds it, so this is
/// the two together with no new dependency: one window, GPU-drawn, touch-aware.</para>
///
/// <para><b>Not the layered path.</b> <see cref="LayeredGpuRenderer"/> draws on a hidden GLFW window
/// and reads every frame back to the CPU, because Windows per-pixel alpha needs a bitmap. This
/// draws into the framebuffer the window will show and swaps it — no readback, no second window.
/// It is what <see cref="SkiaWindow"/> does, minus GLFW.</para>
///
/// <para><b>Wayland is why SDL and not GLX.</b> SDL picks EGL or GLX itself. The Deck's Game Mode
/// is Wayland, where there is no GLX; a hand-rolled context would have had to know that.</para>
/// </summary>
internal sealed unsafe class SdlGlPresenter : IDisposable
{
    private readonly Sdl _sdl;
    private readonly Window* _window;
    private void* _context;
    private GRGlInterface? _interface;
    private GRBackendRenderTarget? _target;
    private int _width, _height;

    public GRContext Context { get; private set; } = null!;
    public SKSurface? Surface { get; private set; }
    public SKCanvas? Canvas => Surface?.Canvas;

    /// <summary>What the driver says it is. Printed at startup because a build that has quietly
    /// fallen off the GPU looks identical to one that has not — the repo's trimming and AOT
    /// history is exactly this, and the console line is the only witness.</summary>
    public string Renderer { get; private set; } = "";

    /// <summary>
    /// Ask for the context BEFORE the window exists. SDL reads these at <c>SDL_CreateWindow</c>;
    /// set afterwards they do nothing, silently, and you get whatever default the driver felt like.
    /// </summary>
    public static void RequestAttributes(Sdl sdl)
    {
        // 3.3 core is what Skia's GL backend wants and what every desktop driver of the last
        // decade provides; the Deck's RADV is 4.6. Stencil 8 because Skia clips with it.
        sdl.GLSetAttribute(GLattr.ContextMajorVersion, 3);
        sdl.GLSetAttribute(GLattr.ContextMinorVersion, 3);
        sdl.GLSetAttribute(GLattr.ContextProfileMask, (int)GLprofile.GLContextProfileCore);
        sdl.GLSetAttribute(GLattr.Doublebuffer, 1);
        sdl.GLSetAttribute(GLattr.StencilSize, 8);
        sdl.GLSetAttribute(GLattr.DepthSize, 0);
    }

    public SdlGlPresenter(Sdl sdl, Window* window)
    {
        _sdl = sdl;
        _window = window;
        try
        {
            _context = sdl.GLCreateContext(window);
            if (_context is null)
                throw new InvalidOperationException($"SDL_GL_CreateContext failed: {sdl.GetErrorS()}");
            if (sdl.GLMakeCurrent(window, _context) != 0)
                throw new InvalidOperationException($"SDL_GL_MakeCurrent failed: {sdl.GetErrorS()}");
            // Vsync via the swap: frames that are not drawn are not swapped (see the window's render
            // loop), so this is a wait only when there is something new to show.
            sdl.GLSetSwapInterval(1);

            // glGetString(GL_RENDERER) through the same loader Skia will use — if THIS returns
            // nothing, Skia is about to be handed a context that cannot answer, and it is better to
            // say so here with the SDL error alongside than to die inside GRContext.CreateGl.
            var getString = (delegate* unmanaged<uint, byte*>)sdl.GLGetProcAddress("glGetString");
            var renderer = getString is null ? null
                : System.Runtime.InteropServices.Marshal.PtrToStringUTF8((nint)getString(0x1F01 /* GL_RENDERER */));
            if (string.IsNullOrEmpty(renderer))
                throw new InvalidOperationException("The SDL GL context is unusable (glGetString returned nothing).");
            Renderer = renderer;

            // Answer ONLY for gl* names. Skia also probes EGL entry points through this loader, and
            // a GLX-backed loader is specified to FABRICATE a stub for names it does not know —
            // Skia then believes EGL is present, calls the stub, and the process dies in an
            // uninitialised dispatch slot. SkiaWindow learned this from a headless-Linux SIGSEGV;
            // the rule is copied here rather than rediscovered.
            _interface = GRGlInterface.Create(name =>
                name.StartsWith("gl", StringComparison.Ordinal)
                    ? (nint)sdl.GLGetProcAddress(name)
                    : IntPtr.Zero)
                ?? throw new InvalidOperationException("Skia could not assemble a GL interface on the SDL context.");
            Context = GRContext.CreateGl(_interface)
                ?? throw new InvalidOperationException("Skia could not create a GPU context on the SDL context.");
        }
        catch { Dispose(); throw; }
    }

    public void MakeCurrent() => _sdl.GLMakeCurrent(_window, _context);

    /// <summary>The size of the thing actually being drawn into. On a scaled Wayland desktop this
    /// is larger than the window; assuming the two agree is how a UI doubles.</summary>
    public (int Width, int Height) DrawableSize
    {
        get { int w, h; _sdl.GLGetDrawableSize(_window, &w, &h); return (w, h); }
    }

    /// <summary>A Skia surface over framebuffer 0 at the drawable size — recreated only when that
    /// size changes, because the target describes a framebuffer the driver owns and reallocates
    /// on resize.</summary>
    public SKCanvas? EnsureSurface()
    {
        var (w, h) = DrawableSize;
        if (w <= 0 || h <= 0) return null;
        if (Surface is not null && w == _width && h == _height) return Surface.Canvas;

        Surface?.Dispose();
        _target?.Dispose();
        const uint GL_RGBA8 = 0x8058;
        _target = new GRBackendRenderTarget(w, h, sampleCount: 0, stencilBits: 8,
            new GRGlFramebufferInfo(fboId: 0, format: GL_RGBA8));
        Surface = SKSurface.Create(Context, _target, GRSurfaceOrigin.BottomLeft, SKColorType.Rgba8888)
            ?? throw new InvalidOperationException("Skia could not wrap the SDL window's framebuffer.");
        _width = w; _height = h;
        return Surface.Canvas;
    }

    /// <summary>Flush Skia's work to the driver and show it. Only ever called for a frame that was
    /// actually drawn — an undrawn frame keeps the front buffer, exactly as the GL window does.</summary>
    public void Present()
    {
        Context.Flush();
        _sdl.GLSwapWindow(_window);
    }

    /// <summary>The pixels the window is showing, read back from the GPU. Diagnostics only
    /// (<c>CUPRIFACE_FRAME_DUMP</c>): this is the same "ground truth" the other two paths offer,
    /// and a GPU path that could not testify would be the one nobody could debug.</summary>
    public SKImage? Snapshot() => Surface?.Snapshot();

    public void Dispose()
    {
        Surface?.Dispose(); Surface = null;
        _target?.Dispose(); _target = null;
        Context?.Dispose(); Context = null!;
        _interface?.Dispose(); _interface = null;
        if (_context is not null) { _sdl.GLDeleteContext(_context); _context = null; }
    }
}
