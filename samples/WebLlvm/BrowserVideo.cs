using System.Runtime.InteropServices;
using CupriFace;
using CupriFace.Dom;
using CupriFace.Media;
using CupriFace.Paint;
using CupriFace.Style;
using SkiaSharp;

// The browser-video backend for the NativeAOT-LLVM host — the same design as WebWasm's
// (samples/WebWasm/BrowserVideo.cs): the BROWSER decodes into an underlaid <video>, the engine
// paints a transparent hole (HostComposited → ClearHole) and its own controls above, rect+clip
// sync after every painted frame. Only the plumbing differs: no [JSImport]/[JSExport] here —
// C-ABI DllImports into wwwroot/imports.js and UnmanagedCallersOnly exports back.
//
// The autoplay/gesture contract holds the same way: Play() runs synchronously inside input
// dispatch, which runs synchronously inside the canvas pointer event — the browser still sees
// the user gesture when video.play() executes.

/// <summary>One underlaid browser video. State (ready/size/playing) is pushed IN from JS events;
/// transport calls go OUT through the C-ABI imports.</summary>
internal sealed unsafe class LlvmBrowserPlayer : IVideoPlayer, ISurfaceSource
{
    internal readonly int Id;
    internal readonly string SurfaceKey;
    private bool _muted;
    private double _volume = 1;
    private bool _loop;

    internal bool Ready;                    // loadeddata fired: the element can show pixels
    internal (int W, int H)? Natural;       // from loadedmetadata
    internal double DurationSeconds;
    internal double PositionSeconds;        // pushed by timeupdate
    internal bool PlayingNow;               // browser truth, via play/pause events

    public LlvmBrowserPlayer(int id, string src)
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
    public void Play() => LlvmBrowserVideoBackend.JsVideoPlay(Id);   // synchronous: gesture on the stack
    public void Pause() => LlvmBrowserVideoBackend.JsVideoPause(Id);

    public bool Muted
    {
        get => _muted;
        set { _muted = value; LlvmBrowserVideoBackend.JsVideoMuted(Id, value ? 1 : 0); }
    }

    public double Volume
    {
        get => _volume;
        set { _volume = value; LlvmBrowserVideoBackend.JsVideoVolume(Id, value); }
    }

    public bool Loop
    {
        get => _loop;
        set { _loop = value; LlvmBrowserVideoBackend.JsVideoLoop(Id, value ? 1 : 0); }
    }

    public double Duration => DurationSeconds;

    public double Position
    {
        get => PositionSeconds;
        set { PositionSeconds = value; LlvmBrowserVideoBackend.JsVideoSeek(Id, value); }
    }

    public event Action? Ended;
    internal void RaiseEnded() => Ended?.Invoke();

    public void Dispose() => LlvmBrowserVideoBackend.Close(this);
}

internal sealed unsafe class LlvmBrowserVideoBackend : IVideoBackend
{
    private static readonly Dictionary<int, LlvmBrowserPlayer> Players = new();
    private static int _nextId;

    // ---- C# → JS: element lifecycle + transport (bound at link time via imports.js) ----------
    [DllImport("js", EntryPoint = "js_video_open")] private static extern void JsVideoOpen(int id, char* src, int len);
    [DllImport("js", EntryPoint = "js_video_open_bytes")] private static extern void JsVideoOpenBytes(int id, byte* data, int len);
    [DllImport("js", EntryPoint = "js_video_close")] private static extern void JsVideoClose(int id);
    [DllImport("js", EntryPoint = "js_video_play")] internal static extern void JsVideoPlay(int id);
    [DllImport("js", EntryPoint = "js_video_pause")] internal static extern void JsVideoPause(int id);
    [DllImport("js", EntryPoint = "js_video_muted")] internal static extern void JsVideoMuted(int id, int muted);
    [DllImport("js", EntryPoint = "js_video_volume")] internal static extern void JsVideoVolume(int id, double volume);
    [DllImport("js", EntryPoint = "js_video_loop")] internal static extern void JsVideoLoop(int id, int loop);
    [DllImport("js", EntryPoint = "js_video_seek")] internal static extern void JsVideoSeek(int id, double seconds);
    [DllImport("js", EntryPoint = "js_video_rect")] private static extern void JsVideoRect(int id,
        double x, double y, double w, double h,
        double clipTop, double clipRight, double clipBottom, double clipLeft,
        int visible, char* fit, int fitLen);

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

    public IVideoPlayer Open(VideoSource source)
    {
        var player = new LlvmBrowserPlayer(++_nextId, source.Src);
        Players[player.Id] = player;
        if (source.IsRemote)
        {
            // http(s): hand the URL straight to the element — the browser streams it.
            fixed (char* p = source.Src) JsVideoOpen(player.Id, p, source.Src.Length);
        }
        else
        {
            // Embedded / file / data:: resolve through the SAME pipeline images use, then serve
            // the bytes as a Blob URL — an app's embedded clip plays identically to desktop.
            var bytes = source.LoadBytes() ?? throw new FileNotFoundException($"Video source '{source.Src}' could not be resolved.");
            fixed (byte* p = bytes) JsVideoOpenBytes(player.Id, p, bytes.Length);
        }
        return player;
    }

    internal static void Close(LlvmBrowserPlayer player)
    {
        Players.Remove(player.Id);
        JsVideoClose(player.Id);
    }

    internal static LlvmBrowserPlayer? Get(int id) => Players.TryGetValue(id, out var p) ? p : null;

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
                SendRect(player.Id, 0, 0, 0, 0, 0, 0, 0, 0, false, "");
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
                SendRect(player.Id, 0, 0, 0, 0, 0, 0, 0, 0, false, "");
                continue;
            }

            var fit = node.Element?.GetAttribute("data-object-fit") ?? "contain";
            SendRect(player.Id,
                x * scale, y * scale, w * scale, h * scale,
                (visT - y) * scale,            // clip-path inset: top
                (x + w - visR) * scale,        // right
                (y + h - visB) * scale,        // bottom
                (visL - x) * scale,            // left
                true, fit);
        }
    }

    private static void SendRect(int id, double x, double y, double w, double h,
        double cT, double cR, double cB, double cL, bool visible, string fit)
    {
        fixed (char* p = fit) JsVideoRect(id, x, y, w, h, cT, cR, cB, cL, visible ? 1 : 0, p, fit.Length);
    }

    private static RenderNode? Find(RenderNode n, string key)
    {
        if (n.SurfaceKey == key) return n;
        foreach (var c in n.Children)
            if (Find(c, key) is { } f) return f;
        return null;
    }
}

public static unsafe partial class Interop
{
    // ---- JS → C#: browser video events (mirroring WebWasm's [JSExport]s) ---------------------

    [UnmanagedCallersOnly(EntryPoint = "VideoMeta")]
    public static void VideoMeta(int id, double duration, int width, int height)
    {
        try
        {
            if (LlvmBrowserVideoBackend.Get(id) is not { } p) return;
            p.DurationSeconds = duration;
            p.Natural = width > 0 && height > 0 ? (width, height) : null;
            MarkDirty(); // intrinsic size may reflow the element
        }
        catch (Exception ex) { Crash("VideoMeta", ex); }
    }

    [UnmanagedCallersOnly(EntryPoint = "VideoReady")]
    public static void VideoReady(int id)
    {
        try
        {
            if (LlvmBrowserVideoBackend.Get(id) is not { } p) return;
            p.Ready = true;   // HostComposited flips on → the next paint punches the hole
            MarkDirty();
        }
        catch (Exception ex) { Crash("VideoReady", ex); }
    }

    /// <summary>The browser's own play/pause truth (autoplay policy rejections included): the
    /// engine's controls follow it, so they can never claim a playback the browser refused.</summary>
    [UnmanagedCallersOnly(EntryPoint = "VideoPlayState")]
    public static void VideoPlayState(int id, int playing)
    {
        try
        {
            if (LlvmBrowserVideoBackend.Get(id) is not { } p) return;
            p.PlayingNow = playing != 0;
            RefreshDoc();     // relabel play/pause controls
        }
        catch (Exception ex) { Crash("VideoPlayState", ex); }
    }

    [UnmanagedCallersOnly(EntryPoint = "VideoTime")]
    public static void VideoTime(int id, double seconds)
    {
        try { if (LlvmBrowserVideoBackend.Get(id) is { } p) p.PositionSeconds = seconds; }
        catch (Exception ex) { Crash("VideoTime", ex); }
    }

    [UnmanagedCallersOnly(EntryPoint = "VideoEnded")]
    public static void VideoEnded(int id)
    {
        try { LlvmBrowserVideoBackend.Get(id)?.RaiseEnded(); }
        catch (Exception ex) { Crash("VideoEnded", ex); }
    }

    /// <summary>The browser's own fullscreen transitions — its Esc never reaches the key handler.</summary>
    [UnmanagedCallersOnly(EntryPoint = "HostFullscreen")]
    public static void HostFullscreen(int active)
    {
        try { NotifyHostFullscreen(active != 0); }
        catch (Exception ex) { Crash("HostFullscreen", ex); }
    }
}
