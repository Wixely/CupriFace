using Xunit;
using Xunit.Abstractions;

namespace CupriFace.Tests;

/// <summary>
/// The SDL window's GL mode (#143): GPU rendering and touch in one window.
///
/// <para>Why it exists in one sentence: the GLFW window has the GPU and no touch API, the SDL
/// window has touch and a CPU rasteriser, and a touchscreen Linux machine had to pick one. This is
/// SDL owning a real GL context, drawing into framebuffer 0 and swapping — no readback, no hidden
/// window — with the same draw contract the GL window and the layered path already share, so GPU
/// surface producers (the 3D viewport) work on it unchanged.</para>
///
/// <para>Checked against the source, in the manner of <see cref="LayeredAlphaTests"/>, because a GL
/// context needs a driver CI does not have. Every rule below is one that was either measured to
/// matter on a real machine or copied from a failure another window already paid for.</para>
/// </summary>
public class SdlGlTests(ITestOutputHelper output)
{
    private static string Root()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "CupriFace.slnx"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("repo root not found");
    }

    private static string Shell(string file) =>
        File.ReadAllText(Path.Combine(Root(), "src", "CupriFace.Shell", file));

    private static string Body(string source, string signature)
    {
        var at = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(at >= 0, $"no member matching '{signature}'");
        var open = source.IndexOf('{', at);
        var depth = 0;
        for (var i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0) return source[open..(i + 1)];
        }
        throw new InvalidOperationException($"unbalanced braces after '{signature}'");
    }

    /// <summary>SDL reads the GL attributes at <c>SDL_CreateWindow</c>. Set a line later they are
    /// silently ignored and the driver hands over whatever it likes — a context with no stencil,
    /// which Skia clips with, is the usual result. Order is the whole point, so order is what is
    /// asserted.</summary>
    [Fact]
    public void Gl_attributes_are_requested_before_the_window_is_created()
    {
        var run = Body(Shell("SdlSoftwareWindow.cs"), "public void Run()");
        var attrs = run.IndexOf("SdlGlPresenter.RequestAttributes(_sdl)", StringComparison.Ordinal);
        var create = run.IndexOf("_sdl.CreateWindow(", StringComparison.Ordinal);

        Assert.True(attrs >= 0, "attributes are never requested");
        Assert.True(create >= 0);
        Assert.True(attrs < create, "attributes are requested AFTER the window exists, which SDL ignores");
        Assert.Contains("flags |= WindowFlags.Opengl;", run, StringComparison.Ordinal);
        output.WriteLine("attributes before CreateWindow; Opengl flag set");
    }

    /// <summary>
    /// The Skia loader must answer ONLY for <c>gl*</c> names. Skia probes EGL entry points through
    /// the same loader, and a GLX-backed one is specified to fabricate a stub for any name it does
    /// not know — Skia then believes EGL is present, calls the stub, and the process dies in an
    /// uninitialised dispatch slot. <see cref="SkiaWindow"/> learned that from a headless-Linux
    /// SIGSEGV; this window must not learn it again.
    /// </summary>
    [Fact]
    public void The_skia_loader_answers_only_for_gl_names()
    {
        var src = Shell("SdlGlPresenter.cs");
        var ctor = Body(src, "public SdlGlPresenter(Sdl sdl, Window* window)");
        Assert.Contains("name.StartsWith(\"gl\", StringComparison.Ordinal)", ctor, StringComparison.Ordinal);
        Assert.Contains("GRGlInterface.Create", ctor, StringComparison.Ordinal);
    }

    /// <summary>A GL window must not also get SDL's software renderer: that would create a second
    /// context on the same window and the two would fight over what is current.</summary>
    [Fact]
    public void No_software_renderer_is_created_on_a_gl_window()
    {
        var run = Body(Shell("SdlSoftwareWindow.cs"), "public void Run()");
        var guard = run.IndexOf("if (_gl is null)", StringComparison.Ordinal);
        var renderer = run.IndexOf("_sdl.CreateRenderer(", StringComparison.Ordinal);
        Assert.True(guard >= 0 && renderer >= 0 && guard < renderer,
            "CreateRenderer is not guarded by the GL presenter");
    }

    /// <summary>The default framebuffer is not retained across a swap, so a frame that was not
    /// drawn must not be swapped either — or a clean frame would show the stale back buffer. The
    /// GL window has the same rule ("manual swap: only drawn frames reach the screen").</summary>
    [Fact]
    public void An_undrawn_frame_is_not_swapped()
    {
        var body = Body(Shell("SdlSoftwareWindow.cs"), "private void RenderFrameGl()");
        var bail = body.IndexOf("if (!drew) return;", StringComparison.Ordinal);
        var present = body.IndexOf("_gl.Present();", StringComparison.Ordinal);
        Assert.True(bail >= 0 && present >= 0 && bail < present);

        // …and the surface targets the window's own framebuffer, not an off-screen one to read back.
        Assert.Contains("fboId: 0", Shell("SdlGlPresenter.cs"), StringComparison.Ordinal);
        Assert.DoesNotContain("ReadPixels", Shell("SdlGlPresenter.cs"));
    }

    /// <summary>Unusable GL must degrade to the software renderer with a line naming why, never
    /// throw out of Run: by then the window exists and the host's GL-to-SDL fallback has already
    /// happened, so a throw here is a dead application. And never silently — a bare fallback is
    /// how the trimmed-GL hunt went blind.</summary>
    [Fact]
    public void A_failed_context_falls_back_and_says_so()
    {
        var run = Body(Shell("SdlSoftwareWindow.cs"), "public void Run()");
        Assert.Contains("SDL GL context unavailable", run, StringComparison.Ordinal);
        Assert.Contains("_gl = null;", run, StringComparison.Ordinal);
        // The success line names the renderer, for the same reason.
        Assert.Contains("SDL GL window: {_gl.Renderer}", run, StringComparison.Ordinal);
    }

    /// <summary>
    /// Opt-in, and the software kill switch wins the tie. <c>CUPRIFACE_SOFTWARE=1</c> is the
    /// "make it work at any cost" override across every path; a GPU mode that could override it
    /// would leave a troubleshooter with no way to turn the GPU off.
    /// </summary>
    [Fact]
    public void Software_override_beats_the_gl_opt_in()
    {
        var host = Shell("DesktopHost.cs");
        Assert.Contains("var sdlGl = !forceSoftware && Environment.GetEnvironmentVariable(\"CUPRIFACE_SDL_GL\")",
            host, StringComparison.Ordinal);
        Assert.Contains("window.UseGl = sdlGl;", host, StringComparison.Ordinal);
        Assert.Contains("forceSoftware || layeredGpu || sdlGl", host, StringComparison.Ordinal);
    }

    /// <summary>Same draw contract as the layered path: full frames through the GL host's
    /// <c>Draw</c>, with the GRContext on the RenderContext so GPU surface producers run on THIS
    /// context. Measured: the Showcase 3D page reported its shared-GPU lane up on the SDL context.</summary>
    [Fact]
    public void The_gl_mode_shares_the_gpu_draw_contract()
    {
        var host = Shell("DesktopHost.cs");
        Assert.Contains("if (window.UseLayeredGpu || window.UseGl)", host, StringComparison.Ordinal);
        Assert.Contains("new RenderContext(canvas, dw, dh, _stats, _gl.Context)",
            Shell("SdlSoftwareWindow.cs"), StringComparison.Ordinal);
        // ThreadedRender cannot share the frame with a UI-thread GL context, and must say so.
        Assert.Contains("!window.UseLayeredGpu && !window.UseGl", host, StringComparison.Ordinal);
    }

    /// <summary>The surface is sized from the DRAWABLE, not the window. On a scaled Wayland desktop
    /// the two differ, and a texture built at window size is stretched to fill the output — which
    /// is exactly the "UI doubled in size" a Steam Deck produced under the touch probe.</summary>
    [Fact]
    public void The_surface_is_sized_from_the_drawable()
    {
        var src = Shell("SdlGlPresenter.cs");
        var ensure = Body(src, "public SKCanvas? EnsureSurface()");
        Assert.Contains("DrawableSize", ensure, StringComparison.Ordinal);
        Assert.Contains("GLGetDrawableSize", src, StringComparison.Ordinal);
        Assert.DoesNotContain("GetWindowSize", ensure);
    }
}
