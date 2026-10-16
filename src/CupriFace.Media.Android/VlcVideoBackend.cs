using Android.Content;
using Android.Widget;
using CupriFace.Android;
using CupriFace.Media;
using LibVLCSharp.Shared;
using AndroidVlcVideoView = LibVLCSharp.Platforms.Android.VideoView;
using VideoSource = CupriFace.Media.VideoSource;

namespace CupriFace.Media.Android;

/// <summary>
/// Opens <c>&lt;cupri-video&gt;</c> sources with the platform's decoder, and keeps each player's
/// underlay view sitting exactly under the element's box.
///
/// Registered at the HOST composition root (never in a portable app class), same as the desktop
/// and web backends: <c>CupriActivity</c> attaches it, so an app's markup is identical on every
/// platform and only the decoder differs.
/// </summary>
public sealed class VlcVideoBackend : IAndroidVideoBackend
{
    private readonly Context _context;
    private readonly FrameLayout _underlays;      // sits BEHIND the GL surface
    private readonly Action _invalidate;
    private readonly Action<Action> _runOnUi;
    private readonly List<AndroidPlayer> _players = new();
    private readonly AndroidMediaSession _mediaSession;
    private readonly LibVLC _libVlc;
    private bool _pictureInPicture;

    /// <summary>Construct from the services the host hands to
    /// <c>CupriActivity.CreateVideoBackend</c>. Initialising LibVLC here, rather than at type load,
    /// keeps an app that never overrides that hook from loading the native libraries at all.</summary>
    public VlcVideoBackend(AndroidVideoServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _context = services.Context;
        _underlays = services.Underlays;
        _invalidate = services.Invalidate;
        _runOnUi = services.RunOnUi;
        _mediaSession = new AndroidMediaSession(services.Context, services.RunOnUi);
        Core.Initialize();
        _libVlc = new LibVLC("--no-video-title-show", "--no-snapshot-preview");
    }

    internal void Invalidate() => _invalidate();
    internal void PlayerStateChanged(AndroidPlayer player) => _mediaSession.Update(player);

    /// <summary>True while any player can show pixels — the paint path only needs to hand back
    /// transparent holes once something is actually underneath them.</summary>
    public bool AnyReady
    {
        get { lock (_players) { foreach (var p in _players) if (p.Ready) return true; } return false; }
    }

    public bool PictureInPictureReady => PictureInPicturePlayer()?.Playing == true;

    public (int W, int H)? PictureInPictureSize => PictureInPicturePlayer()?.NaturalSize;

    public void SetPictureInPictureMode(bool active)
    {
        _pictureInPicture = active;
        var selected = PictureInPicturePlayer();
        _runOnUi(() =>
        {
            AndroidPlayer[] players;
            lock (_players) players = _players.ToArray();
            foreach (var player in players)
            {
                if (player.View is not { } view) continue;
                if (active && ReferenceEquals(player, selected)) Fill(view);
                else if (active) Park(view);
            }
            _invalidate();
        });
    }

    public IVideoPlayer Open(VideoSource source)
    {
        var player = new AndroidPlayer(this, _libVlc, source);
        lock (_players) _players.Add(player);

        _runOnUi(() =>
        {
            // LibVLC's VideoView owns the Android AWindow/surfaces. It is added at index 0 so it
            // remains beneath the translucent GL surface where CupriFace punches the video hole.
            var view = new AndroidVlcVideoView(_context);
            var lp = new FrameLayout.LayoutParams(1, 1);
            view.LayoutParameters = lp;
            _underlays.AddView(view, 0);
            player.AttachView(view);
        });
        return player;
    }

    internal void Close(AndroidPlayer player)
    {
        lock (_players) _players.Remove(player);
        _mediaSession.Remove(player);
        var view = player.View;
        player.View = null;
        if (view is not null) _runOnUi(() =>
        {
            view.MediaPlayer = null;
            _underlays.RemoveView(view);
            view.Dispose();
        });
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
    public void SyncRects(CupriDocument doc, float inputScale)
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

            if (_pictureInPicture)
            {
                var selected = PictureInPicturePlayer();
                _runOnUi(() =>
                {
                    if (ReferenceEquals(player, selected)) Fill(view); else Park(view);
                });
                continue;
            }

            if (node is null || !node.LaidOut)
            {
                // Parked off-screen rather than hidden: hiding a SurfaceView destroys its
                // surface, which would tear down the decoder attached to it.
                _runOnUi(() => Park(view));
                continue;
            }

            var (x, y, w, h) = Interaction.HitTesting.ScreenBox(node);
            int px = (int)(x * inputScale), py = (int)(y * inputScale);
            int pw = (int)(w * inputScale), ph = (int)(h * inputScale);
            if (pw <= 0 || ph <= 0)
            {
                _runOnUi(() => Park(view));
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
            });
        }
    }

    // Off-screen and 1x1 — not hidden. See the note where the view is created: visibility is what
    // decides whether a SurfaceView has a surface at all.
    private static void Park(AndroidVlcVideoView view)
    {
        if (view.LayoutParameters is not FrameLayout.LayoutParams lp) return;
        lp.Width = 1; lp.Height = 1; lp.LeftMargin = -10; lp.TopMargin = -10;
        view.LayoutParameters = lp;
    }

    private static void Fill(AndroidVlcVideoView view)
    {
        if (view.LayoutParameters is not FrameLayout.LayoutParams lp) return;
        lp.Width = FrameLayout.LayoutParams.MatchParent;
        lp.Height = FrameLayout.LayoutParams.MatchParent;
        lp.LeftMargin = 0;
        lp.TopMargin = 0;
        view.LayoutParameters = lp;
    }

    private AndroidPlayer? PictureInPicturePlayer()
    {
        lock (_players)
        {
            for (var index = _players.Count - 1; index >= 0; index--)
                if (_players[index].Ready && _players[index].Playing) return _players[index];
            for (var index = _players.Count - 1; index >= 0; index--)
                if (_players[index].Ready) return _players[index];
            return null;
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

    public void Dispose()
    {
        AndroidPlayer[] players;
        lock (_players) players = _players.ToArray();
        foreach (var player in players) player.Dispose();
        _mediaSession.Dispose();
        _libVlc.Dispose();
    }
}
