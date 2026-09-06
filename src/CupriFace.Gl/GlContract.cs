namespace CupriFace.Gl;

/// <summary>
/// Which GLSL dialect the context compiles — the one difference between the hosts that an app's
/// shader source genuinely cannot ignore.
///
/// <para>Everything else in this package hides the host. This does not, because it cannot: the same
/// shader body needs <c>#version 330 core</c> on a desktop and <c>#version 300 es</c> on a phone or
/// in a browser, and no amount of plumbing makes one header work for both. Read
/// <see cref="GlContext.ShaderHeader"/> and prepend it; that is the whole of the portability tax.</para>
///
/// <para>The pairing is not obvious and is worth stating once: <b>WebGL2 is OpenGL ES 3.0</b>, so
/// the browser and the phone want the identical shader, and only the desktop differs.</para>
/// </summary>
public enum GlDialect
{
    /// <summary>Desktop OpenGL 3.3 core — <c>#version 330 core</c>.</summary>
    Gl330Core,

    /// <summary>OpenGL ES 3.0, which is also WebGL2 — <c>#version 300 es</c>.</summary>
    GlEs300,
}

/// <summary>
/// How this viewport's pixels reach the screen. An app rarely needs to branch on it — that is the
/// point of the package — but it is published because performance questions are unanswerable
/// without it, and because "why is this slow on my machine" deserves a better answer than a shrug.
/// </summary>
public enum GlLane
{
    /// <summary>Not chosen yet.</summary>
    None,

    /// <summary>Drawing on the HOST'S own GL context and handing the engine a texture. No copy, no
    /// second thread, no second context. Desktop GL windows and Android take this.</summary>
    SharedGpu,

    /// <summary>Drawing on a private context the app supplied (see
    /// <see cref="GlViewportOptions.OffscreenContext"/>), reading the pixels back and giving the
    /// engine an ordinary image. What a software window and a headless render fall back to; costs
    /// roughly ten times more to MOVE a frame than to draw it.</summary>
    OffscreenReadback,

    /// <summary>Drawing into a real <c>&lt;canvas&gt;</c> the host created beneath the element, with
    /// the engine punching a transparent hole where the element is. The browser's only option — a
    /// wasm host rasterises on the CPU and has no GPU context to share.</summary>
    HostComposited,
}

/// <summary>
/// What the viewport is doing, as something to branch on rather than a sentence to parse. This is
/// item 5 of the scoping document: a consumer needs to distinguish "this machine has no GL" from
/// "your shader did not compile", and <c>string.Contains</c> is not an API.
/// </summary>
public enum GlViewportState
{
    /// <summary>Attached, nothing drawn yet — the element is off screen, or the host has not yet
    /// offered a context. A normal state that can last for ever without being a failure: in a tabbed
    /// app the viewport may never be opened.</summary>
    Waiting,

    /// <summary>Producing frames.</summary>
    Running,

    /// <summary>There is no usable GL on this host, and there never will be. Not an error — the
    /// element shows its poster and the app carries on. <see cref="GlViewport.Diagnostic"/> says
    /// which of the reasons applied.</summary>
    Unavailable,

    /// <summary>GL was available and something went wrong in it: a shader that would not compile, an
    /// incomplete framebuffer, a lost context. <see cref="GlViewport.Diagnostic"/> carries the
    /// driver's own words where there are any.</summary>
    Failed,
}

/// <summary>One frame's worth of instructions to the drawing code: how big, how far through, and
/// which number.</summary>
/// <param name="Width">Target width in DEVICE pixels — already multiplied by the host's scale, so
/// drawing at this size fills the element exactly rather than being upscaled into it.</param>
/// <param name="Height">Target height in device pixels.</param>
/// <param name="ElapsedSeconds">Seconds since the first frame, for animation. Wall clock rather than
/// a frame count, so a slow frame does not slow the animation down.</param>
/// <param name="Index">Frames drawn so far, starting at 0.</param>
public readonly record struct GlFrame(int Width, int Height, double ElapsedSeconds, long Index);

/// <summary>
/// The drawing code an app supplies. Every method is called with the GL context CURRENT and the
/// viewport's framebuffer BOUND, on whichever thread that context belongs to.
///
/// <para><b>Do not clear.</b> <see cref="GlViewport"/> has already cleared colour and depth to
/// <see cref="GlViewportOptions.ClearColor"/> before <see cref="Render"/> is called. That is not
/// tidiness: on the browser lane the engine punches the element's own CSS background out of the
/// frame along with everything else, so a viewport that clears to transparent renders near-black on
/// a desktop and white in a browser from identical markup. Owning the clear here is how the two
/// lanes are made to agree.</para>
///
/// <para><b>Do not restore GL state, and do not assume any.</b> The viewport resets the state that
/// matters before every <see cref="Render"/> — see <see cref="GlViewportOptions.ResetState"/> — and
/// the engine resets its own afterwards. Both halves are handled; a consumer that tried to do either
/// would be duplicating work that is already correct.</para>
/// </summary>
public interface IGlContent
{
    /// <summary>Build shaders, buffers and textures. Called once per context, before the first
    /// <see cref="Render"/>. Return false to fail cleanly — the viewport goes
    /// <see cref="GlViewportState.Failed"/>, the element shows its poster, and the host keeps
    /// running. Throwing does the same thing with a worse message.</summary>
    bool Initialise(GlContext gl);

    /// <summary>Draw one frame at <paramref name="frame"/>'s size. The framebuffer is bound and
    /// cleared, and the viewport is set. Throwing is treated as "no frame this time" and the
    /// previous frame stays on screen.</summary>
    void Render(GlContext gl, in GlFrame frame);

    /// <summary>Release GL objects. Called with the context still current — which is the only moment
    /// deleting them is legal, and the reason this exists rather than <see cref="IDisposable"/>. Not
    /// called if <see cref="Initialise"/> never succeeded.</summary>
    void Shutdown(GlContext gl) { }
}

/// <summary>
/// A private, offscreen GL context supplied by the APP, for hosts with no GPU context to share — a
/// desktop window that fell back to software rasterisation, and headless rendering.
///
/// <para>This is an interface rather than an implementation on purpose. Making an offscreen context
/// needs a windowing library (GLFW, SDL, EGL), and a package that took one would put a desktop
/// windowing stack into every Android and browser build that referenced it. So the capability stays
/// available and the dependency stays with the app that wants it: pass a factory as
/// <see cref="GlViewportOptions.OffscreenContext"/> and the viewport uses it when, and only when, no
/// host GPU context turns up.</para>
///
/// <para>Without one, such a host reports <see cref="GlViewportState.Unavailable"/> and the element
/// shows its poster — which is a perfectly good outcome, and the default.</para>
/// </summary>
public interface IGlOffscreenContext : IDisposable
{
    /// <summary>Make this context current on the CALLING thread. Called once on the viewport's own
    /// render thread before anything else. False means the context could not be made current, and
    /// the viewport reports <see cref="GlViewportState.Unavailable"/>.</summary>
    bool MakeCurrent();

    /// <summary>Resolve a GL entry point for this context, or 0. Only valid while current.</summary>
    nint GetProcAddress(string name);
}
