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
    private readonly Xunit.Abstractions.ITestOutputHelper _out;
    public NativeDecodeTests(Xunit.Abstractions.ITestOutputHelper o) => _out = o;

    // A silent skip reads exactly like a pass, which is how a "green" run can prove nothing —
    // so say so, loudly, in the test output.
    private bool Skip
    {
        get
        {
            if (NativeDecoders.Available) return false;
            _out.WriteLine("SKIPPED: cupricodecs native library not present beside the test binary.");
            return true;
        }
    }

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
    public void A_cupri_video_element_paints_decoded_frames()
    {
        if (Skip) return;

        // The whole desktop path in one assertion: markup → component → surface registry →
        // WebM demux → libvpx → painter → rasterised pixels.
        var clip = Path.Combine(AppContext.BaseDirectory, "fixtures", "demo.webm");
        using var t = new TestDoc($"<body><cupri-video src='{clip.Replace("\\", "/")}' muted autoplay " +
                                  "style='width:160px;height:90px'></cupri-video></body>", "", components: true);
        t.Doc.UseVideo(new WebmVideoBackend(new NativeDecoders()));
        t.Layout();

        var px = t.Doc.RenderToPixels(160, 90, SkiaSharp.SKColors.Black);
        var distinct = new HashSet<uint>();
        for (var i = 0; i + 3 < px.Length; i += 4) distinct.Add(BitConverter.ToUInt32(px, i));
        Assert.True(distinct.Count > 100,
            $"the painted element must show the decoded picture, not a flat fill (got {distinct.Count} colours)");
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
