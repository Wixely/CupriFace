using Android.Content;
using Android.Views;
using Android.Widget;
using CupriFace.Media;
using VideoSource = CupriFace.Media.VideoSource;

namespace CupriFace.Android;

/// <summary>
/// Opens <c>&lt;cupri-video&gt;</c> sources with the platform's decoder, and keeps each player's
/// underlay view sitting exactly under the element's box.
///
/// Registered at the HOST composition root (never in a portable app class), same as the desktop
/// and web backends: <c>CupriActivity</c> attaches it, so an app's markup is identical on every
/// platform and only the decoder differs.
/// </summary>
internal sealed class AndroidVideoBackend : IVideoBackend
{
    private readonly Context _context;
    private readonly FrameLayout _underlays;      // sits BEHIND the GL surface
    private readonly Action _invalidate;
    private readonly Action<Action> _runOnUi;
    private readonly List<AndroidPlayer> _players = new();

    internal AndroidVideoBackend(Context context, FrameLayout underlays, Action invalidate, Action<Action> runOnUi)
    {
        _context = context;
        _underlays = underlays;
        _invalidate = invalidate;
        _runOnUi = runOnUi;
    }

    internal void Invalidate() => _invalidate();

    /// <summary>True while any player can show pixels — the paint path only needs to hand back
    /// transparent holes once something is actually underneath them.</summary>
    internal bool AnyReady
    {
        get { lock (_players) { foreach (var p in _players) if (p.Ready) return true; } return false; }
    }

    public IVideoPlayer Open(VideoSource source)
    {
        var player = new AndroidPlayer(this, source);
        lock (_players) _players.Add(player);

        _runOnUi(() =>
        {
            // A SurfaceView, not a TextureView: it is the composited path the platform's decoders
            // write into directly, which is what makes this zero-copy. It is added at index 0 so
            // it stays beneath the translucent GL surface the engine paints on.
            var view = new SurfaceView(_context);
            view.SetZOrderMediaOverlay(false);
            var lp = new FrameLayout.LayoutParams(1, 1);
            view.LayoutParameters = lp;
            view.Visibility = ViewStates.Invisible;      // nothing to show until it has a rect
            view.Holder!.AddCallback(new HolderCallback(player));
            player.View = view;
            _underlays.AddView(view, 0);
        });
        return player;
    }

    internal void Close(AndroidPlayer player)
    {
        lock (_players) _players.Remove(player);
        var view = player.View;
        player.View = null;
        if (view is not null) _runOnUi(() => _underlays.RemoveView(view));
    }

    /// <summary>MediaPlayer wants a path or descriptor, and the engine resolves every media source
    /// through ONE pipeline (embedded asset, file, data:, policied https). A resolved source is
    /// therefore spooled once into the cache directory — so an app's embedded clip plays here
    /// exactly as it does on desktop, from the same bytes.</summary>
    internal string? MaterialiseToCache(VideoSource source)
    {
        try
        {
            // A remote URL is handed to MediaPlayer directly: it streams with range requests,
            // which beats downloading the whole clip before the first frame.
            if (source.IsRemote) return source.Src;

            var bytes = source.LoadBytes();
            if (bytes is null) return null;

            var dir = _context.CacheDir?.AbsolutePath ?? Path.GetTempPath();
            var path = Path.Combine(dir, "cupri-video-" + Math.Abs(source.Src.GetHashCode()).ToString("x") + ".bin");
            if (!File.Exists(path) || new FileInfo(path).Length != bytes.LongLength)
                File.WriteAllBytes(path, bytes);
            return path;
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn(AndroidHost.Tag, $"video source: {ex.Message}");
            return null;
        }
    }

    /// <summary>Called after each painted frame: move every underlay to its element's on-screen
    /// box, in PHYSICAL pixels. The engine has already punched a transparent hole there, so the
    /// two must agree to the pixel or the video shows a seam.</summary>
    internal void SyncRects(CupriDocument doc, float inputScale)
    {
        List<AndroidPlayer> snapshot;
        lock (_players)
        {
            if (_players.Count == 0) return;
            snapshot = new List<AndroidPlayer>(_players);
        }

        foreach (var player in snapshot)
        {
            var node = FindSurface(doc.Root, player);
            var view = player.View;
            if (view is null) continue;

            if (node is null || !node.LaidOut)
            {
                _runOnUi(() => view.Visibility = ViewStates.Invisible);
                continue;
            }

            var (x, y, w, h) = Interaction.HitTesting.ScreenBox(node);
            int px = (int)(x * inputScale), py = (int)(y * inputScale);
            int pw = (int)(w * inputScale), ph = (int)(h * inputScale);
            if (pw <= 0 || ph <= 0)
            {
                _runOnUi(() => view.Visibility = ViewStates.Invisible);
                continue;
            }

            _runOnUi(() =>
            {
                if (view.LayoutParameters is FrameLayout.LayoutParams lp)
                {
                    lp.Width = pw; lp.Height = ph;
                    lp.LeftMargin = px; lp.TopMargin = py;
                    view.LayoutParameters = lp;
                }
                view.Visibility = player.Ready ? ViewStates.Visible : ViewStates.Invisible;
            });
        }
    }

    // The element that owns this player, found by the surface key the document assigned it.
    private static Dom.RenderNode? FindSurface(Dom.RenderNode n, AndroidPlayer player)
    {
        if (n.Element?.GetAttribute("data-cupri-surface") is { Length: > 0 } key
            && key.EndsWith(player.Source.Src, StringComparison.Ordinal)) return n;
        foreach (var c in n.Children) if (FindSurface(c, player) is { } f) return f;
        return null;
    }

    private sealed class HolderCallback(AndroidPlayer player) : global::Java.Lang.Object, ISurfaceHolderCallback
    {
        public void SurfaceCreated(ISurfaceHolder holder)
        {
            if (holder.Surface is { } s) player.AttachSurface(s);
        }

        public void SurfaceChanged(ISurfaceHolder holder, global::Android.Graphics.Format format, int w, int h) { }

        public void SurfaceDestroyed(ISurfaceHolder holder) { }
    }
}
