using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using CupriFace.Dom;
using CupriFace.Interaction;
using CupriFace.Text;
using SkiaSharp;
using Xunit;

namespace CupriFace.Tests;

/// <summary>Text rendered from registered faces under <see cref="FontPolicy.RegisteredOnly"/> is a
/// function of the document alone — on one platform. Two documents give byte-identical pixels here.
/// Across platforms the claim is narrower, and measured rather than assumed: HarfBuzz shapes the same
/// font bytes to the same advances everywhere, so the LAYOUT (every text run's box and line count)
/// must match on Windows, Linux and macOS; the PIXELS do not, because Skia's glyph rasteriser is a
/// different one on each (FreeType, DirectWrite, CoreText). Both hashes are written beside the test
/// binary; CI compares the layout hash across the three OSes and reports the pixel hashes.</summary>
public class TextDeterminismTests
{
    private static readonly string Fonts = Path.Combine(AppContext.BaseDirectory, "fonts");
    private const int W = 480, H = 320;

    private const string Html = """
        <h1>Deterministic text</h1>
        <p>The quick brown fox jumps over the lazy dog. <b>Bold</b>, <i>italic</i>, and a
        <span class='s'>small</span> run — with é, ü, ß and “quotes”.</p>
        <p class='l'>Large 36px line</p>
        <p class='r'>Right-aligned, wrapped across more than one line of text so that line breaking is part of what is measured.</p>
        """;
    private const string Css = """
        body { margin: 16px; font-family: sans-serif; font-size: 16px; line-height: 1.4; color: #222; }
        h1 { font-size: 24px; font-weight: 700; }
        .s { font-size: 11px; } .l { font-size: 36px; } .r { text-align: right; }
        """;

    private static (byte[] Pixels, string Layout, int Runs) Render()
    {
        using var doc = CupriDocument.Load(Html, Css);
        doc.LoadFonts(Fonts);
        doc.FontPolicy = FontPolicy.RegisteredOnly;
        var px = doc.RenderToPixels(W, H, SKColors.White);
        Assert.True(doc.FontReport.IsDeterministic, doc.FontReport.ToString());
        var sb = new StringBuilder();
        var runs = 0;
        Walk(doc.Root, sb, ref runs);
        return (px, sb.ToString(), runs);
    }

    // Every laid-out text run: its on-screen box to 1/100 px and how many lines it wrapped to. The
    // pixels of the glyphs are not in here; where each glyph run sits and where each line breaks is.
    private static void Walk(RenderNode n, StringBuilder sb, ref int runs)
    {
        if (n.IsText && n.Lines is { Count: > 0 })
        {
            var b = HitTesting.ScreenBox(n);
            sb.Append(string.Create(CultureInfo.InvariantCulture, $"{b.X:F2},{b.Y:F2},{b.W:F2},{b.H:F2},{n.Lines.Count}\n"));
            runs++;
        }
        foreach (var c in n.Children) Walk(c, sb, ref runs);
    }

    [Fact]
    public void Registered_fonts_render_identically_and_publish_their_hashes()
    {
        var a = Render();
        var b = Render();
        Assert.Equal(W * H * 4, a.Pixels.Length);
        Assert.True(a.Pixels.AsSpan().SequenceEqual(b.Pixels), "two documents rendered the same text differently");
        Assert.Equal(a.Layout, b.Layout);
        Assert.True(a.Runs >= 6, $"expected several text runs, found {a.Runs}");

        // Something was drawn: not the clear colour everywhere.
        Assert.Contains(a.Pixels.Chunk(4), p => p[0] != 255 || p[1] != 255 || p[2] != 255);

        var pixels = Convert.ToHexString(SHA256.HashData(a.Pixels)).ToLowerInvariant();
        var layout = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(a.Layout))).ToLowerInvariant();
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "text-hash.txt"),
            $"pixels {pixels} {W}x{H} {Environment.OSVersion.Platform}\nlayout {layout} {a.Runs} runs\n");
    }
}
