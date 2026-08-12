using CupriFace.Media;
using CupriFace.Media.Decoding;
using CupriFace.Media.Webm;
using CupriFace.Paint;
using SkiaSharp;
using Xunit;

namespace CupriFace.Tests;

/// <summary>
/// The AUDIO half, against a VP9+Opus fixture (the video-only clip can't reach any of this):
/// demuxing an Opus track, decoding it with libopus, and the player feeding a sink while it
/// plays. Decode cases skip without the native library; the demux and sink-contract cases are
/// managed and always run.
/// </summary>
public class AudioTrackTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper _out;
    public AudioTrackTests(Xunit.Abstractions.ITestOutputHelper o) => _out = o;

    private bool SkipNative
    {
        get
        {
            if (NativeDecoders.Available) return false;
            _out.WriteLine("SKIPPED: cupricodecs native library not present beside the test binary.");
            return true;
        }
    }

    private static string AvPath => Path.Combine(AppContext.BaseDirectory, "fixtures", "demo-av.webm");
    private static WebmFile Av() => WebmFile.Parse(File.ReadAllBytes(AvPath));

    /// <summary>Counts what the player pushes at it; no device, no platform, no flakiness.</summary>
    private sealed class RecordingSink : IAudioSink
    {
        public int Rate, Channels, Samples, Flushes;
        public bool Paused = true, Disposed;
        public double Volume { get; set; } = 1;
        public double QueuedSeconds => Rate > 0 ? Samples / (double)(Rate * Math.Max(Channels, 1)) : 0;
        public void Start(int sampleRate, int channels) { Rate = sampleRate; Channels = channels; }
        public void Submit(ReadOnlySpan<float> pcm) => Samples += pcm.Length;
        public void Pause(bool paused) => Paused = paused;
        public void Flush() => Flushes++;
        public void Dispose() => Disposed = true;
    }

    [Fact]
    public void The_demuxer_finds_the_opus_track_and_its_packets()
    {
        var f = Av();
        var audio = f.AudioTrack;
        Assert.NotNull(audio);
        Assert.Equal("A_OPUS", audio!.CodecId);
        Assert.Equal(2, audio.Channels);                       // MediaRecorder writes stereo Opus
        Assert.True(audio.CodecPrivate is { Length: > 0 });    // OpusHead
        Assert.NotNull(f.VideoTrack);                          // …and it's still an A/V file

        var packets = f.Blocks.Where(b => b.Track == audio.Number).ToList();
        Assert.True(packets.Count > 20, $"1.5 s of Opus is dozens of packets (got {packets.Count})");
        Assert.All(packets, p => Assert.True(p.Data.Length > 0));
    }

    [Fact]
    public void Opus_packets_decode_to_pcm()
    {
        if (SkipNative) return;

        var f = Av();
        var track = f.AudioTrack!;
        using var decoder = new NativeDecoders().CreateAudio(track)!;
        Assert.Equal(48000, decoder.SampleRate);
        Assert.Equal(2, decoder.Channels);

        var total = 0;
        var peak = 0f;
        foreach (var block in f.Blocks.Where(b => b.Track == track.Number).Take(30))
        {
            var pcm = decoder.Decode(block.Data.Span);
            total += pcm.Length;
            foreach (var s in pcm.Span) peak = Math.Max(peak, Math.Abs(s));
        }
        Assert.True(total > 10_000, $"30 packets should yield plenty of samples (got {total})");
        Assert.InRange(peak, 0.01f, 1.0f);   // a real 440 Hz tone: audible, not silence, not clipped
    }

    [Fact]
    public void The_player_starts_the_sink_and_feeds_it_while_playing()
    {
        if (SkipNative) return;

        var sink = new RecordingSink();
        var backend = new WebmVideoBackend(new NativeDecoders(), sink);
        using var player = (WebmPlayer)backend.Open(new VideoSource(AvPath));

        Assert.Equal(48000, sink.Rate);       // opened with the decoder's format
        Assert.Equal(2, sink.Channels);

        player.Play();
        Assert.True(SpinWait.SpinUntil(() => sink.Samples > 0, 3000), "playing must feed audio to the sink");
        Assert.False(sink.Paused);

        player.Pause();
        Assert.True(sink.Paused);             // pausing pauses the device, not just the clock
        player.Position = 0.5;
        Assert.True(sink.Flushes > 0, "a seek must drop stale queued audio");
    }

    [Fact]
    public void Muting_silences_the_sink_without_stopping_playback()
    {
        var sink = new RecordingSink();
        var backend = new WebmVideoBackend(new FakeAudioOnlyFactory(), sink);
        using var player = (WebmPlayer)backend.Open(new VideoSource(AvPath));

        player.Volume = 0.5;
        Assert.Equal(0.5, sink.Volume, 3);
        player.Muted = true;
        Assert.Equal(0, sink.Volume, 3);      // mute is a gain of zero…
        player.Muted = false;
        Assert.Equal(0.5, sink.Volume, 3);    // …and unmuting restores the level, not full blast
    }

    /// <summary>Managed stand-in so the mute/volume contract can be asserted without codecs.</summary>
    private sealed class FakeAudioOnlyFactory : IMediaDecoderFactory
    {
        public IVideoFrameDecoder? CreateVideo(WebmTrack track) => null;
        public IAudioDecoder? CreateAudio(WebmTrack track) => new Silent();
        private sealed class Silent : IAudioDecoder
        {
            public int SampleRate => 48000;
            public int Channels => 2;
            public ReadOnlyMemory<float> Decode(ReadOnlySpan<byte> packet) => new float[240];
            public void Dispose() { }
        }
    }
}
