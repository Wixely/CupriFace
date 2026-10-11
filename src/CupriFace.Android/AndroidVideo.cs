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
internal sealed class AndroidPlayer : IVideoPlayer, IVideoTrackSelector, ISurfaceSource
{
    private readonly AndroidVideoBackend _backend;
    private MediaPlayer? _mp;
    private bool _muted, _loop, _prepared;
    private double _volume = 1;
    private double _pendingSeek = -1;
    private bool _playWhenReady;
    private IReadOnlyList<VideoTrack> _audioTracks = [];
    private IReadOnlyList<VideoTrack> _subtitleTracks = [];
    private int _selectedAudioTrack = -1;
    private int _selectedSubtitleTrack = -1;

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
        _backend.PlayerStateChanged(this);
    }

    public void Pause()
    {
        _playWhenReady = false;
        if (_prepared) Safe(() => _mp?.Pause());
        _backend.PlayerStateChanged(this);
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
        get => _pendingSeek >= 0
            ? _pendingSeek
            : _prepared && _mp is { } mp ? mp.CurrentPosition / 1000.0 : 0;
        set
        {
            // A seek before prepare is remembered rather than dropped: the controls are live as
            // soon as the element exists, and a scrub during load must not vanish.
            _pendingSeek = Math.Max(0, value);
            if (_prepared) SeekToPendingPosition();
        }
    }

    public event Action? Ended;
    public event Action? TracksChanged;

    public IReadOnlyList<VideoTrack> AudioTracks => _audioTracks;
    public IReadOnlyList<VideoTrack> SubtitleTracks => _subtitleTracks;
    public int SelectedAudioTrack => _selectedAudioTrack;
    public int SelectedSubtitleTrack => _selectedSubtitleTrack;

    public bool SelectAudioTrack(int trackId) => SelectTrack(MediaTrackType.Audio, trackId);

    public bool SelectSubtitleTrack(int trackId)
    {
        if (!_prepared || _mp is null || !_subtitleTracks.Any(track => track.Id == trackId)) return false;
        var changed = false;
        Safe(() =>
        {
            if (trackId < 0)
            {
                var selected = _mp.GetSelectedTrack(MediaTrackType.Subtitle);
                if (selected < 0) selected = _mp.GetSelectedTrack(MediaTrackType.Timedtext);
                if (selected >= 0) _mp.DeselectTrack(selected);
            }
            else _mp.SelectTrack(trackId);
            _selectedSubtitleTrack = trackId;
            changed = true;
        });
        if (changed) TracksChanged?.Invoke();
        return changed;
    }

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
            _mp.SeekComplete += (_, _) =>
            {
                // Multiple drag samples can enqueue multiple platform seeks. An older completion
                // must not clear a newer target, otherwise the controls read the old native
                // position and snap the thumb backwards. Keep/reissue the newest target until the
                // decoder reports that it actually reached it.
                if (_pendingSeek >= 0 && _mp is { } seeking
                    && Math.Abs(seeking.CurrentPosition / 1000.0 - _pendingSeek) > 1.5)
                    SeekToPendingPosition();
                else
                    _pendingSeek = -1;
                _backend.PlayerStateChanged(this);
            };

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
                Safe(() => _mp?.SetVideoScalingMode(VideoScalingMode.ScaleToFit));
                if (_mp is { } m) Natural = (m.VideoWidth, m.VideoHeight);
                RefreshTracks();
                ApplyVolume();
                if (_pendingSeek >= 0) SeekToPendingPosition();
                if (_playWhenReady) Safe(() => _mp?.Start());
                // The gate's only window into playback: CI cannot see through a punched hole, but
                // it can see that the clip resolved, the platform decoder accepted it and reported
                // real dimensions. What remains unprovable without eyes is whether the pixels are
                // visible — which is a z-order question, not a decode one.
                Console.WriteLine($"cupri-gate: video ready {Natural?.W ?? 0}x{Natural?.H ?? 0}");
                _backend.Invalidate();      // HostComposited flipped on → repaint punches the hole
                _backend.PlayerStateChanged(this);
            };
            _mp.Completion += (_, _) =>
            {
                _backend.PlayerStateChanged(this);
                if (!_loop) Ended?.Invoke();
            };
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

    private void SeekToPendingPosition()
    {
        if (_mp is not { } mp || _pendingSeek < 0) return;
        var milliseconds = (long)Math.Round(_pendingSeek * 1000);
        Safe(() =>
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(26))
                mp.SeekTo(milliseconds, MediaPlayerSeekMode.Closest);
            else
                mp.SeekTo((int)Math.Min(int.MaxValue, milliseconds));
        });
        _backend.PlayerStateChanged(this);
    }

    private bool SelectTrack(MediaTrackType type, int trackId)
    {
        if (!_prepared || _mp is null || !_audioTracks.Any(track => track.Id == trackId)) return false;
        var changed = false;
        Safe(() =>
        {
            _mp.SelectTrack(trackId);
            _selectedAudioTrack = trackId;
            changed = true;
        });
        if (changed) TracksChanged?.Invoke();
        return changed;
    }

    private void RefreshTracks()
    {
        if (_mp is not { } mp) return;
        var audio = new List<VideoTrack>();
        var subtitles = new List<VideoTrack> { new(-1, "Off") };
        var audioNumber = 0;
        var subtitleNumber = 0;
        var infos = mp.GetTrackInfo() ?? [];
        for (var index = 0; index < infos.Length; index++)
        {
            var info = infos[index];
            if (info.TrackType == MediaTrackType.Audio)
                audio.Add(new VideoTrack(index, TrackLabel(info, "Audio", ++audioNumber)));
            else if (info.TrackType is MediaTrackType.Subtitle or MediaTrackType.Timedtext)
                subtitles.Add(new VideoTrack(index, TrackLabel(info, "Subtitle", ++subtitleNumber)));
        }

        _audioTracks = audio;
        _subtitleTracks = subtitles;
        _selectedAudioTrack = SafeSelectedTrack(mp, MediaTrackType.Audio);
        var selectedSubtitle = SafeSelectedTrack(mp, MediaTrackType.Subtitle);
        if (selectedSubtitle < 0) selectedSubtitle = SafeSelectedTrack(mp, MediaTrackType.Timedtext);
        _selectedSubtitleTrack = selectedSubtitle;
        TracksChanged?.Invoke();
    }

    private static string TrackLabel(MediaPlayer.TrackInfo info, string fallback, int number)
    {
        var language = info.Language;
        return string.IsNullOrWhiteSpace(language) || string.Equals(language, "und", StringComparison.OrdinalIgnoreCase)
            ? $"{fallback} {number}"
            : $"{fallback} {number} · {language}";
    }

    private static int SafeSelectedTrack(MediaPlayer player, MediaTrackType type)
    {
        try { return player.GetSelectedTrack(type); }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn(AndroidHost.Tag, $"video tracks: {ex.Message}");
            return -1;
        }
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
        Safe(() => { _mp?.Release(); _mp = null; });
        _prepared = false;
        Ready = false;
        _backend.Close(this);
    }
}
