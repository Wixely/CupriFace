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

        Core.Initialize();
        _debug = Environment.GetEnvironmentVariable("CUPRIFACE_MEDIA_DEBUG") is "1" or "true" or "TRUE";
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

internal sealed class LibVlcVideoPlayer : IVideoPlayer, IHostCompositedSurfaceSource
{
    private const int DefaultControlBarLogicalHeight = 44;
    private readonly object _gate = new();
    private readonly VideoSource _source;
    private readonly string _cacheDirectory;
    private readonly VlcMediaPlayer _player;
    private IHostSurfaceContext? _context;
    private Win32HostWindow? _hostWindow;
    private VlcMedia? _media;
    private nint _child;
    private bool _playWhenReady;
    private volatile bool _ready;
    private volatile bool _disposed;
    private volatile string? _failure;
    private bool _loop;
    private double _volume = 1;
    private (int W, int H)? _naturalSize;

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

        var controls = _source.HasControls ? DefaultControlBarLogicalHeight * placement.DeviceScale : 0;
        var height = Math.Max(0, placement.Height - controls);
        Win32VideoWindow.Place(
            _child,
            placement.X,
            placement.Y,
            placement.Width,
            height,
            placement.ClipTop,
            placement.ClipRight,
            Math.Max(placement.ClipBottom - controls, 0),
            placement.ClipLeft);
    }

    public void Detach()
    {
        if (_child == 0) return;
        _player.Hwnd = 0;
        Win32VideoWindow.Destroy(_child);
        _child = 0;
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
            _player.Play(_media);
            _log("play requested with D3D11VA enabled");
        }
    }

    private void OnPlaying(object? sender, EventArgs args)
    {
        _failure = null;
        _ready = true;
        uint width = 0, height = 0;
        if (_player.Size(0, ref width, ref height) && width > 0 && height > 0)
            _naturalSize = ((int)width, (int)height);
        _log($"playing; video output {_naturalSize?.W ?? 0}x{_naturalSize?.H ?? 0}");
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
        _player.EndReached -= OnEnded;
        _player.EncounteredError -= OnError;
        _player.Stop();
        _media?.Dispose();
        _player.Dispose();
        _closed(this);
    }
}
