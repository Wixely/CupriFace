using CupriFace.Media;
using CupriFace.Media.Decoding;
using Xunit;

namespace CupriFace.Tests;

/// <summary>
/// The REAL codecs (libvpx/libopus inside <c>cupricodecs</c>) decoding the real fixture —
/// everything below the decoder seam, which fakes can't prove. Skipped when the native library
/// isn't present (a source checkout without a codecs.yml artifact): the point is to verify the
/// binding when it CAN run, never to fail a build that legitimately has no codecs.
/// </summary>
public class NativeDecodeTests
{
    private static bool Skip => !NativeDecoders.Available;

    private static byte[] Fixture() =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", "demo.webm"));

    [Fact]
    public void Vp9_frames_decode_to_real_pixels()
    {
        if (Skip) return;

        var backend = new WebmVideoBackend(new NativeDecoders());
        using var player = (WebmPlayer)backend.Open(new VideoSource(
            Path.Combine(AppContext.BaseDirectory, "fixtures", "demo.webm")));

        // Opening presents the first frame: the binding's struct layout, the I420 planes and the
        // YUV→RGBA conversion all have to be right for this to be a picture at all.
        var frame = player.Surface.CurrentFrame;
        Assert.NotNull(frame);
        Assert.Equal(160, frame!.Width);
        Assert.Equal(90, frame.Height);

        using var bitmap = new SkiaSharp.SKBitmap(new SkiaSharp.SKImageInfo(160, 90, SkiaSharp.SKColorType.Rgba8888));
        Assert.True(frame.ReadPixels(bitmap.PeekPixels()));
        var px = bitmap.Bytes;

        // The clip is a copper→steel gradient with a white bar: opaque, not uniform, and
        // genuinely colourful (a broken conversion yields black, grey or a single colour).
        var distinct = new HashSet<uint>();
        long r = 0, g = 0, b = 0;
        for (var i = 0; i + 3 < px.Length; i += 4)
        {
            Assert.Equal(255, px[i + 3]);
            distinct.Add(BitConverter.ToUInt32(px, i));
            r += px[i]; g += px[i + 1]; b += px[i + 2];
        }
        var pixels = px.Length / 4;
        Assert.True(distinct.Count > 200, $"a decoded frame must have many distinct colours (got {distinct.Count})");
        Assert.InRange(r / (double)pixels, 20, 235);   // not black, not blown out
        Assert.InRange(g / (double)pixels, 20, 235);
        Assert.InRange(b / (double)pixels, 20, 235);
    }

    [Fact]
    public void Playback_advances_through_the_clip()
    {
        if (Skip) return;

        var backend = new WebmVideoBackend(new NativeDecoders());
        using var player = (WebmPlayer)backend.Open(new VideoSource(
            Path.Combine(AppContext.BaseDirectory, "fixtures", "demo.webm")));

        var first = player.Surface.CurrentFrame;
        player.Position = 1.0;                       // seek: keyframe + catch-up chain, real codec
        var later = player.Surface.CurrentFrame;
        Assert.NotNull(later);
        Assert.NotSame(first, later);                // the picture actually moved on

        player.Play();
        Assert.True(SpinWait.SpinUntil(() => !ReferenceEquals(player.Surface.CurrentFrame, later), 3000),
            "playing must keep producing frames");
    }
}
