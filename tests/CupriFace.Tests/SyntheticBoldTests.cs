using System.IO;
using CupriFace.Text;
using SkiaSharp;
using Xunit;
using Xunit.Abstractions;

namespace CupriFace.Tests;

/// <summary>
/// A bold request on a face that cannot answer it bold is synthesised (#263).
///
/// <para>Google Fonts answers a modern browser with ONE variable file per family, declared once per
/// weight. Registered that way, <c>font-weight: 700</c> resolved to the same bytes as 400 and drew
/// pixel-identical text, so every bold title in a face from that service was lost. The SkiaSharp
/// this engine builds on cannot set a variation axis (that is SkiaSharp 4's
/// <c>SKFontArguments</c>), so the face is thickened the way a browser's <c>font-synthesis</c>
/// thickens a family with no bold file. The static-file case is unchanged: a real Bold file says
/// 700 of itself and is left alone.</para>
/// </summary>
public class SyntheticBoldTests(ITestOutputHelper output)
{
    private static readonly string Fonts = Path.Combine(AppContext.BaseDirectory, "fonts");
    private static string Regular => Path.Combine(Fonts, "NotoSans-Regular.ttf");
    private static string Bold => Path.Combine(Fonts, "NotoSans-Bold.ttf");
    private static string FileUrl(string path) => new Uri(path).AbsoluteUri;

    /// <summary>The issue's registration: the SAME file declared at 400 and at 700.</summary>
    private static string OneFileTwice(string file) => $$"""
        @font-face { font-family: "Inter"; font-weight: 400; src: url("{{FileUrl(file)}}"); }
        @font-face { font-family: "Inter"; font-weight: 700; src: url("{{FileUrl(file)}}"); }
        body { margin: 0; background: #fff; }
        .t { font-family: "Inter"; font-size: 48px; color: #000; white-space: nowrap; }
        """;

    private int Ink(string css, int weight)
    {
        using var t = new TestDoc($"<body><div class='t' style='font-weight:{weight}'>Hyperframes</div></body>",
            css, width: 400, height: 100);
        t.Doc.FontPolicy = FontPolicy.RegisteredOnly;
        using var bmp = t.Render();
        var n = 0;
        for (var y = 0; y < bmp.Height; y++)
            for (var x = 0; x < bmp.Width; x++)
                if (bmp.GetPixel(x, y).Red < 0x80) n++;
        output.WriteLine($"weight {weight}: {n} px of ink");
        return n;
    }

    [Fact]
    public void A_bold_request_on_a_regular_only_face_draws_heavier_than_regular()
    {
        var css = OneFileTwice(Regular);
        var regular = Ink(css, 400);
        var bold = Ink(css, 700);
        Assert.True(regular > 500, "nothing drawn");
        Assert.True(bold > regular * 1.15f, $"700 ({bold} px) is not heavier than 400 ({regular} px) — the bold was lost");
    }

    /// <summary>Medium is below the browsers' synthesis threshold: it is the regular face,
    /// untouched, exactly as before.</summary>
    [Fact]
    public void A_medium_request_is_not_synthesised()
    {
        var css = OneFileTwice(Regular);
        Assert.Equal(Ink(css, 400), Ink(css, 500));
    }

    /// <summary>A real bold file is already bold: thickening it again would make every static
    /// Regular + Bold pair heavier than it was in every release so far.</summary>
    [Fact]
    public void A_genuine_bold_face_is_left_alone()
    {
        using var fonts = new FontService();
        var boldFace = fonts.RegisterFont(File.ReadAllBytes(Bold), "Pair", 700, 700, null);
        var regularFace = fonts.RegisterFont(File.ReadAllBytes(Regular), "Pair", 400, 400, null);
        Assert.False(FontService.NeedsSyntheticBold(boldFace, 700));
        Assert.True(FontService.NeedsSyntheticBold(regularFace, 700));
        Assert.False(FontService.NeedsSyntheticBold(regularFace, 500));
        Assert.True(fonts.GetFont("Pair", 700, 20f).Embolden == false);
        Assert.True(fonts.GetFont("Pair", 400, 20f).Embolden == false);
    }

    /// <summary>The decision is the cascade's weight against the face's own, so a fallback run
    /// (a glyph the primary lacks) in a bold span is thickened like the rest of the span.</summary>
    [Fact]
    public void The_same_decision_applies_to_a_font_asked_for_by_typeface()
    {
        using var fonts = new FontService();
        var face = fonts.RegisterFont(File.ReadAllBytes(Regular), "Solo", 400, 400, null);
        Assert.True(fonts.GetFont(face, 20f, 700).Embolden);
        Assert.False(fonts.GetFont(face, 20f, 400).Embolden);
        Assert.NotSame(fonts.GetFont(face, 20f, 700), fonts.GetFont(face, 20f, 400));
    }
}
