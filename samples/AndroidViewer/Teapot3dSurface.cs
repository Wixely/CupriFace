using CupriFace.Demo.ThreeD;
using CupriFace.Gl;

namespace CupriFace.AndroidViewer;

/// <summary>
/// The Showcase's 3D viewport on ANDROID — now the shortest of the three, and short for a reason
/// worth stating: there is nothing Android-specific left in it.
///
/// <para>Everything that used to be here was integration. Loading <c>libGLESv3.so</c> with
/// <c>dlsym</c> rather than <c>eglGetProcAddress</c> (whose stubs lie about symbols that do not
/// exist), choosing <c>#version 300 es</c>, building a framebuffer, wrapping its colour attachment as
/// a texture-backed image, gating on whether the element is actually on screen so a phone's GPU is
/// not pinned behind a page nobody is looking at — all of it is in <c>CupriFace.Gl</c>, and all of it
/// is now shared with the hosts that need the same things done differently.</para>
///
/// <para>No <c>OffscreenContext</c> factory is supplied, and that is deliberate rather than an
/// omission: Android's host renders through an <c>SKGLSurfaceView</c> which always has a real
/// <c>GRContext</c>, so the fallback lane can never be reached. Supplying one would be dead code
/// implying a case that does not exist.</para>
/// </summary>
internal static class Teapot3dSurface
{
    internal static GlViewport? TryAttach(CupriDocument doc, Action<string>? log = null)
    {
        log ??= _ => { };
        var content = TeapotContent.FromEmbeddedAsset(m => log("3d: " + m));
        if (content is null) return null;

        return GlViewport.Attach(doc, "showcase3d", content, new GlViewportOptions
        {
            // The gate greps these lines, so they name the host and the driver rather than saying
            // "ok" — a PASS that cannot say what it ran on is worth very little. The package logs
            // the renderer string at startup, which on the emulator reads "SwiftShader" and on a real
            // handset names the GPU.
            Log = m => log("cupri-gate: 3d " + m),
            ClearColor = (0f, 0f, 0f, 0f),
        });
    }
}
