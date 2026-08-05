using SkiaSharp;
using Xunit;

namespace CupriFace.Tests;

public class RenderToPixelsTests
{
    private const string Html = "<body><div class='panel'></div></body>";
    private const string Css = "body{margin:0} .panel{width:40px;height:40px;background:#ff000080}"; // 50% red

    private static (byte r, byte g, byte b, byte a) Px(byte[] buf, int x, int y, int w)
    {
        var i = (y * w + x) * 4;
        return (buf[i], buf[i + 1], buf[i + 2], buf[i + 3]);
    }

    [Fact]
    public void Straight_and_premultiplied_alpha_and_transparent_clear()
    {
        using var doc = CupriDocument.Load(Html, Css);
        var premul = doc.RenderToPixels(100, 100, clear: null, straightAlpha: false);
        var straight = doc.RenderToPixels(100, 100, clear: null, straightAlpha: true);

        var (pr, _, _, pa) = Px(premul, 10, 10, 100);   // inside the 50% red panel
        var (sr, _, _, sa) = Px(straight, 10, 10, 100);
        var (_, _, _, ea1) = Px(premul, 90, 90, 100);   // empty area
        var (_, _, _, ea2) = Px(straight, 90, 90, 100);

        Assert.InRange(pa, 126, 130);                    // ~50% alpha both
        Assert.InRange(sa, 126, 130);
        Assert.InRange(pr, 124, 132);                    // premultiplied red pre-scaled by alpha
        Assert.True(sr >= 250, $"straight R={sr}");      // straight red stays full
        Assert.Equal(0, ea1);                            // empty is fully transparent in both
        Assert.Equal(0, ea2);
    }

    [Fact]
    public void Transparent_clear_composites_over_nothing()
    {
        using var doc = CupriDocument.Load("<body></body>", "body{margin:0}");
        var buf = doc.RenderToPixels(20, 20);            // default clear = transparent
        for (var i = 3; i < buf.Length; i += 4) Assert.Equal(0, buf[i]); // every pixel alpha 0
    }
}
