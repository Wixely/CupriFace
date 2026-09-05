using CupriFace.Demo.ThreeD;
using CupriFace.Gl;

namespace CupriFace.AndroidViewer;

/// <summary>
/// The Showcase's 3D viewport on ANDROID — now the shortest of the three, and short for a reason
/// worth stating: there is nothing Android-specific left in it.
///
/// <para>Everything that used to be here was integration. Loading <c>libGLESv3.so</c> rather than
/// trusting <c>eglGetProcAddress</c> (whose stubs lie about symbols that do not exist), choosing
/// <c>#version 300 es</c>, building a framebuffer, wrapping its colour attachment as a
/// texture-backed image, gating on whether the element is actually on screen so a phone's GPU is not
/// pinned behind a page nobody is looking at — all of it is in <c>CupriFace.Gl</c>, and all of it is
/// now shared with the hosts that need the same things done differently.</para>
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

        return GlViewport.Attach(doc, "showcase3d", new GateReporting(content, log), new GlViewportOptions
        {
            Log = m => log("3d: " + m),
            ClearColor = (0f, 0f, 0f, 0f),
        });
    }

    /// <summary>
    /// Wraps the drawing code to emit the two lines the CI device gate asserts on.
    ///
    /// <para>Kept as a DECORATOR rather than as options on the viewport, because it is a property of
    /// this build and not of the package: a library that logged frame milestones uninvited would be
    /// a library people wrap. <see cref="IGlContent"/> composing cleanly is the point — this is
    /// twelve lines and needs nothing the interface does not already give it.</para>
    ///
    /// <para>The gate asserts on the DRIVER'S OWN ANSWER and then on a frame count, rather than on
    /// the app not crashing: a surface that fails to initialise leaves the viewport showing its
    /// panel, which looks deliberate and would pass any "did it launch" check. On the emulator
    /// <c>GL_RENDERER</c> reads "SwiftShader"; a real handset names its chip, and that difference is
    /// the only one a person can see.</para>
    /// </summary>
    private sealed class GateReporting(IGlContent inner, Action<string> log) : IGlContent
    {
        private long _frames;

        public bool Initialise(GlContext gl)
        {
            if (!inner.Initialise(gl)) return false;
            log($"cupri-gate: 3d ready GL_VERSION={gl.Version} GL_RENDERER={gl.Renderer} lane={gl.Lane}");
            return true;
        }

        public void Render(GlContext gl, in GlFrame frame)
        {
            inner.Render(gl, in frame);
            // Once, at 60 — enough to prove it kept going rather than managing a single frame.
            if (++_frames == 60) log($"cupri-gate: 3d frames={_frames} lane={gl.Lane} size={frame.Width}x{frame.Height}");
        }

        public void Shutdown(GlContext gl) => inner.Shutdown(gl);
    }
}
