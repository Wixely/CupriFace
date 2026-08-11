using System.Runtime.InteropServices.JavaScript;
using CupriFace;
using CupriFace.Dom;
using CupriFace.Media;
using CupriFace.Paint;
using CupriFace.Style;
using SkiaSharp;

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
internal sealed class BrowserPlayer : IVideoPlayer, ISurfaceSource
{
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

    public BrowserPlayer(int id, string src)
    {
        Id = id;
        SurfaceKey = "video:" + src;
    }

    // ---- ISurfaceSource: no frames — the host composites; the engine paints a hole. ----------
    public SKImage? CurrentFrame => null;
    public (int W, int H)? NaturalSize => Natural;
    public bool Ticking => false;           // the browser animates the video; the engine is idle
    public bool HostComposited => Ready;    // poster paints until the element can show pixels

    // ---- IVideoPlayer ------------------------------------------------------------------------
    public ISurfaceSource Surface => this;
    public bool Playing => PlayingNow;
    public void Play() => Interop.VideoPlay(Id);       // synchronous: the gesture is on the stack
    public void Pause() => Interop.VideoPause(Id);

    public bool Muted
    {
        get => _muted;
        set { _muted = value; Interop.VideoMuted(Id, value); }
    }

    public double Volume
    {
        get => _volume;
        set { _volume = value; Interop.VideoVolume(Id, value); }
    }

    public bool Loop
    {
        get => _loop;
        set { _loop = value; Interop.VideoLoop(Id, value); }
    }

    public double Duration => DurationSeconds;

    public double Position
    {
        get => PositionSeconds;
        set { PositionSeconds = value; Interop.VideoSeek(Id, value); }
    }

    public event Action? Ended;
    internal void RaiseEnded() => Ended?.Invoke();

    public void Dispose() => BrowserVideoBackend.Close(this);
}

internal sealed class BrowserVideoBackend : IVideoBackend
{
    private static readonly Dictionary<int, BrowserPlayer> Players = new();
    private static int _nextId;

    /// <summary>Any underlay able to show pixels — the present path must then hand the browser
    /// straight alpha, or the holes' transparency never reaches the page.</summary>
    internal static bool AnyReady
    {
        get
        {
            foreach (var p in Players.Values) if (p.Ready) return true;
            return false;
        }
    }

    public IVideoPlayer Open(string src)
    {
        var player = new BrowserPlayer(++_nextId, src);
        Players[player.Id] = player;
        Interop.VideoOpen(player.Id, src);
        return player;
    }

    internal static void Close(BrowserPlayer player)
    {
        Players.Remove(player.Id);
        Interop.VideoClose(player.Id);
    }

    internal static BrowserPlayer? Get(int id) => Players.TryGetValue(id, out var p) ? p : null;

    /// <summary>Called after every painted frame: send each player's on-screen rect (physical px,
    /// matching the canvas backing store), its visible clip against scroll/overflow ancestors
    /// (a DOM element ignores engine clips — inset clip-path recreates them), and its object-fit.</summary>
    internal static void SyncRects(CupriDocument doc, float scale)
    {
        if (Players.Count == 0) return;
        foreach (var player in Players.Values)
        {
            var node = Find(doc.Root, player.SurfaceKey);
            if (node is null || !node.LaidOut)
            {
                Interop.VideoRect(player.Id, 0, 0, 0, 0, 0, 0, 0, 0, false, "");
                continue;
            }

            var (x, y, w, h) = CupriFace.Interaction.HitTesting.ScreenBox(node);

            // Visible intersection with every clipping ancestor (overflow != visible).
            float visL = x, visT = y, visR = x + w, visB = y + h;
            for (var a = node.Parent; a is not null; a = a.Parent)
            {
                if (a.Style.Overflow == OverflowMode.Visible) continue;
                var (ax, ay, aw, ah) = CupriFace.Interaction.HitTesting.ScreenBox(a);
                visL = MathF.Max(visL, ax);
                visT = MathF.Max(visT, ay);
                visR = MathF.Min(visR, ax + aw);
                visB = MathF.Min(visB, ay + ah);
            }

            if (visR <= visL || visB <= visT)
            {
                Interop.VideoRect(player.Id, 0, 0, 0, 0, 0, 0, 0, 0, false, "");
                continue;
            }

            var fit = node.Element?.GetAttribute("data-object-fit") ?? "contain";
            Interop.VideoRect(player.Id,
                x * scale, y * scale, w * scale, h * scale,
                (visT - y) * scale,            // clip-path inset: top
                (x + w - visR) * scale,        // right
                (y + h - visB) * scale,        // bottom
                (visL - x) * scale,            // left
                true, fit);
        }
    }

    private static RenderNode? Find(RenderNode n, string key)
    {
        if (n.SurfaceKey == key) return n;
        foreach (var c in n.Children)
            if (Find(c, key) is { } f) return f;
        return null;
    }
}

public partial class Interop
{
    // ---- JS → C#: browser video events ------------------------------------------------------

    [JSExport]
    internal static void VideoMeta(int id, double duration, int width, int height)
    {
        if (BrowserVideoBackend.Get(id) is not { } p) return;
        p.DurationSeconds = duration;
        p.Natural = width > 0 && height > 0 ? (width, height) : null;
        _dirty = true; // intrinsic size may reflow the element
    }

    [JSExport]
    internal static void VideoReady(int id)
    {
        if (BrowserVideoBackend.Get(id) is not { } p) return;
        p.Ready = true;   // HostComposited flips on → the next paint punches the hole
        _dirty = true;
    }

    /// <summary>The browser's own play/pause truth (autoplay policy rejections included): the
    /// engine's controls follow it, so they can never claim a playback the browser refused.</summary>
    [JSExport]
    internal static void VideoPlayState(int id, bool playing)
    {
        if (BrowserVideoBackend.Get(id) is not { } p) return;
        p.PlayingNow = playing;
        _doc?.Refresh();  // relabel play/pause controls
        _dirty = true;
    }

    [JSExport]
    internal static void VideoTime(int id, double seconds)
    {
        if (BrowserVideoBackend.Get(id) is { } p) p.PositionSeconds = seconds;
    }

    [JSExport]
    internal static void VideoEnded(int id) => BrowserVideoBackend.Get(id)?.RaiseEnded();

    // ---- C# → JS: element lifecycle + transport ---------------------------------------------

    [JSImport("videoOpen", "cupri")] internal static partial void VideoOpen(int id, string src);
    [JSImport("videoClose", "cupri")] internal static partial void VideoClose(int id);
    [JSImport("videoPlay", "cupri")] internal static partial void VideoPlay(int id);
    [JSImport("videoPause", "cupri")] internal static partial void VideoPause(int id);
    [JSImport("videoMuted", "cupri")] internal static partial void VideoMuted(int id, bool muted);
    [JSImport("videoVolume", "cupri")] internal static partial void VideoVolume(int id, double volume);
    [JSImport("videoLoop", "cupri")] internal static partial void VideoLoop(int id, bool loop);
    [JSImport("videoSeek", "cupri")] internal static partial void VideoSeek(int id, double seconds);
    [JSImport("videoRect", "cupri")] internal static partial void VideoRect(int id,
        double x, double y, double w, double h,
        double clipTop, double clipRight, double clipBottom, double clipLeft,
        bool visible, string fit);

    // Fullscreen request → the browser's Fullscreen API (0 toggle / 1 enter / 2 exit).
    [JSImport("windowCommand", "cupri")] internal static partial void WindowCommand(int command);
}
