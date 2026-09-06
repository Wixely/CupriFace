using CupriFace.Gl;
using CupriFace.Samples.Khalkos;

namespace CupriFace.AndroidViewer;

/// <summary>
/// The Showcase's 3D viewport on ANDROID, drawn by Khalkos3D.
///
/// <para><b>This is the host that matters for the engine.</b> Desktop and the CI render gate both
/// compile <c>#version 330 core</c>; Android is the first place the <c>#version 300 es</c> path runs
/// at all, on a real GLES driver rather than a desktop one. Both libraries decide that dialect by
/// asking the driver rather than by guessing from the platform, and if they ever disagreed this is
/// where it would show — which is why the ready line below logs both answers.</para>
///
/// <para>No <c>OffscreenContext</c> factory is supplied, deliberately: Android's host renders through
/// an <c>SKGLSurfaceView</c> which always has a real <c>GRContext</c>, so the fallback lane cannot be
/// reached and supplying one would imply a case that does not exist.</para>
/// </summary>
internal static class Teapot3dSurface
{
    internal static GlViewport? TryAttach(CupriDocument doc, Action<string>? log = null)
    {
        log ??= _ => { };
        var content = Khalkos3dContent.FromEmbeddedAsset(m => log("3d: " + m));
        if (content is null) return null;

        return GlViewport.Attach(doc, "showcase3d", new GateReporting(content, log), new GlViewportOptions
        {
            Log = m => log("cupri-gate: 3d " + m),
            ClearColor = (0f, 0f, 0f, 0f),
        });
    }

    /// <summary>
    /// Wraps the drawing code to emit the two lines the CI device gate asserts on.
    ///
    /// <para>A DECORATOR rather than a feature of either library, because it is a property of this
    /// build: neither a UI toolkit nor an engine should log frame milestones uninvited.
    /// <see cref="IGlContent"/> composing cleanly is the point — this needs nothing the interface does
    /// not already give it, and it now wraps an engine exactly as it previously wrapped a sample
    /// renderer.</para>
    ///
    /// <para>The gate asserts on the DRIVER'S OWN ANSWER and then on a sustained frame count, because
    /// a surface that fails to initialise leaves the viewport showing its panel — which looks
    /// deliberate and would pass any "did it launch" check.</para>
    /// </summary>
    private sealed class GateReporting(Khalkos3dContent inner, Action<string> log) : IGlContent
    {
        private long _frames;

        public bool Initialise(GlContext gl)
        {
            if (!inner.Initialise(gl)) return false;
            log($"cupri-gate: 3d ready GL_VERSION={inner.Version} GL_RENDERER={inner.Renderer} " +
                $"engine={inner.Dialect} host={gl.Dialect}");
            return true;
        }

        public void Render(GlContext gl, in GlFrame frame)
        {
            inner.Render(gl, in frame);
            // Once, at 60 — enough to prove it kept going rather than managing a single frame.
            if (++_frames == 60)
                log($"cupri-gate: 3d frames={_frames} size={frame.Width}x{frame.Height} " +
                    $"engine={inner.Dialect}");
        }

        public void Shutdown(GlContext gl) => inner.Shutdown(gl);
    }
}
