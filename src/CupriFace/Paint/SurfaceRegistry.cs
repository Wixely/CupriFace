using SkiaSharp;

namespace CupriFace.Paint;

/// <summary>
/// A producer of live pixels for one element — a video player, later a 3D viewport or camera.
/// The paint path reads <see cref="CurrentFrame"/> once per frame; the producer swaps it from
/// any thread (publish an immutable <see cref="SKImage"/>, then call
/// <see cref="SurfaceRegistry.NotifyFrame"/> so a render-on-demand host repaints). The producer
/// owns its frames' lifetime: never dispose an image the paint path may still be reading —
/// swap first, dispose the PREVIOUS frame after the next repaint (or keep a small pool).
/// </summary>
public interface ISurfaceSource
{
    /// <summary>The latest frame, or null before the first one (the element's
    /// <c>data-cupri-image</c> poster shows instead until then).</summary>
    SKImage? CurrentFrame { get; }

    /// <summary>Intrinsic pixel size for layout (like an image's natural size); null until known.</summary>
    (int W, int H)? NaturalSize { get; }

    /// <summary>True while frames are being produced (playing) — keeps hosts rendering
    /// continuously, exactly like a running CSS animation. False when paused/stopped: the last
    /// frame stays on screen and an idle window costs nothing again.</summary>
    bool Ticking { get; }

    /// <summary>True when the HOST composites this surface's pixels itself, UNDER the engine's
    /// output (the web host's underlaid <c>&lt;video&gt;</c> element). The painter then punches a
    /// transparent hole at the element's box instead of drawing frames — engine content after it
    /// still paints on top — and the host syncs the underlay to the element's on-screen rect.
    /// Default false: ordinary surfaces hand frames to the engine.</summary>
    bool HostComposited => false;

    /// <summary>What the host should CREATE beneath the hole, when this surface is host-composited
    /// but does not own an element already. <c>"canvas"</c> asks the web host for a
    /// <c>&lt;canvas&gt;</c> — which is how a WebGL viewport gets somewhere to draw, given the web
    /// host has no GPU context of its own to share.
    ///
    /// <para>Null (the default) means "I manage my own element, just keep it glued to my box" —
    /// which is what <c>&lt;cupri-video&gt;</c> does, because a video element's lifetime is tied to
    /// loading and playback rather than to layout. Either way the host syncs the element's rect,
    /// clip and transform every painted frame; this only decides who creates it.</para>
    ///
    /// <para>Ignored by hosts that composite surfaces themselves (desktop and Android draw the
    /// frames, so there is no underlay to create).</para></summary>
    string? UnderlayElement => null;
}

/// <summary>
/// A surface that draws on the HOST'S GPU rather than handing the engine finished pixels.
///
/// <para>An ordinary <see cref="ISurfaceSource"/> publishes an <see cref="SKImage"/> from any
/// thread, which is simple and costs a round trip: the frame is read back from the GPU, wrapped,
/// and uploaded again when Skia paints it. Measured on one desktop with a small glTF model, drawing
/// took ~0.13 ms and MOVING the result ~1.3 ms — about ten times more to transport a frame than to
/// render it.</para>
///
/// <para>This interface removes the trip. <see cref="RenderOnGpu"/> is called on the render thread
/// with the host's GL context already current, before anything is recorded for the frame, so the
/// producer can draw into its own framebuffer and publish a TEXTURE-BACKED
/// <see cref="ISurfaceSource.CurrentFrame"/> (<c>SKImage.FromTexture</c>) that Skia then draws
/// without copying anything.</para>
///
/// <para><b>The rule that makes it safe:</b> raw GL issued behind Skia's back invalidates the state
/// it thinks the driver is in. The registry calls <c>GRContext.ResetContext()</c> after any producer
/// has run, which is exactly what that method is for — so a producer may issue whatever GL it likes
/// and does not need to restore anything. Producers must NOT draw with the passed
/// <see cref="GRContext"/> themselves; it is handed over so a texture can be wrapped, not so the
/// engine's own recording can be joined.</para>
///
/// <para>Hosts without a GPU context never call this (the web host rasterises on the CPU; a desktop
/// host that fell back to a software window has no <see cref="GRContext"/>), so a producer that
/// wants to run everywhere still needs an <see cref="ISurfaceSource.CurrentFrame"/> path — see
/// <see cref="SurfaceRegistry.HasGpuFrameHook"/>.</para>
/// </summary>
public interface IGpuSurfaceSource : ISurfaceSource
{
    /// <summary>Draw this frame on the host's GPU context, then publish
    /// <see cref="ISurfaceSource.CurrentFrame"/>. Called on the render thread, context current,
    /// before the frame is recorded. Throwing here is treated as "no frame this time" rather than
    /// taking the window down.</summary>
    void RenderOnGpu(GRContext context);
}

/// <summary>
/// A live surface whose pixels are presented by a native host object instead of an
/// <see cref="SKImage"/>. The desktop host supplies an opaque feature context and keeps the
/// native surface aligned with the engine element; platform handles remain in optional adapter
/// packages rather than leaking into portable application code.
/// </summary>
public interface IHostCompositedSurfaceSource : ISurfaceSource
{
    /// <summary>Attach to the current host. Called once before the first placement.</summary>
    void Attach(IHostSurfaceContext context);

    /// <summary>Apply the latest element placement, expressed in physical host pixels.</summary>
    void Arrange(HostSurfacePlacement placement);

    /// <summary>Release host-owned presentation resources when the element or window leaves.</summary>
    void Detach();
}

/// <summary>An opaque, extensible set of host features for a composited surface.</summary>
public interface IHostSurfaceContext
{
    object? GetFeature(Type featureType);
    void RequestFrame();
}

public static class HostSurfaceContextExtensions
{
    public static T? GetFeature<T>(this IHostSurfaceContext context) where T : class =>
        context.GetFeature(typeof(T)) as T;
}

/// <summary>An affine transform matching the engine's screen transform.</summary>
public readonly record struct HostSurfaceTransform(
    float ScaleX, float SkewY, float SkewX, float ScaleY, float TranslateX, float TranslateY)
{
    public static HostSurfaceTransform Identity { get; } = new(1, 0, 0, 1, 0, 0);
    public bool IsIdentity => this == Identity;
}

/// <summary>
/// Physical-pixel geometry for a host-composited surface. Clip values are inset distances from
/// the element edges; adapters that cannot express affine transforms may decline non-identity
/// placements rather than displaying pixels in the wrong location.
/// </summary>
public readonly record struct HostSurfacePlacement(
    float X,
    float Y,
    float Width,
    float Height,
    float ClipTop,
    float ClipRight,
    float ClipBottom,
    float ClipLeft,
    bool Visible,
    string ObjectFit,
    HostSurfaceTransform Transform,
    float DeviceScale,
    IReadOnlyList<HostSurfaceOcclusion>? Occlusions = null);

/// <summary>A rectangular hole, relative to a host-composited surface's top-left corner, where
/// engine-painted UI must remain visible above the native surface.</summary>
public readonly record struct HostSurfaceOcclusion(float X, float Y, float Width, float Height);

/// <summary>Geometry a host needs in order to place a host-composited surface correctly.</summary>
public static class HostSurfaceGeometry
{
    /// <summary>
    /// How far an element's OWN chrome covers the bottom of its surface.
    ///
    /// <para>A control bar written the way the web writes one — <c>position:absolute; bottom:0</c>
    /// over a translucent background — is painted by the engine, and a host-composited surface is
    /// presented by the HOST on top of everything the engine draws. The bar is therefore not merely
    /// un-translucent on that path; it is not visible at all. Handing the host a shorter box and
    /// letting the engine keep the strip is the one answer that shows both.</para>
    ///
    /// <para>It is a MEASUREMENT, never a constant: the bar's height is emergent from its padding
    /// and its tallest child, so the same markup restyled moves the native surface with it. A
    /// constant copied from today's stylesheet drifts the first time anyone restyles the bar, and
    /// drifts silently — a black strip, or a surface painted over its own controls.</para>
    ///
    /// <para>Only chrome flush with the bottom edge counts. An overlay floating in the middle cannot
    /// be resolved by shortening the box, so it is left alone and overdrawn rather than quietly
    /// mis-sizing the surface to hide it.</para>
    /// </summary>
    /// <summary>
    /// Whether engine-painted UI marked <c>data-surface-occluder</c> currently overlaps this
    /// surface's box.
    ///
    /// <para>A host-composited surface is presented OVER everything the engine draws, so a menu,
    /// dialog or dropdown that overlaps it is painted underneath and simply cannot be seen. There
    /// is no z-order to correct — the hole IS the mechanism. The host therefore cuts the popup's
    /// overlap out of the native surface region while leaving the rest of the surface visible.</para>
    ///
    /// <para>Only real overlap counts. A popup opened beside the surface costs it nothing, and a
    /// closed one (laid out with no area) is not an occluder at all — which is why this can be
    /// marked once on the control rather than toggled as it opens.</para>
    /// </summary>
    public static bool IsOccluded(Dom.RenderNode root, float x, float y, float w, float h)
        => GetOcclusions(root, x, y, w, h).Count > 0;

    /// <summary>Find the visible portions of engine-painted occluders that overlap a surface.
    /// Returned rectangles are relative to the supplied surface's top-left corner.</summary>
    public static IReadOnlyList<HostSurfaceOcclusion> GetOcclusions(
        Dom.RenderNode root, float x, float y, float w, float h)
    {
        ArgumentNullException.ThrowIfNull(root);
        if (w <= 0 || h <= 0) return [];
        var matches = new List<HostSurfaceOcclusion>();
        Scan(root);
        return matches;

        void Scan(Dom.RenderNode n)
        {
            if (n.LaidOut && n.Element?.HasAttribute("data-surface-occluder") == true)
            {
                var (ox, oy, ow, oh) = Interaction.HitTesting.ScreenBox(n);
                var left = MathF.Max(x, ox);
                var top = MathF.Max(y, oy);
                var right = MathF.Min(x + w, ox + ow);
                var bottom = MathF.Min(y + h, oy + oh);
                if (right > left && bottom > top)
                    matches.Add(new(left - x, top - y, right - left, bottom - top));
            }
            foreach (var child in n.Children) Scan(child);
        }
    }

    public static float OverlayBottomInset(Dom.RenderNode node, float top, float height)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (height <= 0) return 0;
        var bottom = top + height;
        var inset = 0f;
        Measure(node);
        return MathF.Min(inset, height);

        void Measure(Dom.RenderNode n)
        {
            foreach (var child in n.Children)
            {
                if (child.LaidOut && child.Element?.HasAttribute("data-surface-overlay") == true)
                {
                    var (_, cy, _, ch) = Interaction.HitTesting.ScreenBox(child);
                    if (ch > 0 && MathF.Abs(cy + ch - bottom) <= 0.5f) inset = MathF.Max(inset, ch);
                }
                Measure(child);
            }
        }
    }
}

/// <summary>
/// The document's live surfaces, keyed by the element attribute <c>data-cupri-surface</c>.
/// Mirrors <see cref="ImageStore"/>'s host contract: <see cref="TakeArrived"/> is polled once
/// per host tick (folded into <c>CupriDocument.ConsumeImageArrived</c>) so a frame published
/// while the loop was idle still triggers exactly one repaint.
/// </summary>
public sealed class SurfaceRegistry
{
    private readonly object _lock = new();
    private readonly Dictionary<string, ISurfaceSource> _sources = new(StringComparer.Ordinal);
    private volatile bool _arrived;

    public void Register(string key, ISurfaceSource source)
    {
        lock (_lock) _sources[key] = source;
        _arrived = true; // the surface may already hold a frame — show it
    }

    public void Unregister(string key)
    {
        lock (_lock) _sources.Remove(key);
        _arrived = true; // repaint so the poster (or nothing) replaces the last frame
    }

    public ISurfaceSource? Get(string? key)
    {
        if (string.IsNullOrEmpty(key)) return null;
        lock (_lock) return _sources.TryGetValue(key, out var s) ? s : null;
    }

    /// <summary>Any surface currently producing frames — folded into the document's
    /// "something is animating" signal so every host keeps painting during playback.</summary>
    public bool AnyTicking
    {
        get
        {
            lock (_lock)
            {
                foreach (var s in _sources.Values)
                    if (s.Ticking) return true;
                return false;
            }
        }
    }

    /// <summary>Producers call this after swapping <see cref="ISurfaceSource.CurrentFrame"/> —
    /// the paused-seek / first-frame path, where nothing else would wake the render loop.</summary>
    public void NotifyFrame() => _arrived = true;

    /// <summary>
    /// Device pixels per engine unit, as the host is currently painting — the factor a surface needs
    /// in order to produce frames at the resolution its element will actually be drawn at.
    ///
    /// <para>Set once per frame by the host (it is the same number the host hands
    /// <c>canvas.Scale</c>, and the same one the web host already applies to underlay rects). Left at
    /// 1 by a host that does not, which is exactly right for one that never scales.</para>
    ///
    /// <para><b>Why a surface cannot work this out for itself.</b> <c>RenderNode.Width</c> is in
    /// engine units, and nothing else reaches a producer: a surface that sized its buffer from the
    /// node alone would render a 512-pixel image into a 1536-pixel box on a 3× phone and look soft
    /// for a reason nobody can see in the markup. Video does not care — the browser scales its own
    /// element — but anything that RASTERISES to order does.</para>
    /// </summary>
    public float DeviceScale
    {
        get => _deviceScale;
        set => _deviceScale = value > 0 && float.IsFinite(value) ? value : 1f;
    }

    private volatile float _deviceScale = 1f;

    /// <summary>True once a host has run <see cref="RenderGpuFrames"/> at least once, so a producer
    /// can tell whether the zero-copy path is actually available to it rather than guessing from the
    /// platform. False on the web, and on a desktop host that fell back to a software window.</summary>
    public bool HasGpuFrameHook { get; private set; }

    /// <summary>
    /// Give every <see cref="IGpuSurfaceSource"/> its turn on the host's GPU context, then hand the
    /// context back to Skia in a known state.
    ///
    /// <para>Hosts with a <see cref="GRContext"/> call this once per frame, BEFORE recording
    /// anything — the context must be current and Skia must not be mid-draw. The
    /// <c>ResetContext</c> afterwards is not optional and is deliberately here rather than in each
    /// producer: raw GL issued behind Skia's back leaves its state tracking wrong, and a corruption
    /// that only appears once some unrelated element is painted is not a bug anyone enjoys owning.
    /// Doing it once, centrally, means a producer cannot forget.</para>
    /// </summary>
    /// <returns>True if any producer ran.</returns>
    public bool RenderGpuFrames(GRContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        HasGpuFrameHook = true;

        IGpuSurfaceSource[] gpu;
        lock (_lock)
        {
            var n = 0;
            foreach (var s in _sources.Values) if (s is IGpuSurfaceSource) n++;
            if (n == 0) return false;
            gpu = new IGpuSurfaceSource[n];
            var i = 0;
            foreach (var s in _sources.Values) if (s is IGpuSurfaceSource g) gpu[i++] = g;
        }

        var ran = false;
        foreach (var g in gpu)
        {
            // One producer's bad frame must not take the window down, nor stop the others drawing.
            try { g.RenderOnGpu(context); ran = true; }
            catch { /* treated as "no frame this time"; the last one stays on screen */ }
        }
        if (ran) { context.ResetContext(); _arrived = true; }
        return ran;
    }

    // Frames a host has already seen, per key — so a producer that swaps CurrentFrame without
    // calling NotifyFrame (a decoder thread that knows nothing about hosts) still wakes the next
    // poll. Reference comparison per tick over a handful of surfaces: effectively free.
    private readonly Dictionary<string, SKImage?> _seenFrames = new(StringComparer.Ordinal);

    /// <summary>True (once, then reset) if a frame/registration arrived — or any surface's
    /// <see cref="ISurfaceSource.CurrentFrame"/> reference changed — since the last poll.</summary>
    public bool TakeArrived()
    {
        var arrived = _arrived;
        _arrived = false;
        lock (_lock)
        {
            foreach (var (key, source) in _sources)
            {
                var current = source.CurrentFrame;
                if (!_seenFrames.TryGetValue(key, out var seen) || !ReferenceEquals(seen, current))
                {
                    _seenFrames[key] = current;
                    arrived = true;
                }
            }
            if (_seenFrames.Count > _sources.Count)
            {
                List<string>? gone = null;
                foreach (var key in _seenFrames.Keys)
                    if (!_sources.ContainsKey(key)) (gone ??= new List<string>()).Add(key);
                if (gone is not null) foreach (var key in gone) _seenFrames.Remove(key);
            }
        }
        return arrived;
    }
}
