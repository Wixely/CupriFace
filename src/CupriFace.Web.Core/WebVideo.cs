using System.Runtime.InteropServices.JavaScript;
using CupriFace;
using CupriFace.Dom;
using CupriFace.Media;
using CupriFace.Paint;
using CupriFace.Style;
using SkiaSharp;

namespace CupriFace.Web;

// The web video backend: the BROWSER decodes (hardware, zero wasm bytes of codec). Each player
// is an underlaid <video> element the JS glue creates below the canvas; the engine paints a
// transparent hole at the element's box (ClearHole via ISurfaceSource.HostComposited) and its
// own controls on top. Rects/clips sync after every painted frame, in the same JS task as the
// canvas blit, so hole and element move as one.
//
// The autoplay/gesture contract holds by construction: Play() runs synchronously inside the
// engine's input dispatch, which runs synchronously inside the canvas pointer event — the
// browser still sees the user gesture when video.play() executes.

/// <summary>One underlaid browser video. State (ready/size/playing) is pushed IN from JS events;
/// transport calls go OUT through the JS imports.</summary>
internal sealed class WebVideoPlayer : IVideoPlayer, ISurfaceSource
{
    private readonly IWebBridge _js;
    private readonly WebVideoBackend _owner;

    internal readonly int Id;
    internal readonly string SurfaceKey;
    private bool _muted;
    private double _volume = 1;
    private bool _loop;

    internal bool Ready;                    // loadeddata fired: the element can show pixels
    internal (int W, int H)? Natural;       // from loadedmetadata
    internal double DurationSeconds;
    internal double PositionSeconds;        // pushed by timeupdate (coarse; fine is Phase 3)
    internal bool PlayingNow;               // browser truth, via play/pause events

    public WebVideoPlayer(int id, string src, IWebBridge js, WebVideoBackend owner)
    {
        Id = id;
        SurfaceKey = "video:" + src;
        _js = js;
        _owner = owner;
    }

    // ---- ISurfaceSource: no frames — the host composites; the engine paints a hole. ----------
    public SKImage? CurrentFrame => null;
    public (int W, int H)? NaturalSize => Natural;
    public bool Ticking => false;           // the browser animates the video; the engine is idle
    public bool HostComposited => Ready;    // poster paints until the element can show pixels

    // ---- IVideoPlayer ------------------------------------------------------------------------
    public ISurfaceSource Surface => this;
    public bool Playing => PlayingNow;
    public void Play() => _js.VideoPlay(Id);       // synchronous: the gesture is on the stack
    public void Pause() => _js.VideoPause(Id);

    public bool Muted
    {
        get => _muted;
        set { _muted = value; _js.VideoMuted(Id, value); }
    }

    public double Volume
    {
        get => _volume;
        set { _volume = value; _js.VideoVolume(Id, value); }
    }

    public bool Loop
    {
        get => _loop;
        set { _loop = value; _js.VideoLoop(Id, value); }
    }

    public double Duration => DurationSeconds;

    public double Position
    {
        get => PositionSeconds;
        set { PositionSeconds = value; _js.VideoSeek(Id, value); }
    }

    public event Action? Ended;
    internal void RaiseEnded() => Ended?.Invoke();

    public void Dispose() => _owner.Close(this);
}

internal sealed class WebVideoBackend(IWebBridge js) : IVideoBackend
{
    private readonly IWebBridge _js = js;

    private readonly Dictionary<int, WebVideoPlayer> Players = new();
    private int _nextId;

    /// <summary>Any underlay able to show pixels — the present path must then hand the browser
    /// straight alpha, or the holes' transparency never reaches the page.</summary>
    internal bool AnyReady
    {
        get
        {
            foreach (var p in Players.Values) if (p.Ready) return true;
            return false;
        }
    }

    public IVideoPlayer Open(VideoSource source)
    {
        var player = new WebVideoPlayer(++_nextId, source.Src, _js, this);
        Players[player.Id] = player;
        if (source.IsRemote)
        {
            // http(s): hand the URL straight to the element — the browser streams it (range
            // requests, progressive play) far better than a download-then-blob would.
            _js.VideoOpen(player.Id, source.Src);
        }
        else
        {
            // Embedded / file / data:: resolve through the SAME pipeline images use, then serve
            // the bytes to the element as a Blob URL — so an app's embedded clip plays on the
            // web host identically to the desktop one.
            var bytes = source.LoadBytes() ?? throw new FileNotFoundException($"Video source '{source.Src}' could not be resolved.");
            _js.VideoOpenBytes(player.Id, bytes);
        }
        return player;
    }

    internal void Close(WebVideoPlayer player)
    {
        Players.Remove(player.Id);
        _js.VideoClose(player.Id);
    }

    internal WebVideoPlayer? Get(int id) => Players.TryGetValue(id, out var p) ? p : null;

    /// <summary>The player id backing a surface key, for the underlay syncer — video creates
    /// its own element, so the syncer has to be told which one rather than allocating.</summary>
    internal int? IdForSurface(string key)
    {
        foreach (var p in Players.Values) if (p.SurfaceKey == key) return p.Id;
        return null;
    }
}
