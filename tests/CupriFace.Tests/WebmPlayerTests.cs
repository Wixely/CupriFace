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
        var player = new WebmPlayer(WebmFile.Parse(Fixture()), factory, null, () => clock.Now);
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
