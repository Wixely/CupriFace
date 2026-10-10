using System.Runtime.InteropServices;
using System.Security.Cryptography;
using CupriFace.Media;
using CupriFace.Paint;
using CupriFace.Shell;
using LibVLCSharp.Shared;
using SkiaSharp;
using VlcMedia = LibVLCSharp.Shared.Media;
using VlcMediaPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace CupriFace.Media.Windows;

/// <summary>
/// Hardware-accelerated Windows video using LibVLC and an embedded child HWND. The application
/// still talks only to CupriFace's portable <see cref="IVideoBackend"/> contract.
/// </summary>
public sealed class LibVlcVideoBackend : IVideoBackend, IDisposable
{
    private readonly LibVLC _libVlc;
    private readonly string _cacheDirectory;
    private readonly bool _debug;
    private readonly HashSet<LibVlcVideoPlayer> _players = [];
    private bool _disposed;

    public LibVlcVideoBackend(string? cacheDirectory = null)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The LibVLC Windows backend requires Windows.");

        _debug = Environment.GetEnvironmentVariable("CUPRIFACE_MEDIA_DEBUG") is "1" or "true" or "TRUE";
        // Assembly.Location and, with IncludeAllContentForSelfExtract, AppContext.BaseDirectory can
        // point into the single-file extraction directory. LibVLC is kept external and replaceable
        // for LGPL compliance, so prefer the directory containing the running apphost/executable.
        var architectureDirectory = ArchitectureDirectory();
        var processDirectory = Path.GetDirectoryName(Environment.ProcessPath);
        var applicationDirectory = processDirectory is not null
            && Directory.Exists(Path.Combine(processDirectory, "libvlc", architectureDirectory))
                ? processDirectory
                : AppContext.BaseDirectory;
        var nativeDirectory = Path.Combine(applicationDirectory, "libvlc", architectureDirectory);
        if (_debug) Console.WriteLine($"[cupri-media] native directory selected: {architectureDirectory}");
        Core.Initialize(nativeDirectory);
        var options = new List<string>
        {
            "--avcodec-hw=d3d11va",
            "--no-video-title-show",
            "--no-snapshot-preview",
        };
        if (_debug) options.Add("--verbose=2");
        _libVlc = new LibVLC(options.ToArray());
        if (_debug)
        {
            _libVlc.Log += (_, args) =>
            {
                var line = args.FormattedLog;
                if (line.Contains("d3d", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("dxva", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("direct3d", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("decoder", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("vp9", StringComparison.OrdinalIgnoreCase))
                    Console.WriteLine("[cupri-media] " + line);
            };
        }
        _cacheDirectory = cacheDirectory ?? Path.Combine(Path.GetTempPath(), "CupriFace.Media.Windows");
        Directory.CreateDirectory(_cacheDirectory);
    }

    private static string ArchitectureDirectory() => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X64 => "win-x64",
        Architecture.X86 => "win-x86",
        Architecture.Arm64 => "win-arm64",
        var architecture => throw new PlatformNotSupportedException(
            $"The LibVLC Windows backend does not provide {architecture} native libraries."),
    };

    public IVideoPlayer Open(VideoSource source)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var player = new LibVlcVideoPlayer(_libVlc, source, _cacheDirectory,
            message => { if (_debug) Console.WriteLine("[cupri-media] " + message); },
            closed => { lock (_players) _players.Remove(closed); });
        lock (_players) _players.Add(player);
        return player;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        LibVlcVideoPlayer[] players;
        lock (_players) players = _players.ToArray();
        foreach (var player in players) player.Dispose();
        lock (_players) _players.Clear();
        _libVlc.Dispose();
    }

    internal static string CachePath(string directory, VideoSource source)
    {
        var hash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(source.Src)));
        var extension = Uri.TryCreate(source.Src, UriKind.Absolute, out var uri)
            ? Path.GetExtension(uri.AbsolutePath)
            : Path.GetExtension(source.Src);
        if (string.IsNullOrWhiteSpace(extension) || extension.Length > 10) extension = ".media";
        return Path.Combine(directory, hash + extension);
    }
}

internal sealed class LibVlcVideoPlayer : IVideoPlayer, IVideoTrackSelector, IVideoChapterProvider, IHostCompositedSurfaceSource
{
    private readonly object _gate = new();
    private readonly VideoSource _source;
    private readonly string _cacheDirectory;
    private readonly VlcMediaPlayer _player;
    private IHostSurfaceContext? _context;
    private Win32HostWindow? _hostWindow;
    private VlcMedia? _media;
    private nint _child;
    private int _hostThread;
    private bool _playWhenReady;
    private bool _mediaOpened;
    private volatile bool _ready;
    private volatile bool _disposed;
    private volatile string? _failure;
    private bool _loop;
    private double _volume = 1;
    private (int W, int H)? _naturalSize;
    private VideoTrack[] _audioTracks = [];
    private VideoTrack[] _subtitleTracks = [];
    private VideoChapter[] _chapters = [];

    private readonly Action<string> _log;
    private readonly Action<LibVlcVideoPlayer> _closed;

    internal LibVlcVideoPlayer(
        LibVLC libVlc,
        VideoSource source,
        string cacheDirectory,
        Action<string> log,
        Action<LibVlcVideoPlayer> closed)
    {
        _source = source;
        _cacheDirectory = cacheDirectory;
        _log = log;
        _closed = closed;
        _player = new VlcMediaPlayer(libVlc)
        {
            EnableHardwareDecoding = true,
            EnableKeyInput = false,
            EnableMouseInput = false,
        };
        _player.Playing += OnPlaying;
        _player.ChapterChanged += OnChapterChanged;
        _player.EndReached += OnEnded;
        _player.EncounteredError += OnError;
        _ = PrepareAsync(libVlc);
    }

    public ISurfaceSource Surface => this;
    public SKImage? CurrentFrame => null;
    public (int W, int H)? NaturalSize => _naturalSize;
    public bool Ticking => false;
    public bool HostComposited => _ready;
    public bool Playing => !_disposed && _player.IsPlaying;

    public bool Muted
    {
        get => _player.Mute;
        set => _player.Mute = value;
    }

    public double Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0, 1);
            _player.Volume = (int)Math.Round(_volume * 100);
        }
    }

    public bool Loop
    {
        get => _loop;
        set => _loop = value;
    }

    public double Duration => Math.Max(0, _player.Length / 1000.0);
    public double Position
    {
        get => Math.Max(0, _player.Time / 1000.0);
        set => _player.Time = (long)(Math.Max(0, value) * 1000);
    }

    public event Action? Ended;
    public event Action? TracksChanged;
    public event Action? ChaptersChanged;

    public IReadOnlyList<VideoTrack> AudioTracks
    {
        get { lock (_gate) return _audioTracks; }
    }

    public IReadOnlyList<VideoTrack> SubtitleTracks
    {
        get { lock (_gate) return _subtitleTracks; }
    }

    public int SelectedAudioTrack => _disposed ? -1 : _player.AudioTrack;
    public int SelectedSubtitleTrack => _disposed ? -1 : _player.Spu;
    public IReadOnlyList<VideoChapter> Chapters
    {
        get { lock (_gate) return _chapters; }
    }
    public int SelectedChapter => _disposed ? -1 : _player.Chapter;

    public bool SelectAudioTrack(int trackId)
    {
        lock (_gate)
        {
            if (_disposed || !_audioTracks.Any(track => track.Id == trackId)) return false;
            return _player.SetAudioTrack(trackId);
        }
    }

    public bool SelectSubtitleTrack(int trackId)
    {
        lock (_gate)
        {
            if (_disposed || !_subtitleTracks.Any(track => track.Id == trackId)) return false;
            return _player.SetSpu(trackId);
        }
    }

    public bool SelectChapter(int chapterIndex)
    {
        lock (_gate)
        {
            if (_disposed || !_chapters.Any(chapter => chapter.Index == chapterIndex)) return false;
            _player.Chapter = chapterIndex;
            return true;
        }
    }

    public string DiagnosticsSummary =>
        $"LibVLC D3D11VA requested · {(_failure ?? (_ready ? "ready" : "loading"))} · " +
        $"{_naturalSize?.W ?? 0}x{_naturalSize?.H ?? 0} · {Position:0.0}/{Duration:0.0}s";

    public void Play()
    {
        _playWhenReady = true;
        TryStart();
    }

    public void Pause()
    {
        _playWhenReady = false;
        if (_player.IsPlaying) _player.Pause();
    }

    public void Attach(IHostSurfaceContext context)
    {
        if (_disposed || _child != 0) return;
        _context = context;
        _hostWindow = context.GetFeature<Win32HostWindow>();
        var parent = _hostWindow?.Handle ?? 0;
        if (parent == 0) return;

        _child = Win32VideoWindow.Create(parent);
        _hostThread = Environment.CurrentManagedThreadId;
        _log($"attached child HWND 0x{_child:x} to parent 0x{parent:x}");
        _player.Hwnd = _child;
        TryStart();
    }

    public void Arrange(HostSurfacePlacement placement)
    {
        if (_child == 0) return;
        if (!placement.Visible || !placement.Transform.IsIdentity || !_ready)
        {
            Win32VideoWindow.Hide(_child);
            return;
        }

        // The placement already excludes the element's own overlay chrome, measured from layout.
        Win32VideoWindow.Place(
            _child,
            placement.X,
            placement.Y,
            placement.Width,
            placement.Height,
            placement.ClipTop,
            placement.ClipRight,
            placement.ClipBottom,
            placement.ClipLeft,
            placement.Occlusions);
    }

    /// <summary>
    /// Release the child window. <c>DestroyWindow</c> is thread-affine — called from any thread but
    /// the one that created the window it silently does nothing and the window outlives its element
    /// — so a caller arriving from elsewhere is refused here and the coordinator, which runs on the
    /// host thread, does it on its next sync (or when it is disposed).
    /// </summary>
    public void Detach()
    {
        if (_child == 0) return;
        if (_hostThread != 0 && Environment.CurrentManagedThreadId != _hostThread)
        {
            _log("detach requested off the host thread; leaving the window to the coordinator");
            return;
        }
        // A player disposed before its window throws here; the window still has to go.
        try { _player.Hwnd = 0; } catch { /* already gone */ }
        Win32VideoWindow.Destroy(_child);
        _child = 0;
        _hostThread = 0;
        _hostWindow = null;
        _context = null;
    }

    private async Task PrepareAsync(LibVLC libVlc)
    {
        try
        {
            if (_source.IsRemote)
            {
                if (!Uri.TryCreate(_source.Src, UriKind.Absolute, out var remoteUri)
                    || remoteUri.Scheme != Uri.UriSchemeHttps
                    || string.IsNullOrWhiteSpace(remoteUri.Host)
                    || !string.IsNullOrEmpty(remoteUri.UserInfo))
                    throw new InvalidOperationException("Remote video sources must use an absolute HTTPS URI without user information.");

                lock (_gate)
                {
                    if (_disposed) return;
                    _media = new VlcMedia(libVlc, remoteUri, ":network-caching=3000");
                }
                _log("prepared remote HTTPS stream");
            }
            else
            {
                var bytes = await Task.Run(_source.LoadBytes).ConfigureAwait(false);
                if (bytes is null || _disposed) return;
                var path = LibVlcVideoBackend.CachePath(_cacheDirectory, _source);
                if (!File.Exists(path) || new FileInfo(path).Length != bytes.LongLength)
                    await File.WriteAllBytesAsync(path, bytes).ConfigureAwait(false);

                lock (_gate)
                {
                    if (_disposed) return;
                    _media = new VlcMedia(libVlc, path, FromType.FromPath);
                }
                _log($"prepared media ({bytes.Length} bytes)");
            }

            _failure = null;
            TryStart();
            _context?.RequestFrame();
        }
        catch (Exception exception)
        {
            _failure = "preparation failed";
            _log($"media preparation failed ({exception.GetType().Name})");
            _context?.RequestFrame();
        }
    }

    private void TryStart()
    {
        lock (_gate)
        {
            if (_disposed || !_playWhenReady || _child == 0 || _media is null || _player.IsPlaying) return;
            if (!_mediaOpened)
            {
                _mediaOpened = _player.Play(_media);
                _log(_mediaOpened
                    ? "initial play requested with D3D11VA enabled"
                    : "initial play request was refused");
            }
            else
            {
                // Play(media) assigns the media again and restarts a paused network stream from
                // the beginning. Once opened, parameterless Play() resumes the existing input and
                // preserves LibVLC's current time, decoder and buffered data.
                _player.Play();
                _log("resume requested");
            }
        }
    }

    private void OnPlaying(object? sender, EventArgs args)
    {
        _failure = null;
        _ready = true;
        uint width = 0, height = 0;
        if (_player.Size(0, ref width, ref height) && width > 0 && height > 0)
            _naturalSize = ((int)width, (int)height);
        RefreshTracks();
        RefreshChapters();
        _log($"playing; video output {_naturalSize?.W ?? 0}x{_naturalSize?.H ?? 0}");
        _context?.RequestFrame();
    }

    private void RefreshTracks()
    {
        var audio = _player.AudioTrackDescription
            .Where(track => track.Id >= 0)
            .Select((track, index) => new VideoTrack(track.Id, TrackLabel(track.Name, "Audio", index + 1)))
            .ToArray();
        var subtitles = _player.SpuDescription
            .Select((track, index) => new VideoTrack(
                track.Id,
                track.Id < 0 ? "Off" : TrackLabel(track.Name, "Subtitle", index + 1)))
            .ToList();
        if (subtitles.All(track => track.Id >= 0)) subtitles.Insert(0, new VideoTrack(-1, "Off"));

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
        if (changed) TracksChanged?.Invoke();
    }

    private static string TrackLabel(string? label, string fallback, int number) =>
        string.IsNullOrWhiteSpace(label) ? $"{fallback} {number}" : label.Trim();

    private void RefreshChapters()
    {
        VideoChapter[] chapters;
        try
        {
            chapters = _player.FullChapterDescriptions(-1)
                .Select((chapter, index) => new VideoChapter(
                    index,
                    TrackLabel(chapter.Name, "Chapter", index + 1),
                    Math.Max(0, chapter.TimeOffset / 1000.0),
                    Math.Max(0, chapter.Duration / 1000.0)))
                .ToArray();
        }
        catch
        {
            chapters = [];
        }

        var changed = false;
        lock (_gate)
        {
            if (!_chapters.SequenceEqual(chapters))
            {
                _chapters = chapters;
                changed = true;
            }
        }
        if (changed) ChaptersChanged?.Invoke();
    }

    private void OnChapterChanged(object? sender, MediaPlayerChapterChangedEventArgs args)
    {
        ChaptersChanged?.Invoke();
        _context?.RequestFrame();
    }

    private void OnEnded(object? sender, EventArgs args)
    {
        if (_loop)
        {
            _ = Task.Run(() =>
            {
                if (_disposed) return;
                _player.Stop();
                _player.Play();
            });
            return;
        }
        Ended?.Invoke();
        _context?.RequestFrame();
    }

    private void OnError(object? sender, EventArgs args)
    {
        _ready = false;
        _failure = "playback failed";
        _log("LibVLC reported a playback error");
        _context?.RequestFrame();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Detach();
        _player.Playing -= OnPlaying;
        _player.ChapterChanged -= OnChapterChanged;
        _player.EndReached -= OnEnded;
        _player.EncounteredError -= OnError;
        _player.Stop();
        _media?.Dispose();
        _player.Dispose();
        _closed(this);
    }
}
