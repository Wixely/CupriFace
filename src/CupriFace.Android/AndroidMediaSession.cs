using Android.App;
using Android.Content;
using Android.Media.Session;
using Android.OS;

namespace CupriFace.Android;

/// <summary>
/// Bridges the active CupriFace video to Android's system media surface. That gives applications
/// lock-screen, hardware-button and notification-shade play/pause/seek controls without making
/// the portable document know that Android exists.
/// </summary>
internal sealed class AndroidMediaSession : IDisposable
{
    private const string ChannelId = "cupriface-playback";
    private const int NotificationId = 0x43555052;
    private const string ActionPlayPause = "org.cupriface.media.PLAY_PAUSE";

    private readonly Context _context;
    private readonly Action<Action> _runOnUi;
    private readonly MediaSession _session;
    private readonly NotificationManager? _notifications;
    private readonly ControlReceiver _receiver;
    private AndroidPlayer? _player;
    private bool _disposed;

    internal AndroidMediaSession(Context context, Action<Action> runOnUi)
    {
        _context = context.ApplicationContext ?? context;
        _runOnUi = runOnUi;
        _session = new MediaSession(_context, "CupriFace playback");
        _session.SetFlags(MediaSessionFlags.HandlesMediaButtons | MediaSessionFlags.HandlesTransportControls);
        _session.SetCallback(new SessionCallback(this));
        _notifications = _context.GetSystemService(Context.NotificationService) as NotificationManager;
        _receiver = new ControlReceiver(this);

        var filter = new IntentFilter(ActionPlayPause);
        if (OperatingSystem.IsAndroidVersionAtLeast(33))
            _context.RegisterReceiver(_receiver, filter, ReceiverFlags.NotExported);
        else
            _context.RegisterReceiver(_receiver, filter);

        if (OperatingSystem.IsAndroidVersionAtLeast(26))
            _notifications?.CreateNotificationChannel(new NotificationChannel(
                ChannelId, "Media playback", NotificationImportance.Low)
            {
                Description = "Playback controls for the current video"
            });
    }

    internal void Update(AndroidPlayer player)
    {
        if (_disposed) return;
        _player = player;
        _runOnUi(Publish);
    }

    internal void Remove(AndroidPlayer player)
    {
        if (!ReferenceEquals(_player, player)) return;
        _player = null;
        _runOnUi(Publish);
    }

    private void Publish()
    {
        if (_disposed) return;
        var player = _player;
        if (player is null || !player.Ready)
        {
            _session.Active = false;
            _notifications?.Cancel(NotificationId);
            return;
        }

        var state = player.Playing ? PlaybackStateCode.Playing : PlaybackStateCode.Paused;
        var stateBuilder = new PlaybackState.Builder();
        _ = stateBuilder.SetActions(PlaybackState.ActionPlay | PlaybackState.ActionPause |
                                    PlaybackState.ActionPlayPause | PlaybackState.ActionSeekTo);
        _ = stateBuilder.SetState(state, (long)Math.Max(0, player.Position * 1000), player.Playing ? 1f : 0f);
        if (stateBuilder.Build() is { } playbackState) _session.SetPlaybackState(playbackState);

        var title = PlaybackTitle(player.Source.Src);
        var metadata = new global::Android.Media.MediaMetadata.Builder();
        _ = metadata.PutString(global::Android.Media.MediaMetadata.MetadataKeyTitle, title);
        if (player.Duration > 0)
            _ = metadata.PutLong(global::Android.Media.MediaMetadata.MetadataKeyDuration, (long)(player.Duration * 1000));
        if (metadata.Build() is { } mediaMetadata) _session.SetMetadata(mediaMetadata);
        _session.Active = true;

        var intent = new Intent(ActionPlayPause).SetPackage(_context.PackageName);
        var pending = PendingIntent.GetBroadcast(_context, 0, intent,
            PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);
        var icon = _context.ApplicationInfo?.Icon ?? global::Android.Resource.Drawable.IcMediaPlay;
        var actionIcon = player.Playing
            ? global::Android.Resource.Drawable.IcMediaPause
            : global::Android.Resource.Drawable.IcMediaPlay;
        var actionText = player.Playing ? "Pause" : "Play";
        var style = new Notification.MediaStyle();
        _ = style.SetMediaSession(_session.SessionToken);
        _ = style.SetShowActionsInCompactView(0);
        var builder = OperatingSystem.IsAndroidVersionAtLeast(26)
            ? new Notification.Builder(_context, ChannelId)
            : new Notification.Builder(_context);
        var appLabel = _context.PackageManager is { } packageManager
            ? _context.ApplicationInfo?.LoadLabel(packageManager)
            : null;
        var actionImage = global::Android.Graphics.Drawables.Icon.CreateWithResource(_context, actionIcon);
        var action = new Notification.Action.Builder(actionImage, actionText, pending).Build();
        builder.SetSmallIcon(icon)
            .SetContentTitle(title)
            .SetContentText("Playing in " + (appLabel ?? "CupriFace"))
            .SetCategory(Notification.CategoryTransport)
            .SetVisibility(NotificationVisibility.Public)
            .SetOngoing(player.Playing)
            .SetOnlyAlertOnce(true)
            .SetStyle(style)
            .AddAction(action);
        _notifications?.Notify(NotificationId, builder.Build()!);
    }

    private static string PlaybackTitle(string source)
    {
        if (Uri.TryCreate(source, UriKind.Absolute, out var uri)) source = uri.AbsolutePath;
        var title = Path.GetFileNameWithoutExtension(source);
        return string.IsNullOrWhiteSpace(title) ? "Video playback" : Uri.UnescapeDataString(title);
    }

    private void Toggle()
    {
        _runOnUi(() =>
        {
            if (_player is not { } player) return;
            if (player.Playing) player.Pause(); else player.Play();
        });
    }

    private sealed class SessionCallback(AndroidMediaSession owner) : MediaSession.Callback
    {
        public override void OnPlay() => owner._runOnUi(() => owner._player?.Play());
        public override void OnPause() => owner._runOnUi(() => owner._player?.Pause());
        public override void OnSeekTo(long pos) => owner._runOnUi(() =>
        {
            if (owner._player is { } player) player.Position = pos / 1000.0;
        });
    }

    private sealed class ControlReceiver(AndroidMediaSession owner) : BroadcastReceiver
    {
        public override void OnReceive(Context? context, Intent? intent)
        {
            if (intent?.Action == ActionPlayPause) owner.Toggle();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _context.UnregisterReceiver(_receiver); } catch { }
        _notifications?.Cancel(NotificationId);
        _session.Active = false;
        _session.Release();
        _receiver.Dispose();
        _session.Dispose();
    }
}
