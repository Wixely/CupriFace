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
/// </summary>
public sealed class WebmVideoBackend : IVideoBackend
{
    private readonly IMediaDecoderFactory _decoders;
    private readonly IAudioSink? _audio;
    private readonly Func<string, byte[]> _load;

    /// <param name="load">Resolves a <c>src</c> to bytes. Default: the file system. Hosts pass
    /// their own to serve embedded assets (<c>CupriSource</c>) or vetted URLs.</param>
    public WebmVideoBackend(IMediaDecoderFactory decoders, IAudioSink? audio = null, Func<string, byte[]>? load = null)
    {
        _decoders = decoders;
        _audio = audio;
        _load = load ?? File.ReadAllBytes;
    }

    public IVideoPlayer Open(string src) => new WebmPlayer(WebmFile.Parse(_load(src)), _decoders, _audio);
}

/// <summary>
/// Plays one parsed WebM. Presentation model: a pump advances a media clock and decodes each
/// block as its timestamp comes due, swapping the result into <see cref="CurrentFrame"/>; the
/// engine's render loop (kept live by <see cref="Ticking"/>) paints whatever is current.
/// Retired frames are disposed a few swaps later, honouring the surface contract (never dispose
/// what the paint path may still read). The pump runs on a background thread in production and
/// is driven directly (with a manual clock) in tests.
/// </summary>
public sealed class WebmPlayer : IVideoPlayer, ISurfaceSource
{
    private readonly object _lock = new();
    private readonly WebmFile _file;
    private readonly WebmTrack? _videoTrack;
    private readonly List<WebmBlock> _video = new();
    private readonly List<WebmBlock> _audioBlocks = new();
    private readonly IVideoFrameDecoder? _videoDecoder;
    private readonly IAudioDecoder? _audioDecoder;
    private readonly IAudioSink? _sink;
    private readonly Func<double> _now;

    private Thread? _thread;
    private bool _disposed;

    private int _nextVideo;
    private int _nextAudio;
    private double _mediaBase;     // media time when the clock last (re)started
    private double _wallBase;      // _now() at that moment
    private bool _playing;
    private bool _muted;
    private double _volume = 1;
    private bool _ended;           // raised once per run to the end

    private SKImage? _current;
    private readonly Queue<SKImage> _retired = new();

    internal WebmPlayer(WebmFile file, IMediaDecoderFactory decoders, IAudioSink? sink, Func<double>? clock = null)
    {
        _file = file;
        _now = clock ?? (() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency);
        _videoTrack = file.VideoTrack;
        _videoDecoder = _videoTrack is { } vt ? decoders.CreateVideo(vt) : null;
        var audioTrack = file.AudioTrack;
        _audioDecoder = audioTrack is { } at ? decoders.CreateAudio(at) : null;
        _sink = _audioDecoder is not null ? sink : null;
        foreach (var b in file.Blocks)
        {
            if (_videoTrack is { } v && b.Track == v.Number) _video.Add(b);
            else if (audioTrack is { } a && b.Track == a.Number) _audioBlocks.Add(b);
        }
        if (_sink is not null && _audioDecoder is not null)
        {
            _sink.Start(_audioDecoder.SampleRate, _audioDecoder.Channels);
            _sink.Volume = _volume;
        }
    }

    // ---- ISurfaceSource ----------------------------------------------------------------------
    public SKImage? CurrentFrame => _current;
    public (int W, int H)? NaturalSize => _videoTrack is { Width: > 0, Height: > 0 } t ? (t.Width, t.Height) : null;
    public bool Ticking => _playing;

    // ---- IVideoPlayer ------------------------------------------------------------------------
    public ISurfaceSource Surface => this;
    public bool Playing => _playing;
    public bool Loop { get; set; }
    public double Duration => _file.DurationSeconds
        ?? (_video.Count > 0 ? _video[^1].TimeSeconds : 0);

    public event Action? Ended;

    public void Play()
    {
        lock (_lock)
        {
            if (_disposed || _playing) return;
            if (_ended || MediaTimeLocked() >= Duration) SeekLocked(0);   // replay from the top
            _ended = false;
            _wallBase = _now();
            _playing = true;
            _sink?.Pause(false);
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
        get { lock (_lock) return MediaTimeLocked(); }
        set { lock (_lock) SeekLocked(Math.Clamp(value, 0, Duration)); }
    }

    private double MediaTimeLocked() => _playing ? _mediaBase + (_now() - _wallBase) : _mediaBase;

    // ---- the pump ----------------------------------------------------------------------------

    private void EnsureThread()
    {
        if (_thread is not null) return;
        _thread = new Thread(() =>
        {
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
            if (!_playing || _disposed) return;
            var time = MediaTimeLocked();

            while (_nextVideo < _video.Count && _video[_nextVideo].TimeSeconds <= time)
            {
                var block = _video[_nextVideo++];
                var frame = _videoDecoder?.Decode(block.Data.Span, block.Keyframe);
                if (frame is not null) Present(frame);
            }

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
