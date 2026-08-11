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
        public IVideoPlayer Open(string src)
        {
            Opened.Add(src);
            return Players[src] = new FakePlayer();
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
        Assert.NotNull(t.Find(n => n.Element?.GetAttribute("data-window-command") == "toggle-fullscreen"));
        Assert.NotNull(t.Find(n => n.Element?.GetAttribute("aria-label") == "Trailer"));
        // No backend registered: nothing throws, the element still renders (poster lane).
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
