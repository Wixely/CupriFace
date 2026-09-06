namespace CupriFace.Gl;

/// <summary>
/// How a <see cref="GlViewport"/> should behave. Every value has a default that works, so an app
/// that wants a 3D element and no opinions passes none of this.
/// </summary>
public sealed record GlViewportOptions
{
    /// <summary>
    /// What the frame is cleared to before <see cref="IGlContent.Render"/>, as straight (not
    /// premultiplied) RGBA in 0..1. Transparent by default, so the model sits on whatever CSS put
    /// behind it.
    ///
    /// <para><b>Set this opaque if the app runs in a browser.</b> The browser lane punches a hole
    /// through the engine's frame with <c>BlendMode.Src</c>, which erases the element's own CSS
    /// background along with everything else painted there — so a transparent clear shows the bare
    /// page, and the identical markup that renders near-black on a desktop renders white in a
    /// browser. Matching this to the element's CSS background is the one line that makes the two
    /// lanes agree, and there is no way for the package to read that colour on the app's behalf.</para>
    /// </summary>
    public (float R, float G, float B, float A) ClearColor { get; init; } = (0f, 0f, 0f, 0f);

    /// <summary>
    /// Render at this fixed pixel size instead of following the element.
    ///
    /// <para>Null — the default — is what almost every app wants: the target is sized to the
    /// element's box multiplied by the host's device scale, so it is pixel-exact on a HiDPI monitor
    /// and on a phone, and it follows a resize. A fixed size is for the cases where that is wrong on
    /// purpose: a deliberately low-resolution effect, or a render whose cost must not scale with
    /// somebody's 4K display.</para>
    /// </summary>
    public (int W, int H)? Size { get; init; }

    /// <summary>Smallest and largest edge, in device pixels, that the element-following size is
    /// clamped to. The upper bound is a real safety rail rather than a formality: a maximised window
    /// on a 4K display at 2× asks for a target big enough to matter, and a driver that refuses one
    /// gives an incomplete framebuffer rather than a helpful message.</summary>
    public int MinPixels { get; init; } = 16;

    /// <inheritdoc cref="MinPixels"/>
    public int MaxPixels { get; init; } = 4096;

    /// <summary>
    /// Put the driver into a known state before every frame. On by default, and there is no good
    /// reason to turn it off — the option exists so that a viewport doing something exotic can opt
    /// out knowingly, not as a performance dial (it is a few dozen calls per frame).
    ///
    /// <para>What "known" means is documented on <see cref="IGlContent"/> and enforced in one place.
    /// The reset that matters most is unbinding sampler objects: Skia binds them, they override every
    /// texture parameter on the unit, and the resulting bug looks like a broken UV unwrap rather than
    /// like state leakage.</para>
    /// </summary>
    public bool ResetState { get; init; } = true;

    /// <summary>
    /// Supplies a private offscreen GL context for hosts that have none to share — a desktop window
    /// that fell back to software rasterisation, and headless rendering.
    ///
    /// <para>Null by default, which means such a host reports <see cref="GlViewportState.Unavailable"/>
    /// and the element shows its poster. That is a deliberate default rather than a limitation: making
    /// an offscreen context needs a windowing library, and this package will not put one into every
    /// phone and browser build to serve a fallback most apps do not want. Supply a factory and the
    /// fallback exists; do not, and the app degrades quietly.</para>
    ///
    /// <para>The lane costs about ten times more to MOVE a frame than to draw it — the pixels come
    /// back through <c>glReadPixels</c> and go up again when Skia paints them.</para>
    /// </summary>
    public Func<IGlOffscreenContext>? OffscreenContext { get; init; }

    /// <summary>Frame rate ceiling for the private-context lane, which drives its own clock. Ignored
    /// on the other two, where the host's frame loop sets the pace.</summary>
    public int MaxFramesPerSecond { get; init; } = 60;

    /// <summary>Ask for a multisampled context on the browser lane. Ignored elsewhere — the painted
    /// lanes render to a framebuffer this package creates, where multisampling would need a resolve
    /// step and is better done by the app's own drawing code.</summary>
    public bool Antialias { get; init; } = true;

    /// <summary>Where the viewport's own diagnostics go: which lane was chosen, what the driver calls
    /// itself, and why anything failed. Nothing is written to the console by default — a library that
    /// prints uninvited is a library people wrap.</summary>
    public Action<string>? Log { get; init; }
}
