using System.IO;
using System.Security.Cryptography;
using CupriFace.Text;
using SkiaSharp;
using Xunit;

namespace CupriFace.Tests;

/// <summary>Text rendered from registered faces under <see cref="FontPolicy.RegisteredOnly"/> is a
/// function of the document alone. Two documents give byte-identical pixels here; the hash of those
/// pixels is written beside the test binary so CI can compare it ACROSS operating systems — the claim
/// a frame renderer rests on, and one that only a comparison between machines can actually test.</summary>
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

    private static byte[] Render()
    {
        using var doc = CupriDocument.Load(Html, Css);
        doc.LoadFonts(Fonts);
        doc.FontPolicy = FontPolicy.RegisteredOnly;
        var px = doc.RenderToPixels(W, H, SKColors.White);
        Assert.True(doc.FontReport.IsDeterministic, doc.FontReport.ToString());
        return px;
    }

    [Fact]
    public void Registered_fonts_render_byte_identically_and_publish_their_hash()
    {
        var a = Render();
        var b = Render();
        Assert.Equal(W * H * 4, a.Length);
        Assert.True(a.AsSpan().SequenceEqual(b), "two documents rendered the same text differently");

        // Something was drawn: not the clear colour everywhere.
        Assert.Contains(a.Chunk(4), p => p[0] != 255 || p[1] != 255 || p[2] != 255);

        var hash = Convert.ToHexString(SHA256.HashData(a)).ToLowerInvariant();
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "text-hash.txt"), $"{hash} {W}x{H} {Environment.OSVersion.Platform}\n");
    }
}
