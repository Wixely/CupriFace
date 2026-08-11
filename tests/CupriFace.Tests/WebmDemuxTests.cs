using CupriFace.Media.Webm;
using Xunit;

namespace CupriFace.Tests;

/// <summary>
/// The managed WebM demuxer against the browser-recorded fixture (VP9, unknown-size Segment and
/// Clusters — the streamed shape MediaRecorder emits, i.e. the hard case).
/// </summary>
public class WebmDemuxTests
{
    private static byte[] Fixture() =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", "demo.webm"));

    [Fact]
    public void Finds_the_vp9_track_with_its_dimensions()
    {
        var f = WebmFile.Parse(Fixture());

        var video = f.VideoTrack;
        Assert.NotNull(video);
        Assert.Equal("V_VP9", video!.CodecId);
        Assert.Equal(160, video.Width);
        Assert.Equal(90, video.Height);
        Assert.Null(f.AudioTrack);   // canvas capture has no audio track
    }

    [Fact]
    public void Yields_a_plausible_frame_stream()
    {
        var f = WebmFile.Parse(Fixture());
        var frames = f.Blocks.Where(b => b.Track == f.VideoTrack!.Number).ToList();

        // ~2 s at ~15 fps → dozens of frames, every payload non-empty, first decodable frame is a key.
        Assert.InRange(frames.Count, 15, 90);
        Assert.All(frames, b => Assert.True(b.Data.Length > 0));
        Assert.True(frames[0].Keyframe, "the stream must start on a keyframe");
        Assert.Contains(frames, b => !b.Keyframe);   // and contain delta frames (it's really compressed)

        // Timestamps: start at ~0, ascend, and span roughly the recorded two seconds.
        Assert.True(frames[0].TimeSeconds < 0.25);
        for (var i = 1; i < frames.Count; i++)
            Assert.True(frames[i].TimeSeconds >= frames[i - 1].TimeSeconds, "timestamps must not go backwards");
        Assert.InRange(frames[^1].TimeSeconds, 1.2, 2.6);
    }

    [Fact]
    public void Default_timestamp_scale_is_reported()
    {
        var f = WebmFile.Parse(Fixture());
        Assert.Equal(1_000_000, f.TimestampScaleNs);   // MediaRecorder writes the Matroska default
    }

    [Fact]
    public void Garbage_fails_cleanly_not_catastrophically()
    {
        Assert.Throws<FormatException>(() => WebmFile.Parse(new byte[] { 0x00, 0x01, 0x02, 0x03 }));
        var truncated = Fixture().AsSpan(0, 200).ToArray();   // cut mid-element
        Assert.ThrowsAny<FormatException>(() => WebmFile.Parse(truncated));
    }
}
