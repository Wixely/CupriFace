using CupriFace.Media;
using CupriFace.Paint;
using SkiaSharp;
using Xunit;

namespace CupriFace.Tests;

/// <summary>
/// <c>&lt;cupri-video&gt;</c> + the document's video wiring, against a fake backend: player
/// lifecycle follows the DOM, transport commands ride input dispatch synchronously, and the
/// autoplay policy is the web's rule on every host (autoplay only when muted).
/// </summary>
public class VideoComponentTests
{
    private sealed class FakeSurface : ISurfaceSource
    {
        public SKImage? CurrentFrame { get; set; }
        public (int W, int H)? NaturalSize { get; set; } = (160, 90);
        public bool Ticking { get; set; }
    }

    private sealed class FakePlayer : IVideoPlayer
    {
        public FakeSurface Fake { get; } = new();
        public ISurfaceSource Surface => Fake;
        public bool Playing { get; private set; }
        public bool Muted { get; set; }
        public double Volume { get; set; } = 1;
        public bool Loop { get; set; }
        public double Duration => 10;
        public double Position { get; set; }
        public bool Disposed { get; private set; }
        public event Action? Ended;
        public void Play() { Playing = true; Fake.Ticking = true; }
        public void Pause() { Playing = false; Fake.Ticking = false; }
        public void RaiseEnded() { Playing = false; Fake.Ticking = false; Ended?.Invoke(); }
        public void Dispose() { Disposed = true; Fake.Ticking = false; }
    }

    private sealed class FakeBackend : IVideoBackend
    {
        public readonly List<string> Opened = new();
        public readonly Dictionary<string, FakePlayer> Players = new();
        public IVideoPlayer Open(VideoSource source)
        {
            Opened.Add(source.Src);
            return Players[source.Src] = new FakePlayer();
        }
    }

    private sealed class Model { public string Section { get; set; } = "media"; }

    private const string Html = "<body><cupri-video src='clip.webm' controls muted autoplay loop label='Trailer'></cupri-video></body>";

    [Fact]
    public void Expansion_produces_surface_poster_lane_and_accessible_controls()
    {
        using var t = new TestDoc("<body><cupri-video src='clip.webm' poster='p.png' controls label='Trailer'></cupri-video></body>",
            "", components: true);

        Assert.NotNull(t.Find(n => n.Element?.GetAttribute("data-cupri-surface") == "video:clip.webm"));
        Assert.NotNull(t.Find(n => n.Element?.GetAttribute("data-cupri-image") == "p.png"));
        Assert.NotNull(t.Find(n => n.Element?.GetAttribute("data-video-cmd") == "mute"));
        Assert.NotNull(t.Find(n => n.Element?.GetAttribute("data-video-cmd") == "fullscreen"));
        Assert.NotNull(t.Find(n => n.Element?.GetAttribute("aria-label") == "Trailer"));
        // No backend registered: nothing throws, the element still renders (poster lane).
    }

    [Fact]
    public void Without_a_backend_the_transport_controls_read_disabled()
    {
        // A Play button that looks live and does nothing is a lie — no backend dims the
        // transport (and AT announces it); fullscreen stays enabled (it needs no decoder).
        using var t = new TestDoc("<body><cupri-video src='clip.webm' controls></cupri-video></body>", "", components: true);
        t.Layout();
        var toggle = t.Find(n => n.Element?.GetAttribute("data-video-role") == "toggle")!;
        Assert.Contains("disabled", toggle.Element!.ClassList);
        Assert.Equal("true", toggle.Element!.GetAttribute("aria-disabled"));
        var fullscreen = t.Find(n => n.Element?.GetAttribute("data-video-role") == "fullscreen")!;
        Assert.DoesNotContain("disabled", fullscreen.Element!.ClassList);

        // Registering a backend un-dims them on the next rebuild.
        var backend = new FakeBackend();
        t.Doc.UseVideo(backend);
        t.Layout();
        var live = t.Find(n => n.Element?.GetAttribute("data-video-role") == "toggle")!;
        Assert.DoesNotContain("disabled", live.Element!.ClassList);
    }

    [Fact]
    public void A_player_opens_once_and_survives_the_per_keystroke_rebuild()
    {
        var backend = new FakeBackend();
        using var t = new TestDoc(Html, "", components: true);
        t.Doc.UseVideo(backend);
        t.Layout();

        t.Doc.Refresh();
        t.Doc.Refresh();
        Assert.Equal(new[] { "clip.webm" }, backend.Opened);   // cached by src, not re-opened
    }

    [Fact]
    public void Autoplay_is_honored_only_with_muted_on_every_host()
    {
        var backend = new FakeBackend();
        using (var t = new TestDoc(Html, "", components: true))
        {
            t.Doc.UseVideo(backend);
            t.Layout();
            var p = backend.Players["clip.webm"];
            Assert.True(p.Muted);
            Assert.True(p.Loop);
            Assert.True(p.Playing);                            // muted autoplay: allowed
        }

        var backend2 = new FakeBackend();
        using (var t2 = new TestDoc("<body><cupri-video src='clip.webm' autoplay></cupri-video></body>", "", components: true))
        {
            t2.Doc.UseVideo(backend2);
            t2.Layout();
            Assert.False(backend2.Players["clip.webm"].Playing); // unmuted autoplay: stays paused
        }
    }

    [Fact]
    public void Clicking_the_frame_toggles_and_the_controls_relabel()
    {
        var backend = new FakeBackend();
        using var t = new TestDoc("<body><cupri-video src='clip.webm' controls></cupri-video></body>", "", components: true);
        t.Doc.UseVideo(backend);
        t.Layout();
        var p = backend.Players["clip.webm"];

        var frame = t.Find(n => n.Element?.GetAttribute("data-cupri-surface") == "video:clip.webm")!;
        t.ClickNode(frame);
        Assert.True(p.Playing);
        Assert.NotNull(t.Find(n => n.Element?.GetAttribute("aria-label") == "Pause")); // glyph+label flipped

        t.ClickNode(t.Find(n => n.Element?.GetAttribute("data-cupri-surface") == "video:clip.webm")!);
        Assert.False(p.Playing);
        Assert.NotNull(t.Find(n => n.Element?.GetAttribute("aria-label") == "Play"));
    }

    [Fact]
    public void Mute_toggles_through_the_button()
    {
        var backend = new FakeBackend();
        using var t = new TestDoc("<body><cupri-video src='clip.webm' controls></cupri-video></body>", "", components: true);
        t.Doc.UseVideo(backend);
        t.Layout();
        var p = backend.Players["clip.webm"];

        Assert.False(p.Muted);
        t.ClickMatch(n => n.Element?.GetAttribute("data-video-cmd") == "mute");
        Assert.True(p.Muted);
        Assert.NotNull(t.Find(n => n.Element?.GetAttribute("aria-label") == "Unmute"));
    }

    [Fact]
    public void A_video_in_a_hidden_section_is_stopped_and_disposed()
    {
        var backend = new FakeBackend();
        var m = new Model { Section = "block" };
        const string html = """
            <body>
              <div style="display:{{Section}}"><cupri-video src='clip.webm' muted autoplay></cupri-video></div>
            </body>
            """;
        using var t = new TestDoc(html, "", m, components: true);
        t.Doc.UseVideo(backend);
        t.Layout();
        var p = backend.Players["clip.webm"];
        Assert.True(p.Playing);

        // Hide the subtree (a section switch): the rebuild re-binds the template, expansion skips
        // display:none → the src vanishes from the DOM → the document retires the player.
        m.Section = "none";
        t.Doc.Refresh();
        Assert.True(p.Disposed);
    }


    [Fact]
    public void Authored_inline_size_wins_over_the_intrinsic_video_size()
    {
        var backend = new FakeBackend();
        using var t = new TestDoc("<body><cupri-video src='clip.webm' style='width:320px;height:180px'></cupri-video></body>",
            "", components: true);
        t.Doc.UseVideo(backend);
        t.Layout();

        var node = t.Find(n => n.Element?.GetAttribute("data-cupri-video") == "clip.webm")!;
        Assert.Equal(320, node.Width, 1);
        Assert.Equal(180, node.Height, 1);
    }

    [Fact]
    public void Fullscreen_expands_the_ELEMENT_to_the_viewport_and_asks_for_the_window_too()
    {
        // Deliberately no backend and an authored inline size: fullscreen needs no decoder, and
        // the viewport override must beat the author's own style="width:320px;height:180px".
        using var t = new TestDoc(
            "<body><div style='padding:40px'><cupri-video src='clip.webm' controls style='width:320px;height:180px;border-radius:10px'></cupri-video></div></body>",
            "", components: true, width: 800, height: 500);
        var commands = new List<CupriFace.Interaction.WindowCommand>();
        t.Doc.WindowCommandRequested += commands.Add;
        t.Layout();

        t.ClickMatch(n => n.Element?.GetAttribute("data-video-cmd") == "fullscreen");
        t.Layout();
        var node = t.Find(n => n.Element?.GetAttribute("data-cupri-video") == "clip.webm")!;
        Assert.Equal(new[] { CupriFace.Interaction.WindowCommand.EnterFullscreen }, commands);
        Assert.Contains("cupri-video-fs", node.Element!.ClassList);
        Assert.Equal(800, node.Width, 1);                       // covers the viewport…
        Assert.Equal(500, node.Height, 1);
        Assert.NotNull(t.Find(n => n.Element?.GetAttribute("aria-label") == "Exit fullscreen"));

        t.ClickMatch(n => n.Element?.GetAttribute("data-video-cmd") == "fullscreen");
        t.Layout();
        node = t.Find(n => n.Element?.GetAttribute("data-cupri-video") == "clip.webm")!;
        Assert.Equal(CupriFace.Interaction.WindowCommand.ExitFullscreen, commands[^1]);
        Assert.DoesNotContain("cupri-video-fs", node.Element!.ClassList);
        Assert.Equal(320, node.Width, 1);                       // …and comes back to its place
        Assert.Equal(180, node.Height, 1);
    }

    [Fact]
    public void The_fullscreen_video_is_on_top_it_owns_a_click_anywhere()
    {
        var backend = new FakeBackend();
        using var t = new TestDoc(
            "<body><div style='padding:30px'><cupri-button>Save</cupri-button><cupri-video src='clip.webm' controls style='width:200px;height:120px'></cupri-video></div></body>",
            "", components: true, width: 800, height: 500);
        t.Doc.UseVideo(backend);
        t.Layout();

        t.ClickMatch(n => n.Element?.GetAttribute("data-video-cmd") == "fullscreen");
        t.Layout();

        // The Save button's old spot now hits the top-layer video: the click toggles playback
        // instead of pressing the button underneath.
        var p = backend.Players["clip.webm"];
        Assert.False(p.Playing);
        t.Doc.DispatchClick(60, 45, 1);
        Assert.True(p.Playing);
    }

    [Fact]
    public void Escape_exits_video_fullscreen_and_releases_the_window()
    {
        using var t = new TestDoc(Html, "", components: true, width: 800, height: 500);
        var commands = new List<CupriFace.Interaction.WindowCommand>();
        t.Doc.WindowCommandRequested += commands.Add;
        t.Layout();

        t.ClickMatch(n => n.Element?.GetAttribute("data-video-cmd") == "fullscreen");
        t.Layout();
        Assert.True(t.Doc.DispatchKey(null, CupriFace.Interaction.EditKey.Escape)); // the DOC handles it
        t.Layout();

        Assert.Equal(new[] { CupriFace.Interaction.WindowCommand.EnterFullscreen,
                             CupriFace.Interaction.WindowCommand.ExitFullscreen }, commands);
        Assert.Null(t.Find(n => n.Element?.ClassList.Contains("cupri-video-fs") == true));
    }

    [Fact]
    public void Hiding_the_fullscreen_videos_section_releases_window_fullscreen_too()
    {
        var backend = new FakeBackend();
        var m = new Model { Section = "block" };
        const string html = """
            <body>
              <div style="display:{{Section}}"><cupri-video src='clip.webm' controls muted></cupri-video></div>
            </body>
            """;
        using var t = new TestDoc(html, "", m, components: true, width: 800, height: 500);
        var commands = new List<CupriFace.Interaction.WindowCommand>();
        t.Doc.WindowCommandRequested += commands.Add;
        t.Doc.UseVideo(backend);
        t.Layout();

        t.ClickMatch(n => n.Element?.GetAttribute("data-video-cmd") == "fullscreen");
        m.Section = "none";                                     // the app switches the section away
        t.Doc.Refresh();
        Assert.Equal(new[] { CupriFace.Interaction.WindowCommand.EnterFullscreen,
                             CupriFace.Interaction.WindowCommand.ExitFullscreen }, commands);
    }

    [Fact]
    public void The_browsers_own_fullscreen_exit_puts_the_video_back()
    {
        // On the web the Esc key goes to the BROWSER; the engine only hears fullscreenchange.
        using var t = new TestDoc(Html, "", components: true, width: 800, height: 500);
        t.Layout();
        t.ClickMatch(n => n.Element?.GetAttribute("data-video-cmd") == "fullscreen");
        t.Layout();
        Assert.NotNull(t.Find(n => n.Element?.ClassList.Contains("cupri-video-fs") == true));

        t.Doc.NotifyHostFullscreen(false);                      // what the host reports
        t.Layout();
        Assert.Null(t.Find(n => n.Element?.ClassList.Contains("cupri-video-fs") == true));
    }

    [Fact]
    public void Ended_flips_the_controls_back_on_the_next_host_poll()
    {
        var backend = new FakeBackend();
        using var t = new TestDoc("<body><cupri-video src='clip.webm' controls muted autoplay></cupri-video></body>", "", components: true);
        t.Doc.UseVideo(backend);
        t.Layout();
        t.Doc.ConsumeImageArrived();                            // drain
        Assert.NotNull(t.Find(n => n.Element?.GetAttribute("aria-label") == "Pause"));

        backend.Players["clip.webm"].RaiseEnded();              // may come from any thread
        Assert.True(t.Doc.ConsumeImageArrived());               // the UI-thread poll reacts…
        t.Layout();
        Assert.NotNull(t.Find(n => n.Element?.GetAttribute("aria-label") == "Play")); // …and relabels
    }
}
