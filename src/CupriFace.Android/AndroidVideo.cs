using Android.Media;
using Android.Views;
using CupriFace.Media;
using VideoSource = CupriFace.Media.VideoSource;
using CupriFace.Paint;
using SkiaSharp;

namespace CupriFace.Android;

/// <summary>
/// <c>&lt;cupri-video&gt;</c> on a phone, using the platform's own decoder.
///
/// This is the WEB host's model rather than the desktop one, and deliberately so. The desktop
/// package carries codecs because a desktop app cannot assume any; a phone has hardware decoders
/// for everything it ships with, reachable through <c>MediaPlayer</c>. So the engine paints a
/// transparent HOLE at the element's box (<see cref="ISurfaceSource.HostComposited"/>) and a real
/// <see cref="SurfaceView"/> underneath shows the frames — zero copies, zero codec bytes of ours,
/// hardware decode, and whatever formats the device supports rather than only the ones we ship.
///
/// The trade is the same one the web host makes: an underlay is a rectangle. Engine content still
/// paints ON TOP of the hole, but a rounded corner or a rotation on the video element clips the
/// hole, not the video beneath it. That is stated here rather than discovered later.
/// </summary>
internal sealed class AndroidPlayer : IVideoPlayer, ISurfaceSource
{
    private readonly AndroidVideoBackend _backend;
    private MediaPlayer? _mp;
    private bool _muted, _loop, _prepared;
    private double _volume = 1;
    private double _pendingSeek = -1;
    private bool _playWhenReady;

    /// <summary>The view showing this player's frames. Created and positioned on the UI thread by
    /// the host; null until then.</summary>
    internal SurfaceView? View;
    internal (int W, int H)? Natural;
    internal bool Ready;                 // prepared: the surface has something to show

    internal AndroidPlayer(AndroidVideoBackend backend, VideoSource source)
    {
        _backend = backend;
        Source = source;
    }

    internal VideoSource Source { get; }

    // ---- ISurfaceSource: no frames cross into the engine; it punches a hole -------------------
    public SKImage? CurrentFrame => null;
    public (int W, int H)? NaturalSize => Natural;
    public bool Ticking => false;        // MediaPlayer drives its own surface; the engine can idle
    public bool HostComposited => Ready; // the poster paints until there are real pixels

    // ---- IVideoPlayer ------------------------------------------------------------------------
    public ISurfaceSource Surface => this;
    public bool Playing => _prepared && (_mp?.IsPlaying ?? false);

    public void Play()
    {
        _playWhenReady = true;
        if (_prepared) Safe(() => _mp?.Start());
    }

    public void Pause()
    {
        _playWhenReady = false;
        if (_prepared) Safe(() => _mp?.Pause());
    }

    public bool Muted
    {
        get => _muted;
        set { _muted = value; ApplyVolume(); }
    }

    public double Volume
    {
        get => _volume;
        set { _volume = Math.Clamp(value, 0, 1); ApplyVolume(); }
    }

    public bool Loop
    {
        get => _loop;
        set { _loop = value; Safe(() => { if (_mp is { } mp) mp.Looping = value; }); }
    }

    public double Duration => _prepared && _mp is { } mp && mp.Duration > 0 ? mp.Duration / 1000.0 : 0;

    public double Position
    {
        get => _prepared && _mp is { } mp ? mp.CurrentPosition / 1000.0 : Math.Max(0, _pendingSeek);
        set
        {
            // A seek before prepare is remembered rather than dropped: the controls are live as
            // soon as the element exists, and a scrub during load must not vanish.
            if (!_prepared) { _pendingSeek = value; return; }
            Safe(() => _mp?.SeekTo((int)(value * 1000)));
        }
    }

    public event Action? Ended;

    public string Diagnostics =>
        $"android MediaPlayer · {(Ready ? "ready" : "loading")} · {(Playing ? "playing" : "paused")} · " +
        $"{Natural?.W ?? 0}x{Natural?.H ?? 0} · {Position:0.0}/{Duration:0.0}s";

    // ---- host-side lifecycle (UI thread) ------------------------------------------------------

    /// <summary>Attach the decoder to the view's surface once Android has created it.</summary>
    internal void AttachSurface(Surface surface)
    {
        try
        {
            _mp?.Release();
            _mp = new MediaPlayer();
            _mp.SetSurface(surface);
            _mp.Looping = _loop;

            // The engine resolves every media source through ONE pipeline (embedded asset, file,
            // data: URI, policied https) — so the bytes are already ours. MediaPlayer wants a file
            // or descriptor, so a resolved source is spooled to the cache directory once.
            var path = _backend.MaterialiseToCache(Source);
            if (path is null) return;

            _mp.SetDataSource(path);
            _mp.Prepared += (_, _) =>
            {
                _prepared = true;
                Ready = true;
                if (_mp is { } m) Natural = (m.VideoWidth, m.VideoHeight);
                ApplyVolume();
                if (_pendingSeek >= 0) { Safe(() => _mp?.SeekTo((int)(_pendingSeek * 1000))); _pendingSeek = -1; }
                if (_playWhenReady) Safe(() => _mp?.Start());
                // The gate's only window into playback: CI cannot see through a punched hole, but
                // it can see that the clip resolved, the platform decoder accepted it and reported
                // real dimensions. What remains unprovable without eyes is whether the pixels are
                // visible — which is a z-order question, not a decode one.
                Console.WriteLine($"cupri-gate: video ready {Natural?.W ?? 0}x{Natural?.H ?? 0}");
                _backend.Invalidate();      // HostComposited flipped on → repaint punches the hole
            };
            _mp.Completion += (_, _) => { if (!_loop) Ended?.Invoke(); };
            _mp.PrepareAsync();
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn(AndroidHost.Tag, $"video: {ex.Message}");
        }
    }

    private void ApplyVolume()
    {
        var v = _muted ? 0f : (float)_volume;
        Safe(() => _mp?.SetVolume(v, v));
    }

    // MediaPlayer throws IllegalState for any call in the wrong state, and a video that fails must
    // leave the poster up rather than take the app down with it.
    private static void Safe(Action a)
    {
        try { a(); }
        catch (Exception ex) { global::Android.Util.Log.Warn(AndroidHost.Tag, $"video: {ex.Message}"); }
    }

    public void Dispose()
    {
        _backend.Close(this);
        Safe(() => { _mp?.Release(); _mp = null; });
        _prepared = false;
        Ready = false;
    }
}
