using CupriFace.Interaction;
using CupriFace.Media;
using CupriFace.Paint;
using SkiaSharp;
using Xunit;

namespace CupriFace.Tests;

/// <summary>
/// Issue #304, the half that does not need a window. The engine element-fullscreens a video and
/// asks the HOST to follow, but the host owns the window: it can refuse, or be taken out of
/// fullscreen by a route the engine never sees (Escape, the window manager, the title bar). The
/// host therefore reports the state it ended in, and the document follows that rather than its own
/// assumption — or the two drift and you get a window fullscreen around a video that is not.
/// </summary>
public class HostFullscreenSyncTests
{
    private sealed class Surf : ISurfaceSource
    {
        public SKImage? CurrentFrame => null;
        public (int W, int H)? NaturalSize => (640, 360);
        public bool Ticking => false;
    }

    private sealed class Player : IVideoPlayer
    {
        public ISurfaceSource Surface { get; } = new Surf();
        public bool Playing { get; private set; }
        public bool Muted { get; set; }
        public double Volume { get; set; } = 1;
        public bool Loop { get; set; }
        public double Duration => 10;
        public double Position { get; set; }
        public event Action? Ended;
        public void Play() => Playing = true;
        public void Pause() => Playing = false;
        public void Dispose() => _ = Ended;
    }

    private sealed class Backend : IVideoBackend
    {
        public IVideoPlayer Open(VideoSource source) => new Player();
    }

    private const string Html =
        "<body><cupri-video src='clip.webm' controls style='width:320px;height:180px'></cupri-video></body>";

    private static TestDoc Fullscreened(out WindowCommand[] asked)
    {
        var seen = new List<WindowCommand>();
        var t = new TestDoc(Html, "", components: true);
        t.Doc.UseVideo(new Backend());
        t.Doc.WindowCommandRequested += c => seen.Add(c);
        t.Layout();
        t.ClickMatch(n => n.Element?.GetAttribute("data-video-role") == "fullscreen");
        t.Layout();
        asked = seen.ToArray();
        return t;
    }

    [Fact]
    public void Fullscreening_a_video_asks_the_host_to_follow()
    {
        using var t = Fullscreened(out var asked);
        Assert.Contains(WindowCommand.EnterFullscreen, asked);
        Assert.NotNull(t.Find(n => n.Element?.ClassList.Contains("cupri-video-fs") == true));
    }

    [Fact]
    public void A_host_reporting_it_is_not_fullscreen_releases_the_element()
    {
        // The Escape / window-manager route: the window left fullscreen without the engine seeing
        // a keystroke, so the video must come back into the layout.
        using var t = Fullscreened(out _);
        Assert.NotNull(t.Find(n => n.Element?.ClassList.Contains("cupri-video-fs") == true));

        t.Doc.NotifyHostFullscreen(false);
        t.Layout();

        Assert.Null(t.Find(n => n.Element?.ClassList.Contains("cupri-video-fs") == true));
    }

    [Fact]
    public void A_host_that_refuses_fullscreen_does_not_leave_the_video_expanded()
    {
        // A host reports the state it ENDED in, not the one it was asked for. If it could not go
        // fullscreen, the element must not sit there pretending it did.
        using var t = Fullscreened(out _);
        t.Doc.NotifyHostFullscreen(false);   // what SyncFullscreen reports when SetFullscreen failed
        t.Layout();

        Assert.Null(t.Find(n => n.Element?.ClassList.Contains("cupri-video-fs") == true));
    }

    [Fact]
    public void Confirming_the_state_it_is_already_in_changes_nothing()
    {
        using var t = Fullscreened(out _);
        t.Doc.NotifyHostFullscreen(true);    // idempotent: still fullscreen, nothing to release
        t.Layout();
        Assert.NotNull(t.Find(n => n.Element?.ClassList.Contains("cupri-video-fs") == true));

        t.Doc.NotifyHostFullscreen(false);
        t.Layout();
        t.Doc.NotifyHostFullscreen(false);   // and again, with nothing left to release
        t.Layout();
        Assert.Null(t.Find(n => n.Element?.ClassList.Contains("cupri-video-fs") == true));
    }
}
