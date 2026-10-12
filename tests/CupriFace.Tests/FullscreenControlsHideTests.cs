using CupriFace.Media;
using CupriFace.Paint;
using CupriFace.Style;
using SkiaSharp;
using Xunit;

namespace CupriFace.Tests;

/// <summary>
/// The fullscreen control bar gets out of the way on its own, and the delay is the application's
/// choice. A player watching something does not want a bar across the bottom of the picture for the
/// whole film; a kiosk or a review tool may want it never to leave. Both are reasonable, so the
/// engine picks a default and an app overrides it.
/// </summary>
public class FullscreenControlsHideTests
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
        public double Duration => 120;
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

    /// <summary>Fullscreen the video and start it playing, as a user would.</summary>
    private static TestDoc Playing()
    {
        var t = new TestDoc(Html, "", components: true);
        t.Doc.UseVideo(new Backend());
        t.Layout();
        t.ClickMatch(n => n.Element?.GetAttribute("data-video-role") == "fullscreen");
        t.Layout();
        t.ClickMatch(n => n.Element?.GetAttribute("data-video-role") == "toggle");
        t.Layout();
        return t;
    }

    /// <summary>The bar is hidden by a class on the element, so ask the way CSS does.</summary>
    private static bool BarVisible(TestDoc t) =>
        t.Find(n => n.Element?.ClassList.Contains("cupri-video-bar") == true
                    && n.Style.Display != DisplayType.None) is not null;

    [Fact]
    public void The_bar_goes_away_after_the_delay_and_a_tap_brings_it_back()
    {
        using var t = Playing();
        Assert.True(BarVisible(t), "the bar should be up when fullscreen starts");

        t.Doc.Animate(0);                       // arms the timer
        t.Layout();
        Assert.True(BarVisible(t), "it must not vanish on the first frame");

        t.Doc.Animate(2.9);                     // just before
        t.Layout();
        Assert.True(BarVisible(t));

        t.Doc.Animate(3.1);                     // …and just after the 3s default
        t.Layout();
        Assert.False(BarVisible(t), "the bar should have hidden itself by now");

        // A tap anywhere brings it back, and is swallowed so it does not also press what is under it.
        Assert.True(t.Doc.DispatchTap(10, 10), "the reveal must consume the tap");
        t.Layout();
        Assert.True(BarVisible(t), "the bar should come back on a tap");
    }

    [Fact]
    public void Moving_the_mouse_brings_it_back_and_restarts_the_countdown()
    {
        // The desktop gesture. A bar you can only recover by CLICKING costs the user a click they
        // did not mean to spend — and on a video, a click is a thing with meaning.
        using var t = Playing();
        t.Doc.Animate(0);
        t.Doc.Animate(3.1);
        t.Layout();
        Assert.False(BarVisible(t));

        t.Move(40, 40);
        t.Layout();
        Assert.True(BarVisible(t), "a mouse move should reveal the bar");

        // …and the countdown starts again from the move, rather than the bar staying up for ever.
        t.Doc.Animate(4.0);
        t.Layout();
        Assert.True(BarVisible(t), "still within the delay measured from the move");
        t.Doc.Animate(7.2);
        t.Layout();
        Assert.False(BarVisible(t), "it should hide again once the pointer goes quiet");
    }

    [Fact]
    public void An_application_can_choose_the_delay()
    {
        using var t = Playing();
        t.Doc.FullscreenControlsHideSeconds = 10;

        t.Doc.Animate(0);
        t.Doc.Animate(3.5);                     // past the DEFAULT, nowhere near this one
        t.Layout();
        Assert.True(BarVisible(t), "3.5s must not hide a bar configured for 10s");

        t.Doc.Animate(10.5);
        t.Layout();
        Assert.False(BarVisible(t));
    }

    [Fact]
    public void Zero_or_less_keeps_the_bar_up_for_ever()
    {
        // The kiosk case. Off means off, not "hide immediately" — a zero that hid the bar on the
        // first frame would be the opposite of what someone disabling a timeout is asking for.
        using var t = Playing();
        t.Doc.FullscreenControlsHideSeconds = 0;

        t.Doc.Animate(0);
        t.Doc.Animate(600);
        t.Layout();
        Assert.True(BarVisible(t), "a disabled timeout must never hide the bar");
    }

    [Fact]
    public void A_paused_video_keeps_its_controls()
    {
        // Nothing to get out of the way of: the bar only hides over moving picture.
        using var t = Playing();
        t.ClickMatch(n => n.Element?.GetAttribute("data-video-role") == "toggle");   // pause
        t.Layout();

        t.Doc.Animate(0);
        t.Doc.Animate(60);
        t.Layout();
        Assert.True(BarVisible(t), "a paused video must keep its controls");
    }

    [Fact]
    public void The_app_level_default_reaches_the_document()
    {
        // CupriApp is where a hosted application states it, the same way MouseActivation is.
        var app = new KioskApp();
        using var doc = app.CreateDocument();
        Assert.Equal(0, doc.FullscreenControlsHideSeconds);
    }

    private sealed class KioskApp : CupriApp
    {
        public override string Html => FullscreenControlsHideTests.Html;
        public override double FullscreenControlsHideSeconds => 0;
    }
}
