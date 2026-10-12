using CupriFace.Android;
using CupriFace.Media;
using CupriFace.Paint;
using LibVLCSharp.Shared;
using SkiaSharp;
using AndroidVlcVideoView = LibVLCSharp.Platforms.Android.VideoView;
using CupriVideoTrack = CupriFace.Media.VideoTrack;
using VlcMedia = LibVLCSharp.Shared.Media;
using VlcMediaPlayer = LibVLCSharp.Shared.MediaPlayer;
using VideoSource = CupriFace.Media.VideoSource;

namespace CupriFace.Media.Android;

/// <summary>
/// <c>&lt;cupri-video&gt;</c> on Android, rendered by LibVLC into its native video view.
/// The view remains an underlay and the engine punches the same transparent hole it does for every
/// host-composited surface. LibVLC is used instead of framework MediaPlayer because real-world MKV
/// sources commonly require embedded audio/subtitle enumeration, chapters, and reliable HTTP range
/// seeking that the framework extractor does not provide.
/// </summary>
internal sealed class AndroidPlayer : IVideoPlayer, IVideoTrackSelector, IVideoChapterProvider, IVideoBufferingState, ISurfaceSource
{
    private readonly object _gate = new();
    private readonly VlcVideoBackend _backend;
    private readonly LibVLC _libVlc;
    private readonly VlcMediaPlayer _player;
    private VlcMedia? _media;
    private bool _mediaOpened;
    private bool _playWhenReady;
    private bool _disposed;
    private bool _ready;
    private bool _loop;
    private double _volume = 1;
    private double _pendingSeek = -1;
    // A seek is only BELIEVED for this long. _pendingSeek makes Position report the target rather
    // than the decoder's own time, which is what stops a drag snapping backwards — but it is
    // cleared by a TimeChanged near the target, and a seek past the seekable range (or a source
    // that stops reporting time) never produces one. Without a deadline the scrubber then reads
    // the target for ever while playback carries on somewhere else.
    private const long SeekBeliefMs = 15_000;
    private long _seekDeadlineMs;
    private int _metadataRefreshQueued;
    private int _seekVersion;
    // Workers currently inside a LibVLC call. Dispose waits for this to drain, because the native
    // player is freed there and a worker that merely checked _disposed a moment earlier would
    // otherwise be calling into freed memory — a process crash, not an exception.
    private int _nativeBusy;
    private bool _buffering;
    private CupriVideoTrack[] _audioTracks = [];
    private CupriVideoTrack[] _subtitleTracks = [];
    private VideoChapter[] _chapters = [];

    internal AndroidVlcVideoView? View;
    internal (int W, int H)? Natural;
    internal bool Ready => _ready;
    internal VideoSource Source { get; }

    internal AndroidPlayer(VlcVideoBackend backend, LibVLC libVlc, VideoSource source)
    {
        _backend = backend;
        _libVlc = libVlc;
        Source = source;
        _player = new VlcMediaPlayer(libVlc)
        {
            EnableHardwareDecoding = true,
            EnableKeyInput = false,
            EnableMouseInput = false,
        };
        _player.Playing += OnPlaying;
        _player.TimeChanged += OnTimeChanged;
        _player.Buffering += OnBuffering;
        _player.ESAdded += OnElementaryStreamChanged;
        _player.ESDeleted += OnElementaryStreamChanged;
        _player.ESSelected += OnElementaryStreamChanged;
        _player.TitleChanged += OnTitleChanged;
        _player.ChapterChanged += OnChapterChanged;
        _player.EndReached += OnEnded;
        _player.EncounteredError += OnError;
        _ = PrepareAsync();
    }

    public ISurfaceSource Surface => this;
    public SKImage? CurrentFrame => null;
    public (int W, int H)? NaturalSize => Natural;
    public bool Ticking => false;
    public bool HostComposited => _ready;
    public bool Playing => !_disposed && _player.IsPlaying;
    public bool Buffering => _buffering;

    public void Play()
    {
        _playWhenReady = true;
        TryStart();
        _backend.PlayerStateChanged(this);
    }

    public void Pause()
    {
        _playWhenReady = false;
        if (!_disposed && _player.IsPlaying) _player.Pause();
        _backend.PlayerStateChanged(this);
    }

    public bool Muted
    {
        get => !_disposed && _player.Mute;
        set { if (!_disposed) _player.Mute = value; }
    }

    public double Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0, 1);
            if (!_disposed) _player.Volume = (int)Math.Round(_volume * 100);
        }
    }

    public bool Loop
    {
        get => _loop;
        set => _loop = value;
    }

    public double Duration => _disposed ? 0 : Math.Max(0, _player.Length / 1000.0);

    /// <summary>Whether a requested seek is still worth believing. Expires it in passing, so every
    /// reader — the position, the buffering indicator, the retry on Playing — agrees about it and
    /// none of them can be left waiting on a seek that will never report completion. Call under
    /// <c>_gate</c>.</summary>
    private bool PendingSeekLive()
    {
        if (_pendingSeek < 0) return false;
        if (Environment.TickCount64 <= _seekDeadlineMs) return true;
        _pendingSeek = -1;
        return false;
    }

    // Under _gate: arm a seek and start its clock.
    private void SetPendingSeek(double seconds)
    {
        _pendingSeek = Math.Max(0, seconds);
        _seekDeadlineMs = Environment.TickCount64 + SeekBeliefMs;
    }

    public double Position
    {
        get
        {
            lock (_gate)
            {
                if (PendingSeekLive()) return _pendingSeek;
                return _disposed ? 0 : Math.Max(0, _player.Time / 1000.0);
            }
        }
        set
        {
            lock (_gate) SetPendingSeek(value);
            SetBuffering(true);
            if (_mediaOpened) QueuePendingSeek();
        }
    }

    /// <summary>Enter a LibVLC call from a worker. False when the player is being torn down, which
    /// is the caller's cue to do nothing at all.</summary>
    private bool TryEnterNative()
    {
        lock (_gate)
        {
            if (_disposed) return false;
            _nativeBusy++;
            return true;
        }
    }

    private void ExitNative()
    {
        lock (_gate)
        {
            if (--_nativeBusy <= 0) Monitor.PulseAll(_gate);
        }
    }

    public event Action? Ended;
    public event Action? TracksChanged;
    public event Action? ChaptersChanged;
    public event Action? BufferingChanged;

    public IReadOnlyList<CupriVideoTrack> AudioTracks { get { lock (_gate) return _audioTracks; } }
    public IReadOnlyList<CupriVideoTrack> SubtitleTracks { get { lock (_gate) return _subtitleTracks; } }
    public int SelectedAudioTrack => _disposed ? -1 : _player.AudioTrack;
    public int SelectedSubtitleTrack => _disposed ? -1 : _player.Spu;
    public IReadOnlyList<VideoChapter> Chapters { get { lock (_gate) return _chapters; } }
    public int SelectedChapter => _disposed ? -1 : _player.Chapter;

    public bool SelectAudioTrack(int trackId)
    {
        lock (_gate)
        {
            if (_disposed || !_audioTracks.Any(track => track.Id == trackId)) return false;
            var selected = _player.SetAudioTrack(trackId);
            if (selected) TracksChanged?.Invoke();
            return selected;
        }
    }

    public bool SelectSubtitleTrack(int trackId)
    {
        lock (_gate)
        {
            if (_disposed || !_subtitleTracks.Any(track => track.Id == trackId)) return false;
            var selected = _player.SetSpu(trackId);
            if (selected) TracksChanged?.Invoke();
            return selected;
        }
    }

    public bool SelectChapter(int chapterIndex)
    {
        VideoChapter? chapter;
        lock (_gate)
        {
            if (_disposed || (chapter = _chapters.FirstOrDefault(candidate => candidate.Index == chapterIndex)) is null)
                return false;
            SetPendingSeek(chapter.StartSeconds);
        }

        SetBuffering(true);
        _ = Task.Run(() =>
        {
            // Changing chapters may trigger the same remote range request as a timeline seek.
            // Keep it away from Android's GL/document thread so the loading state can render —
            // and inside the gate, because a blocked request outliving Dispose is a freed player.
            if (!TryEnterNative()) return;
            try { _player.Chapter = chapter.Index; }
            finally { ExitNative(); }
            _backend.PlayerStateChanged(this);
        });
        return true;
    }

    public string DiagnosticsSummary =>
        $"android LibVLC · {(_ready ? "ready" : "loading")} · {(Playing ? "playing" : "paused")} · " +
        $"{Natural?.W ?? 0}x{Natural?.H ?? 0} · {Position:0.0}/{Duration:0.0}s";

    internal void AttachView(AndroidVlcVideoView view)
    {
        if (_disposed) return;
        View = view;
        view.MediaPlayer = _player;
        TryStart();
    }

    private async Task PrepareAsync()
    {
        try
        {
            if (Source.IsRemote)
            {
                if (!Uri.TryCreate(Source.Src, UriKind.Absolute, out var uri)
                    || uri.Scheme != Uri.UriSchemeHttps || string.IsNullOrWhiteSpace(uri.Host)
                    || !string.IsNullOrEmpty(uri.UserInfo))
                    throw new InvalidOperationException("Remote video sources must use an absolute HTTPS URI without user information.");
                _media = new VlcMedia(_libVlc, uri, ":network-caching=5000", ":http-reconnect", ":input-fast-seek");
            }
            else
            {
                var path = await Task.Run(() => _backend.MaterialiseToCache(Source)).ConfigureAwait(false);
                if (path is null) return;
                // Spooling can outlast the element. Publish the native Media under the gate and
                // check the flag there: assigned after Dispose has already run its _media?.Dispose(),
                // it would never be freed at all.
                var media = new VlcMedia(_libVlc, path, FromType.FromPath);
                lock (_gate)
                {
                    if (_disposed) { media.Dispose(); return; }
                    _media = media;
                }
            }
            TryStart();
        }
        catch (Exception exception)
        {
            global::Android.Util.Log.Warn(AndroidHost.Tag, $"video preparation: {exception.GetType().Name}");
        }
    }

    private void TryStart()
    {
        lock (_gate)
        {
            if (_disposed || !_playWhenReady || View is null || _media is null || _player.IsPlaying) return;
            if (!_mediaOpened) _mediaOpened = _player.Play(_media);
            else _player.Play();
        }
    }

    private void QueuePendingSeek()
    {
        double target;
        int version;
        lock (_gate)
        {
            if (_disposed || !PendingSeekLive()) return;
            target = _pendingSeek;
            version = ++_seekVersion;
        }
        _ = Task.Run(() =>
        {
            if (version != Volatile.Read(ref _seekVersion)) return;
            // LibVLC can synchronously wait for a remote range response here. Keeping that call on
            // a worker is what prevents a slow seek from freezing Android's GL/document thread —
            // and the gate is what stops Dispose freeing the player underneath it while it waits.
            if (!TryEnterNative()) return;
            try { _player.Time = (long)Math.Round(target * 1000); }
            finally { ExitNative(); }
            _backend.PlayerStateChanged(this);
        });
    }

    private void OnPlaying(object? sender, EventArgs args)
    {
        _ready = true;
        // LibVLC callbacks must not call back into LibVLC synchronously. Move seeking and metadata
        // queries to a worker after the callback returns.
        _ = Task.Run(() =>
        {
            if (!TryEnterNative()) return;
            bool hasPendingSeek;
            try
            {
                uint width = 0, height = 0;
                if (_player.Size(0, ref width, ref height) && width > 0 && height > 0)
                    Natural = ((int)width, (int)height);
                lock (_gate) hasPendingSeek = PendingSeekLive();
                RefreshTracks();
                RefreshChapters();
            }
            finally { ExitNative(); }
            if (hasPendingSeek) QueuePendingSeek();
            _backend.Invalidate();
            _backend.PlayerStateChanged(this);
            Console.WriteLine($"cupri-gate: video ready {Natural?.W ?? 0}x{Natural?.H ?? 0}");
        });
    }

    private void OnElementaryStreamChanged(object? sender, EventArgs args) => ScheduleMetadataRefresh();

    private void OnTitleChanged(object? sender, MediaPlayerTitleChangedEventArgs args) => ScheduleMetadataRefresh();

    private void ScheduleMetadataRefresh()
    {
        if (_disposed || Interlocked.Exchange(ref _metadataRefreshQueued, 1) != 0) return;
        _ = Task.Run(async () =>
        {
            // ES/title events are emitted while LibVLC is still updating its description lists.
            // Let that callback complete and coalesce a burst of discoveries into one UI update.
            await Task.Delay(100).ConfigureAwait(false);
            Interlocked.Exchange(ref _metadataRefreshQueued, 0);
            if (!TryEnterNative()) return;
            try
            {
                RefreshTracks();
                RefreshChapters();
            }
            finally { ExitNative(); }
            _backend.Invalidate();
        });
    }

    private void OnTimeChanged(object? sender, MediaPlayerTimeChangedEventArgs args)
    {
        lock (_gate)
        {
            if (!PendingSeekLive() || Math.Abs(args.Time / 1000.0 - _pendingSeek) > 2) return;
            _pendingSeek = -1;
        }
        SetBuffering(false);
    }

    private void OnBuffering(object? sender, MediaPlayerBufferingEventArgs args)
    {
        bool seekPending;
        lock (_gate) seekPending = PendingSeekLive();
        SetBuffering(seekPending || args.Cache < 100f);
    }

    private void SetBuffering(bool value)
    {
        if (_buffering == value) return;
        _buffering = value;
        BufferingChanged?.Invoke();
        _backend.Invalidate();
    }

    private void RefreshTracks()
    {
        if (_disposed) return;
        var audio = _player.AudioTrackDescription
            .Where(track => track.Id >= 0)
            .Select((track, index) => new CupriVideoTrack(track.Id, Label(track.Name, "Audio", index + 1)))
            .ToArray();
        var subtitles = _player.SpuDescription
            .Select((track, index) => new CupriVideoTrack(track.Id,
                track.Id < 0 ? "Off" : Label(track.Name, "Subtitle", index + 1)))
            .ToList();
        if (subtitles.All(track => track.Id >= 0)) subtitles.Insert(0, new CupriVideoTrack(-1, "Off"));

        var changed = false;
        lock (_gate)
        {
            if (!_audioTracks.SequenceEqual(audio) || !_subtitleTracks.SequenceEqual(subtitles))
            {
                _audioTracks = audio;
                _subtitleTracks = subtitles.ToArray();
                changed = true;
            }
        }
        if (changed)
        {
            TracksChanged?.Invoke();
            global::Android.Util.Log.Info(AndroidHost.Tag,
                $"video metadata: audio={audio.Length}; subtitles={subtitles.Count}");
        }
    }

    private void RefreshChapters()
    {
        if (_disposed) return;
        VideoChapter[] chapters;
        try
        {
            chapters = _player.FullChapterDescriptions(-1)
                .Select((chapter, index) => new VideoChapter(index,
                    Label(chapter.Name, "Chapter", index + 1),
                    Math.Max(0, chapter.TimeOffset / 1000.0),
                    Math.Max(0, chapter.Duration / 1000.0)))
                .ToArray();
        }
        catch { chapters = []; }

        var changed = false;
        lock (_gate)
        {
            if (!_chapters.SequenceEqual(chapters))
            {
                _chapters = chapters;
                changed = true;
            }
        }
        if (changed)
        {
            ChaptersChanged?.Invoke();
            global::Android.Util.Log.Info(AndroidHost.Tag, $"video metadata: chapters={chapters.Length}");
        }
    }

    private static string Label(string? value, string fallback, int number) =>
        string.IsNullOrWhiteSpace(value) ? $"{fallback} {number}" : value.Trim();

    private void OnChapterChanged(object? sender, MediaPlayerChapterChangedEventArgs args)
    {
        ChaptersChanged?.Invoke();
        _backend.Invalidate();
    }

    private void OnEnded(object? sender, EventArgs args)
    {
        if (_loop)
        {
            _ = Task.Run(() =>
            {
                if (!TryEnterNative()) return;
                try { _player.Stop(); _player.Play(); }
                finally { ExitNative(); }
            });
            return;
        }
        Ended?.Invoke();
        _backend.PlayerStateChanged(this);
    }

    private void OnError(object? sender, EventArgs args)
    {
        _ready = false;
        global::Android.Util.Log.Warn(AndroidHost.Tag, "video: LibVLC reported a playback error");
        _backend.Invalidate();
        _backend.PlayerStateChanged(this);
    }

    public void Dispose()
    {
        // Claim the teardown and WAIT for any worker already inside a LibVLC call. Setting the flag
        // under _gate is what makes TryEnterNative a real gate rather than a racing read: a worker
        // either got in before this and is waited for, or is refused. Without the wait, a seek
        // worker blocked on a remote range response would return into a freed native player.
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            while (_nativeBusy > 0) Monitor.Wait(_gate);
        }
        _player.Playing -= OnPlaying;
        _player.TimeChanged -= OnTimeChanged;
        _player.Buffering -= OnBuffering;
        _player.ESAdded -= OnElementaryStreamChanged;
        _player.ESDeleted -= OnElementaryStreamChanged;
        _player.ESSelected -= OnElementaryStreamChanged;
        _player.TitleChanged -= OnTitleChanged;
        _player.ChapterChanged -= OnChapterChanged;
        _player.EndReached -= OnEnded;
        _player.EncounteredError -= OnError;
        _player.Stop();
        _media?.Dispose();
        _player.Dispose();
        _ready = false;
        _backend.Close(this);
    }
}
