using CupriFace.Media;
using CupriFace.Media.Decoding;
using CupriFace.Media.Webm;
using CupriFace.Paint;
using SkiaSharp;
using Xunit;

namespace CupriFace.Tests;

/// <summary>
/// The WebM player against the real fixture with FAKE decoders and a MANUAL clock — playback
/// scheduling, pause/resume, seek-to-keyframe, end/loop and frame retirement, all deterministic
/// and codec-free (the native decoders plug into the same seam later).
/// </summary>
public class WebmPlayerTests
{
    private sealed class FakeVideoDecoder : IVideoFrameDecoder
    {
        public readonly List<(int Length, bool Keyframe)> Calls = new();
        public bool Disposed;
        public SKImage? Decode(ReadOnlySpan<byte> packet, bool keyframe)
        {
            Calls.Add((packet.Length, keyframe));
            using var s = SKSurface.Create(new SKImageInfo(160, 90));
            s.Canvas.Clear(new SKColor((byte)(Calls.Count * 17), 60, 90));
            return s.Snapshot();
        }
        public void Dispose() => Disposed = true;
    }

    private sealed class FakeFactory : IMediaDecoderFactory
    {
        public readonly FakeVideoDecoder Video = new();
        public IVideoFrameDecoder? CreateVideo(WebmTrack track) => Video;
        public IAudioDecoder? CreateAudio(WebmTrack track) => null;
    }

    private static byte[] Fixture() =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", "demo.webm"));

    private static (WebmPlayer Player, FakeFactory Factory, Box Clock) Make()
    {
        var clock = new Box();
        var factory = new FakeFactory();
        var player = new WebmPlayer(Fixture, deferred: false, factory, null, () => clock.Now);
        return (player, factory, clock);
    }

    private sealed class Box { public double Now; }

    [Fact]
    public void Frames_present_on_schedule_in_order()
    {
        var (p, f, clock) = Make();
        Assert.Equal((160, 90), p.Surface.NaturalSize);
        Assert.InRange(p.Duration, 1.2, 2.6);   // no Duration element → last-block fallback

        p.Play();
        p.Pump();                                // t = 0: only the frames due at the very start
        Assert.NotNull(p.Surface.CurrentFrame);
        var early = f.Video.Calls.Count;
        Assert.True(early >= 1);
        Assert.True(f.Video.Calls[0].Keyframe, "decode must start on the keyframe");

        var before = p.Surface.CurrentFrame;
        clock.Now = 0.8;
        p.Pump();                                // most of the clip's first half is now due
        Assert.True(f.Video.Calls.Count > early, "advancing the clock must decode more frames");
        Assert.NotSame(before, p.Surface.CurrentFrame);
    }

    [Fact]
    public void Pause_freezes_the_clock_and_resume_continues()
    {
        var (p, f, clock) = Make();
        p.Play();
        clock.Now = 0.5;
        p.Pump();
        var at = f.Video.Calls.Count;

        p.Pause();
        Assert.False(p.Playing);
        clock.Now = 50;                          // wall time races ahead while paused
        p.Pump();
        Assert.Equal(at, f.Video.Calls.Count);   // …and decodes nothing
        Assert.InRange(p.Position, 0.45, 0.55);  // media time held where it paused

        p.Play();                                // resume re-bases on the current wall clock
        clock.Now = 50.2;
        p.Pump();
        Assert.True(f.Video.Calls.Count > at);
        Assert.InRange(p.Position, 0.65, 0.75);
    }

    [Fact]
    public void Ends_once_then_replays_from_the_top()
    {
        var (p, f, clock) = Make();
        var ended = 0;
        p.Ended += () => Interlocked.Increment(ref ended);

        p.Play();
        clock.Now = 5;                           // way past the ~2 s clip
        p.Pump();
        Assert.False(p.Playing);
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref ended) == 1, 2000), "Ended must fire");
        p.Pump();
        Assert.Equal(1, Volatile.Read(ref ended));   // once, not per pump

        var decoded = f.Video.Calls.Count;
        p.Play();                                // pressing play after the end replays
        Assert.True(p.Playing);
        Assert.True(f.Video.Calls.Count >= decoded);
        Assert.True(f.Video.Calls[decoded].Keyframe, "replay restarts on the keyframe");
    }

    [Fact]
    public void Loop_wraps_instead_of_ending()
    {
        var (p, f, clock) = Make();
        p.Loop = true;
        var ended = 0;
        p.Ended += () => Interlocked.Increment(ref ended);

        p.Play();
        clock.Now = p.Duration + 1;
        p.Pump();                                // reaches the end → wraps to 0
        Assert.True(p.Playing);
        Assert.Equal(0, ended);
        Assert.InRange(p.Position, 0, 0.3);
    }

    [Fact]
    public void Seek_decodes_forward_from_the_previous_keyframe()
    {
        var (p, f, clock) = Make();
        p.Position = 1.0;                        // paused seek

        Assert.True(f.Video.Calls.Count > 0, "seek must decode the catch-up chain");
        Assert.True(f.Video.Calls[0].Keyframe, "the chain must start at a keyframe");
        Assert.NotNull(p.Surface.CurrentFrame);  // the frame at ~1.0 s is showing
        Assert.InRange(p.Position, 0.95, 1.05);
        Assert.False(p.Playing);                 // seeking does not start playback
    }

    [Fact]
    public void Ticking_follows_playback_and_dispose_is_safe_midway()
    {
        var (p, _, clock) = Make();
        Assert.False(p.Surface.Ticking);
        p.Play();
        Assert.True(p.Surface.Ticking);
        clock.Now = 0.4;
        p.Pump();
        p.Pause();
        Assert.False(p.Surface.Ticking);
        p.Dispose();                             // must not throw with frames retired/current
    }

    [Fact]
    public void The_first_frame_shows_on_open_before_any_play()
    {
        var (p, f, _) = Make();
        Assert.NotNull(p.Surface.CurrentFrame);      // poster → real picture, browser-preload parity
        Assert.True(f.Video.Calls.Count >= 1);
        Assert.False(p.Playing);
        p.Dispose();
    }

    [Fact]
    public void A_remote_source_opens_deferred_and_arrives_without_blocking()
    {
        // The download runs on the player's own thread (a real one here); until the bytes land
        // the poster stays (no frame, no size) — then the first frame appears on its own.
        using var gate = new ManualResetEventSlim(false);
        var factory = new FakeFactory();
        var player = new WebmPlayer(() => { gate.Wait(5000); return Fixture(); },
            deferred: true, factory, null);

        Assert.Null(player.Surface.CurrentFrame);
        Assert.Null(player.Surface.NaturalSize);
        player.Play();                               // autoplay while still "downloading": pending
        Assert.Null(player.Surface.CurrentFrame);

        gate.Set();
        Assert.True(SpinWait.SpinUntil(() => player.Surface.CurrentFrame is not null, 5000),
            "the first frame must appear once the bytes land");
        Assert.Equal((160, 90), player.Surface.NaturalSize);
        Assert.True(player.Playing);
        Assert.InRange(player.Position, 0, 0.5);     // playback starts at 0 — never "catches up" the download
        player.Dispose();
    }

    [Fact]
    public void A_data_uri_source_resolves_through_the_shared_pipeline()
    {
        // The developer's inline option — same scheme images support, end to end through the
        // real backend (only the codecs are fake).
        var factory = new FakeFactory();
        var backend = new WebmVideoBackend(factory);
        var src = "data:video/webm;base64," + Convert.ToBase64String(Fixture());

        using var player = (WebmPlayer)backend.Open(new VideoSource(src));
        Assert.Equal((160, 90), player.Surface.NaturalSize);
        Assert.NotNull(player.Surface.CurrentFrame);
    }

    [Fact]
    public void An_embedded_source_resolves_via_the_registered_assembly()
    {
        // The embedded option, through the DOCUMENT's wiring (UseImages registers the assembly
        // for ALL media): a bare src name finds the resource exactly like an image would.
        var factory = new FakeFactory();
        using var t = new TestDoc("<body><cupri-video src='fixtures.demo.webm' muted autoplay></cupri-video></body>",
            "", components: true);
        t.Doc.UseImages(typeof(WebmPlayerTests).Assembly);
        t.Doc.UseVideo(new WebmVideoBackend(factory));
        t.Layout();

        Assert.True(factory.Video.Calls.Count >= 1, "the embedded clip must demux and decode");
        Assert.NotNull(t.Find(n => n.SurfaceKey == "video:fixtures.demo.webm"));
    }

    [Fact]
    public void A_swapped_frame_wakes_the_host_poll_without_any_notify_call()
    {
        // The registry detects CurrentFrame reference changes on poll — a decode thread never
        // needs to know hosts exist (the paused-seek wake path).
        var registry = new SurfaceRegistry();
        var (p, _, _) = Make();
        registry.Register("v", p.Surface);
        while (registry.TakeArrived()) { }       // drain registration + initial state

        p.Position = 0.5;                        // paused seek swaps a frame in
        Assert.True(registry.TakeArrived());
        Assert.False(registry.TakeArrived());    // exactly once per change
        p.Dispose();
    }
}
