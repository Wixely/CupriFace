using System.Diagnostics;
using CupriFace.Media.Decoding;
using CupriFace.Media.Webm;
using CupriFace.Paint;
using SkiaSharp;

namespace CupriFace.Media;

/// <summary>
/// The desktop video backend: managed WebM demux + injected decoders (native libvpx/libopus in
/// production, fakes in tests) + an optional audio sink. Attach at the host composition root:
/// <c>DesktopHost.Run(app, d =&gt; d.UseVideo(new WebmVideoBackend(NativeDecoders.Factory)))</c>.
/// Sources resolve through <see cref="VideoSource"/> — the image pipeline's schemes and trust
/// model (embedded / file / <c>data:</c> / policied https), the developer's choice per element.
/// </summary>
public sealed class WebmVideoBackend : IVideoBackend
{
    private readonly IMediaDecoderFactory _decoders;
    private readonly IAudioSink? _audio;

    public WebmVideoBackend(IMediaDecoderFactory decoders, IAudioSink? audio = null)
    {
        _decoders = decoders;
        _audio = audio;
    }

    public IVideoPlayer Open(VideoSource source) =>
        // Local sources (embedded/file/data:) open synchronously, like local images. Remote ones
        // open DEFERRED: the poster stays up, the download runs on the player's thread, playback
        // starts at 0 when the bytes land — mirroring the image store's async remote loads.
        new WebmPlayer(source.LoadBytes, deferred: source.IsRemote, _decoders, _audio);
}

/// <summary>
/// Plays one WebM. Presentation model: a pump advances a media clock and decodes each block as
/// its timestamp comes due, swapping the result into <see cref="CurrentFrame"/>; the engine's
/// render loop (kept live by <see cref="Ticking"/>) paints whatever is current. The first frame
/// is presented on open (poster → real picture, like a browser's preload). Retired frames are
/// disposed a few swaps later, honouring the surface contract. The pump runs on a background
/// thread in production and is driven directly (manual clock) in tests.
/// </summary>
public sealed class WebmPlayer : IVideoPlayer, ISurfaceSource
{
    private readonly object _lock = new();
    private readonly IMediaDecoderFactory _factory;
    private readonly IAudioSink? _providedSink;
    private readonly Func<double> _now;
    private readonly Func<byte[]?> _load;

    private Thread? _thread;
    private bool _disposed;
    private bool _loaded;
    private bool _loadFailed;

    private WebmFile? _file;
    private WebmTrack? _videoTrack;
    private readonly List<WebmBlock> _video = new();
    private readonly List<WebmBlock> _audioBlocks = new();
    private IVideoFrameDecoder? _videoDecoder;
    private IAudioDecoder? _audioDecoder;
    private IAudioSink? _sink;

    private int _nextVideo;
    private int _nextAudio;
    private double _mediaBase;     // media time when the clock last (re)started
    private double _wallBase;      // _now() at that moment
    private bool _playing;
    private bool _muted;
    private double _volume = 1;
    private bool _ended;

    private SKImage? _current;
    private readonly Queue<SKImage> _retired = new();

    internal WebmPlayer(Func<byte[]?> load, bool deferred, IMediaDecoderFactory decoders, IAudioSink? sink, Func<double>? clock = null)
    {
        _load = load;
        _factory = decoders;
        _providedSink = sink;
        _now = clock ?? (() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency);

        if (!deferred)
        {
            var bytes = load() ?? throw new FileNotFoundException("Video source could not be resolved.");
            lock (_lock) InitFromLocked(bytes);
        }
        else
        {
            EnsureThread(); // the thread downloads first, then pumps; the poster shows meanwhile
        }
    }

    // Parse + wire decoders + show the first frame. Called under the lock, once.
    private void InitFromLocked(byte[] bytes)
    {
        if (_disposed || _loaded) return;
        _file = WebmFile.Parse(bytes);
        _videoTrack = _file.VideoTrack;
        _videoDecoder = _videoTrack is { } vt ? _factory.CreateVideo(vt) : null;
        var audioTrack = _file.AudioTrack;
        _audioDecoder = audioTrack is { } at ? _factory.CreateAudio(at) : null;
        _sink = _audioDecoder is not null ? _providedSink : null;
        foreach (var b in _file.Blocks)
        {
            if (_videoTrack is { } v && b.Track == v.Number) _video.Add(b);
            else if (audioTrack is { } a && b.Track == a.Number) _audioBlocks.Add(b);
        }
        if (_sink is not null && _audioDecoder is not null)
        {
            _sink.Start(_audioDecoder.SampleRate, _audioDecoder.Channels);
            _sink.Volume = _muted ? 0 : _volume;
        }
        _loaded = true;
        SeekLocked(0);                       // poster → the real first frame
        if (_playing)                        // Play() arrived while downloading: start at 0 NOW —
        {                                    // never "catch up" the time the download took
            _mediaBase = 0;
            _wallBase = _now();
            _sink?.Pause(false);
        }
    }

    // ---- ISurfaceSource ----------------------------------------------------------------------
    public SKImage? CurrentFrame => _current;
    public (int W, int H)? NaturalSize => _videoTrack is { Width: > 0, Height: > 0 } t ? (t.Width, t.Height) : null;
    public bool Ticking => _playing && _loaded;

    // ---- IVideoPlayer ------------------------------------------------------------------------
    public ISurfaceSource Surface => this;
    public bool Playing => _playing;
    public bool Loop { get; set; }
    public double Duration
    {
        get
        {
            lock (_lock)
                return _file?.DurationSeconds ?? (_video.Count > 0 ? _video[^1].TimeSeconds : 0);
        }
    }

    public event Action? Ended;

    public void Play()
    {
        lock (_lock)
        {
            if (_disposed || _playing) return;
            if (_loaded && (_ended || MediaTimeLocked() >= Duration)) SeekLocked(0);   // replay
            _ended = false;
            _wallBase = _now();
            _playing = true;                 // pre-load: pending — playback starts when bytes land
            if (_loaded) _sink?.Pause(false);
            EnsureThread();
        }
    }

    public void Pause()
    {
        lock (_lock)
        {
            if (!_playing) return;
            _mediaBase = MediaTimeLocked();
            _playing = false;
            _sink?.Pause(true);
        }
    }

    public bool Muted
    {
        get => _muted;
        set { lock (_lock) { _muted = value; if (_sink is { } s) s.Volume = value ? 0 : _volume; } }
    }

    public double Volume
    {
        get => _volume;
        set { lock (_lock) { _volume = Math.Clamp(value, 0, 1); if (_sink is { } s && !_muted) s.Volume = _volume; } }
    }

    public double Position
    {
        get { lock (_lock) return _loaded ? MediaTimeLocked() : 0; }
        set { lock (_lock) { if (_loaded) SeekLocked(Math.Clamp(value, 0, Duration)); } }
    }

    private double MediaTimeLocked() => _playing && _loaded ? _mediaBase + (_now() - _wallBase) : _mediaBase;

    // ---- the pump ----------------------------------------------------------------------------

    private void EnsureThread()
    {
        if (_thread is not null) return;
        _thread = new Thread(() =>
        {
            // Deferred open: resolve OFF the UI thread (this may be a network fetch under the
            // document's URL policy), then initialise under the lock.
            if (!Volatile.Read(ref _loaded) && !Volatile.Read(ref _disposed))
            {
                byte[]? bytes = null;
                try { bytes = _load(); } catch { /* unresolved → poster stays */ }
                lock (_lock)
                {
                    if (bytes is { Length: > 0 })
                    {
                        try { InitFromLocked(bytes); }
                        catch { _loadFailed = true; }
                    }
                    else _loadFailed = true;
                    if (_loadFailed) _playing = false;
                }
            }
            while (!Volatile.Read(ref _disposed))
            {
                Pump();
                Thread.Sleep(4);
            }
        })
        { IsBackground = true, Name = "cupriface-webm" };
        _thread.Start();
    }

    /// <summary>One clock advance: decode+present every block whose time has come. Internal so
    /// tests drive it deterministically with a manual clock; the thread just calls it often.</summary>
    internal void Pump()
    {
        lock (_lock)
        {
            if (!_playing || !_loaded || _disposed) return;
            var time = MediaTimeLocked();

            var presentedThisPump = 0;
            while (_nextVideo < _video.Count && _video[_nextVideo].TimeSeconds <= time)
            {
                var block = _video[_nextVideo++];
                var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                var frame = _videoDecoder?.Decode(block.Data.Span, block.Keyframe);
                if (frame is not null)
                {
                    var ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                    _decodeMsEma = _framesDecoded == 0 ? ms : _decodeMsEma * 0.9 + ms * 0.1;
                    _framesDecoded++;
                    presentedThisPump++;
                    Present(frame);
                }
            }
            // More than one due frame in a single pump: the earlier ones never reached the screen.
            if (presentedThisPump > 1) _framesLate += presentedThisPump - 1;

            while (_nextAudio < _audioBlocks.Count && _audioBlocks[_nextAudio].TimeSeconds <= time + 0.20)
            {
                var block = _audioBlocks[_nextAudio++];   // feed the sink slightly ahead
                if (_audioDecoder is { } dec && _sink is { } sink)
                {
                    var pcm = dec.Decode(block.Data.Span);
                    if (!pcm.IsEmpty) sink.Submit(pcm.Span);
                }
            }

            var videoDone = _nextVideo >= _video.Count;
            var audioDone = _nextAudio >= _audioBlocks.Count;
            if (videoDone && audioDone && time >= Duration)
            {
                if (Loop)
                {
                    SeekLocked(0);
                    return;
                }
                _mediaBase = Duration;
                _playing = false;
                _sink?.Pause(true);
                if (!_ended)
                {
                    _ended = true;
                    var handlers = Ended;
                    Task.Run(() => handlers?.Invoke());   // off the lock; doc coalesces on its thread
                }
            }
        }
    }

    // Seek: jump to the last keyframe at/before the target and decode forward (presenting only
    // the final frame) so the decoder's reference state is correct at the target.
    private void SeekLocked(double target)
    {
        var start = 0;
        for (var i = 0; i < _video.Count; i++)
        {
            if (_video[i].TimeSeconds > target) break;
            if (_video[i].Keyframe) start = i;
        }

        SKImage? shown = null;
        var index = start;
        while (index < _video.Count && _video[index].TimeSeconds <= target)
        {
            var block = _video[index++];
            var frame = _videoDecoder?.Decode(block.Data.Span, block.Keyframe);
            if (frame is not null)
            {
                shown?.Dispose();   // intermediate catch-up frames were never published
                shown = frame;
            }
        }
        if (shown is not null) Present(shown);

        _nextVideo = index;
        _nextAudio = 0;
        while (_nextAudio < _audioBlocks.Count && _audioBlocks[_nextAudio].TimeSeconds < target) _nextAudio++;
        _sink?.Flush();
        _mediaBase = target;
        _wallBase = _now();
        _ended = false;
    }

    // ---- live diagnostics (DiagnosticsSummary) ---------------------------------------------------
    private double _decodeMsEma;   // smoothed per-frame video decode cost
    private long _framesDecoded;
    private long _framesLate;      // due frames that were decoded but replaced within the same pump

    /// <summary>Smoothed per-frame video decode time (ms) — the number the SIMD work moves.</summary>
    public double DecodeMsAverage => _decodeMsEma;
    public long FramesDecoded => _framesDecoded;
    public long FramesLate => _framesLate;

    public string? DiagnosticsSummary =>
        $"decode {_decodeMsEma:0.00} ms · {_framesDecoded} frames · {_framesLate} late"
        + (_sink is { } s ? $" · audio queue {s.QueuedSeconds:0.00} s" : "");

    // Swap the new frame in; retire the old with two swaps of grace, honouring the surface
    // contract (the paint path may still be rasterising the previous reference).
    private void Present(SKImage frame)
    {
        var old = _current;
        _current = frame;
        if (old is not null)
        {
            _retired.Enqueue(old);
            while (_retired.Count > 2) _retired.Dequeue().Dispose();
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            Volatile.Write(ref _disposed, true);
            _playing = false;
        }
        _thread?.Join(200);
        lock (_lock)
        {
            _videoDecoder?.Dispose();
            _audioDecoder?.Dispose();
            _sink?.Dispose();
            while (_retired.Count > 0) _retired.Dequeue().Dispose();
            _current?.Dispose();
            _current = null;
        }
    }
}
