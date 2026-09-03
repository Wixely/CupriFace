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
