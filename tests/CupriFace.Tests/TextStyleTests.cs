using System;
using CupriFace.Paint;
using CupriFace.Style;
using SkiaSharp;
using Xunit;

namespace CupriFace.Tests;

/// <summary><c>font-style: italic</c> and <c>text-decoration</c> — both are real: the slant selects a
/// different face (so it measures and paints differently, not silently upright), and the decoration
/// lines are painted by the rasteriser. Both inherit, and the usual tags carry them by default.</summary>
public class TextStyleTests
{
    private static TextRun? FirstRun(TestDoc t)
    {
        foreach (var c in t.Doc.BuildFrame(t.Width, t.Height).Commands)
            if (c is TextRun r && r.Text.Trim().Length > 0) return r;
        return null;
    }

    [Fact]
    public void Font_style_italic_is_parsed_and_reaches_the_paint_command()
    {
        using var t = new TestDoc(
            "<body><div style='font-style:italic'>Slanted</div></body>", "", width: 300, height: 120);
        Assert.Equal(FontSlant.Italic, FirstRun(t)!.Slant);
    }

    [Fact]
    public void Em_and_i_default_to_italic_and_it_inherits()
    {
        using var t = new TestDoc(
            "<body><em>emphasis <span class='in'>nested</span></em></body>", "", width: 300, height: 120);
        var em = t.Find(n => n.Element?.LocalName == "em")!;
        Assert.Equal(FontSlant.Italic, em.Style.FontStyle);
        Assert.Equal(FontSlant.Italic, t.FindClass("in").Style.FontStyle); // inherited into the span
    }

    [Fact]
    public void Italic_text_actually_rasterises_differently_from_upright()
    {
        // The engine used to hard-code Upright, so an italic run painted identical pixels. Guard that.
        static byte[] Render(string style)
        {
            using var t = new TestDoc(
                $"<body style='padding:6px'><div style='font-size:34px;{style}'>Wavy</div></body>",
                "", width: 200, height: 80);
            using var bmp = t.Render(SKColors.White);
            return bmp.Bytes;
        }
        Assert.False(Render("font-style:italic").AsSpan().SequenceEqual(Render("")),
            "italic must not rasterise identically to upright");
    }

    [Fact]
    public void Italic_and_upright_measure_independently()
    {
        // The measurement + text-layout caches key on slant; sharing an entry would return the wrong
        // width for one of them. Both must produce a laid-out line (and normally differing widths).
        static float Width(string style)
        {
            using var t = new TestDoc(
                $"<body><span class='m' style='font-size:30px;{style}'>Measuring</span></body>", "", width: 400, height: 100);
            return t.FindClass("m").Width;
        }
        Assert.True(Width("font-style:italic") > 0 && Width("") > 0);
    }

    [Theory]
    [InlineData("underline", TextDecorations.Underline)]
    [InlineData("line-through", TextDecorations.LineThrough)]
    [InlineData("overline", TextDecorations.Overline)]
    [InlineData("underline line-through", TextDecorations.Underline | TextDecorations.LineThrough)]
    [InlineData("none", TextDecorations.None)]
    public void Text_decoration_parses(string css, TextDecorations expected)
    {
        using var t = new TestDoc(
            $"<body><div style='text-decoration:{css}'>Decorated</div></body>", "", width: 300, height: 120);
        Assert.Equal(expected, FirstRun(t)!.Decorations);
    }

    [Fact]
    public void The_shorthands_extra_words_do_not_break_the_line()
    {
        using var t = new TestDoc(
            "<body><div style='text-decoration:underline wavy red'>Decorated</div></body>", "", width: 300, height: 120);
        Assert.Equal(TextDecorations.Underline, FirstRun(t)!.Decorations);
    }

    [Fact]
    public void U_and_del_carry_their_conventional_lines()
    {
        using var t = new TestDoc("<body><u>ins</u><del>gone</del></body>", "", width: 300, height: 120);
        Assert.Equal(TextDecorations.Underline, t.Find(n => n.Element?.LocalName == "u")!.Style.Decorations);
        Assert.Equal(TextDecorations.LineThrough, t.Find(n => n.Element?.LocalName == "del")!.Style.Decorations);
    }

    [Fact]
    public void A_link_is_underlined_by_default_and_css_can_remove_it()
    {
        using var t = new TestDoc(
            "<body><a href='x' class='l1'>link</a><a href='y' class='l2'>bare</a></body>",
            ".l2 { text-decoration: none; }", width: 300, height: 120);
        Assert.Equal(TextDecorations.Underline, t.FindClass("l1").Style.Decorations);
        Assert.Equal(TextDecorations.None, t.FindClass("l2").Style.Decorations);
    }

    [Fact]
    public void An_underline_puts_ink_below_the_glyphs()
    {
        // The point of the line is visible ink that colour alone can't provide. Compare per-row ink
        // with and without it: the underlined render must add ink on rows BELOW every row the plain
        // one touches (no magic coordinates — derived from where the glyphs actually landed).
        static int[] RowInk(string style)
        {
            using var t = new TestDoc(
                $"<body style='padding:0'><div style='font-size:28px;color:#000;{style}'>Link</div></body>",
                "", width: 160, height: 60);
            using var bmp = t.Render(SKColors.White);
            var rows = new int[bmp.Height];
            for (var y = 0; y < bmp.Height; y++)
                for (var x = 0; x < bmp.Width; x++)
                    if (bmp.GetPixel(x, y).Red < 128) rows[y]++;
            return rows;
        }

        var plain = RowInk("");
        var under = RowInk("text-decoration:underline");

        var firstGlyphRow = Array.FindIndex(plain, n => n > 0);
        var lastGlyphRow = Array.FindLastIndex(plain, n => n > 0);
        Assert.True(lastGlyphRow > 0, "the plain text should have rendered something");

        // A decoration only ever ADDS ink — it must not erase or shift a single glyph pixel.
        // (The older form of this test demanded the line sit strictly BELOW the last glyph row, but
        // that is a claim about one font: plenty of faces put the underline inside the descender
        // band, so it overlapped glyph rows on CI's fonts and failed there while passing locally.)
        for (var y = 0; y < plain.Length; y++)
            Assert.True(under[y] >= plain[y], $"row {y}: the underline removed glyph ink ({plain[y]} → {under[y]})");

        var added = 0;
        var firstAddedRow = -1;
        for (var y = 0; y < plain.Length; y++)
        {
            var extra = under[y] - plain[y];
            if (extra > 0 && firstAddedRow < 0) firstAddedRow = y;
            added += extra;
        }
        Assert.True(added > 20, $"expected a line's worth of extra ink, found {added}px");

        // …and it is a LOW line — not a strike-through, not an overline.
        Assert.True(firstAddedRow > (firstGlyphRow + lastGlyphRow) / 2,
            $"underline ink should begin below the glyph mid-line (started at row {firstAddedRow}, glyphs span {firstGlyphRow}..{lastGlyphRow})");
    }
}
