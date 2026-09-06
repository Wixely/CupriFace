using CupriFace.Demo.ThreeD;
using CupriFace.Gl;
using Silk.NET.Windowing;

namespace CupriFace.Samples.Viewer;

/// <summary>
/// The Showcase's 3D viewport on DESKTOP.
///
/// <para><b>This file used to be 270 lines.</b> It owned a GL context, a render thread, a
/// framebuffer, a readback buffer, a row flip, a texture handoff, a poll-counting heuristic for
/// deciding which of two lanes to take, and a Win32 proc-address loader. All of that was integration
/// rather than rendering, all of it was duplicated in some form by the Android and browser hosts,
/// and all of it now lives in <c>CupriFace.Gl</c>. What is left is the composition root's actual
/// job: say what to draw, and supply the one capability the package deliberately does not carry.</para>
/// </summary>
public static class Teapot3dSurface
{
    /// <summary>
    /// Wire the demo into a document, or leave it alone. Returns null when the model cannot be read,
    /// and never throws: a machine with no usable OpenGL must still run the Showcase — it shows the
    /// poster and a line of text saying so, which is the engine's existing behaviour for a surface
    /// with no frames.
    /// </summary>
    public static GlViewport? TryAttach(CupriDocument doc, Action<string>? log = null)
    {
        log ??= _ => { };
        var content = TeapotContent.FromEmbeddedAsset(m => log("3d: " + m));
        if (content is null) return null;

        return GlViewport.Attach(doc, "showcase3d", content, new GlViewportOptions
        {
            Log = m => log("3d: " + m),
            // Transparent: the engine composites the frame over whatever CSS put behind it, so the
            // model sits on the page's own panel rather than on a plate of its own. The browser host
            // cannot do this and says why in its own file.
            ClearColor = (0f, 0f, 0f, 0f),
            // The one thing the package will not do for itself, and the reason it is a factory rather
            // than a built-in: making an offscreen context needs a windowing library, and putting
            // Silk.NET into the package would drag a desktop windowing stack into every Android and
            // browser build that referenced it. Desktop already has one, so desktop supplies it —
            // and gets a working viewport on a software window and in a headless render, where there
            // is no host GPU context to share.
            OffscreenContext = () => new SilkOffscreenContext(log),
        });
    }
}

/// <summary>
/// A hidden 1×1 window that exists only to own a GL context: GLFW has no portable headless context,
/// and the real rendering goes to a framebuffer, so this window's own buffer is never used.
/// </summary>
internal sealed class SilkOffscreenContext : IGlOffscreenContext
{
    private readonly Action<string> _log;
    private IWindow? _window;

    internal SilkOffscreenContext(Action<string> log) => _log = log;

    public bool MakeCurrent()
    {
        var options = WindowOptions.Default with
        {
            Size = new(1, 1),
            IsVisible = false,
            Title = "cupri-gl-offscreen",
            API = new GraphicsAPI(ContextAPI.OpenGL, ContextProfile.Core, ContextFlags.Default, new APIVersion(3, 3)),
        };

        // Retried because window-system init can be transiently busy: the package only starts this
        // lane once the host has been painting for several frames, but a host may still create or
        // recreate a window while this runs. On Windows two concurrent glfwInit calls collide with
        // "Failed to register window class: Class already exists", and the loser is whoever came
        // second — which, when it was the HOST, silently downgraded the whole app to a software
        // window. A demo that quietly degrades the application it is demonstrating is worse than one
        // that does not run.
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            try
            {
                _window = Window.Create(options);
                _window.Initialize();
                _window.MakeCurrent();
                return true;
            }
            catch (Exception ex)
            {
                _log($"3d: offscreen window attempt {attempt} failed ({ex.GetType().Name})");
                _window?.Dispose();
                _window = null;
                if (attempt < 5) Thread.Sleep(250 * attempt);
            }
        }
        return false;
    }

    public nint GetProcAddress(string name) =>
        _window?.GLContext is { } gl && gl.TryGetProcAddress(name, out var p) ? p : 0;

    public void Dispose() { _window?.Dispose(); _window = null; }
}
